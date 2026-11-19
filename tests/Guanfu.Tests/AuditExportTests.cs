using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 审计账的导出。
///
/// 企业客户要的是「一份能交出去的账」——
/// 但**没验过的账只是一堆日志**，所以导出的内容必须带**验链结论**。
/// </summary>
public sealed class AuditExportTests
{
    [Fact]
    public async Task 导出的报告里带着验链结论()
    {
        using var host = new GuanfuTestHost();

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "db_execute", "agent-a", null, null, "{}",
            RequiredApproval: true, ApprovalId: null,
            Outcome: "success", ResultSummary: "3 行",
            RowsAffected: 3, DurationMs: 12,
            MemberName: "张三"));

        var exports = new ExportService(host.Db, host.Reviews, host.Audit);
        var report = await exports.ExportAuditAsync();

        Assert.Contains("工具调用审计", report);
        Assert.Contains("哈希链：完整", report);
        Assert.Contains("张三", report);
        Assert.Contains("db_execute", report);
    }

    [Fact]
    public async Task CSV带表头与行()
    {
        using var host = new GuanfuTestHost();

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "fs_list", "agent-a", null, null, "{}",
            RequiredApproval: false, ApprovalId: null,
            Outcome: "success", ResultSummary: "5 项",
            RowsAffected: null, DurationMs: 3,
            MemberName: "李四"));

        var exports = new ExportService(host.Db, host.Reviews, host.Audit);
        var csv = await exports.ExportAuditAsync(format: "csv");

        Assert.Contains("时间,工具,调用方,谁的人", csv);
        Assert.Contains("fs_list", csv);
        Assert.Contains("李四", csv);
    }

    [Fact]
    public async Task JSON带着链的状态()
    {
        using var host = new GuanfuTestHost();

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "fs_list", "agent-a", null, null, "{}",
            RequiredApproval: false, ApprovalId: null,
            Outcome: "success", ResultSummary: null,
            RowsAffected: null, DurationMs: 1));

        var exports = new ExportService(host.Db, host.Reviews, host.Audit);
        var json = await exports.ExportAuditAsync(format: "json");

        Assert.Contains("\"chainIntact\":true", json);
        Assert.Contains("\"records\"", json);
    }

    [Fact]
    public async Task 范围里没有记录就说清楚()
    {
        using var host = new GuanfuTestHost();
        var exports = new ExportService(host.Db, host.Reviews, host.Audit);

        await Assert.ThrowsAsync<GuanfuException>(() => exports.ExportAuditAsync());
    }

    [Fact]
    public async Task 成员导出只带自己看得见的()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        var alice = await members.CreateAsync("alice", "爱丽丝", "password-1");
        var bob = await members.CreateAsync("bob", "鲍勃", "password-2");

        var project = await host.Projects.CreateAsync("导出项目");
        var mine = await host.Tasks.CreateAsync(project.Id, "我的活");
        var theirs = await host.Tasks.CreateAsync(project.Id, "别人的活");

        var setup = new TaskService(host.Db, host.Clock, host.Git, host.Reviews);
        await setup.SetAssignmentAsync(mine.Id, "爱丽丝", assigneeMemberId: alice.Id);
        await setup.SetAssignmentAsync(theirs.Id, "鲍勃", assigneeMemberId: bob.Id);

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "db_execute", "agent-a", project.Id, mine.Id, "{}",
            RequiredApproval: false, ApprovalId: null,
            Outcome: "success", ResultSummary: "我的",
            RowsAffected: null, DurationMs: 1));

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "db_execute", "agent-b", project.Id, theirs.Id, "{}",
            RequiredApproval: false, ApprovalId: null,
            Outcome: "success", ResultSummary: "他的",
            RowsAffected: null, DurationMs: 1));

        var scope = new FakeScope(new MemberScope(alice.Id, "alice", "爱丽丝", MemberRole.Member));
        var scopedAudit = new ToolAuditService(host.Db, host.Clock, scope);
        var exports = new ExportService(host.Db, host.Reviews, scopedAudit);

        var report = await exports.ExportAuditAsync();

        Assert.Contains("我的", report);
        Assert.DoesNotContain("他的", report);
    }

    private sealed class FakeScope(MemberScope? scope) : ICurrentMemberScope
    {
        public MemberScope? Current { get; set; } = scope;
    }
}
