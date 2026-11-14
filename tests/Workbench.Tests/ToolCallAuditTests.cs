using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Services;
using Workbench.Mcp;

namespace Workbench.Tests;

/// <summary>
/// 工具调用的统一留痕。
///
/// 以前只有写工具留痕，只读调用不留 —— 「谁访问了什么资源」查不出来，
/// 而这正是企业合规最关心的那一问（谁看了生产库的数据）。
/// </summary>
public sealed class ToolCallAuditTests
{
    private static Dictionary<string, JsonElement> Args(params (string Key, string? Value)[] pairs)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var (key, value) in pairs)
        {
            result[key] = value is null
                ? default
                : JsonSerializer.SerializeToElement(value);
        }

        return result;
    }

    [Fact]
    public async Task 只读工具调用也留痕()
    {
        using var host = new WorkbenchTestHost();
        var recorder = new ToolCallRecorder(host.Audit);

        await recorder.RecordAsync("workbench_list_tasks", Args(("projectId", null)), "success", 12);

        var row = Assert.Single(await host.Db.ToolCallAudits.AsNoTracking().ToListAsync());
        Assert.Equal("workbench_list_tasks", row.ToolName);
        Assert.Equal("success", row.Outcome);
    }

    [Fact]
    public void 自己留痕的工具不再重复留()
    {
        // db_execute / git_commit / git_push / shell_run / db_query
        // 自己记得更细（审批号、影响行数、SQL 原文），中央那份补不回来。
        foreach (var name in new[] { "db_execute", "db_query", "git_commit", "git_push", "shell_run" })
        {
            Assert.Contains(name, ToolCallRecorder.SelfAuditedTools);
        }

        Assert.DoesNotContain("workbench_list_tasks", ToolCallRecorder.SelfAuditedTools);
    }

    [Fact]
    public async Task 敏感参数被抹掉()
    {
        using var host = new WorkbenchTestHost();
        var recorder = new ToolCallRecorder(host.Audit);

        await recorder.RecordAsync(
            "某个工具",
            Args(("password", "hunter2"), ("name", "订单库")),
            "success", 5);

        var row = Assert.Single(await host.Db.ToolCallAudits.AsNoTracking().ToListAsync());
        Assert.DoesNotContain("hunter2", row.Arguments);
        Assert.Contains("***", row.Arguments);
        Assert.Contains("订单库", row.Arguments);
    }

    [Fact]
    public async Task 显式传的项目与调用方被记上()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var recorder = new ToolCallRecorder(host.Audit);

        await recorder.RecordAsync(
            "workbench_list_tasks",
            Args(("projectId", project.Id.ToString()), ("callerId", "claude")),
            "success", 3);

        var row = Assert.Single(await host.Db.ToolCallAudits.AsNoTracking().ToListAsync());
        Assert.Equal(project.Id, row.ProjectId);
        Assert.Equal("claude", row.CallerId);
    }

    [Fact]
    public async Task 有会话时用会话里的项目与任务()
    {
        using var host = new WorkbenchTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var session = new AgentSessionDto(
            Guid.NewGuid(), project.Id, "测试项目", task.Id, "REQ-001",
            "claude", "/tmp/repo", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        var recorder = new ToolCallRecorder(host.Audit, new FakeAccessor(session));

        await recorder.RecordAsync("workbench_get_context", Args(), "success", 8);

        var row = Assert.Single(await host.Db.ToolCallAudits.AsNoTracking().ToListAsync());
        Assert.Equal(project.Id, row.ProjectId);
        Assert.Equal(task.Id, row.TaskId);
        Assert.Equal("claude", row.CallerId);
    }

    [Fact]
    public async Task 没有会话时退回请求头里的调用方()
    {
        using var host = new WorkbenchTestHost();

        // Claude Code 这类客户端不会给工具显式传 callerId，
        // 只在连接头上表明自己是谁 —— 少了这条，审计里就只剩一列空白。
        var recorder = new ToolCallRecorder(host.Audit, new FakeAccessor(null));

        await recorder.RecordAsync("workbench_list_tasks", Args(), "success", 2);

        var row = Assert.Single(await host.Db.ToolCallAudits.AsNoTracking().ToListAsync());
        Assert.Equal("header-agent", row.CallerId);
    }

    [Fact]
    public async Task 审计写不进去也不影响调用本身()
    {
        using var host = new WorkbenchTestHost();

        // 审计服务直接抛异常 —— 留痕失败不能把调用搞挂，
        // 否则 agent 会以为调用失败了（那次操作其实已经发生了）。
        var recorder = new ToolCallRecorder(new ThrowingAudit());

        await recorder.RecordAsync("workbench_list_tasks", Args(), "success", 1);
    }

    [Fact]
    public void 参数为空时记成空对象()
    {
        Assert.Equal("{}", ToolCallRecorder.Redact(null));
        Assert.Equal("{}", ToolCallRecorder.Redact(new Dictionary<string, JsonElement>()));
    }

    private sealed class FakeAccessor(AgentSessionDto? session) : IAgentSessionAccessor
    {
        public Task<AgentSessionDto?> CurrentAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<AgentSessionDto?>(session);

        public string? CurrentMcpSessionId => null;

        public string? CurrentAgentId => "header-agent";
    }

    private sealed class ThrowingAudit : IToolAuditService
    {
        public Task RecordAsync(ToolAuditEntry entry, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("库写不进去");

        public Task<IReadOnlyList<ToolAuditDto>> ListAsync(
            string? toolName = null, Guid? taskId = null, Guid? projectId = null,
            int limit = 100, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ToolAuditDto>>([]);

        public Task<AuditChainReport> VerifyChainAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new AuditChainReport(true, 0, 0, null, null));
    }
}
