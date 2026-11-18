using Guanfu.Infrastructure;

namespace Guanfu.Web;

/// <summary>
/// 工作区搬迁的入口侧路径校验。
///
/// 归档器那边也有检查（WorkspaceArchiver.EnsureNotInsideHome），但那里的前缀比较
/// 没带目录分隔符：/home/documents-backup 会被误判成「在文档目录里面」。
/// 这里用带目录分隔符的边界比较，只认真正的目录嵌套。
///
/// 这一层不替代归档器 —— 两边都留着，入口先挡一道，兜底再挡一道。
/// </summary>
public static class WorkspacePathGuard
{
    /// <summary>导出目标校验。返回 null 表示通过，否则返回给调用方的错误说明。</summary>
    public static string? ValidateExportTarget(string? targetDirectory, GuanfuOptions options)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            return "导出目录不能为空。";
        }

        var target = TryFullPath(targetDirectory);
        if (target is null)
        {
            return "导出目录不是合法路径。";
        }

        var home = NormalizePath(options.HomeDirectory);
        if (PathEquals(target, home))
        {
            // 导出到这个目录会把正在用的 workbench.db 删掉（归档器先删同名文件再 VACUUM INTO）
            return $"导出目录不能就是数据目录本身: {target}";
        }

        var documents = NormalizePath(options.DocumentsDirectory);
        if (PathEquals(target, documents) || IsInside(target, documents))
        {
            // 导出到这个目录会把文档拷进它自己（documents/documents），无限递归
            return $"导出目录不能放在文档目录里面: {target}";
        }

        return null;
    }

    /// <summary>导入目标校验。返回 null 表示通过。</summary>
    public static string? ValidateImportTarget(string? targetHomeDirectory, GuanfuOptions options)
    {
        if (string.IsNullOrWhiteSpace(targetHomeDirectory))
        {
            return "目标数据目录不能为空。";
        }

        var target = TryFullPath(targetHomeDirectory);
        if (target is null)
        {
            return "目标 数据目录 不是合法路径。";
        }

        var home = NormalizePath(options.HomeDirectory);
        if (PathEquals(target, home))
        {
            // 导入到正在跑的目录 = 把正在用的库覆盖掉
            return "不能导入到正在运行的数据目录。请先停掉服务，或指定一个新的目录。";
        }

        return null;
    }

    /// <summary>
    /// 归一化路径：绝对化 + 去掉结尾的目录分隔符。
    ///
    /// 结尾分隔符必须去掉 —— Path.GetFullPath 在 Unix 上会保留它，
    /// 于是 "/home/" 和 "/home" 比不相等，「目标是不是观复目录」这种判断就被绕过了。
    /// （归档器那边的相等判断也有同一个洞，属于归档器的问题，这里只报告不改动。）
    /// </summary>
    public static string NormalizePath(string path)
    {
        var full = Path.GetFullPath(path);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (trimmed.Length == 0)
        {
            return full; // 根目录 "/"
        }

        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            return full; // Windows 的 "C:\"
        }

        return trimmed;
    }

    /// <summary>
    /// target 是否在 parent 目录**里面**（不含相等）。
    ///
    /// 关键点：比较时给 parent 补一个目录分隔符 ——
    /// 不补的话 /home/documents-backup 会被当成 /home/documents 的子目录。
    /// </summary>
    public static bool IsInside(string target, string parent)
    {
        var prefix = parent.EndsWith(Path.DirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;

        return target.StartsWith(prefix, PathComparison);
    }

    /// <summary>两个路径是不是同一个目录。</summary>
    public static bool PathEquals(string left, string right) =>
        string.Equals(left, right, PathComparison);

    /// <summary>
    /// 路径比较的大小写规则。Windows 与 macOS 默认文件系统不区分大小写，
    /// Linux 区分 —— 用错了要么漏判要么误杀。
    /// </summary>
    public static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string? TryFullPath(string path)
    {
        try
        {
            return NormalizePath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (PathTooLongException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
