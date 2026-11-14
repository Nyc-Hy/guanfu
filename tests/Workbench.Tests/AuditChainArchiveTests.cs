using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Services;
using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>
/// 审计链头的外部归档。
///
/// 库里的哈希链只做到「可发现」：拿到库文件的人连哈希一起重算，链照样能对上。
/// 把链头推到工作台数据目录之外，才多一道对上的机会 —— 这一层要能发现
/// 「记录被删过」「库被回滚过」「同一条记录被改过」。
///
/// 归档路径落在工作台数据目录里面等于没归档 —— 那种路径必须拒掉。
/// </summary>
public sealed class AuditChainArchiveTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), $"wb-archive-home-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"wb-archive-outside-{Guid.NewGuid():N}");

    public AuditChainArchiveTests()
    {
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        foreach (var directory in new[] { _home, _outside })
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task 没配路径时不归档并说清原因()
    {
        using var host = new WorkbenchTestHost();
        var archiver = Build(host, archivePath: null);

        var result = await archiver.ArchiveAsync();

        Assert.False(result.Ok);
        Assert.Contains("没有配置审计归档路径", result.Error);
    }

    [Fact]
    public async Task 归档会在文件里追加一行链头快照()
    {
        using var host = new WorkbenchTestHost();
        await RecordAudit(host);

        var file = Path.Combine(_outside, "chain.jsonl");
        var archiver = Build(host, file);

        var result = await archiver.ArchiveAsync();

        Assert.True(result.Ok, result.Error);
        Assert.True(result.Sequence > 0);
        Assert.Equal(file, result.Path);

        var lines = await File.ReadAllLinesAsync(file);
        Assert.Single(lines);
        Assert.Contains("\"sequence\"", lines[0], StringComparison.OrdinalIgnoreCase);

        // 链头没变就不重复追加 —— 定时任务每 10 分钟跑一次，
        // 一次没写入就等于把归档文件灌成流水账
        await archiver.ArchiveAsync();
        Assert.Single(await File.ReadAllLinesAsync(file));
    }

    [Fact]
    public async Task 链头变了会再追加一行()
    {
        using var host = new WorkbenchTestHost();
        await RecordAudit(host);

        var file = Path.Combine(_outside, "chain.jsonl");
        var archiver = Build(host, file);
        await archiver.ArchiveAsync();

        await RecordAudit(host);
        await archiver.ArchiveAsync();

        Assert.Equal(2, (await File.ReadAllLinesAsync(file)).Length);
    }

    [Fact]
    public async Task 配成目录时链头文件放默认文件名()
    {
        using var host = new WorkbenchTestHost();
        await RecordAudit(host);

        Directory.CreateDirectory(_outside);
        var archiver = Build(host, _outside);

        var result = await archiver.ArchiveAsync();

        Assert.True(result.Ok, result.Error);
        Assert.Equal(Path.Combine(_outside, "workbench-audit-chain.jsonl"), result.Path);
    }

    [Fact]
    public async Task 归档在工作台数据目录里面会被拒掉()
    {
        using var host = new WorkbenchTestHost();

        var inside = Path.Combine(_home, "chain.jsonl");
        var archiver = Build(host, inside);

        var error = await Assert.ThrowsAsync<Workbench.Domain.WorkbenchException>(
            () => archiver.ArchiveAsync());

        Assert.Contains("必须在工作台数据目录之外", error.Message);
    }

    [Fact]
    public async Task 对账通过时说是对得上()
    {
        using var host = new WorkbenchTestHost();
        await RecordAudit(host);

        var file = Path.Combine(_outside, "chain.jsonl");
        var archiver = Build(host, file);
        await archiver.ArchiveAsync();

        var report = await archiver.VerifyAsync();

        Assert.True(report.Configured);
        Assert.True(report.Intact, report.Reason);
        Assert.Contains("对得上", report.Reason);
    }

    [Fact]
    public async Task 库里的记录被删过能查出来()
    {
        using var host = new WorkbenchTestHost();
        await RecordAudit(host);

        var file = Path.Combine(_outside, "chain.jsonl");
        var archiver = Build(host, file);
        await archiver.ArchiveAsync();

        // 绕过应用层直接删记录 —— 模拟「拿到库文件的人删了记录」
        var head = await host.Db.ToolCallAudits.AsNoTracking().OrderByDescending(a => a.Sequence).FirstAsync();
        await host.Db.ToolCallAudits.Where(a => a.Id == head.Id).ExecuteDeleteAsync();

        var report = await archiver.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains("删", report.Reason);
    }

    [Fact]
    public async Task 同一条记录被改过能查出来()
    {
        using var host = new WorkbenchTestHost();
        await RecordAudit(host);

        var file = Path.Combine(_outside, "chain.jsonl");
        var archiver = Build(host, file);
        await archiver.ArchiveAsync();

        var head = await host.Db.ToolCallAudits.AsNoTracking().OrderByDescending(a => a.Sequence).FirstAsync();
        await host.Db.ToolCallAudits
            .Where(a => a.Id == head.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ResultSummary, "被改过了"));

        var report = await archiver.VerifyAsync();

        Assert.False(report.Intact);
        Assert.Contains("改过", report.Reason);
    }

    [Fact]
    public async Task 归档之后又新增的记录不影响对账()
    {
        using var host = new WorkbenchTestHost();
        await RecordAudit(host);

        var file = Path.Combine(_outside, "chain.jsonl");
        var archiver = Build(host, file);
        await archiver.ArchiveAsync();

        await RecordAudit(host);

        var report = await archiver.VerifyAsync();

        Assert.True(report.Intact, report.Reason);
        Assert.Contains("新增", report.Reason);
    }

    private AuditChainArchiver Build(WorkbenchTestHost host, string? archivePath)
    {
        var options = Options.Create(new WorkbenchOptions
        {
            HomeDirectory = _home,
            AuditArchivePath = archivePath
        });

        return new AuditChainArchiver(host.Db, host.Audit, options, host.Clock);
    }

    private static Task RecordAudit(WorkbenchTestHost host) =>
        host.Audit.RecordAsync(new ToolAuditEntry(
            "fs_list", "agent-a", null, null, "src", false, null, "success", "3 项", null, 12));
}
