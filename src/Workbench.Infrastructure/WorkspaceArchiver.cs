using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Infrastructure;

/// <summary>
/// 工作区搬迁实现。
///
/// 数据库快照用 SQLite 的 <c>VACUUM INTO</c> —— 边跑边导也不会导出半截文件。
/// 直接复制正在写的 .db 文件可能拿到撕裂的页，导入后就是坏库。
/// </summary>
public sealed class WorkspaceArchiver(
    WorkbenchDbContext db,
    IOptions<WorkbenchOptions> options) : IWorkspaceArchiver
{
    private const string ManifestFileName = "manifest.json";
    private const string DatabaseFileName = "workbench.db";
    private const string DocumentsFolderName = "documents";
    private const string KeyFileName = "secret.key";

    private readonly WorkbenchOptions _options = options.Value;

    public async Task<WorkspaceArchiveResult> ExportAsync(
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new WorkbenchException("导出目录不能为空。");
        }

        var target = Path.GetFullPath(targetDirectory);
        EnsureNotInsideHome(target, "导出");
        Directory.CreateDirectory(target);

        var dbTarget = Path.Combine(target, DatabaseFileName);
        if (File.Exists(dbTarget))
        {
            File.Delete(dbTarget);
        }

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
        var documentsTarget = Path.Combine(target, DocumentsFolderName);
        if (Directory.Exists(documentsSource))
        {
            if (Directory.Exists(documentsTarget))
            {
                Directory.Delete(documentsTarget, recursive: true);
            }

            CopyDirectory(documentsSource, documentsTarget);
            var (count, bytes) = Measure(documentsTarget);
            fileCount += count;
            totalBytes += bytes;
        }

        // 加密密钥：数据库密码是 AES-GCM 加密的，密钥丢了换机后连不上老库
        var keySource = _options.KeyFilePath;
        if (File.Exists(keySource))
        {
            var keyTarget = Path.Combine(target, KeyFileName);
            File.Copy(keySource, keyTarget, overwrite: true);
            fileCount++;
            totalBytes += new FileInfo(keyTarget).Length;
        }

        var manifest = new
        {
            format = "workbench-workspace",
            version = 1,
            exportedAt = DateTimeOffset.UtcNow,
            home = _options.HomeDirectory,
            database = DatabaseFileName,
            documents = DocumentsFolderName,
            key = File.Exists(keySource) ? KeyFileName : null
        };

        var manifestPath = Path.Combine(target, ManifestFileName);
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);

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
            throw new WorkbenchException("导入源目录不能为空。");
        }

        if (string.IsNullOrWhiteSpace(targetHomeDirectory))
        {
            throw new WorkbenchException("目标 WORKBENCH_HOME 不能为空。");
        }

        var source = Path.GetFullPath(sourceDirectory);
        var target = Path.GetFullPath(targetHomeDirectory);

        // 导入到正在跑的 WORKBENCH_HOME 会把正在用的库覆盖掉 —— 直接拒
        if (string.Equals(target, Path.GetFullPath(_options.HomeDirectory), StringComparison.Ordinal))
        {
            throw new WorkbenchException(
                "不能导入到正在运行的 WORKBENCH_HOME。请先停掉服务，或指定一个新的目录。");
        }

        var dbSource = Path.Combine(source, DatabaseFileName);
        if (!File.Exists(dbSource))
        {
            throw new WorkbenchException($"源目录里没有 {DatabaseFileName}，不是一个工作台导出目录: {source}");
        }

        Directory.CreateDirectory(target);

        var dbTarget = Path.Combine(target, DatabaseFileName);
        File.Copy(dbSource, dbTarget, overwrite: true);

        var fileCount = 1;
        var totalBytes = new FileInfo(dbTarget).Length;

        var documentsSource = Path.Combine(source, DocumentsFolderName);
        var documentsTarget = Path.Combine(target, DocumentsFolderName);
        if (Directory.Exists(documentsSource))
        {
            if (Directory.Exists(documentsTarget))
            {
                Directory.Delete(documentsTarget, recursive: true);
            }

            CopyDirectory(documentsSource, documentsTarget);
            var (count, bytes) = Measure(documentsTarget);
            fileCount += count;
            totalBytes += bytes;
        }

        var keySource = Path.Combine(source, KeyFileName);
        if (File.Exists(keySource))
        {
            File.Copy(keySource, Path.Combine(target, KeyFileName), overwrite: true);
            fileCount++;
        }

        await Task.CompletedTask;

        return new WorkspaceArchiveResult(
            true, target, fileCount, totalBytes,
            $"已恢复到 {target}。把 WORKBENCH_HOME 指到这个目录再启动服务即可。");
    }

    /// <summary>
    /// 导出目录不能落在 WORKBENCH_HOME 里（尤其不能是 home 或 home/documents）——
    /// 否则会把源目录当目标删掉，或者把导出物拷进它自己。
    /// </summary>
    private void EnsureNotInsideHome(string target, string action)
    {
        var home = Path.GetFullPath(_options.HomeDirectory);

        if (string.Equals(target, home, StringComparison.Ordinal))
        {
            throw new WorkbenchException($"{action}目录不能就是 WORKBENCH_HOME 本身: {target}");
        }

        var documents = Path.GetFullPath(_options.DocumentsDirectory);
        if (target.StartsWith(documents, StringComparison.Ordinal))
        {
            throw new WorkbenchException($"{action}目录不能放在文档目录里面: {target}");
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }

    private static (int Count, long Bytes) Measure(string directory)
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
