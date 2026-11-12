using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Workbench.Infrastructure;

/// <summary>
/// 轻量 schema 升级。
///
/// 这个项目没用 EF Migrations（`EnsureCreated` 建表，够简单），但 SQLite 的
/// `EnsureCreated` **不会给已存在的库补表，也不会给已存在的表补列** ——
/// 库已经存在时它整个是 no-op。所以升级后老库缺表缺列，
/// 查出来直接报 "no such table" / "no such column"。
///
/// 这里的做法：**一切从 EF 模型推出来**，不维护手写清单。
/// 手写清单必然会漏 —— 以前就漏过：补列清单里只有 tasks 表的两列，
/// 结果 projects 的四个列、tasks 的 Key / MilestoneId 加上五张新表全都没补上，
/// 老库一升级就炸。
///
/// 三步：
/// 1. 缺表 → 按 EF 生成的建表语句建（脚本顺序已经是依赖顺序）；
/// 2. 缺列 → 对着模型逐列比对，缺哪个补哪个；
/// 3. 缺索引 → 建。
///
/// 加了字段/表/索引都不用改这里 —— 模型变了，下次启动自动跟上。
/// </summary>
public static class SchemaUpgrader
{
    /// <summary>升级过程中值得让人知道的事（重复编号、跳过的索引等）。</summary>
    public sealed record UpgradeReport(
        IReadOnlyList<string> CreatedTables,
        IReadOnlyList<string> AddedColumns,
        IReadOnlyList<string> CreatedIndexes,
        IReadOnlyList<string> Warnings)
    {
        public static UpgradeReport Empty { get; } =
            new([], [], [], []);
    }

    public static async Task<UpgradeReport> UpgradeAsync(
        WorkbenchDbContext db,
        CancellationToken cancellationToken = default)
    {        var connection = db.Database.GetDbConnection();

        if (connection is not SqliteConnection sqlite)
        {
            // 只支持 SQLite；换别的存储要另外写迁移
            return UpgradeReport.Empty;
        }

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        // 一切以模型为准
        var createScript = db.Database.GenerateCreateScript();
        var model = SchemaModel.Parse(createScript);

        var createdTables = new List<string>();
        var addedColumns = new List<string>();
        var createdIndexes = new List<string>();
        var warnings = new List<string>();

        // ---- 1) 缺的表 ----
        foreach (var table in model.Tables)
        {
            if (await TableExistsAsync(sqlite, table.Name, cancellationToken))
            {
                continue;
            }

            await ExecuteAsync(sqlite, table.CreateSql, cancellationToken);
            createdTables.Add(table.Name);
        }

        // ---- 2) 缺的列 ----
        foreach (var table in model.Tables)
        {
            if (!await TableExistsAsync(sqlite, table.Name, cancellationToken))
            {
                continue;
            }

            var existing = await GetColumnsAsync(sqlite, table.Name, cancellationToken);

            foreach (var column in table.Columns)
            {
                if (existing.Contains(column.Name))
                {
                    continue;
                }

                await ExecuteAsync(
                    sqlite,
                    $"ALTER TABLE \"{table.Name}\" ADD COLUMN {column.AlterDefinition};",
                    cancellationToken);

                addedColumns.Add($"{table.Name}.{column.Name}");
            }
        }

        // ---- 3) 缺的索引 ----
        foreach (var index in model.Indexes)
        {
            if (await IndexExistsAsync(sqlite, index.Name, cancellationToken))
            {
                continue;
            }

            try
            {
                await ExecuteAsync(sqlite, index.CreateSql, cancellationToken);
                createdIndexes.Add(index.Name);
            }
            catch (SqliteException) when (index.IsUnique)
            {
                // 多半是老库里已经有重号。编号已经被 PR 和 agent 引用了，
                // 不能自动改 —— 只能把问题摆出来让人处理。
                warnings.Add(
                    $"唯一索引 {index.Name} 建不了：{index.Table} 里大概有重复值。" +
                    "请手工清理重号后重启。");
            }
        }

        var report = new UpgradeReport(createdTables, addedColumns, createdIndexes, warnings);

        // 版本记录放在最后 —— 表结构对齐了才能写进去
        await RecordVersionAsync(db, Fingerprint(createScript), report, cancellationToken);

        return report;
    }

    /// <summary>
    /// 记一行 schema 版本。
    ///
    /// 指纹取 EF 建表脚本的哈希：模型不变指纹就不变，
    /// 所以它精确对应「当前库的结构」。没有这张表的话，
    /// 排查老库问题时只能靠猜「这库到底升级到哪了」。
    /// </summary>
    internal static async Task RecordVersionAsync(
        WorkbenchDbContext db,
        string fingerprint,
        UpgradeReport report,
        CancellationToken cancellationToken = default)
    {
        var latest = await db.SchemaVersions
            .AsNoTracking()
            .OrderByDescending(v => v.AppliedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latest is not null && latest.Fingerprint == fingerprint)
        {
            return;
        }

        var parts = new List<string>();
        if (report.CreatedTables.Count > 0)
        {
            parts.Add($"建表 {string.Join("、", report.CreatedTables)}");
        }

        if (report.AddedColumns.Count > 0)
        {
            parts.Add($"补列 {string.Join("、", report.AddedColumns)}");
        }

        if (report.CreatedIndexes.Count > 0)
        {
            parts.Add($"建索引 {string.Join("、", report.CreatedIndexes)}");
        }

        db.SchemaVersions.Add(new Domain.SchemaVersion
        {
            Fingerprint = fingerprint,
            AppliedAt = DateTimeOffset.UtcNow,
            Note = parts.Count == 0 ? "结构对齐（无变更）" : string.Join("；", parts)
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>模型指纹：EF 建表脚本的哈希前 16 位。</summary>
    internal static string Fingerprint(string createScript)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(createScript));

        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>解析 EF 生成的建表脚本。测试用它核对「模型里的表一张都不漏」。</summary>
    internal static SchemaModel ParseModel(string script) => SchemaModel.Parse(script);

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TableExistsAsync(
        SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
        command.Parameters.AddWithValue("@name", table);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async Task<bool> IndexExistsAsync(
        SqliteConnection connection, string index, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name=@name;";
        command.Parameters.AddWithValue("@name", index);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) > 0;
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
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    // ---- 模型解析 ------------------------------------------------------------

    /// <summary>从 EF 生成的建表脚本里解析出表、列与索引。</summary>
    internal sealed record ColumnInfo(string Name, string AlterDefinition);

    internal sealed record TableInfo(string Name, string CreateSql, IReadOnlyList<ColumnInfo> Columns);

    internal sealed record IndexInfo(string Name, string Table, string CreateSql, bool IsUnique);

    internal sealed record SchemaModel(IReadOnlyList<TableInfo> Tables, IReadOnlyList<IndexInfo> Indexes)
    {
        private static readonly Regex TableRegex = new(
            @"CREATE TABLE\s+""(?<name>[^""]+)""\s*\((?<body>.*?)\);",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IndexRegex = new(
            @"CREATE\s+(?<unique>UNIQUE\s+)?INDEX\s+""(?<name>[^""]+)""\s+ON\s+""(?<table>[^""]+)""\s*\((?<cols>.*?)\);",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static SchemaModel Parse(string script)
        {
            var tables = new List<TableInfo>();
            var indexes = new List<IndexInfo>();

            foreach (Match match in TableRegex.Matches(script))
            {
                var name = match.Groups["name"].Value;
                var body = match.Groups["body"].Value;

                tables.Add(new TableInfo(
                    name,
                    $"CREATE TABLE \"{name}\" ({body});",
                    ParseColumns(body)));
            }

            foreach (Match match in IndexRegex.Matches(script))
            {
                var name = match.Groups["name"].Value;

                indexes.Add(new IndexInfo(
                    name,
                    match.Groups["table"].Value,
                    match.Value,
                    match.Groups["unique"].Success));
            }

            return new SchemaModel(tables, indexes);
        }

        private static IReadOnlyList<ColumnInfo> ParseColumns(string body)
        {
            var columns = new List<ColumnInfo>();

            foreach (var part in SplitTopLevel(body))
            {
                var trimmed = part.Trim();

                // 表级约束（CONSTRAINT ... / FOREIGN KEY ...）不是列，跳过
                if (!trimmed.StartsWith('"'))
                {
                    continue;
                }

                var close = trimmed.IndexOf('"', 1);
                if (close < 0)
                {
                    continue;
                }

                var name = trimmed[1..close];
                var rest = trimmed[(close + 1)..].Trim();

                columns.Add(new ColumnInfo(name, BuildAlterDefinition(name, rest)));
            }

            return columns;
        }

        /// <summary>
        /// 把 EF 的列定义改写成能用于 ALTER TABLE ADD COLUMN 的形式。
        ///
        /// SQLite 不允许 ADD COLUMN 带 PRIMARY KEY / UNIQUE；
        /// NOT NULL 的列还必须给默认值，否则加不进去。
        /// EF 生成的脚本里没有 DEFAULT（CLR 默认值不进 DDL），所以要自己补。
        /// </summary>
        private static string BuildAlterDefinition(string name, string rest)
        {
            var type = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.ToUpperInvariant() ?? "TEXT";

            var required = rest.Contains("NOT NULL", StringComparison.OrdinalIgnoreCase);

            if (!required)
            {
                return $"\"{name}\" {type} NULL";
            }

            // 已经有 DEFAULT 就用原来的，没有就按类型补一个 —— NOT NULL 没默认值加不进去
            var defaultValue = rest.Contains("DEFAULT ", StringComparison.OrdinalIgnoreCase)
                ? ExtractDefault(rest)
                : DefaultFor(type);

            return $"\"{name}\" {type} NOT NULL DEFAULT {defaultValue}";
        }

        private static string ExtractDefault(string rest)
        {
            var index = rest.IndexOf("DEFAULT ", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return "0";
            }

            var tail = rest[(index + "DEFAULT ".Length)..].Trim();

            var end = tail.IndexOf(' ');
            return end < 0 ? tail : tail[..end];
        }

        private static string DefaultFor(string type) => type switch
        {
            "INTEGER" => "0",
            "REAL" => "0",
            "BLOB" => "X''",
            _ => "''"
        };
    }

    /// <summary>按顶层逗号切分（括号里、字符串里的逗号不算）。</summary>
    private static IEnumerable<string> SplitTopLevel(string body)
    {
        var depth = 0;
        var inQuote = false;
        var quoteChar = '\0';
        var start = 0;

        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];

            if (inQuote)
            {
                if (c == quoteChar)
                {
                    inQuote = false;
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                inQuote = true;
                quoteChar = c;
                continue;
            }

            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                yield return body[start..i];
                start = i + 1;
            }
        }

        if (start < body.Length)
        {
            yield return body[start..];
        }
    }
}
