using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Domain;

namespace Workbench.Application.Services;

/// <summary>
/// 连接探活。**应用层不依赖 Infrastructure**（规矩 18 的同一条边界），
/// 所以这里只定义「拿一份连接配置去连一下」的能力，
/// 由基础设施层用现有的 SQL 执行器适配进来（跑一条 SELECT 1）。
/// </summary>
public interface IDbConnectionProbe
{
    /// <summary>连得上就正常返回；连不上抛异常，异常信息会原样交给调用方。</summary>
    Task ProbeAsync(DbConnectionConfig config, CancellationToken cancellationToken = default);
}

public interface IDbConnectionService
{
    Task<IReadOnlyList<DbConnectionConfig>> ListAsync(Guid projectId, CancellationToken cancellationToken = default);

    Task<DbConnectionConfig?> GetAsync(Guid projectId, Guid? connectionId = null, CancellationToken cancellationToken = default);

    /// <summary>登记一个数据库连接。密码会被加密存储，不回显。</summary>
    Task<DbConnectionConfig> CreateAsync(
        Guid projectId,
        string name,
        string server,
        string database,
        string? userId,
        string? password,
        bool isDefault = true,
        CancellationToken cancellationToken = default,
        bool trustServerCertificate = true);

    /// <summary>
    /// 这次写操作要不要人工审批。
    ///
    /// 默认要。连接上配了「免审批写」的表才免 ——
    /// 那张表规则表（<see cref="DbTableRule"/>）以前一直在库里，却没人读它，
    /// 语义等于不存在。现在它真的管事了。
    /// </summary>
    Task<bool> RequiresApprovalAsync(
        Guid connectionId,
        string? tableName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 这张表允不允许读（<see cref="DbTableRule.AllowRead"/>）。
    ///
    /// AllowRead 以前只存不读，等于没有。db_query 是读直通的路径，
    /// 必须在这里问一句：规则明确写了「不许读」的表，读也要拦。
    /// 没有匹配规则时放行 —— 读直通是既有策略，规则表不是读的白名单。
    /// </summary>
    Task<bool> IsReadAllowedAsync(
        Guid connectionId,
        string? tableName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 试连一下（跑 SELECT 1）。配置存进去了不等于连得上 ——
    /// 以前要等到 agent 真的执行 SQL 才发现服务器名写错了。
    /// </summary>
    Task<(bool Ok, string? Error)> TestAsync(Guid connectionId, CancellationToken cancellationToken = default);

    /// <summary>列连接上的表级规则。</summary>
    Task<IReadOnlyList<DbTableRuleDto>> ListRulesAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default);

    /// <summary>加一条表级规则。</summary>
    Task<DbTableRuleDto> AddRuleAsync(
        Guid connectionId,
        string schemaName,
        string tableName,
        bool allowRead = true,
        bool writeRequiresApproval = true,
        CancellationToken cancellationToken = default);

    /// <summary>删一条表级规则。</summary>
    Task DeleteRuleAsync(Guid connectionId, Guid ruleId, CancellationToken cancellationToken = default);
}

/// <summary>表级规则。</summary>
public sealed record DbTableRuleDto(
    Guid Id,
    Guid ConnectionId,
    string SchemaName,
    string TableName,
    bool AllowRead,
    bool WriteRequiresApproval);

/// <param name="probe">
/// 连接探活器。Infrastructure 用 SQL 执行器适配进来（跑 SELECT 1）。
/// 可选是为了兼容还没注册适配器的宿主：这时 <see cref="TestAsync"/> 明确报「执行器未注册」，
/// 而不是假装连通。
/// </param>
public sealed class DbConnectionService(
    IAppDbContext db,
    ISecretProtector protector,
    IDbConnectionProbe? probe = null) : IDbConnectionService
{
    /// <summary>SQL Server 的默认 schema。规则和目标都没写 schema 时按它解释。</summary>
    private const string DefaultSchema = "dbo";

    public async Task<IReadOnlyList<DbConnectionConfig>> ListAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        return await db.DbConnections
            .AsNoTracking()
            .Where(c => c.ProjectId == projectId)
            .ToListAsync(cancellationToken);
    }

    public async Task<DbConnectionConfig> CreateAsync(
        Guid projectId,
        string name,
        string server,
        string database,
        string? userId,
        string? password,
        bool isDefault = true,
        CancellationToken cancellationToken = default,
        bool trustServerCertificate = true)
    {
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
        {
            throw new WorkbenchException("服务器与数据库名不能为空。");
        }

        var projectExists = await db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken);
        if (!projectExists)
        {
            throw new NotFoundException("项目", projectId);
        }

        if (isDefault)
        {
            var existingDefaults = await db.DbConnections
                .Where(c => c.ProjectId == projectId && c.IsDefault)
                .ToListAsync(cancellationToken);

            foreach (var item in existingDefaults)
            {
                item.IsDefault = false;
            }
        }

        var config = new DbConnectionConfig
        {
            ProjectId = projectId,
            Name = string.IsNullOrWhiteSpace(name) ? "default" : name.Trim(),
            Server = server.Trim(),
            Database = database.Trim(),
            UserId = userId,
            EncryptedPassword = string.IsNullOrEmpty(password) ? null : protector.Protect(password),
            IsDefault = isDefault,
            // 以前这里写死 true：自签证书能连，但生产上换成受信证书时没法收紧。
            // 默认仍 true（既有连接不受影响），需要严格校验的调用方显式传 false。
            TrustServerCertificate = trustServerCertificate
        };

        db.DbConnections.Add(config);
        await db.SaveChangesAsync(cancellationToken);

        return config;
    }

    public async Task<DbConnectionConfig?> GetAsync(
        Guid projectId,
        Guid? connectionId = null,
        CancellationToken cancellationToken = default)
    {
        var query = db.DbConnections.Where(c => c.ProjectId == projectId);

        query = connectionId is not null
            ? query.Where(c => c.Id == connectionId.Value)
            : query.Where(c => c.IsDefault);

        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> RequiresApprovalAsync(
        Guid connectionId,
        string? tableName,
        CancellationToken cancellationToken = default)
    {
        // 连目标表都认不出来 —— 一律要审批。认错了比多问一次糟糕得多。
        var rule = await FindRuleAsync(connectionId, tableName, cancellationToken);
        return rule is null || rule.WriteRequiresApproval;
    }

    public async Task<bool> IsReadAllowedAsync(
        Guid connectionId,
        string? tableName,
        CancellationToken cancellationToken = default)
    {
        // 读直通是既有策略：没有匹配规则时放行，规则表不是读的白名单。
        // 只有规则明确写了 AllowRead=false 的表才拦。
        var rule = await FindRuleAsync(connectionId, tableName, cancellationToken);
        return rule is null || rule.AllowRead;
    }

    public async Task<(bool Ok, string? Error)> TestAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var config = await db.DbConnections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == connectionId, cancellationToken);

        if (config is null)
        {
            throw new NotFoundException("数据库连接", connectionId);
        }

        if (probe is null)
        {
            return (false, "数据库执行器未注册，无法试连。请在宿主中注册 IDbConnectionProbe 适配器。");
        }

        try
        {
            await probe.ProbeAsync(config, cancellationToken);
            return (true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 按 schema + 表名找规则。**匹配语义**（以前只比表名，会跨 schema 误放行）：
    /// <list type="bullet">
    /// <item>规则写 <c>dbo.Orders</c> 只匹配 <c>dbo.Orders</c>；</item>
    /// <item>规则写裸表名 <c>Orders</c> 时按默认 schema <c>dbo</c> 解释，
    /// 只匹配 <c>dbo.Orders</c>，不再连带放行 <c>staging.Orders</c>；</item>
    /// <item>目标没写 schema 时同样按 <c>dbo</c> 解释。</item>
    /// </list>
    /// 找不到规则返回 null，由各调用方决定「没规则」的默认策略（写要审批 / 读放行）。
    /// </summary>
    private async Task<DbTableRule?> FindRuleAsync(
        Guid connectionId,
        string? tableName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return null;
        }

        var (schemaPart, targetTable) = Split(tableName);
        var targetSchema = schemaPart ?? DefaultSchema;

        var rules = await db.DbTableRules
            .AsNoTracking()
            .Where(r => r.ConnectionId == connectionId)
            .ToListAsync(cancellationToken);

        // 规则表可能把 schema 存在 SchemaName 里，也可能直接写进 TableName（dbo.Orders）。
        // 两种写法都归一到 (schema, table) 再比，大小写不敏感（SQL Server 标识符默认不区分大小写）。
        return rules.FirstOrDefault(r =>
        {
            var (ruleSchema, ruleTable) = Split(r.TableName);
            var schema = ruleSchema ?? NormalizeSchema(r.SchemaName);
            return string.Equals(schema, targetSchema, StringComparison.OrdinalIgnoreCase)
                && string.Equals(ruleTable, targetTable, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// 把 <c>[staging].[Orders]</c> / <c>dbo.Orders</c> / <c>Orders</c> 拆成 (schema, table)。
    /// 没写 schema 时 schema 返回 null，由调用方补默认值。
    /// 只做字符串拆分，不进 SQL（规矩 19：不在 Where 里写 C# 逻辑）。
    /// </summary>
    private static (string? Schema, string Table) Split(string value)
    {
        var parts = value.Trim()
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return (null, string.Empty);
        }

        var table = Unbracket(parts[^1]);
        // 三段式 db.schema.table 取倒数第二段当 schema；两段式取第一段。
        var schema = parts.Length >= 2 ? Unbracket(parts[^2]) : null;
        return (string.IsNullOrWhiteSpace(schema) ? null : schema, table);
    }

    private static string NormalizeSchema(string? schema)
        => string.IsNullOrWhiteSpace(schema) ? DefaultSchema : Unbracket(schema.Trim());

    /// <summary>去掉 SQL Server 标识符的方括号包裹。</summary>
    private static string Unbracket(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']'
            ? trimmed[1..^1]
            : trimmed;
    }

    /// <summary>连接配置 → DTO（不回显密码，只告诉调用方有没有配）。映射只有这一份。</summary>
    internal static DbConnectionConfigDto ToDto(DbConnectionConfig config) => new(
        config.Id,
        config.ProjectId,
        config.Name,
        config.Server,
        config.Database,
        config.UserId,
        config.IsDefault,
        HasPassword: !string.IsNullOrEmpty(config.EncryptedPassword),
        TrustServerCertificate: config.TrustServerCertificate);

    public async Task<IReadOnlyList<DbTableRuleDto>> ListRulesAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        return await db.DbTableRules
            .AsNoTracking()
            .Where(r => r.ConnectionId == connectionId)
            .OrderBy(r => r.SchemaName).ThenBy(r => r.TableName)
            .Select(r => new DbTableRuleDto(
                r.Id, r.ConnectionId, r.SchemaName, r.TableName, r.AllowRead, r.WriteRequiresApproval))
            .ToListAsync(cancellationToken);
    }

    public async Task<DbTableRuleDto> AddRuleAsync(
        Guid connectionId,
        string schemaName,
        string tableName,
        bool allowRead = true,
        bool writeRequiresApproval = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            throw new WorkbenchException("表名不能为空。");
        }

        var connectionExists = await db.DbConnections.AnyAsync(c => c.Id == connectionId, cancellationToken);
        if (!connectionExists)
        {
            throw new NotFoundException("数据库连接", connectionId);
        }

        var schema = string.IsNullOrWhiteSpace(schemaName) ? "dbo" : schemaName.Trim();

        var rule = new DbTableRule
        {
            ConnectionId = connectionId,
            SchemaName = schema,
            TableName = tableName.Trim(),
            AllowRead = allowRead,
            WriteRequiresApproval = writeRequiresApproval
        };

        db.DbTableRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);

        return new DbTableRuleDto(
            rule.Id, rule.ConnectionId, rule.SchemaName, rule.TableName, rule.AllowRead, rule.WriteRequiresApproval);
    }

    public async Task DeleteRuleAsync(
        Guid connectionId,
        Guid ruleId,
        CancellationToken cancellationToken = default)
    {
        var rule = await db.DbTableRules
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.ConnectionId == connectionId, cancellationToken)
            ?? throw new NotFoundException("表级规则", ruleId);

        db.DbTableRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
    }
}
