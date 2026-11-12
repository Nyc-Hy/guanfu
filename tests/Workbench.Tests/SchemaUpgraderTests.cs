using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>
/// schema 升级。
///
/// 这个项目用 `EnsureCreated` 建表，不走 EF Migrations —— 但 SQLite 的
/// `EnsureCreated` 在库已存在时**整个是 no-op**：不补表，也不补列。
/// 不补的话，升级后老库一查就报 "no such table: review_rounds" /
/// "no such column: t.Key"。所以必须有个幂等的升级步骤。
///
/// 这里的升级是从 EF 模型推出来的，不维护手写清单 ——
/// 手写清单漏过（补列清单里只有 tasks 表两列，projects 四个列和五张新表全漏了）。
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

    /// <summary>
    /// 造一个「最初版本」的老库：有 workspaces / projects / tasks / status_events，
    /// 但没有后来加的列（tasks.Key、tasks.MilestoneId、tasks.Type、tasks.Branch、
    /// projects 的四个配置列）和后来加的表。
    /// </summary>
    private void CreateLegacyDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=OFF;

            CREATE TABLE workspaces (
                "Id" TEXT NOT NULL CONSTRAINT "PK_workspaces" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Description" TEXT NULL,
                "CreatedAt" TEXT NOT NULL
            );

            CREATE TABLE projects (
                "Id" TEXT NOT NULL CONSTRAINT "PK_projects" PRIMARY KEY,
                "WorkspaceId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Description" TEXT NULL,
                "RepoPath" TEXT NULL,
                "RepoRemote" TEXT NULL,
                "DefaultBranch" TEXT NULL,
                "ManagedByWorkbench" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL
            );

            CREATE TABLE tasks (
                "Id" TEXT NOT NULL CONSTRAINT "PK_tasks" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "ParentTaskId" TEXT NULL,
                "Title" TEXT NOT NULL,
                "Description" TEXT NULL,
                "Status" TEXT NOT NULL,
                "Weight" REAL NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "UpdatedAt" TEXT NOT NULL,
                "LastActivityAt" TEXT NULL,
                "ClaimedByAgentId" TEXT NULL,
                "LeaseExpiresAt" TEXT NULL
            );

            CREATE TABLE status_events (
                "Id" TEXT NOT NULL CONSTRAINT "PK_status_events" PRIMARY KEY,
                "TaskId" TEXT NOT NULL,
                "FromStatus" INTEGER NULL,
                "ToStatus" INTEGER NULL,
                "Source" TEXT NOT NULL,
                "Actor" TEXT NULL,
                "Note" TEXT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();

        // 塞一条老数据，验证升级不丢东西
        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO workspaces (Id, Name, CreatedAt) VALUES ('11111111-1111-1111-1111-111111111111', '默认工作区', '2026-01-01 00:00:00');
            INSERT INTO projects (Id, WorkspaceId, Name, ManagedByWorkbench, CreatedAt, UpdatedAt)
                VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', '老项目', 0, '2026-01-01 00:00:00', '2026-01-01 00:00:00');
            INSERT INTO tasks (Id, ProjectId, Title, Status, Weight, CreatedAt, UpdatedAt)
                VALUES ('33333333-3333-3333-3333-333333333333', '22222222-2222-2222-2222-222222222222', '老任务', 'Draft', 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00');
            """;
        insert.ExecuteNonQuery();
    }

    private DbContextOptions<WorkbenchDbContext> Options() =>
        new DbContextOptionsBuilder<WorkbenchDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

    [Fact]
    public async Task 老库会被补上类型与分支两列()
    {
        CreateLegacyDatabase();

        await using var db = new WorkbenchDbContext(Options());
        await SchemaUpgrader.UpgradeAsync(db);

        var columns = await ReadColumnsAsync("tasks");

        Assert.Contains("Type", columns);
        Assert.Contains("Branch", columns);
    }

    [Fact]
    public async Task 重复执行不会报错()
    {
        CreateLegacyDatabase();

        await using var db = new WorkbenchDbContext(Options());
        await SchemaUpgrader.UpgradeAsync(db);
        await SchemaUpgrader.UpgradeAsync(db);

        var columns = await ReadColumnsAsync("tasks");

        Assert.Contains("Type", columns);
        Assert.Contains("Branch", columns);
    }

    [Fact]
    public async Task 补丁不会动已有数据()
    {
        CreateLegacyDatabase();

        await using var db = new WorkbenchDbContext(Options());
        await SchemaUpgrader.UpgradeAsync(db);

        using var check = new SqliteConnection($"Data Source={_dbPath}");
        check.Open();
        using var read = check.CreateCommand();
        read.CommandText = "SELECT Title FROM tasks WHERE Id = '33333333-3333-3333-3333-333333333333';";
        Assert.Equal("老任务", (string?)read.ExecuteScalar());
    }

    [Fact]
    public async Task 老库会补齐后来新增的表()
    {
        CreateLegacyDatabase();

        await using var db = new WorkbenchDbContext(Options());
        await SchemaUpgrader.UpgradeAsync(db);

        foreach (var table in new[]
                 {
                     "milestones", "test_runs", "mcp_tool_settings",
                     "review_rounds", "review_items"
                 })
        {
            Assert.True(await TableExistsAsync(table), $"缺表: {table}");
        }
    }

    [Fact]
    public async Task 老库会补齐后来新增的列()
    {
        CreateLegacyDatabase();

        await using var db = new WorkbenchDbContext(Options());
        await SchemaUpgrader.UpgradeAsync(db);

        var taskColumns = await ReadColumnsAsync("tasks");
        Assert.Contains("Key", taskColumns);
        Assert.Contains("MilestoneId", taskColumns);
        Assert.Contains("Type", taskColumns);
        Assert.Contains("Branch", taskColumns);

        var projectColumns = await ReadColumnsAsync("projects");
        Assert.Contains("AllowedCommandsJson", projectColumns);
        Assert.Contains("AgentCommand", projectColumns);
        Assert.Contains("NotificationWebhookUrl", projectColumns);
        Assert.Contains("NotificationFormat", projectColumns);

        var approvalColumns = await ReadColumnsAsync("approvals");
        Assert.Contains("PayloadHash", approvalColumns);
    }

    [Fact]
    public async Task 升级后老库能正常查询()
    {
        CreateLegacyDatabase();

        await using (var db = new WorkbenchDbContext(Options()))
        {
            await SchemaUpgrader.UpgradeAsync(db);
        }

        // 升级前这三张表/列都不存在，一查就报 no such table / no such column
        await using var check = new WorkbenchDbContext(Options());

        var task = await check.Tasks.FirstOrDefaultAsync(t => t.Id == Guid.Parse("33333333-3333-3333-3333-333333333333"));
        Assert.NotNull(task);
        Assert.Equal("老任务", task!.Title);

        var project = await check.Projects.FirstOrDefaultAsync(p => p.Id == Guid.Parse("22222222-2222-2222-2222-222222222222"));
        Assert.NotNull(project);

        // 这两张表是升级后才建的
        Assert.Empty(await check.ReviewRounds.ToListAsync());
        Assert.Empty(await check.Milestones.ToListAsync());
    }

    [Fact]
    public async Task 升级会建出编号唯一索引()
    {
        CreateLegacyDatabase();

        await using var db = new WorkbenchDbContext(Options());
        await SchemaUpgrader.UpgradeAsync(db);

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_tasks_ProjectId_Key';";
        var count = Convert.ToInt64(await command.ExecuteScalarAsync());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task 解析器能认出模型里的每一张表()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<WorkbenchDbContext>().UseSqlite(connection).Options;
        await using var db = new WorkbenchDbContext(options);

        var model = SchemaUpgrader.ParseModel(db.Database.GenerateCreateScript());

        // 模型里的表一张都不能漏 —— 漏了就等于升级时不会建它
        var expected = db.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .Where(t => t is not null)
            .Select(t => t!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var actual = model.Tables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected, actual);
    }

    private async Task<HashSet<string>> ReadColumnsAsync(string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{table}\");";

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var connection = new SqliteConnection($"Data Source={_dbPath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
        command.Parameters.AddWithValue("@name", table);

        return Convert.ToInt64(await command.ExecuteScalarAsync()) > 0;
    }
}
