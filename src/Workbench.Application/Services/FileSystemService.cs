using Microsoft.EntityFrameworkCore;
using Workbench.Domain;

namespace Workbench.Application.Services;

public sealed record FileEntryDto(string Name, string RelativePath, long Size, bool IsDirectory, DateTimeOffset ModifiedAt);

public sealed record FileListResult(string Root, string RelativePath, IReadOnlyList<FileEntryDto> Entries);

public sealed record FileReadResult(string RelativePath, string Content, long Size, bool Truncated, string? Binary);

/// <summary>
/// 项目文件系统访问。
/// 根目录固定为项目的仓库路径，所有路径都必须落在根目录内（防越界）。
/// </summary>
public interface IFileSystemService
{
    Task<FileListResult> ListAsync(Guid projectId, string? relativePath = null, CancellationToken cancellationToken = default);

    Task<FileReadResult> ReadAsync(Guid projectId, string relativePath, int maxBytes = 512 * 1024, CancellationToken cancellationToken = default);
}

public sealed class FileSystemService(IAppDbContext db) : IFileSystemService
{
    public const int DefaultMaxBytes = 512 * 1024;

    public async Task<FileListResult> ListAsync(
        Guid projectId,
        string? relativePath = null,
        CancellationToken cancellationToken = default)
    {
        var root = await ResolveRootAsync(projectId, cancellationToken);
        var target = ResolveInside(root, relativePath);

        if (!Directory.Exists(target))
        {
            throw new WorkbenchException($"目录不存在: {relativePath ?? "."}");
        }

        var entries = new List<FileEntryDto>();
        foreach (var info in new DirectoryInfo(target).EnumerateFileSystemInfos())
        {
            var relative = Path.GetRelativePath(root, info.FullName).Replace('\\', '/');
            entries.Add(new FileEntryDto(
                info.Name,
                relative,
                info is FileInfo f ? f.Length : 0,
                info is DirectoryInfo,
                info.LastWriteTimeUtc));
        }

        var relativeTarget = Path.GetRelativePath(root, target).Replace('\\', '/');
        if (relativeTarget == ".")
        {
            relativeTarget = string.Empty;
        }

        return new FileListResult(root, relativeTarget, entries.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Name).ToList());
    }

    public async Task<FileReadResult> ReadAsync(
        Guid projectId,
        string relativePath,
        int maxBytes = DefaultMaxBytes,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new WorkbenchException("文件路径不能为空。");
        }

        var root = await ResolveRootAsync(projectId, cancellationToken);
        var target = ResolveInside(root, relativePath);

        if (!File.Exists(target))
        {
            throw new WorkbenchException($"文件不存在: {relativePath}");
        }

        var info = new FileInfo(target);
        var limit = Math.Clamp(maxBytes, 1, 8 * 1024 * 1024);
        var truncated = info.Length > limit;

        await using var stream = File.OpenRead(target);
        var buffer = new byte[limit];
        var read = await stream.ReadAsync(buffer, cancellationToken);
        var bytes = buffer.AsSpan(0, read).ToArray();

        // 二进制文件不做文本解码，直接告诉调用方
        if (bytes.Contains((byte)0))
        {
            return new FileReadResult(relativePath, string.Empty, info.Length, truncated, "binary");
        }

        var content = System.Text.Encoding.UTF8.GetString(bytes);
        return new FileReadResult(relativePath, content, info.Length, truncated, null);
    }

    private async Task<string> ResolveRootAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new NotFoundException("项目", projectId);

        if (string.IsNullOrWhiteSpace(project.RepoPath))
        {
            throw new WorkbenchException("该项目没有配置仓库路径，文件系统工具不可用。");
        }

        var root = Path.GetFullPath(project.RepoPath);
        if (!Directory.Exists(root))
        {
            throw new WorkbenchException($"仓库路径不存在: {root}");
        }

        return root;
    }

    /// <summary>把相对路径解析成绝对路径，并确保没有逃出根目录。</summary>
    internal static string ResolveInside(string root, string? relativePath)
    {
        var rootFull = Path.GetFullPath(root);
        var combined = string.IsNullOrWhiteSpace(relativePath)
            ? rootFull
            : Path.GetFullPath(Path.Combine(rootFull, relativePath));

        if (!combined.StartsWith(rootFull, StringComparison.Ordinal))
        {
            throw new WorkbenchException($"路径越界，已拒绝访问: {relativePath}");
        }

        return combined;
    }
}
