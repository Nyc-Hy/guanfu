using Microsoft.EntityFrameworkCore;
using Workbench.Application.Services;
using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>
/// 全局搜索：产出文件与数据库调用也要搜得到。
///
/// 工作台对人的承诺是三件事 —— 追踪任务、记录产出文件、留下数据库调用。
/// 以前搜索只覆盖任务/项目/文档，「哪个任务动过这个文件」「谁跑过这条 SQL」
/// 这两个最常问的问题搜不出来，只能人去翻表。
///
/// 这里守三件事：
/// 1. **搜得到** —— 路径、SQL 原文、库名、目标表任一命中都算数；
/// 2. **不分大小写** —— 人打「delete from」要能搜到「DELETE FROM」；
/// 3. **带得上上下文** —— 结果里必须有任务编号和项目名，光给个路径等于没答。
/// </summary>
public sealed class SearchTests
{
    [Fact]
    public async Task 能按路径搜到产出文件()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("搜索项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        host.Git.CommitFileChanges = [new GitFileChange("src/Auth/Login.cs", "Modified", 3, 1)];
        var files = new FileChangeService(host.Db, host.Git, host.Clock);
        await files.CaptureCommitAsync(task.Id, project.Id, "/tmp/repo", "sha-1");

        var hits = await files.SearchAsync("login");

        var hit = Assert.Single(hits);
        Assert.Equal("src/Auth/Login.cs", hit.Path);
        Assert.Equal(task.Id, hit.TaskId);
    }

    [Fact]
    public async Task 搜产出文件不分大小写()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("搜索项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        host.Git.CommitFileChanges = [new GitFileChange("src/Auth/Login.cs", "Modified", 3, 1)];
        var files = new FileChangeService(host.Db, host.Git, host.Clock);
        await files.CaptureCommitAsync(task.Id, project.Id, "/tmp/repo", "sha-1");

        // 人打小写，文件路径是大写开头的
        var hits = await files.SearchAsync("LOGIN");

        Assert.Single(hits);
    }

    [Fact]
    public async Task 搜产出文件带得上任务编号与项目名()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("搜索项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        host.Git.CommitFileChanges = [new GitFileChange("src/a.cs", "Modified", 1, 0)];
        var files = new FileChangeService(host.Db, host.Git, host.Clock);
        await files.CaptureCommitAsync(task.Id, project.Id, "/tmp/repo", "sha-1");

        var hit = Assert.Single(await files.SearchAsync("a.cs"));

        Assert.Equal("REQ-001", hit.TaskKey);
        Assert.Equal("写登录接口", hit.TaskTitle);
        Assert.Equal("搜索项目", hit.ProjectName);
    }

    [Fact]
    public async Task 搜不到时不返回东西()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("搜索项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        host.Git.CommitFileChanges = [new GitFileChange("src/a.cs", "Modified", 1, 0)];
        var files = new FileChangeService(host.Db, host.Git, host.Clock);
        await files.CaptureCommitAsync(task.Id, project.Id, "/tmp/repo", "sha-1");

        Assert.Empty(await files.SearchAsync("不存在的路径"));
        Assert.Empty(await files.SearchAsync("  "));
    }

    [Fact]
    public async Task 能按SQL原文搜到数据库调用()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("搜索项目");
        var calls = new DbCallService(host.Db, host.Clock);

        await calls.RecordAsync(Entry(project.Id, "db_execute", "DELETE FROM dbo.Users WHERE Id = 3", "Write"));
        await calls.RecordAsync(Entry(project.Id, "db_query", "SELECT 1", "Read"));

        var hits = await calls.SearchAsync("delete from");

        var hit = Assert.Single(hits);
        Assert.Equal("DELETE FROM dbo.Users WHERE Id = 3", hit.Sql);
    }

    [Fact]
    public async Task 按目标表也能搜到数据库调用()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("搜索项目");
        var calls = new DbCallService(host.Db, host.Clock);

        await calls.RecordAsync(Entry(project.Id, "db_query", "SELECT 1", "Read", new[] { "dbo.Orders" }));
        await calls.RecordAsync(Entry(project.Id, "db_query", "SELECT 2", "Read", new[] { "dbo.Users" }));

        var hits = await calls.SearchAsync("dbo.Orders");

        Assert.Single(hits);
        Assert.Equal("SELECT 1", hits[0].Sql);
    }

    [Fact]
    public async Task 按库名也能搜到数据库调用()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("搜索项目");
        var calls = new DbCallService(host.Db, host.Clock);

        await calls.RecordAsync(Entry(project.Id, "db_query", "SELECT 1", "Read"));
        await calls.RecordAsync(Entry(project.Id, "db_query", "SELECT 2", "Read", database: "other"));

        var hits = await calls.SearchAsync("other");

        Assert.Single(hits);
        Assert.Equal("SELECT 2", hits[0].Sql);
    }

    [Fact]
    public async Task 数据库调用页的SQL筛选不分大小写()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("搜索项目");
        var calls = new DbCallService(host.Db, host.Clock);

        await calls.RecordAsync(Entry(project.Id, "db_execute", "UPDATE dbo.Orders SET Total = 1", "Write"));
        await calls.RecordAsync(Entry(project.Id, "db_query", "SELECT 1", "Read"));

        var (rows, total) = await calls.ListAsync(sql: "update dbo");

        Assert.Equal(1, total);
        Assert.Single(rows);
    }

    [Fact]
    public async Task 搜数据库调用带得上任务编号与项目名()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("搜索项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");
        var calls = new DbCallService(host.Db, host.Clock);

        await calls.RecordAsync(Entry(project.Id, "db_execute", "UPDATE dbo.Users SET Name = 'x'", "Write", taskId: task.Id));

        var hit = Assert.Single(await calls.SearchAsync("dbo.Users"));

        Assert.Equal(task.Id, hit.TaskId);
        Assert.Equal("REQ-001", hit.TaskKey);
        Assert.Equal("搜索项目", hit.ProjectName);
    }

    private static DbCallRecordEntry Entry(
        Guid projectId,
        string tool,
        string sql,
        string kind,
        string[]? tables = null,
        Guid? taskId = null,
        string database = "shop")
        => new(
            AuditId: null,
            ProjectId: projectId,
            TaskId: taskId,
            SessionId: null,
            ConnectionId: null,
            ToolName: tool,
            Server: "srv",
            Database: database,
            Sql: sql,
            Kind: kind,
            TargetTables: tables,
            RowsAffected: null,
            Outcome: "success",
            ApprovalId: null,
            CallerId: "agent",
            DurationMs: 1);
}
