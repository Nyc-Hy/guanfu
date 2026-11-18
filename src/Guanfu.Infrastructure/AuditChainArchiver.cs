using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Guanfu.Application;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Infrastructure;

/// <summary>
/// 把审计链头归档到观复的数据目录**之外**的文件里。
///
/// 为什么要在外面：库里的哈希链是应用层算的，拿到库文件的人连哈希一起重算，
/// 链照样能对上。链头归档到观复够不着的地方，才多一道对上的机会 ——
/// 那个地方能不能被同样的人改，是部署方的选择，观复只管诚实地归档与对比。
///
/// 归档文件是 append-only 的 JSONL，一行一条链头快照。
/// </summary>
public sealed class AuditChainArchiver(
    IAppDbContext db,
    IToolAuditService audits,
    IOptions<GuanfuOptions> options,
    IClock clock) : IAuditChainArchiveService
{
    /// <summary>归档路径配的是目录时，链头文件放这个名字。</summary>
    public const string DefaultFileName = "guanfu-audit-chain.jsonl";

    private readonly GuanfuOptions _options = options.Value;

    public async Task<AuditArchiveResult> ArchiveAsync(CancellationToken cancellationToken = default)
    {
        var path = ResolveArchivePath();
        if (path is null)
        {
            return new AuditArchiveResult(
                false, null, 0, null,
                "没有配置审计归档路径（Guanfu:AuditArchivePath）。不配置就不归档 —— " +
                "链只在库内可验，拿到库文件的人连哈希一起重算就挡不住。");
        }

        var head = await LoadHeadAsync(cancellationToken);
        if (head is null)
        {
            return new AuditArchiveResult(false, path, 0, null, "还没有带哈希的审计记录，无从归档。");
        }

        var entry = new AuditArchiveEntry(
            head.Sequence, head.Hash!, head.PrevHash, head.Total, clock.UtcNow);

        try
        {
            // 链头没变就别再追加一行 —— 定时任务每 10 分钟跑一次，
            // 一次没写入就等于把归档文件灌成流水账。
            var last = await ReadLastEntryAsync(path, cancellationToken);
            if (last is not null && last.Sequence == entry.Sequence
                && string.Equals(last.Hash, entry.Hash, StringComparison.Ordinal))
            {
                return new AuditArchiveResult(
                    true, path, entry.Sequence, entry.Hash, null);
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.AppendAllTextAsync(
                path, JsonSerializer.Serialize(entry) + Environment.NewLine, Encoding.UTF8, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AuditArchiveResult(false, path, 0, null, $"归档失败：{ex.Message}");
        }

        return new AuditArchiveResult(true, path, entry.Sequence, entry.Hash, null);
    }

    public async Task<AuditArchiveReport> VerifyAsync(CancellationToken cancellationToken = default)
    {
        var path = ResolveArchivePath();
        if (path is null)
        {
            return new AuditArchiveReport(false, true, 0, 0, 0, "没有配置审计归档路径，无从对账。");
        }

        if (!File.Exists(path))
        {
            return new AuditArchiveReport(true, true, 0, 0, 0, "归档文件还不存在（还没归档过）。");
        }

        var entries = await ReadEntriesAsync(path, cancellationToken);
        if (entries.Count == 0)
        {
            return new AuditArchiveReport(true, false, 0, 0, 0, "归档文件里没有可解析的链头记录。");
        }

        var archived = entries[^1];

        // 先看库内链自身断没断 —— 断了自己先说清楚。
        var chain = await audits.VerifyChainAsync(cancellationToken);
        if (!chain.Intact)
        {
            return new AuditArchiveReport(
                true, false, archived.Sequence, chain.Total, entries.Count,
                $"库内的链断了：{chain.Reason}");
        }

        var head = await LoadHeadAsync(cancellationToken);
        var current = head?.Sequence ?? 0;

        if (current < archived.Sequence)
        {
            return new AuditArchiveReport(
                true, false, archived.Sequence, current, entries.Count,
                $"库里的链头（{current}）比归档时（{archived.Sequence}）更早 —— " +
                "有记录被删掉，或整个库被回滚过。");
        }

        var same = await db.ToolCallAudits.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Sequence == archived.Sequence, cancellationToken);

        if (same is null)
        {
            return new AuditArchiveReport(
                true, false, archived.Sequence, current, entries.Count,
                $"归档里的第 {archived.Sequence} 条在库里找不到了 —— 那条记录被删过。");
        }

        if (!string.Equals(same.Hash, archived.Hash, StringComparison.Ordinal))
        {
            return new AuditArchiveReport(
                true, false, archived.Sequence, current, entries.Count,
                $"第 {archived.Sequence} 条的哈希跟归档时对不上 —— 它被改过。");
        }

        var newer = current - archived.Sequence;
        return new AuditArchiveReport(
            true, true, archived.Sequence, current, entries.Count,
            newer > 0
                ? $"对得上。归档之后又新增了 {newer} 条。"
                : "对得上。库里没有比归档更新的记录。");
    }

    /// <summary>
    /// 解析归档路径。没配就返回 null。
    ///
    /// 配在观复的数据目录里面等于没配 —— 拿到库文件的人连归档文件一起改就白搭。
    /// 这种路径直接拒掉，不给「看起来有归档其实没有」的假象。
    /// </summary>
    internal string? ResolveArchivePath()
    {
        var configured = _options.AuditArchivePath;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var full = Path.GetFullPath(configured);

        // 配的是目录就在里面放默认文件名
        if (Directory.Exists(full) || configured.EndsWith(Path.DirectorySeparatorChar))
        {
            full = Path.Combine(full, DefaultFileName);
        }

        if (PathBoundary.IsSameOrInside(_options.HomeDirectory, full))
        {
            throw new GuanfuException(
                $"审计归档路径必须在观复的数据目录之外（{_options.HomeDirectory}）。" +
                "放在里面的话，拿到库文件的人连归档文件一起改就白搭。");
        }

        return full;
    }

    private async Task<AuditHead?> LoadHeadAsync(CancellationToken cancellationToken)
    {
        var rows = await db.ToolCallAudits.AsNoTracking()
            .OrderByDescending(a => a.Sequence)
            .ToListAsync(cancellationToken);

        // 链启用前的老记录没有哈希，不参与。
        var head = rows.FirstOrDefault(a => !string.IsNullOrEmpty(a.Hash));
        return head is null ? null : new AuditHead(head.Sequence, head.Hash, head.PrevHash, rows.Count);
    }

    private static async Task<List<AuditArchiveEntry>> ReadEntriesAsync(string path, CancellationToken cancellationToken)    {
        var entries = new List<AuditArchiveEntry>();

        foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var entry = JsonSerializer.Deserialize<AuditArchiveEntry>(line);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
            catch (JsonException)
            {
                // 归档文件里有一行坏掉不该让整份对账崩掉 —— 跳过，继续看后面的。
                continue;
            }
        }

        return entries;
    }

    /// <summary>归档文件里最后一条能解析的链头快照。文件还不存在时返回 null。</summary>
    private static async Task<AuditArchiveEntry?> ReadLastEntryAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var entries = await ReadEntriesAsync(path, cancellationToken);
        return entries.Count == 0 ? null : entries[^1];
    }

    private sealed record AuditHead(long Sequence, string? Hash, string? PrevHash, int Total);
}
