using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 跨项目的并行总量上限。
///
/// 项目级那个上限管的是「这个项目里别挤」；人把一个工作区铺成十个项目时，
/// 每个项目都能自己开满，加起来就把机器压垮了 —— 所以还要有**工作区级**的总量。
///
/// 两个都是节流阀（拦并发过头，不是越权），所以消息里要说清：
/// 现在几个在干、上限是多少、怎么���办。
/// </summary>
public sealed class WorkspaceConcurrencyTests
{
    private static async Task<Guid> WorkspaceIdAsync(GuanfuTestHost host)
    {
        var project = await host.Projects.CreateAsync("第一个项目");
        var workspaceId = await host.Db.Projects
            .Where(p => p.Id == project.Id)
            .Select(p => p.WorkspaceId)
            .FirstAsync();

        return workspaceId;
    }

    [Fact]
    public async Task 工作区上限拦得住跨项目的并发()
    {
        using var host = new GuanfuTestHost();
        var workspaceId = await WorkspaceIdAsync(host);

        await host.Specs.UpdateForWorkspaceAsync(
            workspaceId, new ProjectSpecInput(MaxConcurrentAgents: 1));

        var projectA = await host.Projects.CreateAsync("项目 A");
        var projectB = await host.Projects.CreateAsync("项目 B");

        var taskA = await host.Tasks.CreateAsync(projectA.Id, "A 的活");
        var taskB = await host.Tasks.CreateAsync(projectB.Id, "B 的活");

        await host.Tasks.ClaimAsync(taskA.Id, "agent-a");

        // 另一个 agent 在**别的项目**里领活 —— 项目级上限管不着，工作区级管得着
        var ex = await Assert.ThrowsAsync<GuanfuException>(() =>
            host.Tasks.ClaimAsync(taskB.Id, "agent-b"));

        Assert.Contains("工作区的并行上限", ex.Message);
        Assert.Contains("1", ex.Message);
    }

    [Fact]
    public async Task 同一个agent在别的项目领活不算并发过头()
    {
        using var host = new GuanfuTestHost();
        var workspaceId = await WorkspaceIdAsync(host);

        await host.Specs.UpdateForWorkspaceAsync(
            workspaceId, new ProjectSpecInput(MaxConcurrentAgents: 1));

        var projectA = await host.Projects.CreateAsync("项目 A");
        var projectB = await host.Projects.CreateAsync("项目 B");

        var taskA = await host.Tasks.CreateAsync(projectA.Id, "A 的活");
        var taskB = await host.Tasks.CreateAsync(projectB.Id, "B 的活");

        await host.Tasks.ClaimAsync(taskA.Id, "agent-a");

        // 上限管的是「几个 agent」，不是「几个活」——
        // 同一个 agent 手上多个活不算并发过头（项目级也是这个口径）
        var claimed = await host.Tasks.ClaimAsync(taskB.Id, "agent-a");

        Assert.Equal("agent-a", claimed.Task.ClaimedBy);
    }

    [Fact]
    public async Task 上限为零就是不限()
    {
        using var host = new GuanfuTestHost();
        var workspaceId = await WorkspaceIdAsync(host);

        await host.Specs.UpdateForWorkspaceAsync(
            workspaceId, new ProjectSpecInput(MaxConcurrentAgents: 0));

        var projectA = await host.Projects.CreateAsync("项目 A");
        var projectB = await host.Projects.CreateAsync("项目 B");

        var taskA = await host.Tasks.CreateAsync(projectA.Id, "A 的活");
        var taskB = await host.Tasks.CreateAsync(projectB.Id, "B 的活");

        await host.Tasks.ClaimAsync(taskA.Id, "agent-a");
        var claimed = await host.Tasks.ClaimAsync(taskB.Id, "agent-b");

        Assert.Equal("agent-b", claimed.Task.ClaimedBy);
    }

    [Fact]
    public async Task 上限存在规约里_agent能读到()
    {
        using var host = new GuanfuTestHost();
        var workspaceId = await WorkspaceIdAsync(host);

        await host.Specs.UpdateForWorkspaceAsync(
            workspaceId, new ProjectSpecInput(MaxConcurrentAgents: 3));

        var spec = await host.Specs.GetForWorkspaceAsync(workspaceId);
        Assert.Equal(3, spec.MaxConcurrentAgents);

        // 生效的规约里也带着 —— agent 从上下文就能知道这个工作区总共能同时干几个
        var project = await host.Projects.CreateAsync("读规约的项目");
        var task = await host.Tasks.CreateAsync(project.Id, "读规约的活");

        var effective = await host.Specs.GetEffectiveAsync(task.Id);
        Assert.Equal(3, effective.MaxConcurrentAgents);
    }
}
