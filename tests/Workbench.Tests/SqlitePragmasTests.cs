using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>
/// SQLite 连接级 PRAGMA：WAL + busy_timeout。
///
/// rollback journal 模式下读写互相阻塞，备份 VACUUM INTO 期间的写会立刻 SQLITE_BUSY。
/// busy_timeout 是**连接级**设置（Microsoft.Data.Sqlite 默认 0），
/// 所以拦截器必须挂在每条打开的连接上，而不是只在启动时设一次。
/// </summary>
public class SqlitePragmasTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "wb-wal-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task 拦截器给每条连接设WAL与busy_timeout()
    {
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .AddInterceptors(new SqliteWalInterceptor())
            .Options;

        await using var db = new WorkbenchDbContext(options);
        await db.Database.OpenConnectionAsync();

        var connection = (SqliteConnection)db.Database.GetDbConnection();

        await using var journal = connection.CreateCommand();
        journal.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)await journal.ExecuteScalarAsync());

        await using var timeout = connection.CreateCommand();
        timeout.CommandText = "PRAGMA busy_timeout;";
        Assert.Equal(5000L, Convert.ToInt64(await timeout.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task 内存库开WAL不报错()
    {
        // 内存库不支持 WAL，PRAGMA 会安静地返回 memory —— 拦截器不能因此炸掉
        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite("Filename=:memory:")
            .AddInterceptors(new SqliteWalInterceptor())
            .Options;

        await using var db = new WorkbenchDbContext(options);
        await db.Database.EnsureCreatedAsync();

        Assert.True(await db.Database.CanConnectAsync());
    }
}