using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>一份凭证（对外形状）。</summary>
public sealed record CredentialDto(
    Guid Id,
    string Kind,
    string Subject,
    /// <summary>可见的项目（空 = 全部）。</summary>
    IReadOnlyList<Guid> ProjectScope,
    string? Note,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? CreatedBy,
    /// <summary>凭证指纹（哈希前 12 位）—— 审计里用它区分「同一个人」和「换了人」。</summary>
    string Fingerprint,
    /// <summary>归属的成员（这把钥匙是谁的）。</summary>
    Guid? MemberId = null,
    /// <summary>归属成员的显示名。</summary>
    string? MemberName = null,
    /// <summary>明文。**只在签发那一次有** —— 之后谁都看不到了（库里只有哈希）。</summary>
    string? Token = null);

/// <summary>
/// 通过认证之后拿到的身份。
///
/// 「是谁」不再是运行时自报的 —— 而是签发凭证时就绑好的。
/// </summary>
public sealed record CredentialIdentity(
    CredentialKind Kind,
    /// <summary>成员名 / agent id。</summary>
    string Subject,
    /// <summary>可见的项目。空 = 全部可见。</summary>
    IReadOnlyList<Guid> ProjectScope,
    /// <summary>凭证指纹。</summary>
    string Fingerprint,
    /// <summary>归属的成员（agent 凭证背后站着的那个人）。</summary>
    Guid? MemberId = null,
    /// <summary>归属成员的角色（管理员 / 成员）。</summary>
    MemberRole? MemberRole = null)
{
    /// <summary>这个身份能不能看这个项目。</summary>
    public bool AllowsProject(Guid projectId) =>
        ProjectScope.Count == 0 || ProjectScope.Contains(projectId);

    /// <summary>是不是管理员。</summary>
    public bool IsAdmin => MemberRole == Domain.MemberRole.Admin;
}

public interface ICredentialService
{
    /// <summary>签发一把凭证。**明文只在这里返回一次**。</summary>
    Task<CredentialDto> IssueAsync(
        CredentialKind kind,
        string subject,
        IReadOnlyList<Guid>? projectScope = null,
        string? note = null,
        DateTimeOffset? expiresAt = null,
        string? createdBy = null,
        Guid? memberId = null,
        CancellationToken cancellationToken = default);

    /// <summary>用明文凭证换身份。无效 / 过期 / 已吊销都返回 null。</summary>
    Task<CredentialIdentity?> AuthenticateAsync(string? token, CancellationToken cancellationToken = default);

    /// <summary>吊销一把凭证。</summary>
    Task<bool> RevokeAsync(Guid credentialId, CancellationToken cancellationToken = default);

    /// <summary>列出所有凭证（不带明文）。</summary>
    Task<IReadOnlyList<CredentialDto>> ListAsync(CancellationToken cancellationToken = default);
}

public sealed class CredentialService(IAppDbContext db, IClock clock) : ICredentialService
{
    public async Task<CredentialDto> IssueAsync(
        CredentialKind kind,
        string subject,
        IReadOnlyList<Guid>? projectScope = null,
        string? note = null,
        DateTimeOffset? expiresAt = null,
        string? createdBy = null,
        Guid? memberId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new GuanfuException("凭证要绑一个身份：成员名或 agent id。");
        }

        // 归属的成员必须真的存在 —— 挂一个不存在的成员等于没挂。
        if (memberId is Guid ownerId)
        {
            var exists = await db.Members
                .AsNoTracking()
                .AnyAsync(m => m.Id == ownerId, cancellationToken);

            if (!exists)
            {
                throw new GuanfuException("归属的成员不存在。");
            }
        }

        var token = GenerateToken();
        var now = clock.UtcNow;

        var credential = new AccessCredential
        {
            Kind = kind,
            Subject = subject.Trim(),
            TokenHash = Hash(token),
            ProjectScope = projectScope is { Count: > 0 }
                ? string.Join(",", projectScope.Select(p => p.ToString()))
                : null,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            CreatedBy = string.IsNullOrWhiteSpace(createdBy) ? null : createdBy.Trim(),
            MemberId = memberId
        };

        db.AccessCredentials.Add(credential);
        await db.SaveChangesAsync(cancellationToken);

        return ToDto(credential, token);
    }

    public async Task<CredentialIdentity?> AuthenticateAsync(
        string? token,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var presented = Hash(token.Trim());

        // 库里没有明文，只能逐条比对哈希 —— 凭证数量是几十级别，可以接受。
        var candidates = await db.AccessCredentials.AsNoTracking()
            .Where(c => c.RevokedAt == null)
            .ToListAsync(cancellationToken);

        var now = clock.UtcNow;

        foreach (var candidate in candidates)
        {
            if (!FixedTimeEquals(presented, candidate.TokenHash))
            {
                continue;
            }

            if (candidate.ExpiresAt is DateTimeOffset expires && expires <= now)
            {
                // 过期了就是过期了 —— 不因为「哈希对得上」就放行
                continue;
            }

            // 归属的成员被停用 → 名下的钥匙立刻失效（「人走了，钥匙还在」是最大的洞）。
            MemberRole? role = null;

            if (candidate.MemberId is Guid memberId)
            {
                var member = await db.Members.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);

                if (member is null || member.IsDisabled)
                {
                    continue;
                }

                role = member.Role;
            }

            return new CredentialIdentity(
                candidate.Kind,
                candidate.Subject,
                ParseScope(candidate.ProjectScope),
                Fingerprint(candidate.TokenHash),
                candidate.MemberId,
                role);
        }

        return null;
    }

    public async Task<bool> RevokeAsync(Guid credentialId, CancellationToken cancellationToken = default)
    {
        var credential = await db.AccessCredentials
            .FirstOrDefaultAsync(c => c.Id == credentialId, cancellationToken);

        if (credential is null || credential.RevokedAt is not null)
        {
            return false;
        }

        credential.RevokedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<IReadOnlyList<CredentialDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var all = await db.AccessCredentials.AsNoTracking()
            .Include(c => c.Member)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);

        return all.Select(c => ToDto(c)).ToList();
    }

    /// <summary>生成凭证明文（32 字节随机 → base64url）。</summary>
    internal static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    /// <summary>凭证的哈希（存库的就是它）。</summary>
    internal static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));

    /// <summary>凭证指纹（哈希前 12 位）—— 审计里用它区分「同一个人」和「换了人」。</summary>
    internal static string Fingerprint(string tokenHash) =>
        tokenHash.Length <= 12 ? tokenHash : tokenHash[..12];

    private static IReadOnlyList<Guid> ParseScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return [];
        }

        return scope
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => Guid.TryParse(part, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToList();
    }

    /// <summary>固定时间比较：先各自哈希再比，长度不同就直接返回 false 的那种比较会泄长度。</summary>
    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Convert.FromHexString(left);
        var rightBytes = Convert.FromHexString(right);

        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static CredentialDto ToDto(AccessCredential credential, string? token = null) => new(
        credential.Id,
        credential.Kind.ToString(),
        credential.Subject,
        ParseScope(credential.ProjectScope),
        credential.Note,
        credential.CreatedAt,
        credential.ExpiresAt,
        credential.RevokedAt,
        credential.CreatedBy,
        Fingerprint(credential.TokenHash),
        credential.MemberId,
        credential.Member?.DisplayName,
        token);
}
