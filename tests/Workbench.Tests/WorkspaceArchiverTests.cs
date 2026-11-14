using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Domain;
using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>
/// 换机搬迁（决策 6）。导出必须是一份能独立打开的库 —— 复制正在写的 .db 会拿到撕裂的页。
/// </summary>
public class WorkspaceArchiverTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "wb-home-" + Guid.NewGuid().ToString("N"));
    private readonly string _export = Path.Combine(Path.GetTempPath(), "wb-export-" + Guid.NewGuid().ToString("N"));
    private readonly string _imported = Path.Combine(Path.GetTempPath(), "wb-import-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task 导出后目录里有数据库与清单()
    {
        await using var db = CreateDb(_home);
        var archiver = new WorkspaceArchiver(db, Options.Create(new WorkbenchOptions { HomeDirectory = _home }));

        var result = await archiver.ExportAsync(_export);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(_export, "workbench.db")));
        Assert.True(File.Exists(Path.Combine(_export, "manifest.json")));
    }

    [Fact]
    public async Task 导出的库是完整可用的()
    {
        await using var db = CreateDb(_home);
        var workspace = new Workspace { Name = "默认工作区", CreatedAt = DateTimeOffset.UtcNow };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        db.Projects.Add(new Project
        {
            WorkspaceId = workspace.Id,
            Name = "搬迁项目",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var archiver = new WorkspaceArchiver(db, Options.Create(new WorkbenchOptions { HomeDirectory = _home }));
        await archiver.ExportAsync(_export);

        // 用导出的库单独开一个上下文，读得到刚才写的项目
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_export, "workbench.db")}")
            .Options;

        await using var exported = new WorkbenchDbContext(options);
        var projects = await exported.Projects.AsNoTracking().ToListAsync();

        Assert.Single(projects);
        Assert.Equal("搬迁项目", projects[0].Name);
    }

    [Fact]
    public async Task 导入把库恢复到新的家目录()
    {
        await using var db = CreateDb(_home);
        var workspace = new Workspace { Name = "默认工作区", CreatedAt = DateTimeOffset.UtcNow };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();

        db.Projects.Add(new Project
        {
            WorkspaceId = workspace.Id,
            Name = "搬迁项目",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var archiver = new WorkspaceArchiver(db, Options.Create(new WorkbenchOptions { HomeDirectory = _home }));
        await archiver.ExportAsync(_export);

        var result = await archiver.ImportAsync(_export, _imported);

        Assert.True(result.Ok);
        Assert.True(File.Exists(Path.Combine(_imported, "workbench.db")));

        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_imported, "workbench.db")}")
            .Options;

        await using var imported = new WorkbenchDbContext(options);
        var projects = await imported.Projects.AsNoTracking().ToListAsync();

        Assert.Single(projects);
        Assert.Equal("搬迁项目", projects[0].Name);
    }

    [Fact]
    public async Task 导入到正在运行的家目录被拒绝()
    {
        await using var db = CreateDb(_home);
        var archiver = new WorkspaceArchiver(db, Options.Create(new WorkbenchOptions { HomeDirectory = _home }));
        await archiver.ExportAsync(_export);

        await Assert.ThrowsAsync<WorkbenchException>(
            () => archiver.ImportAsync(_export, _home));
    }

    [Fact]
    public async Task 导出到文档目录里面被拒绝()
    {
        await using var db = CreateDb(_home);
        var archiver = new WorkspaceArchiver(db, Options.Create(new WorkbenchOptions { HomeDirectory = _home }));

        await Assert.ThrowsAsync<WorkbenchException>(
            () => archiver.ExportAsync(Path.Combine(_home, "documents", "out")));
    }

    [Fact]
    public async Task 导入源目录不是工作台导出目录时报错()
    {
        await using var db = CreateDb(_home);
        var archiver = new WorkspaceArchiver(db, Options.Create(new WorkbenchOptions { HomeDirectory = _home }));

        await Assert.ThrowsAsync<WorkbenchException>(
            () => archiver.ImportAsync(Path.GetTempPath(), _imported));
    }

    [Fact]
    public async Task 导出到家目录本身被拒绝且不会删掉正在用的库()
    {
        await using var db = CreateDb(_home);
        var archiver = new WorkspaceArchiver(db, Options.Create(new WorkbenchOptions { HomeDirectory = _home }));

        // 带尾斜杠：GetFullPath 会保留它，裸 Equals 比会漏掉
        await Assert.ThrowsAsync<WorkbenchException>(
            () => archiver.ExportAsync(_home + Path.DirectorySeparatorChar));

        // 正在用的库必须还在
        Assert.True(File.Exists(Path.Combine(_home, "workbench.db")));
    }

    [Fact]
    public async Task 导出到家目录里的子目录被拒绝()
    {
        await using var db = CreateDb(_home);
        var archiver = new WorkspaceArchiver(db, Options.Create(new WorkbenchOptions { HomeDirectory = _home }));

        await Assert.ThrowsAsync<WorkbenchException>(
            () => archiver.ExportAsync(Path.Combine(_home, "backup")));
    }

    [Fact]
    public async Task 导入到家目录本身被拒绝()
    {
        await using var db = CreateDb(_home);
        var archiver = new WorkspaceArchiver(db, Options.Create(new WorkbenchOptions { HomeDirectory = _home }));
        await archiver.ExportAsync(_export);

        // 带尾斜杠：以前比不出来，然后 import 会把正在用的库覆盖掉
        await Assert.ThrowsAsync<WorkbenchException>(
            () => archiver.ImportAsync(_export, _home + Path.DirectorySeparatorChar));

        Assert.True(File.Exists(Path.Combine(_home, "workbench.db")));
    }

    [Fact]
    public async Task 导出目录里有无关文件时拒绝覆盖()
    {
        await using var db = CreateDb(_home);
        var archiver = new WorkspaceArchiver(db, Options.Create(new WorkbenchOptions { HomeDirectory = _home }));

        // 模拟"用户把导出目录指到了放着别的数据的地方"
        Directory.CreateDirectory(_export);
        await File.WriteAllTextAsync(Path.Combine(_export, "我自己的笔记.txt"), "别删我");

        await Assert.ThrowsAsync<WorkbenchException>(
            () => archiver.ExportAsync(_export));

        Assert.True(File.Exists(Path.Combine(_export, "我自己的笔记.txt")));
    }

    [Fact]
    public async Task 重复导出到同一个目录不会被自己拦下来()
    {
        await using var db = CreateDb(_home);
        var archiver = new WorkspaceArchiver(db, Options.Create(new WorkbenchOptions { HomeDirectory = _home }));

        await archiver.ExportAsync(_export);
        var second = await archiver.ExportAsync(_export);

        Assert.True(second.Ok);
        Assert.True(File.Exists(Path.Combine(_export, "workbench.db")));
    }

    [Fact]
    public async Task 导出带走运行日志且导入能恢复()
    {
        await using var db = CreateDb(_home);
        var archiver = new WorkspaceArchiver(db, Options.Create(new WorkbenchOptions { HomeDirectory = _home }));

        var runDir = Path.Combine(_home, "runs", "task-1");
        Directory.CreateDirectory(runDir);
        await File.WriteAllTextAsync(Path.Combine(runDir, "agent.log"), "第一行\n第二行\n");

        await archiver.ExportAsync(_export);

        Assert.True(File.Exists(Path.Combine(_export, "runs", "task-1", "agent.log")));

        await archiver.ImportAsync(_export, _imported);

        Assert.True(File.Exists(Path.Combine(_imported, "runs", "task-1", "agent.log")));
    }

    [Fact]
    public async Task 导出保留密钥文件的Unix权限()
    {
        if (OperatingSystem.IsWindows())
        {
            return;  // Unix 权限只在非 Windows 上有意义
        }

        await using var db = CreateDb(_home);

        // secret.key 源文件是 0600 —— File.Copy 默认不保留权限，会变 0644
        var keyPath = Path.Combine(_home, "secret.key");
        await File.WriteAllBytesAsync(keyPath, new byte[] { 1, 2, 3 });
        File.SetUnixFileMode(keyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var archiver = new WorkspaceArchiver(db, Options.Create(new WorkbenchOptions { HomeDirectory = _home }));
        await archiver.ExportAsync(_export);

        var exportedKey = Path.Combine(_export, "secret.key");
        Assert.True(File.Exists(exportedKey));
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            File.GetUnixFileMode(exportedKey));

        // 权限信息也要进 manifest —— 导出物经 zip / 不保留权限的文件系统中转后，
        // 导入侧靠它恢复（导入落地权限由 Application 侧负责）
        var manifest = System.Text.Json.JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(_export, "manifest.json")));
        Assert.Equal("0600", manifest.RootElement.GetProperty("keyMode").GetString());
    }

    private WorkbenchDbContext CreateDb(string home)
    {
        Directory.CreateDirectory(home);
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={Path.Combine(home, "workbench.db")}")
            .Options;

        var db = new WorkbenchDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    public void Dispose()
    {
        foreach (var dir in new[] { _home, _export, _imported })
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        GC.SuppressFinalize(this);
    }
}
