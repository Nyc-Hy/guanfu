using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Infrastructure;

/// <summary>
/// 工作区搬迁实现。
///
/// 数据库快照用 SQLite 的 <c>VACUUM INTO</c> —— 边跑边导也不会导出半截文件。
/// 直接复制正在写的 .db 文件可能拿到撕裂的页，导入后就是坏库。
/// </summary>
public sealed class WorkspaceArchiver(
    GuanfuDbContext db,
    IOptions<GuanfuOptions> options) : IWorkspaceArchiver
{
    private const string ManifestFileName = WorkspaceArchiveFiles.ManifestFileName;
    private const string DatabaseFileName = WorkspaceArchiveFiles.DatabaseFileName;
    private const string WorkspaceFormat = WorkspaceArchiveFiles.WorkspaceFormat;
    private const string DocumentsFolderName = WorkspaceArchiveFiles.DocumentsFolderName;
    private const string KeyFileName = WorkspaceArchiveFiles.KeyFileName;

    private readonly GuanfuOptions _options = options.Value;

    public async Task<WorkspaceArchiveResult> ExportAsync(
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new GuanfuException("导出目录不能为空。");
        }

        var target = Path.GetFullPath(targetDirectory);
        EnsureNotInsideHome(target, "导出");
        EnsureTargetIsExportSlot(target);

        cancellationToken.ThrowIfCancellationRequested();
        using var staging = new WorkspaceArchiveStaging(target);
        var dbTarget = Path.Combine(staging.StagePath, DatabaseFileName);

        // VACUUM INTO 的目标不能已存在，且路径里的单引号要转义。
        // SQLite 的 VACUUM INTO 不接受绑定参数，只能内联；路径是我们自己拼的，
        // 且已把单引号翻倍转义，不存在注入面。
#pragma warning disable EF1002
        var escaped = dbTarget.Replace("'", "''", StringComparison.Ordinal);
        await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{escaped}'", cancellationToken);
#pragma warning restore EF1002

        var fileCount = 1;
        var totalBytes = new FileInfo(dbTarget).Length;

        // 文档原件
        var documentsSource = _options.DocumentsDirectory;
        var documentsTarget = Path.Combine(staging.StagePath, DocumentsFolderName);
        if (Directory.Exists(documentsSource))
        {
            WorkspaceArchiveFiles.CopyDirectory(documentsSource, documentsTarget, cancellationToken);
            var (count, bytes) = WorkspaceArchiveFiles.Measure(documentsTarget);
            fileCount += count;
            totalBytes += bytes;
        }

        // 外部 agent 的运行日志：换机后还要能看到之前的运行记录
        var runsSource = _options.RunsDirectory;
        var runsTarget = Path.Combine(staging.StagePath, WorkspaceArchiveFiles.RunsFolderName);
        if (Directory.Exists(runsSource))
        {
            WorkspaceArchiveFiles.CopyDirectory(runsSource, runsTarget, cancellationToken);
            var (count, bytes) = WorkspaceArchiveFiles.Measure(runsTarget);
            fileCount += count;
            totalBytes += bytes;
        }

        var packagesSource = Path.Combine(_options.HomeDirectory, WorkspaceArchiveFiles.PackagesFolderName);
        var packagesTarget = Path.Combine(staging.StagePath, WorkspaceArchiveFiles.PackagesFolderName);
        // 空目录也保留，恢复时可区分空的新版备份和没有收录交付包的旧备份。
        Directory.CreateDirectory(packagesTarget);
        if (Directory.Exists(packagesSource))
        {
            WorkspaceArchiveFiles.CopyDirectory(packagesSource, packagesTarget, cancellationToken);
            var (count, bytes) = WorkspaceArchiveFiles.Measure(packagesTarget);
            fileCount += count;
            totalBytes += bytes;
        }

        // 加密密钥：数据库密码是 AES-GCM 加密的，密钥丢了换机后连不上老库
        var keySource = _options.KeyFilePath;
        string? keyMode = null;
        if (File.Exists(keySource))
        {
            var keyTarget = Path.Combine(staging.StagePath, KeyFileName);
            File.Copy(keySource, keyTarget, overwrite: true);

            // File.Copy 不保留 Unix 权限：secret.key 源是 0600，复制出来变 0644，
            // 导出目录里人人都能读密钥。复制后按源文件把权限设回去（非 Windows）。
            // 权限信息同时写进 manifest（keyMode，八进制），导入侧据此恢复 ——
            // 导出物可能被打包成 zip / 经不保留权限的文件系统中转，光靠文件本身不够。
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(keySource);
                File.SetUnixFileMode(keyTarget, mode);
                keyMode = Convert.ToString((int)mode, 8).PadLeft(4, '0');
            }

            fileCount++;
            totalBytes += new FileInfo(keyTarget).Length;
        }

        var manifest = new
        {
            format = WorkspaceFormat,
            version = 2,
            exportedAt = DateTimeOffset.UtcNow,
            home = _options.HomeDirectory,
            database = DatabaseFileName,
            documents = DocumentsFolderName,
            runs = Directory.Exists(runsSource) ? WorkspaceArchiveFiles.RunsFolderName : null,
            packages = WorkspaceArchiveFiles.PackagesFolderName,
            key = File.Exists(keySource) ? KeyFileName : null,
            keyMode
        };

        var manifestPath = Path.Combine(staging.StagePath, ManifestFileName);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);

        staging.Publish(ExportEntries, cancellationToken, () => EnsureTargetIsExportSlot(target));
        return new WorkspaceArchiveResult(
            true, target, fileCount, totalBytes,
            $"已导出到 {target}（{fileCount} 个文件，{totalBytes} 字节）。整个目录拷到新机器即可。");
    }

    public async Task<WorkspaceArchiveResult> ImportAsync(
        string sourceDirectory,
        string targetHomeDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            throw new GuanfuException("导入源目录不能为空。");
        }

        if (string.IsNullOrWhiteSpace(targetHomeDirectory))
        {
            throw new GuanfuException("目标数据目录不能为空。");
        }

        var target = Path.GetFullPath(targetHomeDirectory);

        // 导入到正在跑的 数据目录 会把正在用的库覆盖掉 —— 直接拒。
        // 比较要走归一化：带尾斜杠的 "~/.workbench/" 不等于 "~/.workbench"，
        // 用裸 Equals 比会漏掉，然后 import 就把正在用的库覆盖了。
        if (PathBoundary.IsSameOrInside(_options.HomeDirectory, target))
        {
            throw new GuanfuException(
                "不能导入到正在运行的数据目录。请先停掉服务，或指定一个新的目录。");
        }

        var (fileCount, totalBytes) = WorkspaceArchiveFiles.Import(sourceDirectory, target, cancellationToken);

        await Task.CompletedTask;

        return new WorkspaceArchiveResult(
            true, target, fileCount, totalBytes,
            $"已恢复到 {target}。把 数据目录 指到这个目录再启动服务即可。");
    }

    private static readonly string[] ExportEntries =
    [
        DatabaseFileName, ManifestFileName, DocumentsFolderName,
        WorkspaceArchiveFiles.RunsFolderName, WorkspaceArchiveFiles.PackagesFolderName, KeyFileName
    ];

    /// <summary>导出只替换本工具的归档文件。</summary>
    private static void EnsureTargetIsExportSlot(string target)
    {
        if (!Directory.Exists(target))
        {
            return;
        }
        var unexpected = Directory.EnumerateFileSystemEntries(target)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) && !ExportEntries.Contains(name, StringComparer.Ordinal))
            .Take(5)
            .ToList();

        if (unexpected.Count == 0)
        {
            return;
        }

        throw new GuanfuException(
            $"导出目录里已有非导出文件（{string.Join("、", unexpected)}），拒绝覆盖: {target}。" +
            "请换一个空目录。");
    }

    /// <summary>
    /// 导出目录不能落在数据目录里 —— 否则会把源目录当目标删掉：
    /// 目标是 home 时 workbench.db 会被删（正在用的库），home/documents 会被删（原件）。
    ///
    /// 比较必须归一化：GetFullPath 会保留尾斜杠，"/home/" != "/home"，
    /// 而 StartsWith("/root/documents") 又会把 "/root/documents-backup" 误判成子目录。
    /// 这里统一用 PathBoundary 的口径（字面 + 解软链两层）。
    /// </summary>
    private void EnsureNotInsideHome(string target, string action)
    {
        var home = PathBoundary.Normalize(Path.GetFullPath(_options.HomeDirectory));

        // home 就是文件系统根目录时，"在里面"没有意义（否则任何路径都在里面），
        // 但"就是 home 本身"仍然要拦。
        var homeIsRoot = string.Equals(home, Path.GetPathRoot(home), StringComparison.Ordinal);

        if (string.Equals(target, home, StringComparison.Ordinal) && !homeIsRoot)
        {
            throw new GuanfuException($"{action}目录不能就是数据目录本身: {target}");
        }

        if (homeIsRoot)
        {
            return;
        }

        if (PathBoundary.IsSameOrInside(home, target))
        {
            throw new GuanfuException(
                $"{action}目录不能放在数据目录 里面（会把正在用的数据当导出物删掉）: {target}");
        }
    }

}
