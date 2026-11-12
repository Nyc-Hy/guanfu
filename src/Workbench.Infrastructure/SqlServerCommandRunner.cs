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
/// </summary>
public interface ISqlCommandRunner
{
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

    public async Task<SqlQueryResult> QueryAsync(
        DbConnectionConfig config,
        string sql,
        int maxRows = 200,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var limit = Math.Clamp(maxRows, 1, 5000);

        await using var connection = new SqlConnection(BuildConnectionString(config));
        await connection.OpenAsync(cancellationToken);

        await using var command = new SqlCommand(sql, connection)
        {
            CommandTimeout = 30
        };

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<string>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(reader.GetName(i));
        }

        var rows = new List<IReadOnlyList<string?>>();
        var truncated = false;

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

        return new SqlQueryResult(columns, rows, truncated, limit, DateTimeOffset.UtcNow - started);
    }

    public async Task<SqlExecResult> ExecuteAsync(
        DbConnectionConfig config,
        string sql,
        CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;

        await using var connection = new SqlConnection(BuildConnectionString(config));
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

    private string BuildConnectionString(DbConnectionConfig config)
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

        if (!string.IsNullOrEmpty(config.EncryptedPassword))
        {
            builder.Password = _protector.Unprotect(config.EncryptedPassword);
        }

        return builder.ConnectionString;
    }
}
