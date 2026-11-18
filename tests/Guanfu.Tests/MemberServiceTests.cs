using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 成员与身份。
///
/// 一个人用的时候不需要这张表 —— 团队用的时候必须有：
/// **谁在操作必须是真的**，不能谁都能自称张三。
///
/// 这里钉三件事：
/// - 密码只存哈希（PBKDF2 + 盐），库里没有明文；
/// - 登录名唯一（大小写不同的同一个名字算重号）；
/// - **人走了，钥匙也要走** —— 停用成员，名下的凭证立刻失效。
/// </summary>
public sealed class MemberServiceTests : IDisposable
{
    public void Dispose()
    {
    }

    [Fact]
    public async Task 建了成员就能用账号密码登录()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        await members.CreateAsync("zhangsan", "张三", "correct-horse");

        var member = await members.AuthenticateAsync("zhangsan", "correct-horse");

        Assert.NotNull(member);
        Assert.Equal("张三", member!.DisplayName);
        Assert.Equal("Member", member.Role);
        Assert.NotNull(member.LastLoginAt);
    }

    [Fact]
    public async Task 密码不对就登不进去()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        await members.CreateAsync("zhangsan", "张三", "correct-horse");

        Assert.Null(await members.AuthenticateAsync("zhangsan", "wrong-password"));
        Assert.Null(await members.AuthenticateAsync("nobody", "correct-horse"));
    }

    [Fact]
    public async Task 库里只有哈希没有明文()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        await members.CreateAsync("zhangsan", "张三", "correct-horse");

        var stored = host.Db.Members.Single().PasswordHash;

        Assert.NotNull(stored);
        Assert.DoesNotContain("correct-horse", stored);
        Assert.StartsWith("pbkdf2$", stored);
    }

    [Fact]
    public async Task 同一个登录名不能建两次()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        await members.CreateAsync("zhangsan", "张三", "correct-horse");

        // 大小写不同也是同一个登录名
        await Assert.ThrowsAsync<GuanfuException>(
            () => members.CreateAsync("ZhangSan", "张三三", "another-one"));
    }

    [Fact]
    public async Task 停用的人登不进去()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        var created = await members.CreateAsync("zhangsan", "张三", "correct-horse");
        await members.SetDisabledAsync(created.Id, true);

        Assert.Null(await members.AuthenticateAsync("zhangsan", "correct-horse"));
    }

    [Fact]
    public async Task 人走了钥匙也要走()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);
        var credentials = new CredentialService(host.Db, host.Clock);

        var member = await members.CreateAsync("zhangsan", "张三", "correct-horse");

        var credential = await credentials.IssueAsync(
            CredentialKind.Agent, "claude-code", memberId: member.Id);

        // 凭证带着归属成员的身份
        var identity = await credentials.AuthenticateAsync(credential.Token);
        Assert.NotNull(identity);
        Assert.Equal(member.Id, identity!.MemberId);
        Assert.False(identity.IsAdmin);

        // 人停用 → 钥匙立刻失效
        await members.SetDisabledAsync(member.Id, true);

        Assert.Null(await credentials.AuthenticateAsync(credential.Token));
    }

    [Fact]
    public async Task 管理员身份跟着凭证走()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);
        var credentials = new CredentialService(host.Db, host.Clock);

        var admin = await members.CreateAsync(
            "boss", "老板", "correct-horse", MemberRole.Admin);

        var credential = await credentials.IssueAsync(
            CredentialKind.Agent, "claude-code", memberId: admin.Id);

        var identity = await credentials.AuthenticateAsync(credential.Token);

        Assert.NotNull(identity);
        Assert.True(identity!.IsAdmin);
    }

    [Fact]
    public async Task 密码太短要拦下来()
    {
        using var host = new GuanfuTestHost();
        var members = new MemberService(host.Db, host.Clock);

        await Assert.ThrowsAsync<GuanfuException>(
            () => members.CreateAsync("zhangsan", "张三", "short"));
    }

    [Fact]
    public void 哈希与校验对得上()
    {
        var hash = PasswordHasher.Hash("correct-horse");

        Assert.True(PasswordHasher.Verify("correct-horse", hash));
        Assert.False(PasswordHasher.Verify("wrong", hash));
        Assert.False(PasswordHasher.Verify("correct-horse", null));
        Assert.False(PasswordHasher.Verify("correct-horse", "garbage"));
    }
}
