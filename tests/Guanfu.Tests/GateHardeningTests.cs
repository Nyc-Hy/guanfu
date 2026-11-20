using System.Text;
using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Services;
using Guanfu.Domain;
using Guanfu.Infrastructure;
using Guanfu.Mcp;
using Guanfu.Web;

namespace Guanfu.Tests;

/// <summary>
/// 这一轮修的问题的回归测试。
///
/// 每条都对应一个具体的漏洞或缺陷，不是「测一下看看」：
/// - 免审批写被绕过（别名 / 跨库 / INSERT … EXEC）
/// - 建项目不要凭证 → agent 自己扩权读任意目录
/// - 一次调用写两条审计、审批卡在 Executing、撤销归属写成 human
/// - 进 Review 的产出采集只在 MCP 那条路上
/// - 写侧没有「禁止写」这一档
/// </summary>
public class GateHardeningTests
{
    // ---- 假实现 ----

    /// <summary>假 SQL 执行器：按脚本回话，不连真库。</summary>
    private sealed class FakeSqlRunner : ISqlCommandRunner
    {
        public List<string> Executed { get; } = new();
        public Action? OnExecute { get; init; }

        public SqlStatementAnalysis Analyze(string sql) => SqlStatementClassifier.Classify(sql);

        public Task<SqlQueryResult> QueryAsync(
            DbConnectionConfig config, string sql, int maxRows = 200, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SqlQueryResult([], [], false, maxRows, TimeSpan.Zero));

        public Task<SqlExecResult> ExecuteAsync(
            DbConnectionConfig config, string sql, CancellationToken cancellationToken = default)
        {
            Executed.Add(sql);
            OnExecute?.Invoke();
            return Task.FromResult(new SqlExecResult(3, TimeSpan.Zero));
        }

        public Task<DbSchemaResult> GetSchemaAsync(
            DbConnectionConfig config, string? schemaName = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DbSchemaResult([]));
    }

    // ---- P0-1：免审批写的绕过口子 ----

    [Fact]
    public async Task 别名绕过免审批写时会被认出来()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var connection = await host.DbConnections.CreateAsync(
            project.Id, "c", "srv", "db", "u", "p");

        // 给 Orders 配「免审批写」
        await host.DbConnections.AddRuleAsync(
            connection.Id, "dbo", "Orders", allowRead: true, writeRequiresApproval: false);

        // 再给 Secret 配「要审批」（或者压根没配 —— 都没配就意味着要审批）
        var runner = new FakeSqlRunner();
        var tools = new DatabaseMcpTools(
            host.DbConnections, runner, host.Gate, host.Approvals, host.Audit, host.Clock);

        // UPDATE Orders … FROM Secret AS Orders —— 看着写的是 Orders（免批），
        // 实际写的是 Secret。
        var result = await tools.ExecuteAsync(
            project.Id, "UPDATE Orders SET Orders.v = 1 FROM Secret AS Orders", waitSeconds: 1);

        Assert.False(result.Ok);
        Assert.Equal("Pending", result.ApprovalStatus);
        Assert.Empty(runner.Executed);
    }

    [Fact]
    public async Task 跨库写不会被免审批规则放过()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var connection = await host.DbConnections.CreateAsync(
            project.Id, "c", "srv", "db", "u", "p");

        await host.DbConnections.AddRuleAsync(
            connection.Id, "dbo", "Orders", allowRead: true, writeRequiresApproval: false);

        var runner = new FakeSqlRunner();
        var tools = new DatabaseMcpTools(
            host.DbConnections, runner, host.Gate, host.Approvals, host.Audit, host.Clock);

        var result = await tools.ExecuteAsync(
            project.Id, "UPDATE OtherDb.dbo.Orders SET v = 1", waitSeconds: 1);

        Assert.False(result.Ok);
        Assert.Empty(runner.Executed);
    }

    // ---- P0-2：建项目要人工凭证 ----

    [Fact]
    public void 建项目属于敏感端点()
    {
        Assert.True(HumanTokenPolicy.IsSensitiveEndpoint("POST", "/api/projects"));
        Assert.False(HumanTokenPolicy.IsSensitiveEndpoint("GET", "/api/projects"));
    }

    // ---- B1：一次调用只写一条审计 ----

    [Fact]
    public void 自己留痕的工具不再被中央记录器重复记()
    {
        Assert.Equal(new[] { "db_execute", "db_query", "db_schema" },
            ToolCallRecorder.SelfAuditedTools.Order(StringComparer.Ordinal));
    }

    // ---- B2：调用方取消也要把审批收干净 ----

    [Fact]
    public async Task 调用方取消后审批不会卡在执行中()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        await host.DbConnections.CreateAsync(project.Id, "c", "srv", "db", "u", "p");

        using var cts = new CancellationTokenSource();
        var runner = new FakeSqlRunner { OnExecute = cts.Cancel };
        var tools = new DatabaseMcpTools(
            host.DbConnections, runner, host.Gate, host.Approvals, host.Audit, host.Clock);

        const string sql = "UPDATE dbo.Orders SET v = 1";
        var first = await tools.ExecuteAsync(project.Id, sql, waitSeconds: 1);
        await host.Approvals.DecideAsync(first.ApprovalId, approve: true, decidedBy: "tester");

        await Record.ExceptionAsync(
            () => tools.ExecuteAsync(project.Id, sql, approvalId: first.ApprovalId, cancellationToken: cts.Token));

        Assert.Single(runner.Executed);
        var approval = await host.Approvals.GetAsync(first.ApprovalId);
        Assert.Equal("Executed", approval!.Status);
    }

    // ---- B3：执行中断要标「结果未知」 ----

    [Fact]
    public async Task 执行中断超时回收时要标结果未知()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");

        var approval = await host.Approvals.RequestAsync(
            "shell_run", "shell.write", "echo x", "摘要", projectId: project.Id);
        await host.Approvals.DecideAsync(approval.Id, approve: true, decidedBy: "tester");

        // 占住它（Approved → Executing），模拟执行方崩了没收尾
        var gate = await host.Gate.AcquireAsync(
            "shell_run", "shell.write", "echo x", "摘要", "tester", null, project.Id, approval.Id, 1);
        Assert.True(gate.Approved);

        host.Clock.Advance(TimeSpan.FromMinutes(ApprovalService.ExecutingTimeoutMinutes + 5));
        await host.Approvals.ExpireOverdueAsync();

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == approval.Id);
        Assert.Equal(ApprovalStatus.Expired, row.Status);
        Assert.True(row.OutcomeUnknown);
    }

    // ---- B4：撤销归属 ----

    [Fact]
    public async Task 撤销审批记的是真的人或agent()
    {
        using var host = new GuanfuTestHost();

        var approval = await host.Approvals.RequestAsync(
            "shell_run", "shell.write", "echo x", "摘要");

        await host.Approvals.RevokeAsync(approval.Id, "不做了", default, "agent-7");

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == approval.Id);
        Assert.Equal("agent-7", row.DecidedBy);
    }

    [Fact]
    public async Task 没指定撤销人时按人记()
    {
        using var host = new GuanfuTestHost();

        var approval = await host.Approvals.RequestAsync(
            "shell_run", "shell.write", "echo x", "摘要");

        await host.Approvals.RevokeAsync(approval.Id, "不做了");

        var row = await host.Db.Approvals.AsNoTracking().FirstAsync(a => a.Id == approval.Id);
        Assert.Equal("human", row.DecidedBy);
    }

    // ---- B5：db_execute 的审批要留下结果摘要 ----

    [Fact]
    public async Task 数据库写执行完审批上有结果摘要()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var connection = await host.DbConnections.CreateAsync(
            project.Id, "c", "srv", "db", "u", "p");

        var runner = new FakeSqlRunner();
        var tools = new DatabaseMcpTools(
            host.DbConnections, runner, host.Gate, host.Approvals, host.Audit, host.Clock);

        var first = await tools.ExecuteAsync(project.Id, "UPDATE dbo.Orders SET v = 1", waitSeconds: 1);
        Assert.Equal("Pending", first.ApprovalStatus);
        Assert.Empty(runner.Executed);
        await host.Approvals.DecideAsync(first.ApprovalId, approve: true, decidedBy: "tester");

        var second = await tools.ExecuteAsync(
            project.Id, "UPDATE dbo.Orders SET v = 1", approvalId: first.ApprovalId);

        Assert.True(second.Ok, second.Message);

        var approval = await host.Approvals.GetAsync(first.ApprovalId);
        Assert.Contains("影响", approval!.ResultSummary);
        var replay = await tools.ExecuteAsync(
            project.Id, "UPDATE dbo.Orders SET v = 1", approvalId: first.ApprovalId);

        Assert.False(replay.Ok);
        Assert.Single(runner.Executed);
    }

    // ---- B8：试连要校验连接属于当前项目 ----

    [Fact]
    public async Task 试连别的项目的连接要被拒()
    {
        using var host = new GuanfuTestHost();
        var mine = await host.Projects.CreateAsync("我的项目");
        var other = await host.Projects.CreateAsync("别的项目");

        var connection = await host.DbConnections.CreateAsync(
            other.Id, "c", "srv", "db", "u", "p");

        var tools = new DatabaseMcpTools(
            host.DbConnections, new FakeSqlRunner(), host.Gate, host.Approvals, host.Audit, host.Clock);

        var result = await tools.TestConnectionAsync(connection.Id, mine.Id);

        Assert.False(result.Ok);
        Assert.Contains("不属于项目", result.Error);
    }

    // ---- B9：归一化后的重号要在数据库这一层被挡住 ----

    [Fact]
    public async Task 大小写不同的重号进不来()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");

        var first = new TaskItem
        {
            ProjectId = project.Id,
            Key = "req-001",
            Title = "一",
            Status = TaskStatus.Draft,
            CreatedAt = host.Clock.UtcNow,
            UpdatedAt = host.Clock.UtcNow,
            LastActivityAt = host.Clock.UtcNow
        };

        host.Db.Tasks.Add(first);
        await host.Db.SaveChangesAsync();

        Assert.Equal("req001", first.KeyNormalized);

        // 「REQ-001」归一化后跟「req-001」是同一个号 —— 数据库要挡下来
        var second = new TaskItem
        {
            ProjectId = project.Id,
            Key = "REQ-001",
            Title = "二",
            Status = TaskStatus.Draft,
            CreatedAt = host.Clock.UtcNow,
            UpdatedAt = host.Clock.UtcNow,
            LastActivityAt = host.Clock.UtcNow
        };

        host.Db.Tasks.Add(second);
        await Assert.ThrowsAsync<DbUpdateException>(() => host.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task 同一个项目里不能有归一化后相同的编号()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");

        await host.Tasks.CreateAsync(project.Id, "一", key: "req-001");

        await Assert.ThrowsAsync<GuanfuException>(
            async () => await host.Tasks.CreateAsync(project.Id, "二", key: "REQ-001"));
    }

    // ---- R2：验链要能跨批 ----

    [Fact]
    public async Task 审计链跨过批次边界仍然验得过()
    {
        using var host = new GuanfuTestHost();

        // 批次大小是 500 —— 写 520 条，逼着验链翻页
        for (var i = 0; i < 520; i++)
        {
            await host.Audit.RecordAsync(new ToolAuditEntry(
                "fs_list", "agent", null, null, $"path-{i}",
                RequiredApproval: false, ApprovalId: null,
                Outcome: "success", ResultSummary: $"{i} 项",
                RowsAffected: null, DurationMs: 1));
        }

        var report = await host.Audit.VerifyChainAsync();

        Assert.True(report.Intact, report.Reason);
        Assert.Equal(520, report.Total);
        Assert.Equal(520, report.Chained);
    }

    // ---- R3：写侧「禁止写」这一档 ----

    [Fact]
    public async Task 禁止写的表连人工审批都不放行()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var connection = await host.DbConnections.CreateAsync(
            project.Id, "c", "srv", "db", "u", "p");

        await host.DbConnections.AddRuleAsync(
            connection.Id, "dbo", "Ledger", allowRead: true, writeRequiresApproval: false, writeForbidden: true);

        var runner = new FakeSqlRunner();
        var tools = new DatabaseMcpTools(
            host.DbConnections, runner, host.Gate, host.Approvals, host.Audit, host.Clock);

        var result = await tools.ExecuteAsync(
            project.Id, "UPDATE dbo.Ledger SET v = 1", waitSeconds: 1);

        Assert.False(result.Ok);
        Assert.Contains("禁止写", result.Message);
        Assert.Empty(runner.Executed);
    }

    [Fact]
    public async Task 禁写规则默认不生效()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var connection = await host.DbConnections.CreateAsync(
            project.Id, "c", "srv", "db", "u", "p");

        var rule = await host.DbConnections.AddRuleAsync(connection.Id, "dbo", "Orders");

        Assert.False(rule.WriteForbidden);
        Assert.True(await host.DbConnections.IsWriteAllowedAsync(connection.Id, "dbo.Orders"));
    }

    [Fact]
    public async Task 禁写规则要存得住也要读得回()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");
        var connection = await host.DbConnections.CreateAsync(
            project.Id, "c", "srv", "db", "u", "p");

        var created = await host.DbConnections.AddRuleAsync(
            connection.Id, "dbo", "Ledger", writeForbidden: true);

        // 返回的 DTO 要带上这个标记（漏了的话界面上看不出规则生效了）
        Assert.True(created.WriteForbidden);

        var listed = await host.DbConnections.ListRulesAsync(connection.Id);
        Assert.True(listed.Single(r => r.TableName == "Ledger").WriteForbidden);

        Assert.False(await host.DbConnections.IsWriteAllowedAsync(connection.Id, "dbo.Ledger"));
    }

    // ---- O1：分页下推到 SQL ----

    [Fact]
    public async Task 分页在库里就切好了()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");

        for (var i = 0; i < 12; i++)
        {
            await host.Tasks.CreateAsync(project.Id, $"任务 {i}");
        }

        var page = await host.Tasks.ListAsync(project.Id, limit: 5, offset: 0);
        Assert.Equal(5, page.Count);

        var second = await host.Tasks.ListAsync(project.Id, limit: 5, offset: 5);
        Assert.Equal(5, second.Count);
        Assert.Empty(page.Select(t => t.Id).Intersect(second.Select(t => t.Id)));
    }

    [Fact]
    public async Task 按编号查任务仍然查得到()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("P");

        var created = await host.Tasks.CreateAsync(project.Id, "带编号的活", key: "REQ-007");

        var byKey = await host.Tasks.ListAsync(project.Id, key: "req007");
        Assert.Single(byKey);
        Assert.Equal(created.Id, byKey[0].Id);

        var byExact = await host.Tasks.ListAsync(project.Id, key: "REQ-007");
        Assert.Single(byExact);
    }

    // ---- O4：二进制判定 ----

    [Fact]
    public void 带字节序标记的文本不算二进制()
    {
        var utf16 = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("你好，world")).ToArray();
        Assert.False(FileSystemService.LooksBinary(utf16));
        Assert.Equal("你好，world", FileSystemService.DecodeText(utf16));

        var utf8Bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("你好")).ToArray();
        Assert.False(FileSystemService.LooksBinary(utf8Bom));
        Assert.Equal("你好", FileSystemService.DecodeText(utf8Bom));
    }

    [Fact]
    public void 真正的二进制还是判得出来()
    {
        var binary = new byte[] { 0x00, 0x01, 0x02, 0x00, 0x03, 0x00 };
        Assert.True(FileSystemService.LooksBinary(binary));
    }
}
