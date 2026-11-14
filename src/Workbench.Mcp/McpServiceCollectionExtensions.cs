using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Contracts;
using Workbench.Domain;
using Workbench.Application.Services;

namespace Workbench.Mcp;

public static class McpServiceCollectionExtensions
{
    public static IServiceCollection AddWorkbenchMcp(this IServiceCollection services)
    {
        // 工具目录从实际注册的工具反射出来，加了工具不用手改清单
        var catalog = McpToolCatalog.Discover(typeof(WorkbenchMcpTools).Assembly);
        services.AddSingleton<IReadOnlyList<McpToolDescriptor>>(catalog);

        // 开关的内存态：MCP 请求热路径上不查库
        services.AddSingleton<McpToolState>();
        services.AddScoped<IMcpToolService, McpToolSettingsService>();

        // 当前会话：从 HTTP 头认出「这次调用属于哪个 agent / 项目 / 任务」。
        // 绑上之后工具调用不用再自己背项目 GUID。
        services.AddScoped<IAgentSessionAccessor, AgentSessionAccessor>();
        services.AddScoped<ToolCallRecorder>();

        services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation
                {
                    Name = "workbench",
                    Version = "0.1.0",
                    Title = "开发工作台"
                };

                options.ServerInstructions =
                    "开发工作台是状态中枢与审批门禁，不提供 AI 服务。" +
                    "典型流程：workbench_claim_task 领任务 → workbench_get_context 取上下文 → " +
                    "workbench_set_plan 回写计划 → 开发 → workbench_report_test_result 回写测试 → " +
                    "workbench_update_status 推进状态。写数据库必须走 db_execute（需人工审批）。";

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
                        var message = ex is WorkbenchException or InvalidOperationException or ArgumentException
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
                                    Text = JsonSerializer.Serialize(payload, WorkbenchJson.Options)
                                }
                            ]
                        };
                    }
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
                                            error = $"工具 {name} 已在工作台的「MCP 工具」页面被关闭，请先在设置里打开。",
                                            tool = name
                                        },
                                        WorkbenchJson.Options)
                                }
                            ]
                        };
                    }

                    return await next(context, cancellationToken);
                });
            })
            .WithToolsFromAssembly(typeof(WorkbenchMcpTools).Assembly, WorkbenchJson.Options)
            // 有状态会话：Claude Code / Codex 这类老协议客户端在 initialize 时
            // 会拿到 Mcp-Session-Id，之后每次请求都带着它 —— 工作台靠它认出
            // 「还是那次干活」，agent 才不用每次都自己传 projectId。
            // 新协议（2026-07-28 及以后）的客户端仍然按请求无状态服务。
            .WithHttpTransport(options =>
                options.SessionMode = HttpServerSessionMode.StatefulForInitializeClients);

        return services;
    }
}
