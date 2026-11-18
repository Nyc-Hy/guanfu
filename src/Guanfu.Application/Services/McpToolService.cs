namespace Guanfu.Application.Services;

/// <summary>一个 MCP 工具的开关状态。</summary>
/// <param name="Name">工具名（下划线命名，如 guanfu_get_context）</param>
/// <param name="Category">所属分类（观复组 / 文档组 / 数据库组 / 通用组）</param>
/// <param name="Description">工具说明</param>
/// <param name="Enabled">是否对外暴露</param>
/// <param name="RequiresApproval">写类工具（执行前必须人工审批）</param>
/// <param name="UpdatedAt">最后一次改动时间；从未改过为 null</param>
public sealed record McpToolInfo(
    string Name,
    string Category,
    string Description,
    bool Enabled,
    bool RequiresApproval,
    DateTimeOffset? UpdatedAt);

/// <summary>
/// MCP 工具开关。关掉的工具不再出现在 tools/list，调用也会被拒。
///
/// 作用域是**观复级**（不是项目级）：MCP 的 tools/list 是会话级的，
/// 没有项目参数，没法按项目返回不同的工具清单。要按项目管就用项目配置里的
/// 命令白名单。
/// </summary>
public interface IMcpToolService
{
    /// <summary>列出全部工具（含开关状态），按分类分组顺序返回。</summary>
    Task<IReadOnlyList<McpToolInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>开关单个工具。工具名不存在时抛 NotFoundException。</summary>
    Task<McpToolInfo> SetEnabledAsync(string toolName, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>恢复默认（全部开启），清掉所有显式改动。</summary>
    Task<IReadOnlyList<McpToolInfo>> ResetAsync(CancellationToken cancellationToken = default);
}
