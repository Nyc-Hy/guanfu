using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Guanfu.Infrastructure;

/// <summary>
/// SQLite 连接级 PRAGMA。
///
/// - <c>journal_mode=WAL</c>：读写不再互相阻塞（rollback journal 模式下读会挡写、写会挡读），
///   备份 VACUUM INTO 期间的写也不至于立刻 SQLITE_BUSY。WAL 是**库级持久**设置，
///   设一次就跟着库文件走；这里每次开连接都执行一遍，开销极小，且能覆盖
///   「库是从别处拷来的、还没开过 WAL」的情况。内存库不支持 WAL，PRAGMA 会安静地
///   返回 memory，不报错。
/// - <c>busy_timeout=5000</c>：撞上锁时等 5 秒再报 SQLITE_BUSY，而不是立刻失败。
///   这是**连接级**设置（Microsoft.Data.Sqlite 默认是 0），所以每条连接打开时都要设 ——
///   靠 <see cref="SqliteWalInterceptor"/> 挂在 DbContext 上，连接池里每条连接都生效。
/// </summary>
internal static class SqlitePragmas
{
    private const string BusyTimeout = "PRAGMA busy_timeout=5000;";

    private static string CommandText(SqliteConnection connection) =>
        new SqliteConnectionStringBuilder(connection.ConnectionString).Mode == SqliteOpenMode.ReadOnly
            ? BusyTimeout
            : BusyTimeout + " PRAGMA journal_mode=WAL;";

    internal static async Task ApplyAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = CommandText(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static void Apply(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = CommandText(connection);
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// 每条 SQLite 连接打开时执行 WAL + busy_timeout。
/// 注册在 AddDbContext 的选项里，连接池复用的连接也只在真正打开时执行一次。
/// </summary>
public sealed class SqliteWalInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is SqliteConnection sqlite)
        {
            SqlitePragmas.Apply(sqlite);
        }
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (connection is SqliteConnection sqlite)
        {
            await SqlitePragmas.ApplyAsync(sqlite, cancellationToken);
        }
    }
}
