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
    public static async Task<Guid?> TaskIdAsync(GuanfuClient client, string reference)
    {
        if (Guid.TryParse(reference, out var id))
        {
            return id;
        }

        var found = await client.GetAsync<List<TaskSummaryDto>>(
            $"/api/tasks?key={Uri.EscapeDataString(reference)}&limit=1");

        return found.Count > 0 ? found[0].Id : null;
    }

    /// <summary>里程碑 id 或名字。</summary>
    public static async Task<Guid?> MilestoneIdAsync(GuanfuClient client, string reference)
    {
        if (Guid.TryParse(reference, out var id))
        {
            return id;
        }

        // 里程碑是项目下的 —— 先按名字在所有项目里找一份（同名取最近建的）。
        var projects = await client.GetAsync<List<ProjectSummaryDto>>("/api/projects");

        foreach (var project in projects)
        {
            var milestones = await client.GetAsync<List<MilestoneDto>>(
                $"/api/projects/{project.Id}/milestones");

            var hit = milestones.FirstOrDefault(m =>
                string.Equals(m.Name, reference, StringComparison.OrdinalIgnoreCase));

            if (hit is not null)
            {
                return hit.Id;
            }
        }

        return null;
    }
}
