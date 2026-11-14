namespace Workbench.Application.Services;

/// <summary>git 报出来的一次文件变更。</summary>
public sealed record GitFileChange(
    string Path,
    string ChangeKind,
    int LinesAdded,
    int LinesDeleted,
    string? OldPath = null);

/// <summary>
/// Git 仓库的只读快照。
/// </summary>
public sealed record GitState(
    string? Branch,
    string? HeadSha,
    bool Dirty,
    int ChangedFiles,
    string? Error)
{
    /// <summary>有改动的文件（相对 HEAD）。评审时要逐个看，所以单独列出来。</summary>
    public IReadOnlyList<string> ChangedFilePaths { get; init; } = [];
}

/// <summary>
/// 只读 Git 检视。P3 会扩展出 commit/push（写操作走审批）。
/// 这里刻意只做只读：任何写操作都不能出现在这条通路上。
/// </summary>
public interface IGitInspector
{
    GitState Inspect(string repoPath);

    /// <summary>
    /// 待提交内容的指纹（工作区状态 + 相对 HEAD 的 diff + 未跟踪文件内容）。
    /// git_commit 的审批要绑它：人批的是「这些内容」，不是「提交这个动作」。
    /// 读不到仓库时返回 null —— 调用方必须按「拒绝」处理。
    /// </summary>
    string? GetPendingCommitFingerprint(string repoPath);

    /// <summary>一个 commit 改了哪些文件。</summary>
    IReadOnlyList<GitFileChange> GetCommitFileChanges(string repoPath, string commitSha);

    /// <summary>工作区里改了哪些文件（相对 HEAD，含未跟踪文件）。</summary>
    IReadOnlyList<GitFileChange> GetWorkingTreeFileChanges(string repoPath);

    /// <summary>当前 HEAD 的 commit sha。读不到返回 null。</summary>
    string? GetHeadSha(string repoPath);
}
