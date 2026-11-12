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
}

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
}
