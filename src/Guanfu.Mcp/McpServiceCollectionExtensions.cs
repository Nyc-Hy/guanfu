using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Guanfu.Application;
using Guanfu.Application.Contracts;
using Guanfu.Domain;
using Guanfu.Application.Services;

namespace Guanfu.Mcp;

public static class McpServiceCollectionExtensions
{
    public static IServiceCollection AddGuanfuMcp(this IServiceCollection services)
    {
        // 工具目录从实际注册的工具反射出来，加了工具不用手改清单
        var catalog = McpToolCatalog.Discover(typeof(GuanfuMcpTools).Assembly);
        services.AddSingleton<IReadOnlyList<McpToolDescriptor>>(catalog);

        // 开关的内存态：MCP 请求热路径上不查库
        services.AddSingleton<McpToolState>();
        services.AddScoped<IMcpToolService, McpToolSettingsService>();

        // 当前会话：从 HTTP 头认出「这次调用属于哪个 agent / 项目 / 任务」。
        // 绑上之后工具调用不用再自己背项目 GUID。
        services.AddScoped<IAgentSessionAccessor, AgentSessionAccessor>();
        services.AddScoped<ToolCallRecorder>();
        services.AddScoped<McpProjectAuthorization>();

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "guanfu",
                    Version = "0.1.0",
                    Title = "观复"
                };

                options.ServerInstructions =
                    "观复是给 agent 用的项目管理工具：任务、里程碑、依赖、状态机在这里，" +
                    "观复记录状态与产出。用开发机上的工具读取仓库、修改代码、运行测试和 Git。" +
                    "远程接入时用 projectId 选择项目，或使用单项目凭证；拿到任务 ID 后可直接用 taskId。" +
                    "典型流程：guanfu_get_task_by_key 按编号找任务 → guanfu_claim_task 领任务（带租约）→ " +
                    "guanfu_get_context 取全量上下文（文档、计划、产出、事件、当前允许的状态迁移）→ " +
                    "guanfu_set_plan 回写需求理解与开发计划 → 开发 → " +
                    "guanfu_report_test_result 回写测试 → guanfu_update_status 推进状态 → " +
                    "guanfu_get_review_pack 取评审上下文，在本机按变更范围检查与跑测试，" +
                    "guanfu_submit_review 逐维度回写结论，再用 guanfu_record_review_evidence 登记比较基线、" +
                    "按需求拆分的 diff、PRD 对照、测试和问题证据，guanfu_export_review 以 html 导出。" +
                    "要点：① 状态迁移过状态机，非法迁移会带回「当前允许的迁移列表」，照着它改；" +
                    "② 任务编号（REQ-001）就是给人看的 id，不要用 GUID；" +
                    "③ 数据库写入按配置的审批规则执行；" +
                    "④ 用 guanfu_add_artifact 登记报告、脚本等产物；CLI 的 guanfu report 可读取开发机上的文件再上传；" +
                    "⑤ 需求理解的判断是你做的，观复只存不生成。";

                // 关掉的工具不出现在 tools/list —— agent 就看不到它，不会去调
                options.Filters.Request.ListToolsFilters.Add(next => async (context, cancellationToken) =>
                {
                    var result = await next(context, cancellationToken);

                    var state = context.Services?.GetService<McpToolState>();
                    if (state is null)
                    {
                        return result;
                    }

                    var visible = result.Tools.Where(t => state.IsEnabled(t.Name)).ToList();

                    if (visible.Count != result.Tools.Count)
                    {
                        result.Tools = visible;
                    }

                    return result;
                });

                // 最外层：所有工具调用都留痕。
                //
                // 注册在最前面 = 洋葱模型的最外层 = 能看到最终结果
                // （包括被关掉的工具、被兜底过滤器转成结构化错误的调用）。
                //
                // 以前只有写工具留痕，只读调用不留 —— 「谁访问了什么资源」查不出来，
                // 而这正是企业合规最关心的那一问。
                options.Filters.Request.CallToolFilters.Add(next => async (context, cancellationToken) =>
                {
                    var name = context.Params.Name;
                    var started = DateTimeOffset.UtcNow;

                    try
                    {
                        var result = await next(context, cancellationToken);

                        // 工具自己留痕的那几个跳过：它们记得更细（审批号、影响行数、
                        // 结果摘要、SQL 原文），中央这份补不回来，两边都写就成了一行两条。
                        if (!ToolCallRecorder.SelfAuditedTools.Contains(name))
                        {
                            var recorder = context.Services?.GetService<ToolCallRecorder>();
                            var state = context.Services?.GetService<McpToolState>();

                            var outcome = result.IsError == true
                                ? (state is not null && !state.IsEnabled(name) ? "denied" : "error")
                                : "success";

                            if (recorder is not null)
                            {
                                await recorder.RecordAsync(
                                    name, context.Params.Arguments, outcome,
                                    (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                                    cancellationToken);
                            }
                        }

                        return result;
                    }
                    catch (Exception)
                    {
                        // 工具抛异常时（内层的兜底过滤器会把它转成结构化错误），
                        // 这里也留一条「出错」，然后照原样抛出去。
                        if (!ToolCallRecorder.SelfAuditedTools.Contains(name))
                        {
                            var recorder = context.Services?.GetService<ToolCallRecorder>();
                            if (recorder is not null)
                            {
                                await recorder.RecordAsync(
                                    name, context.Params.Arguments, "error",
                                    (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds,
                                    cancellationToken);
                            }
                        }

                        throw;
                    }
                });

                // 最外层兜底。MCP SDK 会把工具抛出的异常吞成一句
                // "An error occurred invoking 'xxx'."，agent 看不到到底错在哪，
                // 只能瞎猜。这里统一把异常转成结构化错误（ok=false + error）。
                // 必须注册在最前面 —— 过滤器是洋葱模型，先注册的在最外层。
                options.Filters.Request.CallToolFilters.Add(next => async (context, cancellationToken) =>
                {
                    try
                    {
                        return await next(context, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        var message = ex is GuanfuException or InvalidOperationException or ArgumentException
                            ? ex.Message
                            : $"{ex.GetType().Name}: {ex.Message}";

                        var payload = new
                        {
                            ok = false,
                            error = message,
                            tool = context.Params.Name
                        };

                        return new CallToolResult
                        {
                            IsError = true,
                            Content =
                            [
                                new TextContentBlock
                                {
                                    Text = JsonSerializer.Serialize(payload, GuanfuJson.Options)
                                }
                            ]
                        };
                    }
                });

                // 用量闸门：超过项目配置的「每任务工具调用上限」就不再执行。
                //
                // 上限是**节流阀**：拦的是「一个 agent 跑飞、把观复当自己的工具箱刷」，
                // 不是安全边界。所以它放在最里层 —— 被拦下的这次调用照样被审计记一笔
                // （中央记录器在外面，看得到最终结果）。
                options.Filters.Request.CallToolFilters.Add(next => async (context, cancellationToken) =>
                {
                    var services = context.Services;
                    if (services is null)
                    {
                        return await next(context, cancellationToken);
                    }

                    var usage = services.GetService<ITaskUsageService>();
                    var scopeResolver = services.GetService<IProjectScopeResolver>();

                    if (usage is null || scopeResolver is null)
                    {
                        return await next(context, cancellationToken);
                    }

                    ProjectScope? scope;
                    try
                    {
                        scope = await scopeResolver.ResolveOrNullAsync(null, cancellationToken);
                    }
                    catch
                    {
                        // 作用域认不出来（没绑会话之类）—— 不拦，交给工具自己报。
                        return await next(context, cancellationToken);
                    }

                    if (scope?.TaskId is not Guid taskId)
                    {
                        return await next(context, cancellationToken);
                    }

                    var budget = await usage.GetCallBudgetAsync(scope.ProjectId, taskId, cancellationToken);
                    if (!budget.Over)
                    {
                        return await next(context, cancellationToken);
                    }

                    var payload = new
                    {
                        ok = false,
                        error = $"这个任务已经用了 {budget.Used} 次工具调用，超过项目设定的上限（{budget.Budget}）。" +
                                "要么收敛动作，要么让人调高上限（项目设置 → agent 并发）。",
                        tool = context.Params.Name
                    };

                    return new CallToolResult
                    {
                        IsError = true,
                        Content =
                        [
                            new TextContentBlock
                            {
                                Text = JsonSerializer.Serialize(payload, GuanfuJson.Options)
                            }
                        ]
                    };
                });

                // 先校验凭证的项目范围，再执行工具与审批。
                options.Filters.Request.CallToolFilters.Add(next => async (context, cancellationToken) =>
                {
                    var authorization = context.Services?.GetService<McpProjectAuthorization>();
                    if (authorization is not null)
                    {
                        var started = DateTimeOffset.UtcNow;
                        try
                        {
                            await authorization.EnsureAllowedAsync(
                                context.Params.Name, context.Params.Arguments, cancellationToken);
                        }
                        catch (GuanfuException) when (ToolCallRecorder.SelfAuditedTools.Contains(context.Params.Name))
                        {
                            // 工具没执行，自带的审计不会发生，由门禁记一条拒绝。
                            var recorder = context.Services?.GetService<ToolCallRecorder>();
                            if (recorder is not null)
                            {
                                await recorder.RecordAsync(context.Params.Name, context.Params.Arguments, "denied",
                                    (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds, cancellationToken);
                            }

                            throw;
                        }
                    }

                    return await next(context, cancellationToken);
                });

                // 双保险：就算客户端缓存了工具清单直接调，也会被拦
                options.Filters.Request.CallToolFilters.Add(next => async (context, cancellationToken) =>
                {
                    var state = context.Services?.GetService<McpToolState>();
                    var name = context.Params.Name;

                    if (state is null)
                    {
                        return await next(context, cancellationToken);
                    }

                    if (!state.IsEnabled(name))
                    {
                        // 形状要跟全局兜底一致（{ok:false,error}）——
                        // 之前这里回的是纯文本，agent 只能靠猜来分辨两种错误
                        return new CallToolResult
                        {
                            IsError = true,
                            Content =
                            [
                                new TextContentBlock
                                {
                                    Text = JsonSerializer.Serialize(
                                        new
                                        {
                                            ok = false,
                                            error = $"工具 {name} 已在观复的「MCP 工具」页面被关闭，请先在设置里打开。",
                                            tool = name
                                        },
                                        GuanfuJson.Options)
                                }
                            ]
                        };
                    }

                    return await next(context, cancellationToken);
                });
            })
            .WithToolsFromAssembly(typeof(GuanfuMcpTools).Assembly, GuanfuJson.Options)
            // 有状态会话：Claude Code / Codex 这类老协议客户端在 initialize 时
            // 会拿到 Mcp-Session-Id，之后每次请求都带着它 —— 观复靠它认出
            // 「还是那次干活」，agent 才不用每次都自己传 projectId。
            // 新协议（2026-07-28 及以后）的客户端仍然按请求无状态服务。
            .WithHttpTransport(options =>
                options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);

        return services;
    }
}
