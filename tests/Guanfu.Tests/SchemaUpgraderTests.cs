using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Guanfu.Infrastructure;

namespace Guanfu.Tests;

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
                "ManagedByGuanfu" INTEGER NOT NULL,
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
            INSERT INTO projects (Id, WorkspaceId, Name, ManagedByGuanfu, CreatedAt, UpdatedAt)
                VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', '老项目', 0, '2026-01-01 00:00:00', '2026-01-01 00:00:00');
            INSERT INTO tasks (Id, ProjectId, Title, Status, Weight, CreatedAt, UpdatedAt)
                VALUES ('33333333-3333-3333-3333-333333333333', '22222222-2222-2222-2222-222222222222', '老任务', 'Draft', 1, '2026-01-01 00:00:00', '2026-01-01 00:00:00');
            """;
        insert.ExecuteNonQuery();
    }

    private DbContextOptions<GuanfuDbContext> Options() =>
        new DbContextOptionsBuilder<GuanfuDbContext>()
            .UseSqlite($"Data Source={_dbPath}")
            .Options;

    [Fact]
    public async Task 老库会被补上类型与分支两列()
    {
        CreateLegacyDatabase();

        await using var db = new GuanfuDbContext(Options());
        await SchemaUpgrader.UpgradeAsync(db);

        var columns = await ReadColumnsAsync("tasks");

        Assert.Contains("Type", columns);
        Assert.Contains("Branch", columns);
    }

    [Fact]
    public async Task 重复执行不会报错()
    {
        CreateLegacyDatabase();

        await using var db = new GuanfuDbContext(Options());
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

        await using var db = new GuanfuDbContext(Options());
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

        await using var db = new GuanfuDbContext(Options());
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

        await using var db = new GuanfuDbContext(Options());
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

        await using (var db = new GuanfuDbContext(Options()))
        {
            await SchemaUpgrader.UpgradeAsync(db);
        }

        // 升级前这三张表/列都不存在，一查就报 no such table / no such column
        await using var check = new GuanfuDbContext(Options());

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

        await using var db = new GuanfuDbContext(Options());
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

        var options = new DbContextOptionsBuilder<GuanfuDbContext>().UseSqlite(connection).Options;
        await using var db = new GuanfuDbContext(options);

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

    // ---------- 补列默认值（按 CLR 类型，不毒化老数据） ----------

    /// <summary>
    /// 在老库基础上造「缺列」的 milestones / plans：
    /// 都缺 NOT NULL 的 CreatedAt（DateTimeOffset）。以前按 SQL 文本补 DEFAULT ''，
    /// 老行一读就抛 FormatException。
    /// </summary>
    private void CreateLegacyPlansAndMilestones(bool duplicatePlanVersions = false)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE milestones (
                "Id" TEXT NOT NULL CONSTRAINT "PK_milestones" PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Order" INTEGER NOT NULL
            );

            CREATE TABLE plans (
                "Id" TEXT NOT NULL CONSTRAINT "PK_plans" PRIMARY KEY,
                "TaskId" TEXT NOT NULL,
                "Version" INTEGER NOT NULL
            );

            INSERT INTO milestones (Id, ProjectId, Name, Kind, "Order")
                VALUES ('44444444-4444-4444-4444-444444444444', '22222222-2222-2222-2222-222222222222', '老里程碑', 'Release', 1);

            INSERT INTO plans (Id, TaskId, Version)
                VALUES ('55555555-5555-5555-5555-555555555555', '33333333-3333-3333-3333-333333333333', 1);
            """;
        command.ExecuteNonQuery();

        if (duplicatePlanVersions)
        {
            using var dup = connection.CreateCommand();
            dup.CommandText = """
                INSERT INTO plans (Id, TaskId, Version)
                    VALUES ('66666666-6666-6666-6666-666666666666', '33333333-3333-3333-3333-333333333333', 1);
                CREATE INDEX "IX_plans_TaskId_Version" ON "plans" ("TaskId", "Version");
                """;
            dup.ExecuteNonQuery();
        }
    }

    [Fact]
    public async Task 补列默认值按CLR类型_老行读得回来()
    {
        CreateLegacyDatabase();
        CreateLegacyPlansAndMilestones();

        await using (var db = new GuanfuDbContext(Options()))
        {
            await SchemaUpgrader.UpgradeAsync(db);
        }

        await using var check = new GuanfuDbContext(Options());

        // DateTimeOffset 列补的默认值必须是合法时间文本（'0001-01-01 00:00:00'），
        // 不能是 '' —— 否则这里直接抛 FormatException
        var milestone = await check.Milestones.FirstAsync(m => m.Id == Guid.Parse("44444444-4444-4444-4444-444444444444"));
        Assert.Equal(default(DateTimeOffset), milestone.CreatedAt);
        Assert.Equal("老里程碑", milestone.Name);

        var plan = await check.Plans.FirstAsync(p => p.Id == Guid.Parse("55555555-5555-5555-5555-555555555555"));
        Assert.Equal(default(DateTimeOffset), plan.CreatedAt);
        Assert.Equal(1, plan.Version);
    }

    [Fact]
    public async Task 各类型补列默认值都合法()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GuanfuDbContext>().UseSqlite(connection).Options;
        await using var db = new GuanfuDbContext(options);

        var model = db.Model;
        Microsoft.EntityFrameworkCore.Metadata.IProperty Prop<T>(string name) =>
            model.FindEntityType(typeof(T))!.FindProperty(name)!;

        // DateTimeOffset → EF SQLite 的 DateTime 存储格式（与 UtcDateTimeOffsetConverter 一致）
        Assert.Equal(
            "\"CreatedAt\" TEXT NOT NULL DEFAULT '0001-01-01 00:00:00'",
            SchemaUpgrader.SchemaModel.BuildAlterDefinition("CreatedAt", "TEXT NOT NULL", Prop<Domain.TaskItem>("CreatedAt")));

        // Guid → 全零
        Assert.Equal(
            "\"Id\" TEXT NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'",
            SchemaUpgrader.SchemaModel.BuildAlterDefinition("Id", "TEXT NOT NULL", Prop<Domain.TaskItem>("Id")));

        // 枚举配了字符串转换器 → 转换后的默认值（不是数字 0）
        Assert.Equal(
            "\"Status\" TEXT NOT NULL DEFAULT 'Draft'",
            SchemaUpgrader.SchemaModel.BuildAlterDefinition("Status", "TEXT NOT NULL", Prop<Domain.TaskItem>("Status")));

        // 枚举没配转换器 → 0
        Assert.Equal(
            "\"Type\" INTEGER NOT NULL DEFAULT 0",
            SchemaUpgrader.SchemaModel.BuildAlterDefinition("Type", "INTEGER NOT NULL", Prop<Domain.TaskItem>("Type")));

        // bool → 0
        Assert.Equal(
            "\"IsArchived\" INTEGER NOT NULL DEFAULT 0",
            SchemaUpgrader.SchemaModel.BuildAlterDefinition("IsArchived", "INTEGER NOT NULL", Prop<Domain.TaskItem>("IsArchived")));

        // 数值 → 0
        Assert.Equal(
            "\"Weight\" REAL NOT NULL DEFAULT 0",
            SchemaUpgrader.SchemaModel.BuildAlterDefinition("Weight", "REAL NOT NULL", Prop<Domain.TaskItem>("Weight")));

        // 字符串 → ''
        Assert.Equal(
            "\"Title\" TEXT NOT NULL DEFAULT ''",
            SchemaUpgrader.SchemaModel.BuildAlterDefinition("Title", "TEXT NOT NULL", Prop<Domain.TaskItem>("Title")));
    }

    [Theory]
    [InlineData("TEXT NOT NULL DEFAULT 'hello world'", "'hello world'")]
    [InlineData("TEXT NOT NULL DEFAULT 'it''s ok'", "'it''s ok'")]
    [InlineData("TEXT NOT NULL DEFAULT (CURRENT_TIMESTAMP)", "(CURRENT_TIMESTAMP)")]
    [InlineData("INTEGER NOT NULL DEFAULT 0", "0")]
    [InlineData("TEXT NOT NULL DEFAULT 'a b' ", "'a b'")]
    public void 多词默认值不被首个空格截断(string rest, string expected)
    {
        Assert.Equal(expected, SchemaUpgrader.SchemaModel.ExtractDefault(rest));
    }

    // ---------- Plans 唯一索引 ----------

    [Fact]
    public async Task 新库的Plans版本索引是唯一的()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GuanfuDbContext>().UseSqlite(connection).Options;
        await using var db = new GuanfuDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var workspace = new Domain.Workspace { Name = "w" };
        var project = new Domain.Project { Workspace = workspace, Name = "p" };
        var task = new Domain.TaskItem { Project = project, Title = "t" };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();

        db.Plans.Add(new Domain.Plan { TaskId = task.Id, Version = 1 });
        await db.SaveChangesAsync();

        // 同一任务同一版本再来一条 → 唯一索引必须拦下（版本重试的兜底）
        db.Plans.Add(new Domain.Plan { TaskId = task.Id, Version = 1 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task 唯一索引撞上老库重复数据_跳过并记警告不改数据()
    {
        CreateLegacyDatabase();
        CreateLegacyPlansAndMilestones(duplicatePlanVersions: true);

        await using var db = new GuanfuDbContext(Options());
        var report = await SchemaUpgrader.UpgradeAsync(db);

        // 警告要点名这条索引
        Assert.Contains(report.Warnings, w => w.Contains("IX_plans_TaskId_Version"));

        // 不崩启动、不静默改数据：两条重复计划都还在
        await using var check = new GuanfuDbContext(Options());
        Assert.Equal(2, await check.Plans.CountAsync());

        // 旧（非唯一）索引原样保留 —— savepoint 回滚了重建
        await using var conn = new SqliteConnection($"Data Source={_dbPath}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name='IX_plans_TaskId_Version';";
        var sql = (string?)await cmd.ExecuteScalarAsync();
        Assert.NotNull(sql);
        Assert.DoesNotContain("UNIQUE", sql);
    }

    [Fact]
    public async Task 无重复数据时老索引升级成唯一()
    {
        CreateLegacyDatabase();
        CreateLegacyPlansAndMilestones();  // 只有一条计划，但先建非唯一索引模拟老库

        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE INDEX \"IX_plans_TaskId_Version\" ON \"plans\" (\"TaskId\", \"Version\");";
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = new GuanfuDbContext(Options());
        var report = await SchemaUpgrader.UpgradeAsync(db);

        Assert.Contains("IX_plans_TaskId_Version", report.CreatedIndexes);
        Assert.Empty(report.Warnings);

        await using var check = new SqliteConnection($"Data Source={_dbPath}");
        await check.OpenAsync();
        await using var verify = check.CreateCommand();
        verify.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name='IX_plans_TaskId_Version';";
        var sql = (string?)await verify.ExecuteScalarAsync();
        Assert.NotNull(sql);
        Assert.Contains("UNIQUE", sql);
    }

    // ---------- 索引定义漂移 ----------

    [Fact]
    public async Task 同名索引定义不同会重建()
    {
        CreateLegacyDatabase();

        // 老库里 IX_tasks_Branch 建在了错误的列上（模拟定义漂移）
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE INDEX \"IX_tasks_Branch\" ON \"tasks\" (\"Title\");";
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = new GuanfuDbContext(Options());
        await SchemaUpgrader.UpgradeAsync(db);

        await using var check = new SqliteConnection($"Data Source={_dbPath}");
        await check.OpenAsync();
        await using var verify = check.CreateCommand();
        verify.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name='IX_tasks_Branch';";
        var sql = (string?)await verify.ExecuteScalarAsync();

        Assert.NotNull(sql);
        Assert.Contains("Branch", sql);
        Assert.DoesNotContain("Title", sql);
    }

    // ---------- WAL ----------

    [Fact]
    public async Task 升级会开启WAL()
    {
        CreateLegacyDatabase();

        await using var db = new GuanfuDbContext(Options());
        await SchemaUpgrader.UpgradeAsync(db);

        await using var check = new SqliteConnection($"Data Source={_dbPath}");
        await check.OpenAsync();
        await using var cmd = check.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)await cmd.ExecuteScalarAsync());
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
