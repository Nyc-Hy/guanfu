using Workbench.Domain;

namespace Workbench.Application.Services;

/// <summary>
/// 审计链头的外部归档。
///
/// 库里的哈希链只能做到「可发现」：拿到库文件的人如果**连哈希一起重算**，
/// 链照样能对上。要挡住这一手，得把链头定期推到**工作台数据目录之外** ——
/// 那个地方能不能被同样的人改，是部署方自己的选择（只读挂载、另一台机器、
/// 另一个权限域）。工作台负责的是：诚实地归档、诚实地对比。
/// </summary>
public interface IAuditChainArchiveService
{
    /// <summary>
    /// 把当前链头追加到归档文件。
    ///
    /// 归档文件是 append-only 的 JSONL：每行一条链头快照，
    /// 记下序号、哈希、总数与归档时刻。
    /// </summary>
    Task<AuditArchiveResult> ArchiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 用归档文件里的链头跟库里的现状对账。
    ///
    /// 能发现两种情况：
    /// ① 库里的记录被删过或被回滚过（归档里有、库里没有）
    /// ② 库里的记录被改过（同序号但哈希对不上）
    /// </summary>
    Task<AuditArchiveReport> VerifyAsync(CancellationToken cancellationToken = default);
}

/// <summary>归档结果。</summary>
public sealed record AuditArchiveResult(
    bool Ok,
    /// <summary>归档文件路径。没配置归档路径时为空。</summary>
    string? Path,
    /// <summary>归档下去的链头序号。没有可归档的链头时为 0。</summary>
    long Sequence,
    /// <summary>链头哈希。</summary>
    string? Hash,
    /// <summary>失败原因。</summary>
    string? Error);

/// <summary>与归档对账的结果。</summary>
public sealed record AuditArchiveReport(
    /// <summary>有没有配置归档路径。没配置就没什么可对的。</summary>
    bool Configured,
    /// <summary>对账是否通过。</summary>
    bool Intact,
    /// <summary>归档文件里的链头序号。</summary>
    long ArchivedSequence,
    /// <summary>库里当前的链头序号。</summary>
    long CurrentSequence,
    /// <summary>归档文件里一共归档过多少个链头。</summary>
    int ArchivedCount,
    /// <summary>对不上的原因。</summary>
    string? Reason);

/// <summary>归档文件里的一行（链头快照）。</summary>
public sealed record AuditArchiveEntry(
    long Sequence,
    string Hash,
    string? PrevHash,
    int Total,
    DateTimeOffset ArchivedAt);
