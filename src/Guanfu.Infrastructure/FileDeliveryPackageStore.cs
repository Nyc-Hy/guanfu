using Microsoft.Extensions.Options;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Infrastructure;

/// <summary>
/// 交付包在数据目录里的落盘位置。
///
/// 跟文档原件一个规矩：包留在数据目录内（换机时一起搬），
/// 写临时文件再原子改名，避免进程被杀时留下半个包。
/// </summary>
public sealed class FileDeliveryPackageStore(IOptions<GuanfuOptions> options) : IDeliveryPackageStore
{
    private readonly GuanfuOptions _options = options.Value;

    public async Task<StoredFile> SaveAsync(
        Guid milestoneId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var relativeDir = Path.Combine(WorkspaceArchiveFiles.PackagesFolderName, milestoneId.ToString("N"));
        var absoluteDir = Path.Combine(_options.HomeDirectory, relativeDir);
        Directory.CreateDirectory(absoluteDir);

        var safeName = SanitizeFileName(fileName);
        var tempPath = Path.Combine(absoluteDir, $".{safeName}.{Guid.NewGuid():N}.tmp");
        long written;

        try
        {
            written = await WriteTempAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        string? finalPath = null;

        foreach (var candidate in CandidateNames(safeName))
        {
            var target = Path.Combine(absoluteDir, candidate);

            if (File.Exists(target))
            {
                continue;
            }

            try
            {
                File.Move(tempPath, target, overwrite: false);
                finalPath = target;
                break;
            }
            catch (IOException) when (File.Exists(target))
            {
                // 竞态：刚检查完就被别人占了，换下一个候选
            }
        }

        if (finalPath is null)
        {
            TryDelete(tempPath);
            throw new GuanfuException($"同名交付包过多，无法保存：{safeName}");
        }

        return new StoredFile(Path.Combine(relativeDir, Path.GetFileName(finalPath)), written);
    }

    public string ResolveAbsolutePath(string relativePath) =>
        PathBoundary.Resolve(
            _options.HomeDirectory,
            relativePath,
            message => new UnauthorizedAccessException(message));

    private static async Task<long> WriteTempAsync(
        string tempPath,
        Stream content,
        CancellationToken cancellationToken)
    {
        long written = 0;

        await using (var file = new FileStream(tempPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        }))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
            }
        }

        return written;
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new System.Text.StringBuilder(name.Length);

        foreach (var ch in name)
        {
            builder.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? "package.zip" : cleaned;
    }

    /// <summary>同名文件时的候选名：原名 + -2 .. -999。</summary>
    private static IEnumerable<string> CandidateNames(string safeName)
    {
        yield return safeName;

        var stem = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);

        for (var i = 2; i < 1000; i++)
        {
            yield return $"{stem}-{i}{extension}";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 删不掉就算了 —— 临时文件留在那里也比把主流程带崩强
        }
    }
}
