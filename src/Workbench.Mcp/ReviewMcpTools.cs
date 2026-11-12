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
public sealed class ReviewMcpTools(IReviewService reviews)
{
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
        "取评审包：每个维度的检查清单 + 任务上下文（需求理解、验收标准、文档、产物）+ 变更文件列表。" +
        "拿到包之后逐维度评审（可以开子 agent 并行），再逐维度调 workbench_submit_review 回写结论。")]
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

    [McpServerTool(Name = "workbench_submit_review")]
    [Description(
        "回写某个维度的评审结论。dimension 取值: " +
        "code_quality(代码是否合适) / comments(注释是否合适) / security(是否有安全问题) / " +
        "scope_impact(是否影响非本次需求的功能) / missing_requirement(是否有需求未实现) / " +
        "over_implementation(是否有需求过度实现) / over_operation(是否有过度操作的代码)。" +
        "不通过时必须写清 findings 与证据；七个维度全部通过后任务才能结项。")]
    public async Task<ReviewToolResult> SubmitReviewAsync(
        Guid taskId,
        string dimension,
        bool passed,
        string? findings = null,
        string? evidence = null,
        string? recommendation = null,
        string? reviewer = null,
        int? round = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var item = await reviews.SubmitAsync(
                taskId, dimension, passed, findings, evidence, recommendation, reviewer, round, cancellationToken);

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
    [Description("取当前评审轮次（含各维度当前结论与汇总判定）")]
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
