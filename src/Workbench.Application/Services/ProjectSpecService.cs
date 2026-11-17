using Microsoft.EntityFrameworkCore;
using Workbench.Application.Contracts;
using Workbench.Domain;

namespace Workbench.Application.Services;

/// <summary>
/// 项目级规约。
///
/// **人定规矩，agent 读得到** —— 这是这一层存在的全部理由。
///
/// 以前这些规矩只活在人的 AGENTS.md 和记忆里，agent 读不到：
/// 「什么算做完」「提交信息怎么写」「测试怎么跑」全靠人转述，
/// 转述不到的地方 agent 就自己猜 —— 于是「人以为做完了、agent 以为做完了」。
///
/// 现在规矩是**一等实体**：人填（界面 / REST，要人工凭证），agent 读
/// （MCP 工具、上下文包、评审包）。改规矩是人的决定，所以只有人能改。
/// </summary>
public interface IProjectSpecService
{
    /// <summary>读这个项目的规约。没填过就返回一份空的（形状一致，agent 不用判空）。</summary>
    Task<ProjectSpecDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>读某个里程碑自己的规约。没填过就返回空的。</summary>
    Task<ProjectSpecDto> GetForMilestoneAsync(
        Guid projectId,
        Guid milestoneId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 这个任务**生效**的规约：项目级 + 它所在里程碑的那一层（里程碑的字段优先）。
    ///
    /// agent 开工前读的是这一份 —— 不是「项目级」也不是「里程碑级」，
    /// 是对这个活真正生效的那份。
    /// </summary>
    Task<ProjectSpecDto> GetEffectiveAsync(Guid taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 这个任务**生效**的完成定义：项目级的 + 它所在里程碑的（去重）。
    ///
    /// agent 与评审看到的都是这一份 —— 别让「哪个层级说了算」变成隐性规则。
    /// </summary>
    Task<IReadOnlyList<string>> GetEffectiveDefinitionOfDoneAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);

    /// <summary>读工作区标准规约（所有项目通用）。没填过就返回空的。</summary>
    Task<ProjectSpecDto> GetForWorkspaceAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>写工作区标准规约。</summary>
    Task<ProjectSpecDto> UpdateForWorkspaceAsync(
        Guid workspaceId,
        ProjectSpecInput input,
        string? updatedBy = null,
        CancellationToken cancellationToken = default);

    /// <summary>写规约。整份覆盖 —— 规矩是「现在该是什么样」，不是补丁。</summary>
    Task<ProjectSpecDto> UpdateAsync(
        Guid projectId,
        ProjectSpecInput input,
        string? updatedBy = null,
        CancellationToken cancellationToken = default);

    /// <summary>写某个里程碑自己的规约。</summary>
    Task<ProjectSpecDto> UpdateForMilestoneAsync(
        Guid projectId,
        Guid milestoneId,
        ProjectSpecInput input,
        string? updatedBy = null,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectSpecService(IAppDbContext db, IClock clock) : IProjectSpecService
{
    public async Task<ProjectSpecDto> GetAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var spec = await db.ProjectSpecs
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);

        return spec is null ? ProjectSpecDto.Empty(projectId) : ToDto(spec);
    }

    public async Task<ProjectSpecDto> GetForMilestoneAsync(
        Guid projectId,
        Guid milestoneId,
        CancellationToken cancellationToken = default)
    {
        var spec = await db.MilestoneSpecs
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.MilestoneId == milestoneId, cancellationToken);

        return spec is null ? ProjectSpecDto.Empty(projectId) : ToDto(spec);
    }

    public async Task<ProjectSpecDto> GetEffectiveAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.ProjectId, t.MilestoneId })
            .FirstOrDefaultAsync(cancellationToken);

        if (task is null)
        {
            return ProjectSpecDto.Empty(Guid.Empty);
        }

        var workspaceId = await db.Projects.AsNoTracking()
            .Where(p => p.Id == task.ProjectId)
            .Select(p => p.WorkspaceId)
            .FirstOrDefaultAsync(cancellationToken);

        var workspace = await db.WorkspaceSpecs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId, cancellationToken);

        var project = await db.ProjectSpecs.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ProjectId == task.ProjectId, cancellationToken);

        var milestone = task.MilestoneId is Guid milestoneId
            ? await db.MilestoneSpecs.AsNoTracking()
                .FirstOrDefaultAsync(s => s.MilestoneId == milestoneId, cancellationToken)
            : null;

        if (workspace is null && project is null && milestone is null)
        {
            return ProjectSpecDto.Empty(task.ProjectId);
        }

        // 越具体的越优先（工作区 → 项目 → 里程碑），DoD 是三层叠加
        return new ProjectSpecDto(
            task.ProjectId,
            Pick(milestone?.Purpose, Pick(project?.Purpose, workspace?.Purpose)),
            SplitLines(workspace?.DefinitionOfDone)
                .Concat(SplitLines(project?.DefinitionOfDone))
                .Concat(SplitLines(milestone?.DefinitionOfDone))
                .Distinct(StringComparer.Ordinal).ToList(),
            Pick(milestone?.CommitConvention, Pick(project?.CommitConvention, workspace?.CommitConvention)),
            Pick(milestone?.TestCommand, Pick(project?.TestCommand, workspace?.TestCommand)),
            Pick(project?.BranchConvention, workspace?.BranchConvention),
            Pick(milestone?.Guardrails, Pick(project?.Guardrails, workspace?.Guardrails)),
            milestone?.UpdatedAt ?? project?.UpdatedAt ?? workspace?.UpdatedAt,
            milestone?.UpdatedBy ?? project?.UpdatedBy ?? workspace?.UpdatedBy);
    }

    /// <summary>里程碑的那层优先，没填的用项目级的。</summary>
    private static string? Pick(string? milestoneValue, string? projectValue) =>
        string.IsNullOrWhiteSpace(milestoneValue) ? projectValue : milestoneValue;

    public async Task<IReadOnlyList<string>> GetEffectiveDefinitionOfDoneAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        var task = await db.Tasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => new { t.ProjectId, t.MilestoneId })
            .FirstOrDefaultAsync(cancellationToken);

        if (task is null)
        {
            return [];
        }

        var workspaceDoD = await ReadWorkspaceDoDAsync(task.ProjectId, cancellationToken);
        var projectDoD = await db.ProjectSpecs.AsNoTracking()
            .Where(s => s.ProjectId == task.ProjectId)
            .Select(s => s.DefinitionOfDone)
            .FirstOrDefaultAsync(cancellationToken);

        var milestoneDoD = task.MilestoneId is Guid milestoneId
            ? await db.MilestoneSpecs.AsNoTracking()
                .Where(s => s.MilestoneId == milestoneId)
                .Select(s => s.DefinitionOfDone)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        // 工作区 → 项目 → 里程碑：越具体的越靠后，顺序稳定，agent 与人看到的一样
        return SplitLines(workspaceDoD)
            .Concat(SplitLines(projectDoD))
            .Concat(SplitLines(milestoneDoD))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task<ProjectSpecDto> GetForWorkspaceAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        var spec = await db.WorkspaceSpecs
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId, cancellationToken);

        return spec is null ? ProjectSpecDto.Empty(workspaceId) : ToDto(spec);
    }

    public async Task<ProjectSpecDto> UpdateForWorkspaceAsync(
        Guid workspaceId,
        ProjectSpecInput input,
        string? updatedBy = null,
        CancellationToken cancellationToken = default)
    {
        var exists = await db.Workspaces
            .AnyAsync(w => w.Id == workspaceId, cancellationToken);

        if (!exists)
        {
            throw new NotFoundException("工作区", workspaceId);
        }

        var spec = await db.WorkspaceSpecs
            .FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId, cancellationToken);

        if (spec is null)
        {
            spec = new WorkspaceSpec { WorkspaceId = workspaceId };
            db.WorkspaceSpecs.Add(spec);
        }

        spec.Purpose = Trim(input.Purpose);
        spec.DefinitionOfDone = JoinLines(input.DefinitionOfDone);
        spec.CommitConvention = Trim(input.CommitConvention);
        spec.TestCommand = Trim(input.TestCommand);
        spec.BranchConvention = Trim(input.BranchConvention);
        spec.Guardrails = Trim(input.Guardrails);
        spec.UpdatedAt = clock.UtcNow;
        spec.UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim();

        await db.SaveChangesAsync(cancellationToken);

        return ToDto(spec);
    }

    public async Task<ProjectSpecDto> UpdateAsync(
        Guid projectId,
        ProjectSpecInput input,
        string? updatedBy = null,
        CancellationToken cancellationToken = default)
    {
        var projectExists = await db.Projects
            .AnyAsync(p => p.Id == projectId, cancellationToken);

        if (!projectExists)
        {
            throw new NotFoundException("项目", projectId);
        }

        var spec = await db.ProjectSpecs
            .FirstOrDefaultAsync(s => s.ProjectId == projectId, cancellationToken);

        if (spec is null)
        {
            spec = new ProjectSpec { ProjectId = projectId };
            db.ProjectSpecs.Add(spec);
        }

        spec.Purpose = Trim(input.Purpose);
        spec.DefinitionOfDone = JoinLines(input.DefinitionOfDone);
        spec.CommitConvention = Trim(input.CommitConvention);
        spec.TestCommand = Trim(input.TestCommand);
        spec.BranchConvention = Trim(input.BranchConvention);
        spec.Guardrails = Trim(input.Guardrails);
        spec.UpdatedAt = clock.UtcNow;
        spec.UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim();

        await db.SaveChangesAsync(cancellationToken);

        return ToDto(spec);
    }

    public async Task<ProjectSpecDto> UpdateForMilestoneAsync(
        Guid projectId,
        Guid milestoneId,
        ProjectSpecInput input,
        string? updatedBy = null,
        CancellationToken cancellationToken = default)
    {
        var milestone = await db.Milestones.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == milestoneId, cancellationToken)
            ?? throw new NotFoundException("里程碑", milestoneId);

        if (milestone.ProjectId != projectId)
        {
            throw new WorkbenchException("里程碑不属于这个项目。");
        }

        var spec = await db.MilestoneSpecs
            .FirstOrDefaultAsync(s => s.MilestoneId == milestoneId, cancellationToken);

        if (spec is null)
        {
            spec = new MilestoneSpec { MilestoneId = milestoneId };
            db.MilestoneSpecs.Add(spec);
        }

        spec.Purpose = Trim(input.Purpose);
        spec.DefinitionOfDone = JoinLines(input.DefinitionOfDone);
        spec.CommitConvention = Trim(input.CommitConvention);
        spec.TestCommand = Trim(input.TestCommand);
        spec.Guardrails = Trim(input.Guardrails);
        spec.UpdatedAt = clock.UtcNow;
        spec.UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? null : updatedBy.Trim();

        await db.SaveChangesAsync(cancellationToken);

        return ToDto(spec);
    }

    private static string? Trim(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim();
    }

    /// <summary>DoD 逐条存：一行一条，空行丢掉。</summary>
    private static string? JoinLines(IReadOnlyList<string>? lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return null;
        }

        var kept = lines
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim())
            .ToList();

        return kept.Count == 0 ? null : string.Join("\n", kept);
    }

    /// <summary>
    /// 实体 → DTO。**公开给同层的服务复用** ——
    /// 上下文包和评审包都要带上规约，别各抄一份映射（抄的那份会漏字段）。
    /// </summary>
    internal static ProjectSpecDto ToDto(ProjectSpec spec) => new(
        spec.ProjectId,
        spec.Purpose,
        SplitLines(spec.DefinitionOfDone),
        spec.CommitConvention,
        spec.TestCommand,
        spec.BranchConvention,
        spec.Guardrails,
        spec.UpdatedAt,
        spec.UpdatedBy);

    /// <summary>这个项目所在工作区的标准完成定义。</summary>
    private async Task<string?> ReadWorkspaceDoDAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var workspaceId = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.WorkspaceId)
            .FirstOrDefaultAsync(cancellationToken);

        return await db.WorkspaceSpecs.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId)
            .Select(s => s.DefinitionOfDone)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static ProjectSpecDto ToDto(WorkspaceSpec spec) => new(
        spec.WorkspaceId,
        spec.Purpose,
        SplitLines(spec.DefinitionOfDone),
        spec.CommitConvention,
        spec.TestCommand,
        spec.BranchConvention,
        spec.Guardrails,
        spec.UpdatedAt,
        spec.UpdatedBy);

    private static ProjectSpecDto ToDto(MilestoneSpec spec) => new(
        spec.Milestone?.ProjectId ?? Guid.Empty,
        spec.Purpose,
        SplitLines(spec.DefinitionOfDone),
        spec.CommitConvention,
        spec.TestCommand,
        null,
        spec.Guardrails,
        spec.UpdatedAt,
        spec.UpdatedBy);

    private static IReadOnlyList<string> SplitLines(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }
}
