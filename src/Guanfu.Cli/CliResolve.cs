using Guanfu.Application.Contracts;
using Guanfu.Application.Services;

namespace Guanfu.Cli;

/// <summary>
/// 引用解析：人嘴里的**编号**与**名字**都要能用。
///
/// 人说「REQ-001」「12 月版本」，不说一串 GUID ——
/// 命令行是给人用的，就该认人话。
/// </summary>
internal static class CliResolve
{
    /// <summary>任务 id 或编号（req001 / REQ-001 都认）。</summary>
    public static async Task<Guid?> TaskIdAsync(GuanfuClient client, string reference, bool requireUnique = false)
    {
        if (Guid.TryParse(reference, out var id))
        {
            return id;
        }

        var limit = requireUnique ? 2 : 1;
        var found = await client.GetAsync<List<TaskSummaryDto>>(
            $"/api/tasks?key={Uri.EscapeDataString(reference)}&limit={limit}");

        if (requireUnique && found.Count > 1)
        {
            throw new GuanfuCliException($"「{reference}」匹配多个任务，请用任务 ID 指定报告范围。");
        }

        return found.Count > 0 ? found[0].Id : null;
    }

    /// <summary>里程碑 id 或名字。</summary>
    public static async Task<Guid?> MilestoneIdAsync(GuanfuClient client, string reference, bool requireUnique = false)
    {
        if (Guid.TryParse(reference, out var id))
        {
            return id;
        }

        // 里程碑是项目下的 —— 先按名字在所有项目里找一份（同名取最近建的）。
        var projects = await client.GetAsync<List<ProjectSummaryDto>>("/api/projects");

        Guid? match = null;
        foreach (var project in projects)
        {
            var milestones = await client.GetAsync<List<MilestoneDto>>(
                $"/api/projects/{project.Id}/milestones");

            foreach (var hit in milestones.Where(m => string.Equals(m.Name, reference, StringComparison.OrdinalIgnoreCase)))
            {
                if (!requireUnique)
                {
                    return hit.Id;
                }
                if (match is not null)
                {
                    throw new GuanfuCliException($"「{reference}」匹配多个迭代版本，请用版本 ID 指定发版范围。");
                }
                match = hit.Id;
            }
        }

        return match;
    }
}
