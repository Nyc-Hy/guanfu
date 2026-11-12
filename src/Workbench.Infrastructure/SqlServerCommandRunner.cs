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
