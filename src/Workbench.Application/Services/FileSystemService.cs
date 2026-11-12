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
    internal static string ResolveInside(string root, string? relativePath) =>
        PathBoundary.Resolve(root, relativePath, message => new WorkbenchException(message));
}

/// <summary>
/// 路径边界检查。「读仓库里的文件」和「读 WORKBENCH_HOME 里的文档」得用同一套规则——
/// 规则一旦分叉就一定会有一处漏掉，所以抽到这里共用。
/// </summary>
public static class PathBoundary
{
    /// <summary>
    /// 把相对路径解析成绝对路径，并确保结果落在 <paramref name="root"/> 之内（root 本身也算在内）。
    /// 越界时用 <paramref name="violation"/> 造异常抛出——不同调用方要的异常类型不一样
    /// （文件系统工具用 WorkbenchException，文档存储沿用 UnauthorizedAccessException）。
    /// </summary>
    public static string Resolve(string root, string? relativePath, Func<string, Exception> violation)
    {
        var rootFull = Normalize(Path.GetFullPath(root));
        var combined = string.IsNullOrWhiteSpace(relativePath)
            ? rootFull
            : Normalize(Path.GetFullPath(Path.Combine(rootFull, relativePath)));

        // 第一层：字面边界。比较时必须带上目录分隔符，
        // 否则 /tmp/wb-root-secrets 会被当成 /tmp/wb-root 的子目录放过去。
        if (!IsInside(rootFull, combined))
        {
            throw violation(Describe(relativePath, combined, rootFull));
        }

        // 第二层：符号链接边界。GetFullPath 不解析软链，
        // 根目录里一个指向外部的软链照样能把文件读出去，所以要按真实路径再判一次。
        // 根目录自身也要解软链后再比较，否则 macOS 上 /tmp -> /private/tmp 会被误判成越界。
        var realRoot = ResolveRealPath(rootFull);
        var realCombined = ResolveRealPath(combined);
        if (!IsInside(realRoot, realCombined))
        {
            throw violation(Describe(relativePath, realCombined, realRoot));
        }

        return combined;
    }

    /// <summary>
    /// 判断 candidate 是否等于 root 或落在 root 之内。
    /// 两层判定：字面路径 + 解软链后的真实路径。
    ///
    /// 单独提供这个入口是因为「某目录是不是在另一个目录里」这个判断散落在好几处
    /// （工作区导出、导入、文档存储），口径必须一致 —— 一处用 StartsWith、
    /// 一处用 Equals，迟早会漏掉带尾斜杠或兄弟前缀的那种绕过。
    /// </summary>
    public static bool IsSameOrInside(string root, string candidate)
    {
        var rootFull = Normalize(Path.GetFullPath(root));
        var candidateFull = Normalize(Path.GetFullPath(candidate));

        if (!IsInside(rootFull, candidateFull))
        {
            return false;
        }

        return IsInside(ResolveRealPath(rootFull), ResolveRealPath(candidateFull));
    }

    /// <summary>去掉结尾的目录分隔符（GetFullPath 会保留它），文件系统根目录本身除外。</summary>
    public static string Normalize(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root) || path.Length <= root.Length)
        {
            return path;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>path 是否落在 root 之内（root 本身也算）。</summary>
    public static bool IsInside(string root, string path)
    {
        // Windows 上路径大小写不敏感，别用 Ordinal 把同一个目录判成越界
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (string.Equals(root, path, comparison))
        {
            return true;
        }

        var prefix = root.EndsWith(Path.DirectorySeparatorChar) || root.EndsWith(Path.AltDirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return path.StartsWith(prefix, comparison);
    }

    /// <summary>逐段解析软链，得到真实路径。</summary>
    public static string ResolveRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return full;
        }

        return Path.GetFullPath(ResolveSegments(root, SplitSegments(full[root.Length..])));
    }

    private static string ResolveSegments(string current, string[] segments)
    {
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);

            var resolved = ResolveLink(current);
            if (resolved is null)
            {
                continue;
            }

            // 链接目标自己可能还含软链，必须递归解析一遍：
            // 否则目标写成 /var/... 而根目录解析成 /private/var/... 时，
            // 明明在根内也会被判成越界。
            var targetRoot = Path.GetPathRoot(resolved);
            if (string.IsNullOrEmpty(targetRoot))
            {
                continue;
            }

            current = ResolveSegments(targetRoot, SplitSegments(resolved[targetRoot.Length..]));
        }

        return current;
    }

    private static string[] SplitSegments(string remainder) =>
        remainder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>path 本身是软链时返回它指向的真实路径；不是软链返回 null。</summary>
    private static string? ResolveLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);

            var target = info.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null)
            {
                return null;
            }

            // 软链目标可能是相对路径，要自己拼成绝对路径
            var targetPath = target.FullName;
            if (!Path.IsPathRooted(targetPath))
            {
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                {
                    targetPath = Path.Combine(parent, targetPath);
                }
            }

            return Path.GetFullPath(targetPath);
        }
        catch (IOException)
        {
            // 自指软链之类的坏链：解析不了就当普通路径，后面的 File.Exists 会自然失败
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Describe(string? relativePath, string resolved, string root) =>
        $"路径越界，已拒绝访问: {relativePath ?? "."}（解析为 {resolved}，根目录 {root}）";
}
