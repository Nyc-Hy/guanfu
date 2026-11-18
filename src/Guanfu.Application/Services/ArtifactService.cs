using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Domain;

namespace Guanfu.Application.Services;

/// <summary>
/// 产物的编辑与版本历史。
///
/// 人会改 AI 写出来的东西 —— 尤其是脚本：AI 写的 SQL 能跑，但不一定合乎
/// 生产口径。改是正常的，**改完不留痕才是问题**：
/// 「谁在什么时候把它改成这样」是复盘时最常问的问题。
/// </summary>
public interface IArtifactService
{
    /// <summary>列某个任务的产物（新到旧）。</summary>
    Task<IReadOnlyList<ArtifactDto>> ListAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 改产物。title / content 传 null 表示不动那个字段。
    /// 每次改动都会把新的一版写进历史，并留一条流水。
    /// </summary>
    Task<ArtifactDto> UpdateAsync(
        Guid taskId,
        Guid artifactId,
        string? title,
        string? content,
        string? editor,
        string? note,
        CancellationToken cancellationToken = default);

    /// <summary>版本的改动历史（新到旧）。</summary>
    Task<IReadOnlyList<ArtifactRevisionDto>> ListRevisionsAsync(
        Guid taskId,
        Guid artifactId,
        CancellationToken cancellationToken = default);
}

public sealed class ArtifactService(IAppDbContext db, IClock clock) : IArtifactService
{
    public async Task<IReadOnlyList<ArtifactDto>> ListAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var rows = await db.Artifacts
            .AsNoTracking()
            .Where(a => a.TaskId == taskId)
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    public async Task<ArtifactDto> UpdateAsync(
        Guid taskId,
        Guid artifactId,
        string? title,
        string? content,
        string? editor,
        string? note,
        CancellationToken cancellationToken = default)
    {
        // **当前值要从库里再读一遍**。
        //
        // Blazor 的电路级 DbContext 会一直活着：页面加载时读过一次产物，
        // 之后这条实体就一直在跟踪里。EF 遇到已跟踪的实体**不会再去查库**，
        // 拿到的还是旧值 —— 表现就是「版本号算错了」（同一个号出现两版）。
        //
        // 所以这里拿一份不走跟踪的快照当基准，改动写回跟踪的那份。
        var current = await db.Artifacts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == artifactId && a.TaskId == taskId, cancellationToken)
            ?? throw new NotFoundException("产物", artifactId);

        var artifact = await db.Artifacts
            .FirstOrDefaultAsync(a => a.Id == artifactId && a.TaskId == taskId, cancellationToken)
            ?? throw new NotFoundException("产物", artifactId);

        var task = await db.Tasks
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new NotFoundException("任务", taskId);

        var now = clock.UtcNow;
        var newTitle = title is null ? current.Title : title.Trim();
        var newContent = content ?? current.Content;

        if (string.Equals(newTitle, current.Title, StringComparison.Ordinal)
            && string.Equals(newContent, current.Content, StringComparison.Ordinal))
        {
            // 没改就别造一版 —— 历史里全是「改了一模一样的」没人看得下去
            return ToDto(artifact);
        }

        // 第一版（登记时那版）先补进历史，之后每一版都在表里。
        await EnsureRevisionRowAsync(artifact, current, cancellationToken);

        artifact.Revision = current.Revision + 1;
        artifact.Title = newTitle;
        artifact.Content = newContent;
        artifact.UpdatedAt = now;
        artifact.EditedBy = string.IsNullOrWhiteSpace(editor) ? "human" : editor.Trim();

        db.ArtifactRevisions.Add(new ArtifactRevision
        {
            ArtifactId = artifact.Id,
            Version = artifact.Revision,
            Title = artifact.Title,
            Content = artifact.Content,
            Editor = artifact.EditedBy,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedAt = now
        });

        task.UpdatedAt = now;
        task.LastActivityAt = now;

        db.StatusEvents.Add(new StatusEvent
        {
            TaskId = taskId,
            FromStatus = null,
            ToStatus = null,
            Source = TaskEventSource.Human,
            Actor = artifact.EditedBy,
            Note = $"改了产物「{artifact.Title}」（第 {artifact.Revision} 版）",
            EventType = TaskEventTypes.ArtifactUpdated,
            Payload = GuanfuJson.Serialize(new
            {
                artifactId = artifact.Id,
                title = artifact.Title,
                version = artifact.Revision,
                editor = artifact.EditedBy,
                note
            }),
            CreatedAt = now
        });

        await db.SaveChangesAsync(cancellationToken);

        return ToDto(artifact);
    }

    public async Task<IReadOnlyList<ArtifactRevisionDto>> ListRevisionsAsync(
        Guid taskId,
        Guid artifactId,
        CancellationToken cancellationToken = default)
    {
        var artifact = await db.Artifacts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == artifactId && a.TaskId == taskId, cancellationToken)
            ?? throw new NotFoundException("产物", artifactId);

        var rows = await db.ArtifactRevisions
            .AsNoTracking()
            .Where(r => r.ArtifactId == artifactId)
            .OrderByDescending(r => r.Version)
            .ToListAsync(cancellationToken);

        // 历史表里没有当前版时（登记后一次没改过），把当前版补上 ——
        // 「这一版是什么时候、谁写的」也是版本信息的一部分。
        if (rows.Count == 0 || rows.Max(r => r.Version) < artifact.Revision)
        {
            rows.Add(new ArtifactRevision
            {
                Id = Guid.Empty,
                ArtifactId = artifact.Id,
                Version = artifact.Revision,
                Title = artifact.Title,
                Content = artifact.Content,
                Editor = artifact.EditedBy ?? artifact.Author,
                Note = "登记时的版本",
                CreatedAt = artifact.UpdatedAt ?? artifact.CreatedAt
            });

            rows = rows.OrderByDescending(r => r.Version).ToList();
        }

        return rows
            .Select(r => new ArtifactRevisionDto(
                r.Id,
                r.Version,
                r.Title,
                r.Content,
                r.Editor,
                r.Note,
                r.CreatedAt))
            .ToList();
    }

    /// <summary>当前这一版在历史表里没有就补一条（登记时那版）。</summary>
    private async Task EnsureRevisionRowAsync(
        Artifact artifact,
        Artifact current,
        CancellationToken cancellationToken)
    {
        var exists = await db.ArtifactRevisions
            .AnyAsync(r => r.ArtifactId == artifact.Id && r.Version == current.Revision, cancellationToken);

        if (exists)
        {
            return;
        }

        db.ArtifactRevisions.Add(new ArtifactRevision
        {
            ArtifactId = artifact.Id,
            Version = current.Revision,
            Title = current.Title,
            Content = current.Content,
            Editor = current.EditedBy ?? current.Author,
            Note = "登记时的版本",
            CreatedAt = current.UpdatedAt ?? current.CreatedAt
        });
    }

    private static ArtifactDto ToDto(Artifact artifact) => new(
        artifact.Id,
        artifact.Type.ToString(),
        artifact.Title,
        artifact.Content,
        artifact.ExternalPath,
        artifact.CommitSha,
        artifact.Branch,
        artifact.Author,
        artifact.CreatedAt,
        artifact.Revision,
        artifact.UpdatedAt,
        artifact.EditedBy);
}
