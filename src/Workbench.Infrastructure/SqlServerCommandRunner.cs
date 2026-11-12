using System.Data;
using Microsoft.Data.SqlClient;
using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Infrastructure;

public sealed record SqlQueryResult(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    bool Truncated,
    int RowLimit,
    TimeSpan Duration);

public sealed record SqlExecResult(int RowsAffected, TimeSpan Duration);

/// <summary>列信息。</summary>
public sealed record DbColumnInfo(
    string Name,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey,
    int? MaxLength);

/// <summary>表信息。</summary>
public sealed record DbTableInfo(string Schema, string Table, IReadOnlyList<DbColumnInfo> Columns);

/// <summary>库结构快照。</summary>
public sealed record DbSchemaResult(IReadOnlyList<DbTableInfo> Tables);

/// <summary>
/// SQL Server 命令执行器。只做「执行」，不含审批策略——
/// 策略由上层的 MCP 工具层负责（读直通、写进审批）。
/// 但「读」这一侧在基础设施层是**物理只读**的：ADO.NET 的 ExecuteReader 对
/// DELETE / UPDATE / DROP 照样真正执行，光在工具层做判断不够。
/// </summary>
public interface ISqlCommandRunner
{
    /// <summary>
    /// 判定一条 SQL 是读还是写。认不出来时返回 <see cref="SqlStatementKind.Unknown"/>
    /// —— 调用方据此决定要不要人工审批。
    /// </summary>
    SqlStatementAnalysis Analyze(string sql);

    Task<SqlQueryResult> QueryAsync(
        DbConnectionConfig config,
        string sql,
        int maxRows = 200,
        CancellationToken cancellationToken = default);

    Task<SqlExecResult> ExecuteAsync(
        DbConnectionConfig config,
        string sql,
        CancellationToken cancellationToken = default);

    /// <summary>读取库结构（表 / 列 / 主键）。schemaName 为空表示所有 schema。</summary>
    Task<DbSchemaResult> GetSchemaAsync(
        DbConnectionConfig config,
        string? schemaName = null,
        CancellationToken cancellationToken = default);
}

public sealed class SqlServerCommandRunner(ISecretProtector protector) : ISqlCommandRunner
{
    private readonly ISecretProtector _protector = protector;

    public SqlStatementAnalysis Analyze(string sql) => SqlStatementClassifier.Classify(sql);

    public async Task<SqlQueryResult> QueryAsync(
        DbConnectionConfig config,
        string sql,
        int maxRows = 200,
        CancellationToken cancellationToken = default)
    {
        // 防线一（唯一不依赖服务端配置的）：把 SQL 解析成 AST，判定它是不是只读。
        // 认不出来也算不通过 —— 那类语句一律交给人工审批，不在这里猜。
        var analysis = SqlStatementClassifier.Classify(sql);
        if (analysis.Kind != SqlStatementKind.Read)
        {
            throw new WorkbenchException(SqlStatementClassifier.BuildRejection(analysis, sql));
        }

        var started = DateTimeOffset.UtcNow;
        var limit = Math.Clamp(maxRows, 1, 5000);

        await using var connection = new SqlConnection(BuildConnectionString(config, readOnlyIntent: true));
        await connection.OpenAsync(cancellationToken);

        // 防线二：整段查询跑在显式事务里，跑完统一回滚。
        // 这里**不能**用 IsolationLevel.ReadOnly —— SQL Server 没有「只读隔离级别」
        // （T-SQL 只有 READ UNCOMMITTED / READ COMMITTED / REPEATABLE READ / SNAPSHOT /
        // SERIALIZABLE），SqlClient 收到 IsolationLevel.ReadOnly 会直接抛
        // ArgumentOutOfRangeException，连不上库。用「事务 + 回滚」代替：
        // DML / DDL 在 SQL Server 里是可回滚的，形态校验万一漏判也不会留下持久写入。
        await using var transaction =
            (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 30,
            Transaction = transaction
        };

        var columns = new List<string>();
        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;

        // reader 必须在回滚之前关掉：同一个连接上还挂着 reader 时发 ROLLBACK 会报错
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }

            while (await reader.ReadAsync(cancellationToken))
            {
                if (rows.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                var values = new string?[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    values[i] = await reader.IsDBNullAsync(i, cancellationToken)
                        ? null
                        : reader.GetValue(i).ToString();
                }

                rows.Add(values);
            }
        }

        await RollbackAsync(transaction);

        return new SqlQueryResult(columns, rows, truncated, limit, DateTimeOffset.UtcNow - started);
    }

    public async Task<SqlExecResult> ExecuteAsync(
        DbConnectionConfig config,
        string sql,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;

        await using var connection = new SqlConnection(BuildConnectionString(config, readOnlyIntent: false));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 60
        };

        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        return new SqlExecResult(affected, DateTimeOffset.UtcNow - started);
    }

    public async Task<DbSchemaResult> GetSchemaAsync(
        DbConnectionConfig config,
        string? schemaName = null,
        CancellationToken cancellationToken = default)
    {
        // 表与列
        const string columnsSql = """
            SELECT t.TABLE_SCHEMA, t.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE,
                   c.IS_NULLABLE, c.CHARACTER_MAXIMUM_LENGTH, c.ORDINAL_POSITION
            FROM INFORMATION_SCHEMA.TABLES t
            JOIN INFORMATION_SCHEMA.COLUMNS c
              ON c.TABLE_SCHEMA = t.TABLE_SCHEMA AND c.TABLE_NAME = t.TABLE_NAME
            WHERE t.TABLE_TYPE = 'BASE TABLE'
            ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME, c.ORDINAL_POSITION
            """;

        // 主键列
        const string primaryKeySql = """
            SELECT k.TABLE_SCHEMA, k.TABLE_NAME, k.COLUMN_NAME
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
            WHERE k.CONSTRAINT_NAME IN (
                SELECT CONSTRAINT_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS
                WHERE CONSTRAINT_TYPE = 'PRIMARY KEY')
            """;

        var columns = await QueryAsync(config, columnsSql, 5000, cancellationToken);
        var primaryKeys = await QueryAsync(config, primaryKeySql, 5000, cancellationToken);

        var keySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in primaryKeys.Rows)
        {
            if (row.Count >= 3 && row[0] is not null && row[1] is not null && row[2] is not null)
            {
                keySet.Add($"{row[0]}.{row[1]}.{row[2]}");
            }
        }

        var tables = new List<DbTableInfo>();
        var byKey = new Dictionary<string, DbTableInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in columns.Rows)
        {
            if (row.Count < 7)
            {
                continue;
            }

            var schema = row[0] ?? string.Empty;
            var table = row[1] ?? string.Empty;
            var columnName = row[2] ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(schemaName) &&
                !string.Equals(schema, schemaName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var mapKey = $"{schema}.{table}";
            if (!byKey.TryGetValue(mapKey, out var tableInfo))
            {
                tableInfo = new DbTableInfo(schema, table, new List<DbColumnInfo>());
                byKey[mapKey] = tableInfo;
                tables.Add(tableInfo);
            }

            var maxLength = int.TryParse(row[5], out var parsedMax) ? parsedMax : (int?)null;
            var isPrimaryKey = keySet.Contains($"{schema}.{table}.{columnName}");

            ((List<DbColumnInfo>)tableInfo.Columns).Add(new DbColumnInfo(
                columnName,
                row[3] ?? "unknown",
                string.Equals(row[4], "YES", StringComparison.OrdinalIgnoreCase),
                isPrimaryKey,
                maxLength));
        }

        return new DbSchemaResult(tables);
    }

    /// <summary>
    /// 只读通道的拒绝消息。
    ///
    /// 要说清两件事：**为什么拒**、**接下来该干什么**。
    /// agent 拿到「请改用 db_execute」才知道该走哪条路；只说「不允许」它只会瞎试。
    /// </summary>
    private string BuildConnectionString(DbConnectionConfig config, bool readOnlyIntent = false)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = config.Server,
            InitialCatalog = config.Database,
            UserID = config.UserId,
            TrustServerCertificate = config.TrustServerCertificate,
            ApplicationName = "Workbench",
            ConnectTimeout = 15,
            MultipleActiveResultSets = false
        };

        // 只读意图：库在 AG 里时连接会被路由到只读副本，服务端直接拒写；
        // 不在 AG 里时服务端会忽略这个参数（不影响连通性）。
        // 写路径不能带这个意图 —— 写操作必须落在主副本上。
        if (readOnlyIntent)
        {
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;
        }

        if (!string.IsNullOrEmpty(config.EncryptedPassword))
        {
            builder.Password = _protector.Unprotect(config.EncryptedPassword);
        }

        return builder.ConnectionString;
    }

    /// <summary>
    /// 只读查询结束后把事务收掉。用 CancellationToken.None —— 请求被取消时也要回滚。
    /// </summary>
    private static async Task RollbackAsync(SqlTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
        catch (InvalidOperationException)
        {
            // 服务器已经中止了这个事务（语句报错等），回滚不了也无所谓
        }
    }
}
