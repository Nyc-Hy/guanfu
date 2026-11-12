using Microsoft.EntityFrameworkCore;
using Workbench.Domain;

namespace Workbench.Application.Services;

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
        CancellationToken cancellationToken = default);

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

public sealed class DbConnectionService(IAppDbContext db, ISecretProtector protector) : IDbConnectionService
{
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
        CancellationToken cancellationToken = default)
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
            TrustServerCertificate = true
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
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return true;
        }

        var normalized = tableName.Trim();

        var rules = await db.DbTableRules
            .AsNoTracking()
            .Where(r => r.ConnectionId == connectionId)
            .ToListAsync(cancellationToken);

        if (rules.Count == 0)
        {
            return true;
        }

        // 表名可以带 schema（dbo.Orders），规则里也可能只写表名 —— 两边都比一下
        var rule = rules.FirstOrDefault(r =>
            string.Equals(r.TableName, normalized, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(r.TableName, normalized.Split('.').Last(), StringComparison.OrdinalIgnoreCase));

        if (rule is null)
        {
            return true;
        }

        return rule.WriteRequiresApproval;
    }

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
