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
