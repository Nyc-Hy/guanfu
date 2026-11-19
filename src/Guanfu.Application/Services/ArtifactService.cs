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

    /// <summary>
    /// 搜产物（标题与内容）。
    ///
    /// 「这段 SQL 在哪个任务里？」—— 产物是交付物，交付物得能被搜到，
    /// 否则脚本一多就没人找得到它是哪个版本带来的。
    ///
    /// 可见性跟任务一致：普通成员只搜得到**自己任务**下的产物。
    /// </summary>
    Task<IReadOnlyList<ArtifactSearchHitDto>> SearchAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default);
}

public sealed class ArtifactService(
    IAppDbContext db,
    IClock clock,
    ICurrentMemberScope? currentMember = null) : IArtifactService
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

    public async Task<IReadOnlyList<ArtifactSearchHitDto>> SearchAsync(
        string query,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var value = query.Trim();

        if (value.Length == 0)
        {
            return [];
        }

        // 大小写不敏感：人打「delete from」要能搜到「DELETE FROM」。
        var lowered = value.ToLowerInvariant();

        var scope = currentMember?.Current;

        var visibleTaskIds = db.Tasks
            .AsNoTracking()
            .VisibleTo(scope)
            .Select(t => t.Id);

        var rows = await db.Artifacts
            .AsNoTracking()
            .Where(a =>
                (a.Title != null && a.Title.ToLower().Contains(lowered)) ||
                (a.Content != null && a.Content.ToLower().Contains(lowered)))
            .Where(a => scope == null || scope.IsAdmin || visibleTaskIds.Contains(a.TaskId))
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            return [];
        }

        var taskIds = rows.Select(a => a.TaskId).Distinct().ToList();

        var tasks = await db.Tasks
            .AsNoTracking()
            .Where(t => taskIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Key, t.Title, t.ProjectId })
            .ToListAsync(cancellationToken);

        var projectIds = tasks.Select(t => t.ProjectId).Distinct().ToList();

        var projects = await db.Projects
            .AsNoTracking()
            .Where(p => projectIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(cancellationToken);

        var taskById = tasks.ToDictionary(t => t.Id);
        var projectById = projects.ToDictionary(p => p.Id);

        return rows.Select(a =>
        {
            var task = taskById.GetValueOrDefault(a.TaskId);
            string projectName = "—";
            if (task is not null && projectById.TryGetValue(task.ProjectId, out var project))
            {
                projectName = project.Name;
            }

            return new ArtifactSearchHitDto(
                a.Id,
                a.Title,
                a.Type.ToString(),
                a.TaskId,
                task?.Key,
                task?.Title,
                projectName,
                a.Content is null ? "标题命中" : Snippet(a.Content!, value));
        }).ToList();
    }

    /// <summary>命中处的前后文（让人一眼看出是哪一段）。</summary>
    private static string Snippet(string content, string keyword)
    {
        const int Window = 40;

        var index = content.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);

        if (index < 0)
        {
            var limit = Window * 2;
            return content.Length <= limit ? content : content[..limit] + "…";
        }

        var start = Math.Max(0, index - Window);
        var end = Math.Min(content.Length, index + keyword.Length + Window);

        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = end < content.Length ? "…" : string.Empty;

        return prefix + content[start..end].Replace("\n", " ") + suffix;
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
