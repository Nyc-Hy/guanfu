using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>团队成员（对外形状）。</summary>
public sealed record MemberDto(
    Guid Id,
    string UserName,
    string DisplayName,
    string Role,
    bool IsDisabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

/// <summary>
/// 成员管理。
///
/// 一个人用的时候不需要这套 —— 那时「一个人 = 全部可见 = owner」。
/// 团队用的时候必须有：**谁在操作必须是真的**。
/// </summary>
public interface IMemberService
{
    /// <summary>建一个成员。密码可空（没密码的成员只能靠人工凭证进）。</summary>
    Task<MemberDto> CreateAsync(
        string userName,
        string displayName,
        string? password,
        MemberRole role = MemberRole.Member,
        string? createdBy = null,
        CancellationToken cancellationToken = default);

    /// <summary>用户名 + 密码换成员。密码不对 / 停用 / 没设密码都返回 null。</summary>
    Task<MemberDto?> AuthenticateAsync(
        string? userName,
        string? password,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemberDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<MemberDto?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>按登录名找。</summary>
    Task<MemberDto?> FindByUserNameAsync(string? userName, CancellationToken cancellationToken = default);

    /// <summary>改角色。</summary>
    Task<MemberDto> SetRoleAsync(Guid id, MemberRole role, CancellationToken cancellationToken = default);

    /// <summary>停用 / 启用。停用后不能登录，名下的凭证也失效。</summary>
    Task<MemberDto> SetDisabledAsync(Guid id, bool disabled, CancellationToken cancellationToken = default);

    /// <summary>重置密码。</summary>
    Task<MemberDto> ResetPasswordAsync(Guid id, string password, CancellationToken cancellationToken = default);
}

public sealed class MemberService(IAppDbContext db, IClock clock) : IMemberService
{
    /// <summary>登录名的最小长度。太短的名字挡不住误操作，也没必要更严。</summary>
    private const int MinUserNameLength = 2;

    /// <summary>密码的最小长度。</summary>
    private const int MinPasswordLength = 8;

    public async Task<MemberDto> CreateAsync(
        string userName,
        string displayName,
        string? password,
        MemberRole role = MemberRole.Member,
        string? createdBy = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeUserName(userName);

        if (normalized.Length < MinUserNameLength)
        {
            throw new GuanfuException($"登录名至少 {MinUserNameLength} 个字符。");
        }

        var exists = await db.Members
            .AsNoTracking()
            .AnyAsync(m => m.UserNameNormalized == normalized, cancellationToken);

        if (exists)
        {
            throw new GuanfuException($"登录名「{userName.Trim()}」已经有了。");
        }

        var member = new Member
        {
            UserName = userName.Trim(),
            UserNameNormalized = normalized,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName.Trim() : displayName.Trim(),
            Role = role,
            PasswordHash = string.IsNullOrWhiteSpace(password) ? null : PasswordHasher.Hash(password),
            CreatedAt = clock.UtcNow
        };

        ValidatePassword(password);

        db.Members.Add(member);
        await db.SaveChangesAsync(cancellationToken);

        return ToDto(member);
    }

    public async Task<MemberDto?> AuthenticateAsync(
        string? userName,
        string? password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
        {
            return null;
        }

        var normalized = NormalizeUserName(userName);

        var member = await db.Members
            .FirstOrDefaultAsync(m => m.UserNameNormalized == normalized, cancellationToken);

        if (member is null || member.IsDisabled)
        {
            return null;
        }

        if (member.PasswordHash is null)
        {
            // 没设密码的成员不能自己登录 —— 他只能靠人工凭证进（人给他开门）。
            return null;
        }

        if (!PasswordHasher.Verify(password, member.PasswordHash))
        {
            return null;
        }

        member.LastLoginAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return ToDto(member);
    }

    public async Task<IReadOnlyList<MemberDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await db.Members
            .AsNoTracking()
            .OrderBy(m => m.UserNameNormalized)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    public async Task<MemberDto?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var member = await db.Members
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

        return member is null ? null : ToDto(member);
    }

    public async Task<MemberDto?> FindByUserNameAsync(
        string? userName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        var normalized = NormalizeUserName(userName);

        var member = await db.Members
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserNameNormalized == normalized, cancellationToken);

        return member is null ? null : ToDto(member);
    }

    public async Task<MemberDto> SetRoleAsync(
        Guid id,
        MemberRole role,
        CancellationToken cancellationToken = default)
    {
        var member = await db.Members
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new NotFoundException("成员", id);

        member.Role = role;
        await db.SaveChangesAsync(cancellationToken);

        return ToDto(member);
    }

    public async Task<MemberDto> SetDisabledAsync(
        Guid id,
        bool disabled,
        CancellationToken cancellationToken = default)
    {
        var member = await db.Members
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new NotFoundException("成员", id);

        member.IsDisabled = disabled;

        // 停用成员 → 名下的凭证立刻作废。
        // 「人走了，钥匙还在」是团队部署最常见的漏洞。
        if (disabled)
        {
            var credentials = await db.AccessCredentials
                .Where(c => c.MemberId == id && c.RevokedAt == null)
                .ToListAsync(cancellationToken);

            foreach (var credential in credentials)
            {
                credential.RevokedAt = clock.UtcNow;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        return ToDto(member);
    }

    public async Task<MemberDto> ResetPasswordAsync(
        Guid id,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);

        var member = await db.Members
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new NotFoundException("成员", id);

        member.PasswordHash = PasswordHasher.Hash(password);
        await db.SaveChangesAsync(cancellationToken);

        return ToDto(member);
    }

    private static void ValidatePassword(string? password)
    {
        if (password is null)
        {
            return;
        }

        if (password.Length < MinPasswordLength)
        {
            throw new GuanfuException($"密码至少 {MinPasswordLength} 位。");
        }
    }

    /// <summary>登录名归一化：去空白 + 转小写（大小写不同的名字是同一个）。</summary>
    private static string NormalizeUserName(string? userName) =>
        (userName ?? string.Empty).Trim().ToLowerInvariant();

    private static MemberDto ToDto(Member member) => new(
        member.Id,
        member.UserName,
        member.DisplayName,
        member.Role.ToString(),
        member.IsDisabled,
        member.CreatedAt,
        member.LastLoginAt);
}

/// <summary>
/// 密码哈希。
///
/// PBKDF2-HMAC-SHA256 + 每人一个随机盐。
/// 库被拿到时，光有哈希猜不出密码 —— 这是「数据库泄露 ≠ 密码泄露」的底线。
/// </summary>
public static class PasswordHasher
{
    /// <summary>迭代次数。太高会拖慢登录，太低会挡不住离线爆破。</summary>
    private const int Iterations = 100_000;

    private const int SaltBytes = 16;

    private const int HashBytes = 32;

    /// <summary>算哈希。存库的格式：<c>pbkdf2$迭代次数$盐(base64)$哈希(base64)</c>。</summary>
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return string.Join('$', "pbkdf2", Iterations, Convert.ToBase64String(salt), Convert.ToBase64String(hash));
    }

    /// <summary>验密码。存的东西认不出来就当不对（不猜）。</summary>
    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        var parts = stored.Split('$');

        if (parts.Length != 4 || !string.Equals(parts[0], "pbkdf2", StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations) || iterations <= 0)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>生成一个初始密码（人念给人听的那种）。</summary>
    public static string GenerateInitialPassword()
    {
        const string alphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        var bytes = RandomNumberGenerator.GetBytes(16);
        var builder = new StringBuilder(16);

        foreach (var value in bytes)
        {
            builder.Append(alphabet[value % alphabet.Length]);
        }

        return builder.ToString();
    }
}
