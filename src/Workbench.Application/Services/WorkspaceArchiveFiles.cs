using Workbench.Domain;

namespace Workbench.Application.Services;

/// <summary>
/// 工作区搬迁的纯文件操作。
///
/// 单独拎出来是因为**导入不需要数据库**——它只是把 workbench.db、documents/、secret.key
/// 三个文件搬到新目录。既然不碰数据库，就不必依赖服务在跑：
/// 换机时服务还没起，直接本地搬完再设 WORKBENCH_HOME 就行。
///
/// 放在应用层是刻意的：Infrastructure 的导出实现和 CLI 的本地导入共用同一份，
/// 别写两遍。
/// </summary>
public static class WorkspaceArchiveFiles
{
    public const string DatabaseFileName = "workbench.db";
    public const string DocumentsFolderName = "documents";
    public const string KeyFileName = "secret.key";
    public const string ManifestFileName = "manifest.json";

    /// <summary>外部 agent 的运行日志目录名。</summary>
    public const string RunsFolderName = "runs";

    /// <summary>把导出目录恢复到目标 WORKBENCH_HOME。</summary>
    public static (int FileCount, long TotalBytes) Import(string sourceDirectory, string targetHomeDirectory)
    {
        var source = Path.GetFullPath(sourceDirectory);
        var target = Path.GetFullPath(targetHomeDirectory);

        // 源和目标同一个目录（或目标在源里面）时，导入过程会把自己删掉：
        // documents 目标目录先被 recursive 删除，而它正好就是源。
        if (PathBoundary.IsSameOrInside(source, target))
        {
            throw new WorkbenchException(
                $"目标目录不能是源目录本身或它的子目录: {target}（源: {source}）");
        }

        var dbSource = Path.Combine(source, DatabaseFileName);
        if (!File.Exists(dbSource))
        {
            throw new WorkbenchException($"源目录里没有 {DatabaseFileName}，不是一个工作台导出目录: {source}");
        }

        Directory.CreateDirectory(target);

        var dbTarget = Path.Combine(target, DatabaseFileName);
        CopyFile(dbSource, dbTarget);

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
            CopyFile(keySource, Path.Combine(target, KeyFileName));
            fileCount++;
        }

        // 外部 agent 的运行日志：换机后也要能看到之前的运行记录
        var runsSource = Path.Combine(source, RunsFolderName);
        if (Directory.Exists(runsSource))
        {
            var runsTarget = Path.Combine(target, RunsFolderName);
            if (Directory.Exists(runsTarget))
            {
                Directory.Delete(runsTarget, recursive: true);
            }

            CopyDirectory(runsSource, runsTarget);
            var (count, bytes) = Measure(runsTarget);
            fileCount += count;
            totalBytes += bytes;
        }

        return (fileCount, totalBytes);
    }

    public static void CopyFile(string source, string target)
    {
        try
        {
            File.Copy(source, target, overwrite: true);
        }
        catch (IOException ex)
        {
            throw new WorkbenchException(
                $"拷贝 {Path.GetFileName(source)} 失败：{ex.Message}。" +
                "如果目标 WORKBENCH_HOME 正在被服务占用，请先停掉服务再导入。");
        }
    }

    public static void CopyDirectory(string source, string target)
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
