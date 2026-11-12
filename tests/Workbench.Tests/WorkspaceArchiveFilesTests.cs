using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// 工作区搬迁的文件操作（导入不碰数据库，所以放在应用层，CLI 能直接用）。
/// </summary>
public class WorkspaceArchiveFilesTests : IDisposable
{
    private readonly string _source = Path.Combine(Path.GetTempPath(), "wb-src-" + Guid.NewGuid().ToString("N"));
    private readonly string _target = Path.Combine(Path.GetTempPath(), "wb-dst-" + Guid.NewGuid().ToString("N"));

    public WorkspaceArchiveFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_source, "documents", "task1"));
        File.WriteAllText(Path.Combine(_source, "workbench.db"), "fake-db");
        File.WriteAllText(Path.Combine(_source, "secret.key"), "fake-key");
        File.WriteAllText(Path.Combine(_source, "documents", "task1", "PRD.md"), "# 需求");
    }

    [Fact]
    public void 导入把库文档与密钥都搬过去()
    {
        var (fileCount, totalBytes) = WorkspaceArchiveFiles.Import(_source, _target);

        Assert.True(File.Exists(Path.Combine(_target, "workbench.db")));
        Assert.True(File.Exists(Path.Combine(_target, "secret.key")));
        Assert.True(File.Exists(Path.Combine(_target, "documents", "task1", "PRD.md")));
        Assert.True(fileCount >= 3);
        Assert.True(totalBytes > 0);
    }

    [Fact]
    public void 导入不存在的源目录报明确错误()
    {
        var missing = Path.Combine(Path.GetTempPath(), "wb-not-exist-" + Guid.NewGuid().ToString("N"));

        var ex = Assert.Throws<WorkbenchException>(
            () => WorkspaceArchiveFiles.Import(missing, _target));

        Assert.Contains("workbench.db", ex.Message);
    }

    [Fact]
    public void 源目录不是导出目录时报错()
    {
        var notAnExport = Path.Combine(Path.GetTempPath(), "wb-random-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(notAnExport);

        try
        {
            Assert.Throws<WorkbenchException>(
                () => WorkspaceArchiveFiles.Import(notAnExport, _target));
        }
        finally
        {
            Directory.Delete(notAnExport, recursive: true);
        }
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _source, _target })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        GC.SuppressFinalize(this);
    }
}
