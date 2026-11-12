using Workbench.Application.Services;
using Workbench.Domain;

namespace Workbench.Tests;

/// <summary>
/// shell.run 的两条底线：白名单拦得住，且命令拼接注入不了。
/// </summary>
public class ShellRunnerTests
{
    private readonly ShellRunner _runner = new();

    [Fact]
    public void 命中白名单前缀放行()
    {
        Assert.True(_runner.IsAllowed(new[] { "dotnet test" }, "dotnet test"));
        Assert.True(_runner.IsAllowed(new[] { "dotnet test" }, "dotnet test --filter Foo"));
    }

    [Fact]
    public void 白名单为空一律拒绝()
    {
        Assert.False(_runner.IsAllowed(Array.Empty<string>(), "dotnet test"));
    }

    [Fact]
    public void 不在白名单里的命令被拒绝()
    {
        Assert.False(_runner.IsAllowed(new[] { "dotnet test" }, "rm -rf /"));
        Assert.False(_runner.IsAllowed(new[] { "dotnet test" }, "npm run build"));
    }

    [Fact]
    public void 前缀只是前缀不能被绕过()
    {
        // "dotnet test" 不应该匹配 "dotnet test-foo"（那不是同一个命令）
        Assert.False(_runner.IsAllowed(new[] { "dotnet test" }, "dotnet test-foo"));
    }

    [Fact]
    public void 大小写不敏感()
    {
        Assert.True(_runner.IsAllowed(new[] { "Dotnet Test" }, "dotnet test"));
    }

    [Fact]
    public void 命令不会被拆成多段执行()
    {
        // 分号不是 shell 分隔符，只是普通参数 —— 走 exec 不会有第二个进程
        var tokens = IGitWriter.Tokenize("dotnet test; rm -rf /");

        Assert.Equal(new[] { "dotnet", "test;", "rm", "-rf", "/" }, tokens);
        Assert.DoesNotContain(";", tokens);
    }

    [Fact]
    public void 引号内的空格不会被拆开()
    {
        var tokens = IGitWriter.Tokenize("agent -p \"帮我做登录接口\"");

        Assert.Equal(new[] { "agent", "-p", "帮我做登录接口" }, tokens);
    }

    [Fact]
    public void JSON白名单解析失败时按空处理()
    {
        Assert.Empty(ShellRunner.ParseAllowed("不是 JSON"));
        Assert.Empty(ShellRunner.ParseAllowed(null));
        Assert.Equal(new[] { "dotnet test" }, ShellRunner.ParseAllowed("[\"dotnet test\"]"));
    }

    [Fact]
    public void 白名单可以按子命令收窄()
    {
        // 用户只勾了 npm run，npm publish 就不该被放行
        Assert.True(_runner.IsAllowed(new[] { "npm run" }, "npm run build"));
        Assert.False(_runner.IsAllowed(new[] { "npm run" }, "npm publish"));
    }

    [Fact]
    public void 白名单只写可执行名时语义不变()
    {
        // 显式勾了整个 git —— 全部子命令都放行，这是用户自己选的
        Assert.True(_runner.IsAllowed(new[] { "git" }, "git status"));
        Assert.True(_runner.IsAllowed(new[] { "git" }, "git push --force"));
    }

    [Fact]
    public void 子命令前缀不会误伤更长的命令名()
    {
        // "git commit" 不该放行 "git commit-tree"
        Assert.False(_runner.IsAllowed(new[] { "git commit" }, "git commit-tree -m x"));
        Assert.True(_runner.IsAllowed(new[] { "git commit" }, "git commit -m x"));
    }

    [Fact]
    public void 白名单条目里带参数时按词前缀匹配()
    {
        Assert.True(_runner.IsAllowed(new[] { "git log --oneline" }, "git log --oneline -5"));
        Assert.False(_runner.IsAllowed(new[] { "git log --oneline" }, "git log --graph"));
    }

    [Fact]
    public void 带引号的命令能正确按词匹配()
    {
        Assert.True(_runner.IsAllowed(new[] { "agent" }, "agent -p \"帮我做登录接口\""));
        Assert.False(_runner.IsAllowed(new[] { "agent -p" }, "agent --prompt \"帮我做登录接口\""));
    }
}
