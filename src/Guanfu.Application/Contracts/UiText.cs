using Guanfu.Domain;

namespace Guanfu.Application.Contracts;

/// <summary>
/// 界面上的中文说法。
///
/// 规矩：**代码标识不许漏到界面上**。用户看到的是「提交代码」「开发中」，
/// 不是 `git_commit`、`InDev`。要查原始标识的人去审计记录里看原始数据，
/// 日常界面用不着。
/// </summary>
public static class UiText
{
    /// <summary>任务状态。</summary>
    public static string Status(TaskStatus status) => status switch
    {
        TaskStatus.Draft => "草稿",
        TaskStatus.DocsReady => "文档就绪",
        TaskStatus.Planned => "已计划",
        TaskStatus.InDev => "开发中",
        TaskStatus.Testing => "测试中",
        TaskStatus.Review => "评审中",
        TaskStatus.Done => "已完成",
        TaskStatus.Blocked => "受阻",
        TaskStatus.Cancelled => "已取消",
        _ => status.ToString()
    };

    /// <summary>任务状态（可空）。</summary>
    public static string Status(TaskStatus? status)
        => status is null ? "—" : Status(status.Value);

    /// <summary>
    /// 任务状态的展示色（CSS 类）。
    ///
    /// 规矩跟 <see cref="Status"/> 一样：**别在页面里各写各的映射**。
    /// 同一个状态在总览、项目页、搜索结果、详情页得是同一个颜色，
    /// 否则人扫一眼表就知道「这个状态在这页是红的、在那页是黄的」，是同一件事两套说法。
    /// </summary>
    public static string StatusClass(TaskStatus status) => status switch
    {
        TaskStatus.InDev => "wb-status-active",
        TaskStatus.Testing => "wb-status-active",
        TaskStatus.Review => "wb-status-review",
        TaskStatus.Done => "wb-status-ok",
        TaskStatus.Blocked => "wb-status-danger",
        TaskStatus.Cancelled => "wb-status-muted",
        _ => "wb-status-neutral"
    };

    /// <summary>任务状态的展示色（字符串形式，认不出来就用中性色）。</summary>
    public static string StatusClass(string? status)
        => Enum.TryParse<TaskStatus>(status, out var parsed)
            ? StatusClass(parsed)
            : "wb-status-neutral";

    /// <summary>
    /// 认状态：既能认枚举名（`DocsReady`），也能认中文说法（`文档就绪`）。
    ///
    /// 人敲命令用中文，agent 用枚举名 —— 两种都得能落地，不然「界面上说中文、
    /// 命令行只认英文」就变成了两套话。
    /// </summary>
    public static bool TryParseStatus(string? text, out TaskStatus status)
    {
        status = TaskStatus.Draft;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        if (Enum.TryParse<TaskStatus>(trimmed, ignoreCase: true, out status))
        {
            return true;
        }

        foreach (var candidate in Enum.GetValues<TaskStatus>())
        {
            if (Status(candidate) == trimmed)
            {
                status = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>任务优先级。</summary>
    public static string Priority(TaskPriority priority) => priority switch
    {
        TaskPriority.Low => "低",
        TaskPriority.Normal => "普通",
        TaskPriority.High => "高",
        TaskPriority.Critical => "紧急",
        _ => priority.ToString()
    };

    /// <summary>所有状态的清单（中文），给「你传错了」的错误消息用。</summary>
    public static string StatusNames()
        => string.Join("、", Enum.GetValues<TaskStatus>().Select(Status));

    /// <summary>任务状态（字符串形式，认不出来就原样透出）。</summary>
    public static string Status(string? status)
        => Enum.TryParse<TaskStatus>(status, out var parsed)
            ? Status(parsed)
            : string.IsNullOrWhiteSpace(status) ? "—" : status;

    /// <summary>审批状态。</summary>
    public static string ApprovalStatus(string status) => status switch
    {
        "Pending" => "待审批",
        "Approved" => "已批准",
        "Rejected" => "已驳回",
        "Revoked" => "已撤销",
        "Executing" => "执行中",
        "Executed" => "已执行",
        _ => status
    };

    /// <summary>工具调用结果。</summary>
    public static string Outcome(string outcome) => outcome switch
    {
        "success" => "成功",
        "denied" => "已拒绝",
        "error" => "出错",
        "pending" => "待审批",
        _ => outcome
    };

    /// <summary>读写类型。</summary>
    public static string DbKind(string? kind) => kind switch
    {
        "Read" => "只读",
        "Write" => "写入",
        "Unknown" => "未识别",
        null or "" => "—",
        _ => kind
    };

    /// <summary>文件变更类型。</summary>
    public static string ChangeKind(string kind) => kind switch
    {
        "Added" => "新增",
        "Modified" => "修改",
        "Deleted" => "删除",
        "Renamed" => "改名",
        _ => kind
    };

    /// <summary>事件来源：是谁干的。</summary>
    public static string EventSource(string? source) => source switch
    {
        "Human" => "人",
        "Agent" => "agent",
        "Ci" => "CI",
        "System" => "系统",
        null or "" => "—",
        _ => source
    };

    /// <summary>事件类型。</summary>
    public static string EventType(string? eventType) => eventType switch
    {
        "status.changed" => "状态变更",
        "plan.saved" => "回写计划",
        "artifact.added" => "登记产物",
        "test.reported" => "回写测试",
        "dependency.added" => "加依赖",
        "dependency.removed" => "去依赖",
        "task.assigned" => "分派",
        "task.archived" => "归档",
        "task.claimed" => "认领",
        "task.released" => "释放",
        "task.type_changed" => "改类型",
        "task.branch_bound" => "绑分支",
        "review.round_opened" => "开评审",
        "review.dimension_result" => "评审结论",
        "review.round_closed" => "评审收口",
        "agent.launched" => "启动 agent",
        "agent.exited" => "agent 退出",
        "milestone.assigned" => "归组",
        "note" => "备注",
        null or "" => "动态",
        _ => eventType
    };

    /// <summary>
    /// 工具名 → 人话。
    ///
    /// 认不出来的原样透出（而不是编一个）—— 宁可显示一个陌生名字，
    /// 也不要显示一个错误的名字。
    /// </summary>
    public static string Tool(string? tool) => tool switch
    {
        null or "" => "—",

        // 文件与 git
        "fs_list" => "查看仓库目录",
        "fs_read" => "读取文件",
        "git_commit" => "提交代码",
        "git_push" => "推送代码",
        "shell_run" => "执行命令",

        // 数据库
        "db_query" => "查询数据库",
        "db_execute" => "执行 SQL",
        "db_schema" => "查看表结构",
        "db_list_connections" => "查看数据库连接",
        "db_list_rules" => "查看表级规则",
        "db_test_connection" => "测试数据库连接",

        // 审批
        "guanfu_revoke_approval" => "撤销审批",

        // 文档
        "doc_parse" => "解析文档",
        "doc_get_outline" => "查看文档大纲",
        "doc_get_text" => "读取文档正文",
        "doc_get_revisions" => "查看文档修订",
        "doc_list_struck" => "查看删除线内容",
        "doc_search" => "检索文档",
        "doc_list_formats" => "查看文档格式",

        // 观复
        "guanfu_list_projects" => "查看项目列表",
        "guanfu_get_project" => "查看项目",
        "guanfu_create_task" => "新建任务",
        "guanfu_list_tasks" => "查看任务列表",
        "guanfu_get_task" => "查看任务",
        "guanfu_get_task_by_key" => "按编号找任务",
        "guanfu_claim_task" => "认领任务",
        "guanfu_get_context" => "读取任务上下文",
        "guanfu_set_plan" => "回写开发计划",
        "guanfu_update_status" => "推进状态",
        "guanfu_add_artifact" => "登记产物",
        "guanfu_report_test_result" => "回写测试结果",
        "guanfu_post_event" => "写一条动态",
        "guanfu_list_events" => "查看事件流水",
        "guanfu_archive_task" => "归档任务",
        "guanfu_assign_task" => "分派任务",
        "guanfu_list_dependencies" => "查看依赖",
        "guanfu_add_task_dependency" => "加依赖",
        "guanfu_remove_task_dependency" => "去依赖",
        "guanfu_list_approvals" => "查看审批",
        "guanfu_get_approval" => "查看审批详情",
        "guanfu_await_approval" => "等待审批",
        "guanfu_list_audit" => "查看审计",
        "guanfu_verify_audit_chain" => "验审计哈希链",
        "guanfu_bind_session" => "绑定会话",
        "guanfu_get_session" => "查看会话",
        "guanfu_start_review" => "开评审轮",
        "guanfu_get_review_pack" => "取评审包",
        "guanfu_submit_review" => "回写评审结论",
        "guanfu_list_reviews" => "查看评审历史",
        "guanfu_get_review" => "查看评审汇总",
        "guanfu_export_events" => "导出事件流水",
        "guanfu_export_review" => "导出评审报告",
        "guanfu_list_milestones" => "查看里程碑",
        "guanfu_get_metrics" => "查看度量",
        "guanfu_get_project_config" => "查看项目配置",

        // 项目 / 里程碑
        "guanfu_create_project" => "新建项目",
        "guanfu_create_milestone" => "新建里程碑",
        "guanfu_update_milestone" => "改里程碑",
        "guanfu_delete_milestone" => "删里程碑",
        "guanfu_assign_milestone" => "归组里程碑",

        _ => tool
    };

    /// <summary>
    /// 任务怎么称呼人。没有编号就用标题，两个都没有就说明「未关联任务」。
    /// 界面上不该出现一串 GUID —— 人认不出那是哪个任务。
    /// </summary>
    public static string TaskRef(string? key, string? title = null)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return key;
            }

            // 标题里已经带了编号就别再前面加一遍 ——
            // 「REQ-001 REQ-001 订单列表要分页」看着像出了两次同样的东西。
            return title.TrimStart().StartsWith(key, StringComparison.OrdinalIgnoreCase)
                ? title
                : $"{key} {title}";
        }

        return string.IsNullOrWhiteSpace(title) ? "未关联任务" : title;
    }

    /// <summary>
    /// MCP 工具分组 → URL 短码。
    ///
    /// 分组名是中文，直接塞进 URL 会变成一串百分号编码（?cat=%E5%B7%A5%E4%BD%9C...），
    /// 没法发给人看。短码放这里，页面不各写各的。
    /// </summary>
    public static string ToolGroupSlug(string? group) => group switch
    {
        "观复组" => "guanfu",
        "文档组" => "docs",
        "数据库组" => "db",
        "通用组" => "general",
        _ => "other"
    };

    /// <summary>URL 短码 → MCP 工具分组名。认不出来就回「通用组」。</summary>
    public static string ToolGroupOf(string? slug) => slug switch
    {
        "guanfu" => "观复组",
        "docs" => "文档组",
        "db" => "数据库组",
        "general" => "通用组",
        _ => "通用组"
    };
}
