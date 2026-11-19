using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 审计链的格式版本化。
///
/// **链的格式不能随便改** —— 改了就等于老记录的哈希全对不上，
/// 整条链当场报断裂。加字段时要给格式编版本：老记录按老格式算，
/// 新记录按新格式算，两代同处一条链里也验得过。
///
/// 这里钉三件事：
/// - 第 1 版的老记录照旧验得过（升级不能把老库搞坏）；
/// - 新记录带上「这把钥匙背后的人」，而且它进了哈希（改了验得出来）；
/// - 改掉成员名字 → 链报断裂（不能白改）。
/// </summary>
public sealed class AuditHashVersionTests
{
    [Fact]
    public async Task 新记录带成员且成员进了哈希()
    {
        using var host = new GuanfuTestHost();

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "db_execute", "agent-a", null, null, "{}",
            RequiredApproval: true, ApprovalId: null,
            Outcome: "success", ResultSummary: "3 行",
            RowsAffected: 3, DurationMs: 12,
            MemberName: "张三"));

        var rows = await host.Audit.ListAsync();
        var row = Assert.Single(rows);

        Assert.Equal("张三", row.MemberName);

        var report = await host.Audit.VerifyChainAsync();
        Assert.True(report.Intact);
    }

    [Fact]
    public async Task 改了成员名字验链就断()
    {
        using var host = new GuanfuTestHost();

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "db_execute", "agent-a", null, null, "{}",
            RequiredApproval: false, ApprovalId: null,
            Outcome: "success", ResultSummary: null,
            RowsAffected: null, DurationMs: 1,
            MemberName: "张三"));

        // 直接改库（模拟「拿到库文件的人改了内容」）
        await host.Db.Database.ExecuteSqlRawAsync(
            "UPDATE tool_call_audit SET MemberName = '李四'");

        var report = await host.Audit.VerifyChainAsync();

        Assert.False(report.Intact);
    }

    [Fact]
    public async Task 改版本号也验链就断()
    {
        using var host = new GuanfuTestHost();

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "fs_list", "agent-a", null, null, "{}",
            RequiredApproval: false, ApprovalId: null,
            Outcome: "success", ResultSummary: null,
            RowsAffected: null, DurationMs: 1,
            MemberName: "张三"));

        // 版本号本身不在哈希里，但改它一样会被发现：
        // 版本对不上 → 算出来的哈希对不上。
        await host.Db.Database.ExecuteSqlRawAsync(
            "UPDATE tool_call_audit SET HashVersion = 1");

        var report = await host.Audit.VerifyChainAsync();

        Assert.False(report.Intact);
    }

    [Fact]
    public async Task 老记录按第一版格式照样验得过()
    {
        using var host = new GuanfuTestHost();

        // 手写一条「第 1 版格式」的记录（字段顺序与当年一模一样），
        // 模拟升级前就在库里的老数据。
        await host.Db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO tool_call_audit
                (Id, ToolName, CallerId, Arguments, RequiredApproval, Outcome, DurationMs, CreatedAt, Sequence, HashVersion)
            VALUES
                ('11111111-1111-1111-1111-111111111111', 'fs_list', 'agent-old', '{{}}', 0, 'success', 5, '2026-01-01 00:00:00', 1, 1)
            """);

        // 按第 1 版的算法算它的哈希 —— 算对了，说明老记录没被作废。
        var legacy = new ToolCallAudit
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ToolName = "fs_list",
            CallerId = "agent-old",
            Arguments = "{}",
            RequiredApproval = false,
            Outcome = "success",
            DurationMs = 5,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Sequence = 1,
            HashVersion = 1
        };

        legacy.Hash = ToolAuditService.ComputeHash(legacy, null);
        await host.Db.Database.ExecuteSqlRawAsync(
            "UPDATE tool_call_audit SET Hash = {0} WHERE Id = {1}",
            legacy.Hash,
            legacy.Id.ToString());

        var report = await host.Audit.VerifyChainAsync();

        Assert.True(report.Intact);
    }

    [Fact]
    public async Task 两代记录同处一条链也验得过()
    {
        using var host = new GuanfuTestHost();

        // 一条第 1 版的老记录
        await host.Db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO tool_call_audit
                (Id, ToolName, CallerId, Arguments, RequiredApproval, Outcome, DurationMs, CreatedAt, Sequence, HashVersion)
            VALUES
                ('22222222-2222-2222-2222-222222222222', 'fs_list', 'agent-old', '{{}}', 0, 'success', 5, '2026-01-01 00:00:00', 1, 1)
            """);

        var legacy = new ToolCallAudit
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ToolName = "fs_list",
            CallerId = "agent-old",
            Arguments = "{}",
            Outcome = "success",
            DurationMs = 5,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            Sequence = 1,
            HashVersion = 1
        };

        legacy.Hash = ToolAuditService.ComputeHash(legacy, null);
        await host.Db.Database.ExecuteSqlRawAsync(
            "UPDATE tool_call_audit SET Hash = {0} WHERE Id = {1}",
            legacy.Hash,
            legacy.Id.ToString());

        // 后面接一条第 2 版的新记录（它要链在老记录后面）
        await host.Audit.RecordAsync(new ToolAuditEntry(
            "db_execute", "agent-new", null, null, "{}",
            RequiredApproval: false, ApprovalId: null,
            Outcome: "success", ResultSummary: null,
            RowsAffected: null, DurationMs: 1,
            MemberName: "张三"));

        var report = await host.Audit.VerifyChainAsync();

        Assert.True(report.Intact);
        Assert.Equal(2, report.Chained);
    }
}
