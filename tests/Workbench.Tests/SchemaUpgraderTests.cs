using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>
/// schema 补丁。
///
/// 这个项目用 `EnsureCreated` 建表，不走 EF Migrations —— 但 SQLite 的
/// EnsureCreated **不会给已存在的表补列**。不补的话，升级后老数据库一查就报
/// "no such column: Type"。所以必须有个幂等的补列步骤。
/// </summary>
public class SchemaUpgraderTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "wb-schema-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    private void CreateLegacyDatabase()
    {
        // 老库的 tasks 表：没有 Type，也没有 Branch
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE tasks (
                Id TEXT NOT NULL PRIMARY KEY,
                ProjectId TEXT NOT NULL,
                Title TEXT NOT NULL,
                Status TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task 老库会被补上类型与分支两列()
    {
        CreateLegacyDatabase();

        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        await using var db = new WorkbenchDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db);

        var columns = await ReadColumnsAsync();

        Assert.Contains("Type", columns);
        Assert.Contains("Branch", columns);
    }

    [Fact]
    public async Task 重复执行不会报错()
    {
        CreateLegacyDatabase();

        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        await using var db = new WorkbenchDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db);
        await SchemaUpgrader.UpgradeAsync(db);

        var columns = await ReadColumnsAsync();

        Assert.Contains("Type", columns);
        Assert.Contains("Branch", columns);
    }

    [Fact]
    public async Task 补丁不会动已有数据()
    {
        CreateLegacyDatabase();

        using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO tasks (Id, ProjectId, Title, Status) VALUES ('t1', 'p1', '老任务', 'Draft');";
            insert.ExecuteNonQuery();
        }

        var options = new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

        await using var db = new WorkbenchDbContext(options);
        await SchemaUpgrader.UpgradeAsync(db);

        using var check = new SqliteConnection($"Data Source={_dbPath}");
        check.Open();
        using var read = check.CreateCommand();
        read.CommandText = "SELECT Title FROM tasks WHERE Id = 't1';";
        Assert.Equal("老任务", (string?)read.ExecuteScalar());
    }

    private async Task<HashSet<string>> ReadColumnsAsync()
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"tasks\");";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
