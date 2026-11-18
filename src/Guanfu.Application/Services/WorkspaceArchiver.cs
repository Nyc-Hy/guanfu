namespace Guanfu.Application.Services;

/// <summary>导出/导入结果。</summary>
public sealed record WorkspaceArchiveResult(
    bool Ok,
    string Directory,
    int FileCount,
    long TotalBytes,
    string Message);

/// <summary>
/// 工作区搬迁。决策 6 里的「换电脑开发」靠这个落地：
/// 整个 数据目录 打包带走 —— 数据库、文档原件、加密密钥都在里面。
/// </summary>
public interface IWorkspaceArchiver
{
    /// <summary>把当前工作区导出到一个目录（不覆盖已有目录里的同名文件除非先清掉）。</summary>
    Task<WorkspaceArchiveResult> ExportAsync(string targetDirectory, CancellationToken cancellationToken = default);

    /// <summary>把一个导出目录恢复到一个新的 数据目录。</summary>
    Task<WorkspaceArchiveResult> ImportAsync(
        string sourceDirectory,
        string targetHomeDirectory,
        CancellationToken cancellationToken = default);
}
