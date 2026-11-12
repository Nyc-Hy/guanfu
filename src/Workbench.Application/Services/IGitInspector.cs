namespace Workbench.Application.Services;

/// <summary>Git 仓库的只读快照。</summary>
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
}
