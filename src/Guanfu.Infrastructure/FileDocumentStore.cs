using Microsoft.Extensions.Options;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Infrastructure;

/// <summary>
/// 文档原件落盘。目录结构 <c>documents/{taskId}/{fileName}</c>，
/// 全部相对 数据目录，整个目录可直接搬迁到另一台机器。
/// </summary>
public sealed class FileDocumentStore(IOptions<GuanfuOptions> options) : IDocumentStore
{
    /// <summary>
    /// 单个文档原件的体积上限（50 MB）。Web UI 的 InputFile 已经限了同样大小，
    /// 这里再兜一层底——REST API / MCP 走的是别的路径，不能只指望前端拦。
    /// </summary>
    public const long MaxFileBytes = 50L * 1024 * 1024;

    /// <summary>同名文件的最大候选数：原名 + 编号 2..999。</summary>
    private const int MaxNameCandidates = 1000;

    private readonly GuanfuOptions _options = options.Value;

    public async Task<StoredFile> SaveAsync(
        Guid taskId,
        string fileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        var relativeDir = Path.Combine("documents", taskId.ToString("N"));
        var absoluteDir = Path.Combine(_options.HomeDirectory, relativeDir);
        Directory.CreateDirectory(absoluteDir);

        var safeName = SanitizeFileName(fileName);

        // 体积上限：能提前知道长度就先拒，别白占磁盘
        if (content.CanSeek && content.Length > MaxFileBytes)
        {
            throw new GuanfuException($"文档超过体积上限（{MaxFileBytes / 1024 / 1024} MB），已拒绝保存: {safeName}");
        }

        // 先写同目录下的临时文件，写完再原子改名。
        // 直接写最终路径的话，进程中途被杀会留下一个截断文件，
        // 而库里可能已经有指向它的 Document 记录，之后只会报「文件损坏」而不是「没写完」。
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

        // 发布到最终文件名。File.Move(overwrite:false) 在同一文件系统上是原子的：
        // 目标已存在会失败，而不是覆盖——同名文件绝不冲掉先写进去的那份。
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
                // 竞态：刚检查完就被别人占了，换下一个候选继续
            }
        }

        if (finalPath is null)
        {
            TryDelete(tempPath);
            throw new GuanfuException(
                $"同名文件过多（{safeName} 已有 {MaxNameCandidates - 1} 个副本），无法再保存。已拒绝覆盖任何已有文件。");
        }

        var relativePath = Path.Combine(relativeDir, Path.GetFileName(finalPath));

        return new StoredFile(relativePath, written);
    }

    public string ResolveAbsolutePath(string relativePath)
    {
        // 越界保护：不允许逃出 HOME。和仓库路径用同一套规则（PathBoundary），
        // 同样要防「同前缀兄弟目录」和软链逃逸。
        // 异常类型沿用 UnauthorizedAccessException：Exists 和文档解析都靠它兜底。
        return PathBoundary.Resolve(
            _options.HomeDirectory,
            relativePath,
            message => new UnauthorizedAccessException(message));
    }

    public bool Exists(string relativePath)
    {
        try
        {
            return File.Exists(ResolveAbsolutePath(relativePath));
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<long> WriteTempAsync(string tempPath, Stream content, CancellationToken cancellationToken)
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
                if (written + read > MaxFileBytes)
                {
                    throw new GuanfuException($"文档超过体积上限（{MaxFileBytes / 1024 / 1024} MB），已拒绝保存。");
                }

                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
            }

            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            // 落到磁盘再改名：否则改完名还是半个文件
            file.Flush(flushToDisk: true);
        }

        return written;
    }

    /// <summary>同名文件的候选名：原名、name(2)、name(3)……最多 MaxNameCandidates 个。</summary>
    private static IEnumerable<string> CandidateNames(string safeName)
    {
        yield return safeName;

        var stem = Path.GetFileNameWithoutExtension(safeName);
        var extension = Path.GetExtension(safeName);
        for (var i = 2; i < MaxNameCandidates; i++)
        {
            yield return $"{stem}({i}){extension}";
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
            // 临时文件删不掉就留着，下次整理目录时再清
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "unnamed";
        }

        // "." / ".." 拼路径时会指到上一层，不能当文件名用
        if (cleaned is "." or "..")
        {
            cleaned = "unnamed";
        }

        return cleaned;
    }
}
