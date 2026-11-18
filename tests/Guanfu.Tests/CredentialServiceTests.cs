using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 访问凭证（团队部署的身份基础）。
///
/// 一个团队把观复部署到服务器上之后，「谁在操作」必须是真的：
/// agent id 自报、审批人自己填名字，出了事查不出是谁。
///
/// 所以凭证在**签发**的时候就绑好身份与可见范围；运行时带的只是钥匙。
/// </summary>
public sealed class CredentialServiceTests
{
    private static CredentialService NewService(GuanfuTestHost host) =>
        new(host.Db, host.Clock);

    [Fact]
    public async Task 签发的凭证明文只给一次()
    {
        using var host = new GuanfuTestHost();
        var service = NewService(host);

        var issued = await service.IssueAsync(CredentialKind.Agent, "agent-a");

        Assert.NotNull(issued.Token);
        Assert.Equal("agent-a", issued.Subject);

        // 之后再也看不到明文了
        var listed = (await service.ListAsync()).Single();
        Assert.Null(listed.Token);
        Assert.Equal(issued.Fingerprint, listed.Fingerprint);
    }

    [Fact]
    public async Task 用明文换身份()
    {
        using var host = new GuanfuTestHost();
        var service = NewService(host);

        var issued = await service.IssueAsync(CredentialKind.Agent, "agent-a");

        var identity = await service.AuthenticateAsync(issued.Token);

        Assert.NotNull(identity);
        Assert.Equal(CredentialKind.Agent, identity!.Kind);
        Assert.Equal("agent-a", identity.Subject);
    }

    [Fact]
    public async Task 错的凭证换不到身份()
    {
        using var host = new GuanfuTestHost();
        var service = NewService(host);

        await service.IssueAsync(CredentialKind.Agent, "agent-a");

        Assert.Null(await service.AuthenticateAsync("不对的钥匙"));
        Assert.Null(await service.AuthenticateAsync(null));
        Assert.Null(await service.AuthenticateAsync("  "));
    }

    [Fact]
    public async Task 吊销之后立刻失效()
    {
        using var host = new GuanfuTestHost();
        var service = NewService(host);

        var issued = await service.IssueAsync(CredentialKind.Agent, "agent-a");
        Assert.NotNull(await service.AuthenticateAsync(issued.Token));

        Assert.True(await service.RevokeAsync(issued.Id));

        Assert.Null(await service.AuthenticateAsync(issued.Token));
    }

    [Fact]
    public async Task 过期了就是过期了()
    {
        using var host = new GuanfuTestHost();
        var service = NewService(host);

        var issued = await service.IssueAsync(
            CredentialKind.Agent,
            "agent-a",
            expiresAt: host.Clock.UtcNow.AddMinutes(-1));

        // 哈希对得上，但过期了 —— 不放行
        Assert.Null(await service.AuthenticateAsync(issued.Token));
    }

    [Fact]
    public async Task 可见范围只认自己那几个项目()
    {
        using var host = new GuanfuTestHost();
        var service = NewService(host);

        var mine = Guid.NewGuid();
        var other = Guid.NewGuid();

        var issued = await service.IssueAsync(CredentialKind.Agent, "agent-a", [mine]);

        var identity = await service.AuthenticateAsync(issued.Token);

        Assert.NotNull(identity);
        Assert.True(identity!.AllowsProject(mine));
        Assert.False(identity.AllowsProject(other));
    }

    [Fact]
    public async Task 没限范围就是全都能看()
    {
        using var host = new GuanfuTestHost();
        var service = NewService(host);

        var issued = await service.IssueAsync(CredentialKind.Agent, "agent-a");

        var identity = await service.AuthenticateAsync(issued.Token);

        Assert.True(identity!.AllowsProject(Guid.NewGuid()));
    }

    [Fact]
    public async Task 身份不能空()
    {
        using var host = new GuanfuTestHost();
        var service = NewService(host);

        await Assert.ThrowsAsync<GuanfuException>(
            () => service.IssueAsync(CredentialKind.Agent, "  "));
    }
}
