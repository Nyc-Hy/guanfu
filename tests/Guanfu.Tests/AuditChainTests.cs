using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 审计哈希链：有人直接改数据库文件时，改动要能被查出来。
///
/// 应用层本来就不提供改审计的入口 —— 但拿到库文件的人能直接改。
/// 哈希链就是为了让那种改动**可发现**，不是防止它发生。
/// </summary>
public sealed class AuditChainTests
{
    [Fact]
    public async Task 连续写的记录能连成一条链()
    {
        using var host = new GuanfuTestHost();

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "fs_list", "agent-a", null, null, "src", false, null, "success", "3 项", null, 12));

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "git_commit", "agent-a", null, null, "msg", true, null, "pending", "等审批", null, 30));

        var report = await host.Audit.VerifyChainAsync();

        Assert.True(report.Intact, report.Reason);
        Assert.Equal(2, report.Chained);
        Assert.Null(report.FirstBrokenId);
    }

    [Fact]
    public async Task 改一条记录的内容会被验出来()
    {
        using var host = new GuanfuTestHost();

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "fs_list", "agent-a", null, null, "src", false, null, "success", "3 项", null, 12));

        var target = await host.Db.ToolCallAudits.AsNoTracking().FirstAsync();

        // 绕过应用层，直接改库文件里的内容 —— 模拟「拿到库文件的人手改」。
        await host.Db.ToolCallAudits
            .Where(a => a.Id == target.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResultSummary, "被改过了"));

        var report = await host.Audit.VerifyChainAsync();

        Assert.False(report.Intact);
        Assert.NotNull(report.FirstBrokenId);
        Assert.Contains("改过", report.Reason);
    }

    [Fact]
    public async Task 删掉中间一条记录链会断()
    {
        using var host = new GuanfuTestHost();

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "fs_list", "agent-a", null, null, "第一条", false, null, "success", "1", null, 5));

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "fs_read", "agent-a", null, null, "第二条", false, null, "success", "2", null, 6));

        await host.Audit.RecordAsync(new ToolAuditEntry(
            "fs_read", "agent-a", null, null, "第三条", false, null, "success", "3", null, 7));

        var middle = await host.Db.ToolCallAudits
            .AsNoTracking()
            .OrderBy(a => a.CreatedAt)
            .Skip(1)
            .FirstAsync();

        await host.Db.ToolCallAudits.Where(a => a.Id == middle.Id).ExecuteDeleteAsync();

        var report = await host.Audit.VerifyChainAsync();

        Assert.False(report.Intact);
        Assert.NotNull(report.FirstBrokenId);
        Assert.Contains("断", report.Reason);
    }

    [Fact]
    public void 哈希会因内容不同而不同()
    {
        var first = new ToolCallAudit
        {
            ToolName = "fs_list",
            CallerId = "agent-a",
            Arguments = "src",
            Outcome = "success",
            DurationMs = 1,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        var second = new ToolCallAudit
        {
            ToolName = "fs_list",
            CallerId = "agent-a",
            Arguments = "other",
            Outcome = "success",
            DurationMs = 1,
            CreatedAt = first.CreatedAt
        };

        Assert.NotEqual(
            ToolAuditService.ComputeHash(first, "prev"),
            ToolAuditService.ComputeHash(second, "prev"));
    }

    [Fact]
    public void 前一条的哈希变了本条的哈希也跟着变()
    {
        var record = new ToolCallAudit
        {
            ToolName = "fs_list",
            CallerId = "agent-a",
            Arguments = "src",
            Outcome = "success",
            DurationMs = 1,
            CreatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };

        Assert.NotEqual(
            ToolAuditService.ComputeHash(record, "prev-a"),
            ToolAuditService.ComputeHash(record, "prev-b"));
    }
}
