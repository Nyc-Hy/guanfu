using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Guanfu.Application.Services;
using Guanfu.Domain;
using Guanfu.Infrastructure;

namespace Guanfu.Tests;

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
        // 老的导出包里库文件叫 workbench.db —— 导入时要认（并落成新名字）
        File.WriteAllText(Path.Combine(_source, "workbench.db"), "fake-db");
        File.WriteAllText(Path.Combine(_source, "secret.key"), "fake-key");
        File.WriteAllText(Path.Combine(_source, "documents", "task1", "PRD.md"), "# 需求");
    }

    [Fact]
    public void 导入把库文档与密钥都搬过去()
    {
        var (fileCount, totalBytes) = WorkspaceArchiveFiles.Import(_source, _target);

        // 老包里的库导进来后按新名字落地
        Assert.True(File.Exists(Path.Combine(_target, "guanfu.db")));
        Assert.True(File.Exists(Path.Combine(_target, "secret.key")));
        Assert.True(File.Exists(Path.Combine(_target, "documents", "task1", "PRD.md")));
        Assert.True(fileCount >= 3);
        Assert.True(totalBytes > 0);
    }

    [Fact]
    public void 导入无法替换文档时保留原数据库和密钥()
    {
        Directory.CreateDirectory(_target);
        var db = Path.Combine(_target, "guanfu.db");
        var key = Path.Combine(_target, "secret.key");
        var documents = Path.Combine(_target, "documents");
        File.WriteAllText(db, "previous-db");
        File.WriteAllText(key, "previous-key");
        File.WriteAllText(documents, "不能替换成目录");

        Assert.Throws<GuanfuException>(() => WorkspaceArchiveFiles.Import(_source, _target));

        Assert.Equal("previous-db", File.ReadAllText(db));
        Assert.Equal("previous-key", File.ReadAllText(key));
        Assert.Equal("不能替换成目录", File.ReadAllText(documents));
    }

    [Fact]
    public void 覆盖恢复移走旧数据库日志并保留本机配置()
    {
        Directory.CreateDirectory(_target);
        var db = Path.Combine(_target, "guanfu.db");
        File.WriteAllText(db, "previous-db");
        File.WriteAllText(db + "-wal", "previous-wal");
        var config = Path.Combine(_target, "本机配置.txt");
        File.WriteAllText(config, "keep");

        WorkspaceArchiveFiles.Import(_source, _target);

        Assert.Equal("fake-db", File.ReadAllText(db));
        Assert.False(File.Exists(db + "-wal"));
        Assert.Equal("keep", File.ReadAllText(config));
    }

    [Fact]
    public void 导入不存在的源目录报明确错误()
    {
        var missing = Path.Combine(Path.GetTempPath(), "wb-not-exist-" + Guid.NewGuid().ToString("N"));

        var ex = Assert.Throws<GuanfuException>(
            () => WorkspaceArchiveFiles.Import(missing, _target));

        Assert.Contains("guanfu.db", ex.Message);
    }

    [Fact]
    public void 源目录不是导出目录时报错()
    {
        var notAnExport = Path.Combine(Path.GetTempPath(), "wb-random-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(notAnExport);

        try
        {
            Assert.Throws<GuanfuException>(
                () => WorkspaceArchiveFiles.Import(notAnExport, _target));
        }
        finally
        {
            Directory.Delete(notAnExport, recursive: true);
        }
    }

    [Fact]
    public void 导入按清单里的keyMode恢复密钥权限()
    {
        if (OperatingSystem.IsWindows())
        {
            return;  // Unix 权限只在非 Windows 上有意义
        }

        // 导出物经 zip / 不保留权限的文件系统中转：文件本身已经是 0644，
        // 只有 manifest 里的 keyMode 还记得它原本是 0600
        File.SetUnixFileMode(
            Path.Combine(_source, "secret.key"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.WriteAllText(
            Path.Combine(_source, "manifest.json"),
            """{"format":"workbench-workspace","version":1,"key":"secret.key","keyMode":"0600"}""");

        WorkspaceArchiveFiles.Import(_source, _target);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(_target, "secret.key")));
    }

    [Fact]
    public void 老导出包没有keyMode时密钥按0600落地()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            Path.Combine(_source, "secret.key"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        // 老导出包：清单里压根没有 keyMode 这个字段
        File.WriteAllText(
            Path.Combine(_source, "manifest.json"),
            """{"format":"workbench-workspace","version":1,"key":"secret.key"}""");

        WorkspaceArchiveFiles.Import(_source, _target);

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(Path.Combine(_target, "secret.key")));
    }

    [Fact]
    public async Task 导出再导入会恢复密钥的Unix权限()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var home = Path.Combine(Path.GetTempPath(), "wb-key-home-" + Guid.NewGuid().ToString("N"));
        var export = Path.Combine(Path.GetTempPath(), "wb-key-export-" + Guid.NewGuid().ToString("N"));
        var imported = Path.Combine(Path.GetTempPath(), "wb-key-import-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(home);
            var dbOptions = new DbContextOptionsBuilder<GuanfuDbContext>()
                .UseSqlite($"Data Source={Path.Combine(home, "guanfu.db")}")
                .Options;

            await using var db = new GuanfuDbContext(dbOptions);
            await db.Database.EnsureCreatedAsync();

            var keyPath = Path.Combine(home, "secret.key");
            await File.WriteAllBytesAsync(keyPath, new byte[] { 1, 2, 3 });
            File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var archiver = new WorkspaceArchiver(db, Options.Create(new GuanfuOptions { HomeDirectory = home }));
            await archiver.ExportAsync(export);

            // 模拟中转把权限位抹平（zip / FAT / 网络盘）：导入侧只能靠 manifest 恢复
            File.SetUnixFileMode(
                Path.Combine(export, "secret.key"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

            var result = await archiver.ImportAsync(export, imported);

            Assert.True(result.Ok);
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(imported, "secret.key")));
        }
        finally
        {
            foreach (var dir in new[] { home, export, imported })
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
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
