using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using Workbench.Infrastructure;

namespace Workbench.Web;

/// <summary>
/// 定时备份 SQLite 库。
///
/// 用 SQLite 自己的 <c>VACUUM INTO</c> 而不是文件复制 ——
/// 复制正在写的 .db 会拿到撕裂的页，导入后就是坏库。
/// VACUUM INTO 产出的是一致性快照，边跑边备也安全。
///
/// 备份留在 WORKBENCH_HOME/backups/ 下，按时间命名，保留最近 N 份。
/// 旧的删掉 —— 不然备份目录自己就会把磁盘吃光。
/// </summary>
public sealed class DatabaseBackupService(
    IOptions<WorkbenchOptions> options,
    ILogger<DatabaseBackupService> logger) : BackgroundService
{
    /// <summary>多久备一次。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>保留最近几份。</summary>
    public const int KeepCount = 24;

    public static string BackupDirectoryName => "backups";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 启动后先等一会儿再备 —— 刚起来就备一份，跟启动前的状态一样，没意义
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
                await BackupOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 备份失败不能拖垮服务 —— 记日志，下一轮再来
                logger.LogWarning(ex, "数据库备份失败。");
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

    /// <summary>备一份。公开出来是为了能手动触发与测试。</summary>
    public async Task<string?> BackupOnceAsync(CancellationToken cancellationToken = default)
    {
        var workbenchOptions = options.Value;
        var dbPath = workbenchOptions.DatabasePath;

        if (!File.Exists(dbPath))
        {
            return null;
        }

        var directory = Path.Combine(workbenchOptions.HomeDirectory, BackupDirectoryName);
        Directory.CreateDirectory(directory);

        var target = Path.Combine(
            directory,
            $"workbench-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.db");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // VACUUM INTO 的目标不能已存在
        await using var command = connection.CreateCommand();
        command.CommandText = $"VACUUM INTO '{target.Replace("'", "''", StringComparison.Ordinal)}'";
        await command.ExecuteNonQueryAsync(cancellationToken);

        PruneOldBackups(directory);
        logger.LogInformation("数据库已备份到 {Target}", target);

        return target;
    }

    /// <summary>只留最近 N 份，旧的删掉。</summary>
    internal static void PruneOldBackups(string directory, int keep = KeepCount)
    {
        try
        {
            var files = new DirectoryInfo(directory)
                .GetFiles("workbench-*.db")
                .OrderByDescending(f => f.Name)
                .Skip(keep)
                .ToList();

            foreach (var file in files)
            {
                file.Delete();
            }
        }
        catch (IOException)
        {
            // 删不掉就留着，下次再试
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
