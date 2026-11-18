using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Guanfu.Infrastructure;

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
/// 三步（整体包在一个事务里，中途失败全部回滚，不留半截 schema）：
/// 1. 缺表 → 按 EF 生成的建表语句建（脚本顺序已经是依赖顺序）；
/// 2. 缺列 → 对着模型逐列比对，缺哪个补哪个（NOT NULL 列按**属性的 CLR 类型**给合法默认值）；
/// 3. 缺索引 / 索引定义漂移 → 建或重建（唯一索引撞上重复数据时跳过并记警告）。
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
        GuanfuDbContext db,
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

        // WAL + busy_timeout 必须在事务外执行（journal_mode 不能在事务里改）。
        // 读写不再互相阻塞，备份 VACUUM INTO 期间的写也不至于立刻 SQLITE_BUSY。
        await SqlitePragmas.ApplyAsync(sqlite, cancellationToken);

        // 一切以模型为准
        var createScript = db.Database.GenerateCreateScript();
        var model = SchemaModel.Parse(createScript, db.Model);

        var createdTables = new List<string>();
        var addedColumns = new List<string>();
        var createdIndexes = new List<string>();
        var warnings = new List<string>();

        // 升级包事务：建表/补列/建索引要么全成，要么全回滚。
        // 以前逐条自动提交，中途失败会留下「补了一半」的 schema，下次启动接着错。
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

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

        // ---- 3) 缺的索引 / 定义漂移的索引 ----
        foreach (var index in model.Indexes)
        {
            var (exists, existingSql) = await GetIndexAsync(sqlite, index.Name, cancellationToken);

            if (exists && (existingSql is null || DefinitionMatches(existingSql, index.CreateSql)))
            {
                // 名字在且定义一致 → 不动。existingSql 为 null 的是 SQLite 自动索引
                // （UNIQUE 约束生成的 sqlite_autoindex_*），删不得也比不了，跳过。
                continue;
            }

            // 名字在但定义不同（典型：老库的非唯一索引要升级成唯一）→ 重建。
            // 用 savepoint 包住：重建失败（比如唯一索引撞上老库重复数据）就回滚到
            // savepoint，**旧索引原样保留**，记一条警告让人处理 ——
            // 不静默改数据，也不崩启动。
            var savepoint = "wb_idx_" + Guid.NewGuid().ToString("N")[..12];
            await ExecuteAsync(sqlite, $"SAVEPOINT \"{savepoint}\";", cancellationToken);

            try
            {
                if (exists)
                {
                    await ExecuteAsync(sqlite, $"DROP INDEX \"{index.Name}\";", cancellationToken);
                }

                await ExecuteAsync(sqlite, index.CreateSql, cancellationToken);
                await ExecuteAsync(sqlite, $"RELEASE \"{savepoint}\";", cancellationToken);
                createdIndexes.Add(index.Name);
            }
            catch (SqliteException) when (index.IsUnique)
            {
                await ExecuteAsync(sqlite, $"ROLLBACK TO \"{savepoint}\";", cancellationToken);
                await ExecuteAsync(sqlite, $"RELEASE \"{savepoint}\";", cancellationToken);

                // 多半是老库里已经有重号。编号已经被 PR 和 agent 引用了，
                // 不能自动改 —— 只能把问题摆出来让人处理。
                warnings.Add(
                    $"唯一索引 {index.Name} 建不了：{index.Table} 里大概有重复值。" +
                    "请手工清理重号后重启。");
            }
        }

        var report = new UpgradeReport(createdTables, addedColumns, createdIndexes, warnings);

        // 版本记录放在最后 —— 表结构对齐了才能写进去（与结构变更同事务）
        await RecordVersionAsync(db, Fingerprint(createScript), report, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

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
        GuanfuDbContext db,
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

    /// <summary>索引是否存在，以及它的定义 SQL（自动索引的定义是 null）。</summary>
    private static async Task<(bool Exists, string? Sql)> GetIndexAsync(
        SqliteConnection connection, string index, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sql FROM sqlite_master WHERE type='index' AND name=@name;";
        command.Parameters.AddWithValue("@name", index);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? (false, null) : (true, result as string);
    }

    /// <summary>
    /// 索引定义是否一致：压掉空白差异、去掉结尾分号后忽略大小写比较。
    /// 只按名字比对会漏掉「同名但定义不同」的漂移（非唯一 → 唯一就是这么漏的）。
    /// </summary>
    internal static bool DefinitionMatches(string existingSql, string modelSql)
    {
        static string Normalize(string sql)
        {
            var collapsed = string.Join(' ', sql.Split(
                (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return collapsed.TrimEnd(';');
        }

        return string.Equals(Normalize(existingSql), Normalize(modelSql), StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// 解析建表脚本。<paramref name="model"/> 给出时，补列的默认值按属性的
        /// **CLR 类型**（经值转换器折算到存储侧）来定 —— 光看 SQL 文本的 TEXT/INTEGER
        /// 会把 DateTimeOffset 列补成 ''，老行一读就抛 FormatException。
        /// </summary>
        public static SchemaModel Parse(string script, IModel? model = null)
        {
            var tables = new List<TableInfo>();
            var indexes = new List<IndexInfo>();
            var properties = model is null ? null : BuildPropertyLookup(model);

            foreach (Match match in TableRegex.Matches(script))
            {
                var name = match.Groups["name"].Value;
                var body = match.Groups["body"].Value;

                tables.Add(new TableInfo(
                    name,
                    $"CREATE TABLE \"{name}\" ({body});",
                    ParseColumns(name, body, properties)));
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

        /// <summary>(表名.列名) → 属性。列名大小写不敏感（SQLite 的口径）。</summary>
        private static Dictionary<string, IProperty> BuildPropertyLookup(IModel model)
        {
            var lookup = new Dictionary<string, IProperty>(StringComparer.OrdinalIgnoreCase);

            foreach (var entityType in model.GetEntityTypes())
            {
                var tableName = entityType.GetTableName();
                if (tableName is null)
                {
                    continue;
                }

                var storeObject = StoreObjectIdentifier.Table(tableName, entityType.GetSchema());

                foreach (var property in entityType.GetProperties())
                {
                    var columnName = property.GetColumnName(storeObject);
                    if (columnName is not null)
                    {
                        lookup[$"{tableName}.{columnName}"] = property;
                    }
                }
            }

            return lookup;
        }

        private static IReadOnlyList<ColumnInfo> ParseColumns(
            string tableName, string body, Dictionary<string, IProperty>? properties)
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

                IProperty? property = null;
                properties?.TryGetValue($"{tableName}.{name}", out property);

                columns.Add(new ColumnInfo(name, BuildAlterDefinition(name, rest, property)));
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
        internal static string BuildAlterDefinition(string name, string rest, IProperty? property)
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
                : DefaultFor(type, property);

            return $"\"{name}\" {type} NOT NULL DEFAULT {defaultValue}";
        }

        /// <summary>
        /// 取出 DEFAULT 后面的完整字面量。
        /// 不能按首个空格截断 —— 多词默认值（'hello world'、(CURRENT_TIMESTAMP)）
        /// 会被截成 'hello / (CURRENT_TIMESTAMP，拼出坏 SQL。
        /// </summary>
        internal static string ExtractDefault(string rest)
        {
            var index = rest.IndexOf("DEFAULT ", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return "0";
            }

            var tail = rest[(index + "DEFAULT ".Length)..].TrimStart();
            if (tail.Length == 0)
            {
                return "0";
            }

            if (tail[0] == '\'')
            {
                // 引号字符串：读到闭合引号为止（'' 是转义的引号，不算闭合）
                var i = 1;
                while (i < tail.Length)
                {
                    if (tail[i] == '\'')
                    {
                        if (i + 1 < tail.Length && tail[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }

                        return tail[..(i + 1)];
                    }

                    i++;
                }

                return tail;
            }

            if (tail[0] == '(')
            {
                // 括号表达式：按配平取整段
                var depth = 0;
                for (var i = 0; i < tail.Length; i++)
                {
                    if (tail[i] == '(')
                    {
                        depth++;
                    }
                    else if (tail[i] == ')')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            return tail[..(i + 1)];
                        }
                    }
                }

                return tail;
            }

            var end = tail.IndexOf(' ');
            return end < 0 ? tail : tail[..end];
        }

        /// <summary>
        /// NOT NULL 补列的默认值。**优先按属性的 CLR 类型**（经值转换器折算到存储侧）：
        /// DateTimeOffset/DateTime → '0001-01-01 00:00:00'（EF SQLite 的 DateTime 存储格式，
        /// 与 UtcDateTimeOffsetConverter 落库的文本一致，老行读回来不抛 FormatException）、
        /// 枚举 → 0（配了字符串转换器的给转换后的默认值，如 'Draft'）、
        /// Guid → 全零、bool → 0、数值 → 0、字符串 → ''。
        /// 拿不到属性时退回按 SQL 类型给（老口径，只对纯数值列安全）。
        /// </summary>
        private static string DefaultFor(string sqlType, IProperty? property)
        {
            if (property is not null)
            {
                var literal = DefaultLiteralFor(property);
                if (literal is not null)
                {
                    return literal;
                }
            }

            return sqlType switch
            {
                "INTEGER" => "0",
                "REAL" => "0",
                "BLOB" => "X''",
                _ => "''"
            };
        }

        private static string? DefaultLiteralFor(IProperty property)
        {
            var clrType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
            object? value = clrType.IsValueType ? Activator.CreateInstance(clrType) : null;

            // 值转换器（枚举→string、DateTimeOffset→DateTime 等）：
            // 默认值要折算到**存储侧**再定字面量，否则 TEXT 列会补进数字 0。
            // 注意 HasConversion<string>() 这类配置不落在 GetValueConverter() 上
            // （它返回 null），真正的转换器在类型映射里 —— 两处都取，映射优先兜底。
            var converter = property.GetValueConverter() ?? property.GetTypeMapping().Converter;
            if (converter is not null)
            {
                try
                {
                    value = converter.ConvertToProvider(value);
                }
                catch
                {
                    return null;  // 转换不了就退回 SQL 类型口径
                }
            }

            return value switch
            {
                null => "''",
                string s => Quote(s),
                bool b => b ? "1" : "0",
                DateTime dt => Quote(dt.ToString(DateTimeFormat, CultureInfo.InvariantCulture)),
                DateTimeOffset dto => Quote(dto.UtcDateTime.ToString(DateTimeFormat, CultureInfo.InvariantCulture)),
                Guid g => Quote(g.ToString()),
                byte[] => "X''",
                TimeSpan ts => Quote(ts.ToString("c", CultureInfo.InvariantCulture)),
                Enum => "0",
                char c => Quote(c.ToString()),
                sbyte or byte or short or ushort or int or uint or long or ulong
                    or float or double or decimal => "0",
                _ => null
            };
        }

        /// <summary>EF Core SQLite 的 DateTime 文本格式（SqliteDateTimeTypeMapping）。</summary>
        private const string DateTimeFormat = "yyyy-MM-dd HH:mm:ss.FFFFFFF";

        private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
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
