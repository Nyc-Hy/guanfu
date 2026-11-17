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

    /// <summary>写规约。整份覆盖 —— 规矩是「现在该是什么样」，不是补丁。</summary>
    Task<ProjectSpecDto> UpdateAsync(
        Guid projectId,
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
