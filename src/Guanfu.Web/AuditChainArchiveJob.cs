using Microsoft.Extensions.Options;
using Guanfu.Application.Services;
using Guanfu.Infrastructure;

namespace Guanfu.Web;

/// <summary>
/// 定时把审计链头归档到观复的数据目录**之外**。
///
/// 库里的哈希链是应用层算的：拿到库文件的人连哈希一起重算，链照样能对上。
/// 把链头定期推到观复够不着的地方，才多一道对上的机会 ——
/// 那个地方能不能被同样的人改，是部署方的选择。
///
/// 没配归档路径就什么都不做（每次调用都会返回「未配置」，直接跳过）。
/// </summary>
public sealed class AuditChainArchiveJob(
    IServiceScopeFactory scopeFactory,
    IOptions<GuanfuOptions> options,
    ILogger<AuditChainArchiveJob> logger) : BackgroundService
{
    /// <summary>多久归档一次。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先等一会儿 —— 刚起来时链头跟启动前一样，归档了也是空的
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ArchiveOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 归档失败不能拖垮服务 —— 记日志，下一轮再来
                logger.LogWarning(ex, "审计链头归档失败。");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>归档一次。公开出来是为了能手动触发与测试。</summary>
    public async Task<AuditArchiveResult> ArchiveOnceAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Value.AuditArchivePath))
        {
            return new AuditArchiveResult(false, null, 0, null, null);
        }

        using var scope = scopeFactory.CreateScope();
        var archive = scope.ServiceProvider.GetRequiredService<IAuditChainArchiveService>();

        var result = await archive.ArchiveAsync(cancellationToken);

        if (result.Ok)
        {
            logger.LogInformation(
                "审计链头已归档：第 {Sequence} 条 → {Path}", result.Sequence, result.Path);
        }
        else
        {
            logger.LogWarning("审计链头没归档成：{Error}", result.Error);
        }

        return result;
    }
}
