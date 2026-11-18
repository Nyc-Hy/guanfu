using System.Text;
using Microsoft.EntityFrameworkCore;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

public sealed record FileEntryDto(string Name, string RelativePath, long Size, bool IsDirectory, DateTimeOffset ModifiedAt);

public sealed record FileListResult(string Root, string RelativePath, IReadOnlyList<FileEntryDto> Entries);

public sealed record FileReadResult(string RelativePath, string Content, long Size, bool Truncated, string? Binary);

/// <summary>
/// 项目文件系统访问。
/// 根目录固定为项目的仓库路径，所有路径都必须落在根目录内（防越界）。
/// </summary>
public interface IFileSystemService
{
    Task<FileListResult> ListAsync(
        Guid projectId,
        string? relativePath = null,
        CancellationToken cancellationToken = default,
        /// <summary>
        /// 这次调用属于哪个任务 —— 有活跃租约且登记了工位时，文件工具按**工位**走。
        /// </summary>
        Guid? taskId = null,
        /// <summary>
        /// 这次调用的 agent。会话没挂任务时靠它找回工位。
        /// </summary>
        string? agentId = null);

    Task<FileReadResult> ReadAsync(
        Guid projectId,
        string relativePath,
        int maxBytes = 512 * 1024,
        CancellationToken cancellationToken = default,
        Guid? taskId = null,
        string? agentId = null);
}

public sealed class FileSystemService(IAppDbContext db, IWorkstationResolver? workstations = null) : IFileSystemService
{
    public const int DefaultMaxBytes = 512 * 1024;

    public async Task<FileListResult> ListAsync(
        Guid projectId,
        string? relativePath = null,
        CancellationToken cancellationToken = default,
        Guid? taskId = null,
        string? agentId = null)
    {
        var root = await ResolveRootAsync(projectId, taskId, agentId, cancellationToken);
        var target = ResolveInside(root, relativePath);

        if (!Directory.Exists(target))
        {
            throw new GuanfuException($"目录不存在: {relativePath ?? "."}");
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
        CancellationToken cancellationToken = default,
        Guid? taskId = null,
        string? agentId = null)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new GuanfuException("文件路径不能为空。");
        }

        var root = await ResolveRootAsync(projectId, taskId, agentId, cancellationToken);
        var target = ResolveInside(root, relativePath);

        if (!File.Exists(target))
        {
            throw new GuanfuException($"文件不存在: {relativePath}");
        }

        var info = new FileInfo(target);
        var limit = Math.Clamp(maxBytes, 1, 8 * 1024 * 1024);

        await using var stream = File.OpenRead(target);
        var buffer = new byte[limit];
        var total = 0;

        // Stream.ReadAsync **允许短读**：一次调用不保证填满缓冲区
        // （网络盘、管道、甚至本地大文件都可能只返回一部分）。
        // 以前只读一次，结果就是文件明明有内容却只拿到开头一截，还被告知「没截断」。
        // 必须循环读到 limit 或 EOF（返回 0）为止。
        while (total < limit)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, limit - total), cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        var bytes = buffer.AsSpan(0, total).ToArray();
        // 以实际读到的字节数为准：文件比上限大、或读的过程中被改小，都要如实标注
        var truncated = info.Length > total;

        // 二进制文件不做文本解码，直接告诉调用方。
        if (LooksBinary(bytes))
        {
            return new FileReadResult(relativePath, string.Empty, info.Length, truncated, "binary");
        }

        var content = DecodeText(bytes);
        return new FileReadResult(relativePath, content, info.Length, truncated, null);
    }

    /// <summary>
    /// 是不是二进制文件。
    ///
    /// 判断不能只看「有没有 0 字节」：UTF-16 的文本里就有 0 字节
    /// （中英文混排的 .txt / .csv 常见），照旧判成二进制，agent 就读不到内容了；
    /// 反过来，普通文本里混进一个 0 字节就被当成二进制，同样误导人。
    ///
    /// 现在的口径：**带 BOM 的 UTF-8 / UTF-16 都算文本**（按对应编码解码），
    /// 其余情况下出现 0 字节判二进制，控制字符占比过高也判二进制。
    /// </summary>
    internal static bool LooksBinary(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        if (HasUtf16Bom(bytes) || HasUtf8Bom(bytes))
        {
            return false;
        }

        var suspicious = 0;
        foreach (var b in bytes)
        {
            if (b == 0)
            {
                return true;
            }

            // 0x09~0x0D（制表、换行、回车）之外的 C0 控制字符
            if (b < 0x09 || (b > 0x0D && b < 0x20))
            {
                suspicious++;
            }
        }

        // 控制字符超过 5% → 多半是二进制
        return suspicious > bytes.Length / 20;
    }

    /// <summary>按 BOM 决定编码；没有 BOM 就按 UTF-8 解。</summary>
    internal static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

    private static bool HasUtf16Bom(byte[] bytes) =>
        bytes.Length >= 2 &&
        ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));

    /// <summary>
    /// 这次访问的根目录。
    ///
    /// **按工位走**：任务登记了工位（自己的 git worktree）且租约还活着，
    /// 就以工位为根 —— agent 在哪儿干活，工具就跟着看哪儿。
    /// 不然它在自己的 worktree 里写代码，观复却去读主工作区，
    /// 产出采集还会把主工作区的改动记成这个任务的产出。
    /// </summary>
    private async Task<string> ResolveRootAsync(
        Guid projectId,
        Guid? taskId,
        string? agentId,
        CancellationToken cancellationToken)
    {
        var station = workstations is null
            ? null
            : await workstations.ResolveAsync(projectId, taskId, agentId, cancellationToken);

        if (!string.IsNullOrWhiteSpace(station))
        {
            return station;
        }

        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw new NotFoundException("项目", projectId);

        if (string.IsNullOrWhiteSpace(project.RepoPath))
        {
            throw new GuanfuException("该项目没有配置仓库路径，文件系统工具不可用。");
        }

        var root = Path.GetFullPath(project.RepoPath);
        if (!Directory.Exists(root))
        {
            throw new GuanfuException($"仓库路径不存在: {root}");
        }

        return root;
    }

    /// <summary>把相对路径解析成绝对路径，并确保没有逃出根目录。</summary>
    internal static string ResolveInside(string root, string? relativePath) =>
        PathBoundary.Resolve(root, relativePath, message => new GuanfuException(message));
}

/// <summary>
/// 路径边界检查。「读仓库里的文件」和「读 数据目录 里的文档」得用同一套规则——
/// 规则一旦分叉就一定会有一处漏掉，所以抽到这里共用。
/// </summary>
public static class PathBoundary
{
    /// <summary>
    /// 把相对路径解析成绝对路径，并确保结果落在 <paramref name="root"/> 之内（root 本身也算在内）。
    /// 越界时用 <paramref name="violation"/> 造异常抛出——不同调用方要的异常类型不一样
    /// （文件系统工具用 GuanfuException，文档存储沿用 UnauthorizedAccessException）。
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
