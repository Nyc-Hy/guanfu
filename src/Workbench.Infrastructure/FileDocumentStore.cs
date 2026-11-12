using Microsoft.Extensions.Options;
using Workbench.Application.Services;

namespace Workbench.Infrastructure;

/// <summary>
/// 文档原件落盘。目录结构 <c>documents/{taskId}/{fileName}</c>，
/// 全部相对 WORKBENCH_HOME，整个目录可直接搬迁到另一台机器。
/// </summary>
public sealed class FileDocumentStore(IOptions<WorkbenchOptions> options) : IDocumentStore
{
    private readonly WorkbenchOptions _options = options.Value;

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
        var absolutePath = Path.Combine(absoluteDir, safeName);

        // 同名不覆盖，加序号
        if (File.Exists(absolutePath))
        {
            var stem = Path.GetFileNameWithoutExtension(safeName);
            var extension = Path.GetExtension(safeName);
            for (var i = 2; i < 1000; i++)
            {
                var candidate = Path.Combine(absoluteDir, $"{stem}({i}){extension}");
                if (!File.Exists(candidate))
                {
                    absolutePath = candidate;
                    break;
                }
            }
        }

        await using (var file = File.Create(absolutePath))
        {
            await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var relativePath = Path.Combine(relativeDir, Path.GetFileName(absolutePath));
        var size = new FileInfo(absolutePath).Length;

        return new StoredFile(relativePath, size);
    }

    public string ResolveAbsolutePath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(_options.HomeDirectory, relativePath));

        // 越界保护：不允许逃出 HOME
        var root = Path.GetFullPath(_options.HomeDirectory);
        if (!full.StartsWith(root, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException($"路径越界: {relativePath}");
        }

        return full;
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

    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "unnamed";
        }

        return cleaned;
    }
}
