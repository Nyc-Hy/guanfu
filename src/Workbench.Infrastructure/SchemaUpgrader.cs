using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Workbench.Infrastructure;

/// <summary>
/// 轻量 schema 升级。
///
/// 这个项目没用 EF Migrations（`EnsureCreated` 建表，够简单），但 SQLite 的
/// `EnsureCreated` **不会给已存在的表补列** —— 升级后老数据库会缺列，
/// 查出来直接报 "no such column"。所以这里补一个幂等的列补丁：
/// 缺哪个补哪个，补过就跳过。
///
/// 只处理「加可空 / 有默认值的列」—— 这类改动 SQLite 支持且不会丢数据。
/// 将来要改列类型或加约束，得走真正的迁移。
/// </summary>
public static class SchemaUpgrader
{
    /// <summary>表名 → 要补的列（列名 → SQLite 列定义）。</summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Columns =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["tasks"] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // 任务类型：需求/缺陷/优化。0 = Requirement，跟旧数据默认值一致。
                ["Type"] = "INTEGER NOT NULL DEFAULT 0",
                // 绑定的开发分支
                ["Branch"] = "TEXT NULL"
            }
        };

    public static async Task UpgradeAsync(WorkbenchDbContext db, CancellationToken cancellationToken = default)
    {
        var connection = db.Database.GetDbConnection();

        if (connection is not SqliteConnection sqlite)
        {
            // 只支持 SQLite；换别的存储要另外写迁移
            return;
        }

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        foreach (var (table, columns) in Columns)
        {
            var existing = await GetColumnsAsync(sqlite, table, cancellationToken);

            if (existing.Count == 0)
            {
                // 表还没建（首次启动，EnsureCreated 刚建过）—— 不用管
                continue;
            }

            foreach (var (column, definition) in columns)
            {
                if (existing.Contains(column))
                {
                    continue;
                }

                await using var alter = sqlite.CreateCommand();
                alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition};";
                await alter.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static async Task<HashSet<string>> GetColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(1);
            columns.Add(name);
        }

        return columns;
    }
}
