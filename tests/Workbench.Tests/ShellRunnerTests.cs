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
}
