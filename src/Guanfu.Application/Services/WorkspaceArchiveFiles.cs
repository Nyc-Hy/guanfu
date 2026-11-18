using System.Text.Json;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>
/// 工作区搬迁的纯文件操作。
///
/// 单独拎出来是因为**导入不需要数据库**——它只是把 workbench.db、documents/、secret.key
/// 三个文件搬到新目录。既然不碰数据库，就不必依赖服务在跑：
/// 换机时服务还没起，直接本地搬完再设 数据目录 就行。
///
/// 放在应用层是刻意的：Infrastructure 的导出实现和 CLI 的本地导入共用同一份，
/// 别写两遍。
/// </summary>
public static class WorkspaceArchiveFiles
{
    /// <summary>导出包里的库文件名。</summary>
    public const string DatabaseFileName = "guanfu.db";

    /// <summary>改名之前的库文件名 —— 老的导出包里是它，导入时也认。</summary>
    public const string LegacyDatabaseFileName = "workbench.db";

    /// <summary>导出清单的格式标记。</summary>
    public const string WorkspaceFormat = "guanfu-workspace";

    /// <summary>改名之前的格式标记 —— 老导出包导入时也认。</summary>
    public const string LegacyWorkspaceFormat = "workbench-workspace";
    public const string DocumentsFolderName = "documents";
    public const string KeyFileName = "secret.key";
    public const string ManifestFileName = "manifest.json";

    /// <summary>外部 agent 的运行日志目录名。</summary>
    public const string RunsFolderName = "runs";

    /// <summary>
    /// secret.key 的兜底权限（0600，只有属主可读写）。
    /// 老导出包的 manifest 里没有 keyMode，一律按这个落地 —— 密钥宁可更紧，不能人人可读。
    /// </summary>
    private const UnixFileMode DefaultKeyMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>把导出目录恢复到目标 数据目录。</summary>
    public static (int FileCount, long TotalBytes) Import(
        string sourceDirectory,
        string targetHomeDirectory,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var target = Path.GetFullPath(targetHomeDirectory);

        // 边界必须**双向**判：
        // 1) 目标在源里面 —— documents 目标目录先被 recursive 删除，而它正好就是源，导入把自己删掉；
        // 2) 源在目标里面 —— 同样会被那次 recursive 删除干掉（比如拿 数据目录/backup 当源）。
        // 只判一个方向等于只堵了一半。
        if (PathBoundary.IsSameOrInside(source, target))
        {
            throw new GuanfuException(
                $"目标目录不能是源目录本身或它的子目录: {target}（源: {source}）");
        }

        if (PathBoundary.IsSameOrInside(target, source))
        {
            throw new GuanfuException(
                $"源目录不能是目标目录本身或它的子目录: {source}（目标: {target}）。" +
                "导入会先清空目标里的 documents/runs，那样会把源一起删掉。");
        }

        // 老的导出包里库文件叫 workbench.db —— 两种都认
        var dbSource = Path.Combine(source, DatabaseFileName);
        if (!File.Exists(dbSource))
        {
            var legacy = Path.Combine(source, LegacyDatabaseFileName);
            if (File.Exists(legacy))
            {
                dbSource = legacy;
            }
        }

        if (!File.Exists(dbSource))
        {
            throw new GuanfuException($"源目录里没有 {DatabaseFileName}，不是一个观复导出目录: {source}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(target);

        var dbTarget = Path.Combine(target, DatabaseFileName);
        CopyFile(dbSource, dbTarget);

        var fileCount = 1;
        var totalBytes = new FileInfo(dbTarget).Length;

        var documentsSource = Path.Combine(source, DocumentsFolderName);
        var documentsTarget = Path.Combine(target, DocumentsFolderName);
        if (Directory.Exists(documentsSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReplaceDirectory(documentsTarget);
            CopyDirectory(documentsSource, documentsTarget, cancellationToken);
            var (count, bytes) = Measure(documentsTarget);
            fileCount += count;
            totalBytes += bytes;
        }

        var keySource = Path.Combine(source, KeyFileName);
        if (File.Exists(keySource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var keyTarget = Path.Combine(target, KeyFileName);
            CopyFile(keySource, keyTarget);

            // File.Copy 不保留 Unix 权限：不恢复的话 0600 的密钥落地变 0644，
            // 新机器上人人都能读它（数据库密码就是用它加密的）。
            RestoreKeyMode(source, keyTarget);

            fileCount++;
        }

        // 外部 agent 的运行日志：换机后也要能看到之前的运行记录
        var runsSource = Path.Combine(source, RunsFolderName);
        if (Directory.Exists(runsSource))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runsTarget = Path.Combine(target, RunsFolderName);
            ReplaceDirectory(runsTarget);
            CopyDirectory(runsSource, runsTarget, cancellationToken);
            var (count, bytes) = Measure(runsTarget);
            fileCount += count;
            totalBytes += bytes;
        }

        return (fileCount, totalBytes);
    }

    /// <summary>
    /// 按导出清单恢复 secret.key 的 Unix 权限（非 Windows）。
    ///
    /// 权限来自 manifest 的 <c>keyMode</c>（八进制字符串，如 "0600"）而不是源文件的当前权限：
    /// 导出物可能被打包成 zip、或经过不保留权限的文件系统中转，文件本身的位已经不可信。
    /// </summary>
    private static void RestoreKeyMode(string sourceDirectory, string keyTarget)
    {
        if (OperatingSystem.IsWindows())
        {
            return;   // Windows 没有 Unix 权限位，SetUnixFileMode 会抛 PlatformNotSupported
        }

        File.SetUnixFileMode(keyTarget, ReadKeyMode(sourceDirectory));
    }

    /// <summary>读 manifest 里的 keyMode；读不到 / 认不出来一律回落到 0600。</summary>
    private static UnixFileMode ReadKeyMode(string sourceDirectory)
    {
        var manifestPath = Path.Combine(sourceDirectory, ManifestFileName);

        try
        {
            if (File.Exists(manifestPath))
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));

                if (manifest.RootElement.ValueKind == JsonValueKind.Object &&
                    manifest.RootElement.TryGetProperty("keyMode", out var keyMode) &&
                    keyMode.ValueKind == JsonValueKind.String &&
                    TryParseOctalMode(keyMode.GetString(), out var mode))
                {
                    return mode;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 清单坏了不该让整次搬迁失败：密钥已经拷过去了，按最紧的默认权限落地就行。
        }

        return DefaultKeyMode;
    }

    /// <summary>
    /// 八进制权限字符串（"0600" / "600"）→ <see cref="UnixFileMode"/>。
    /// 只取 9 个权限位，setuid / setgid / sticky 一概不带过来 —— 密钥文件不需要它们。
    /// </summary>
    private static bool TryParseOctalMode(string? text, out UnixFileMode mode)
    {
        mode = DefaultKeyMode;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var digits = text.Trim();
        if (digits.Length is < 1 or > 4)
        {
            return false;
        }

        var value = 0;
        foreach (var digit in digits)
        {
            if (digit is < '0' or > '7')
            {
                return false;
            }

            value = (value << 3) | (digit - '0');
        }

        mode = (UnixFileMode)(value & 0b1_1111_1111);
        return true;
    }

    /// <summary>
    /// 清掉目标子目录，准备整份覆盖。
    /// 目标是软链时直接拒绝：recursive 删除可能顺着链接把**工作区之外**的目录清空。
    /// </summary>
    private static void ReplaceDirectory(string target)
    {
        if (!Directory.Exists(target))
        {
            return;
        }

        if (IsLink(new DirectoryInfo(target)))
        {
            throw new GuanfuException($"目标目录是符号链接，拒绝删除: {target}。请先手工处理这个链接再导入。");
        }

        Directory.Delete(target, recursive: true);
    }

    public static void CopyFile(string source, string target)
    {
        try
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new GuanfuException(
                $"拷贝 {Path.GetFileName(source)} 失败：{ex.Message}。" +
                "如果目标 数据目录 正在被服务占用，请先停掉服务再导入。");
        }
    }

    /// <summary>
    /// 递归拷贝目录。**不跟随目录软链**，并且按真实路径防环。
    ///
    /// 以前是无条件递归：documents 里放一个指向 / 或指向自己祖先的软链，
    /// 导出/导入就会顺着它把整个磁盘走一遍（环则直接栈溢出或拷到天荒地老）。
    /// </summary>
    public static void CopyDirectory(string source, string target, CancellationToken cancellationToken = default)
    {
        var root = PathBoundary.ResolveRealPath(source);
        CopyDirectoryCore(source, target, root, new HashSet<string>(PathComparer), cancellationToken);
    }

    private static void CopyDirectoryCore(
        string source,
        string target,
        string root,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 防环：同一个真实目录只进一次。软链指向祖先时，字面路径不同但真实路径相同。
        if (!visited.Add(PathBoundary.ResolveRealPath(source)))
        {
            return;
        }

        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = new FileInfo(file);
            if (IsLink(info))
            {
                // 文件软链：只有指向导出根目录**内部**的才拷内容，
                // 指向外部的一律跳过 —— 否则等于把工作区之外的文件偷偷带进归档。
                var real = PathBoundary.ResolveRealPath(file);
                if (!PathBoundary.IsSameOrInside(root, real))
                {
                    continue;
                }
            }

            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 目录软链一律不跟随：它可能指向磁盘任何地方，也可能就是自己的祖先。
            if (IsLink(new DirectoryInfo(dir)))
            {
                continue;
            }

            CopyDirectoryCore(dir, Path.Combine(target, Path.GetFileName(dir)), root, visited, cancellationToken);
        }
    }

    /// <summary>是不是软链 / 重解析点。</summary>
    private static bool IsLink(FileSystemInfo info) =>
        info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>路径比较口径跟 <see cref="PathBoundary"/> 保持一致（Windows 不区分大小写）。</summary>
    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static (int Count, long Bytes) Measure(string directory)
    {
        var count = 0;
        long bytes = 0;

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            count++;
            bytes += new FileInfo(file).Length;
        }

        return (count, bytes);
    }
}
