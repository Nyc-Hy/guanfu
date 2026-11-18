using System.ComponentModel;
using ModelContextProtocol.Server;
using Workbench.Application.Contracts;
using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Mcp;

/// <summary>
/// 评审工作流的 MCP 工具。
///
/// 工作台给骨架，agent（或它开的子 agent / 专家团）做判断：
///   start_review → get_review_pack（拿清单和上下文）→ submit_review（逐维度回写）→ list_reviews（看历史）
///
/// 七个维度可以并行评（agent 自己开子 agent），但每条结论都要单独回写，
/// 工作台才知道这一轮过没过。
///
/// **不抛异常**：MCP SDK 会把异常吞成一句 "An error occurred invoking ..."，
/// agent 就看不到「错在哪、该改什么」了。所有失败都走 <see cref="ReviewToolResult"/> 的 error 字段。
/// </summary>
[McpServerToolType]
public sealed class ReviewMcpTools(IReviewService reviews, IAgentSessionAccessor? sessionAccessor = null)
{
    /// <summary>没显式给 agentId 时用会话里的那个（认领得知道是谁认的）。</summary>
    private async Task<string> ResolveAgentIdAsync(CancellationToken cancellationToken)
    {
        var agentId = sessionAccessor?.CurrentAgentId;

        if (string.IsNullOrWhiteSpace(agentId))
        {
            var session = sessionAccessor is null
                ? null
                : await sessionAccessor.CurrentAsync(cancellationToken);

            agentId = session?.AgentId;
        }

        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new WorkbenchException("认领要说明是谁认领的：给 agentId，或让会话带上 agent 标识。");
        }

        return agentId;
    }

    [McpServerTool(Name = "workbench_start_review")]
    [Description(
        "开始一轮评审（专家团）。按固定的七个维度各建一条待办：代码是否合适、注释是否合适、" +
        "是否有安全问题、是否影响非本次需求的功能、是否有需求未实现、是否有需求过度实现、" +
        "是否有过度操作的代码。开发完成后调用。")]
    public async Task<ReviewToolResult> StartReviewAsync(
        Guid taskId,
        string? reviewer = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var round = await reviews.StartAsync(taskId, reviewer, cancellationToken);
            return new ReviewToolResult { Ok = true, Round = round };
        }
        catch (Exception ex) when (ex is WorkbenchException or InvalidOperationException)
        {
            return ReviewToolResult.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "workbench_get_review_pack")]
    [Description(
        "取评审包：每个维度的检查清单 + 任务上下文（需求理解、验收标准、文档、产物）+ 变更文件列表" +
        "+ 本轮分工（round.judgements：谁评了哪一条、结论是什么）。" +
        "拿到包之后逐维度评审（可以开子 agent 并行认领），再逐维度调 workbench_submit_review 回写结论。")]
    public async Task<ReviewToolResult> GetReviewPackAsync(
        Guid taskId,
        int? round = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pack = await reviews.GetPackAsync(taskId, round, cancellationToken);
            return new ReviewToolResult { Ok = true, Pack = pack };
        }
        catch (Exception ex) when (ex is WorkbenchException or InvalidOperationException)
        {
            return ReviewToolResult.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "workbench_claim_review_dimension")]
    [Description(
        "认领一个评审维度的评审工作（多 agent 分工）。" +
        "一轮七个维度可以拆给几个 agent 各评各的 —— 先认领再评，别人就不会重复评同一个维度。" +
        "认领带租约（默认 30 分钟）：到期还没交结论，别人可以接手，不会把维度卡死。" +
        "维度被别人认领（租约没过）时返回 ok=false，并说清是谁占着；" +
        "已经有结论的维度不用再认领。agentId 不给就用当前会话的 agent。")]
    public async Task<ReviewToolResult> ClaimReviewDimensionAsync(
        Guid taskId,
        string dimension,
        string? agentId = null,
        int leaseMinutes = 30,
        int? round = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolvedAgentId = agentId ?? await ResolveAgentIdAsync(cancellationToken);
            var item = await reviews.ClaimDimensionAsync(
                taskId, dimension, resolvedAgentId, leaseMinutes, round, cancellationToken);

            var current = await reviews.GetCurrentAsync(taskId, cancellationToken);
            return new ReviewToolResult { Ok = true, Item = item, Round = current };
        }
        catch (Exception ex) when (ex is WorkbenchException or InvalidOperationException)
        {
            return ReviewToolResult.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "workbench_claim_spec_item")]
    [Description(
        "认领一条完成定义（DoD）的判定工作（多 agent 分工）。" +
        "跟认领维度一个道理：先认领再判，别人就不会重复判同一条。" +
        "item 填规约里的原文（评审包里 specItems 那一列给的就是它）。" +
        "被别人认领（租约没过）时返回 ok=false，并说清是谁占着。")]
    public async Task<SpecVerdictToolResult> ClaimSpecItemAsync(
        Guid taskId,
        string item,
        string? agentId = null,
        int leaseMinutes = 30,
        int? round = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolvedAgentId = agentId ?? await ResolveAgentIdAsync(cancellationToken);
            var verdict = await reviews.ClaimSpecItemAsync(
                taskId, item, resolvedAgentId, leaseMinutes, round, cancellationToken);

            return new SpecVerdictToolResult { Ok = true, Verdict = verdict };
        }
        catch (Exception ex) when (ex is WorkbenchException or InvalidOperationException)
        {
            return SpecVerdictToolResult.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "workbench_submit_review")]
    [Description(
        "回写某个维度的评审结论。dimension 取值: " +
        "code_quality(代码是否合适) / comments(注释是否合适) / security(是否有安全问题) / " +
        "scope_impact(是否影响非本次需求的功能) / missing_requirement(是否有需求未实现) / " +
        "over_implementation(是否有需求过度实现) / over_operation(是否有过度操作的代码)。" +
        "不通过时必须写清 findings 与证据；七个维度全部通过后任务才能结项。" +
        "某个维度确实不适用时，把 skipped 设成 true —— 标了「不适用」也算有结论，不算不通过。")]
    public async Task<ReviewToolResult> SubmitReviewAsync(
        Guid taskId,
        string dimension,
        bool passed,
        string? findings = null,
        string? evidence = null,
        string? recommendation = null,
        string? reviewer = null,
        int? round = null,
        CancellationToken cancellationToken = default,
        bool skipped = false)
    {
        try
        {
            var item = await reviews.SubmitAsync(
                taskId, dimension, passed, findings, evidence, recommendation, reviewer, round, cancellationToken, skipped);

            var current = await reviews.GetCurrentAsync(taskId, cancellationToken);
            return new ReviewToolResult { Ok = true, Item = item, Round = current };
        }
        catch (Exception ex) when (ex is WorkbenchException or InvalidOperationException)
        {
            return ReviewToolResult.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "workbench_list_reviews")]
    [Description("列出任务的所有评审轮次与历史（每轮每个维度的结论都留痕）")]
    public async Task<ReviewToolResult> ListReviewsAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rounds = await reviews.ListAsync(taskId, cancellationToken);
            return new ReviewToolResult { Ok = true, Rounds = rounds };
        }
        catch (Exception ex) when (ex is WorkbenchException or InvalidOperationException)
        {
            return ReviewToolResult.Fail(ex.Message);
        }
    }

    [McpServerTool(Name = "workbench_get_review")]
    [Description(
        "取当前评审轮次：各维度当前结论 + **本轮的分工**（judgements：谁评了哪一条、结论是什么）" +
        "+ 还差几条（pendingCount）/ 几条不通过（failedCount）。" +
        "收尾的人（协调者）调这个就知道还差谁的活、哪条要改。")]
    public async Task<ReviewToolResult> GetReviewAsync(
        Guid taskId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var round = await reviews.GetCurrentAsync(taskId, cancellationToken);
            return new ReviewToolResult { Ok = true, Round = round };
        }
        catch (Exception ex) when (ex is WorkbenchException or InvalidOperationException)
        {
            return ReviewToolResult.Fail(ex.Message);
        }
    }
    [McpServerTool(Name = "workbench_submit_spec_verdict")]
    [Description(
        "回写**项目规约（DoD）某一条**的判定：满足 / 不满足 / 不适用。" +
        "item 填规约里的原文（评审包里 specItems 那一列给的就是它）。" +
        "七个维度是通用的，规约是这个项目特有的 —— 评审时要一条一条对着判；" +
        "DoD 没判完不许结项（结项门禁会拦）。" +
        "判定为不适用必须写明理由。")]
    public async Task<SpecVerdictToolResult> SubmitSpecVerdictAsync(
        Guid taskId,
        string item,
        bool satisfied,
        string? note = null,
        string? reviewer = null,
        int? round = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var verdict = await reviews.SubmitSpecVerdictAsync(
                taskId, item, satisfied, note, reviewer, round, cancellationToken);

            return new SpecVerdictToolResult { Ok = true, Verdict = verdict };
        }
        catch (Exception ex) when (ex is WorkbenchException or InvalidOperationException)
        {
            return new SpecVerdictToolResult { Ok = false, Error = ex.Message };
        }
    }
}

/// <summary>
/// 评审工具的统一返回形状。字段都是可选的，按调用的工具看对应那个：
/// start_review / get_review 看 round，get_review_pack 看 pack，submit_review 看 item，
/// list_reviews 看 rounds。失败时 ok=false 且 error 有值。
/// </summary>
public sealed record ReviewToolResult
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    public ReviewRoundDto? Round { get; init; }

    public ReviewPackDto? Pack { get; init; }

    public ReviewItemDto? Item { get; init; }

    public IReadOnlyList<ReviewRoundDto>? Rounds { get; init; }

    public static ReviewToolResult Fail(string error) => new() { Ok = false, Error = error };
}

/// <summary>规约逐条判定的返回形状。</summary>
public sealed record SpecVerdictToolResult
{
    public bool Ok { get; init; }

    public string? Error { get; init; }

    public SpecVerdictDto? Verdict { get; init; }

    public static SpecVerdictToolResult Fail(string error) => new() { Ok = false, Error = error };
}
