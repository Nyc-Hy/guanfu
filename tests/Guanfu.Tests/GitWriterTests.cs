using System.Diagnostics;
using Guanfu.Infrastructure;

namespace Guanfu.Tests;

/// <summary>
/// git push 的参数拼装与注入防护。
///
/// git 的语法是 push [&lt;remote&gt; [&lt;refspec&gt;]]，只给一个位置参数时它当远端名 ——
/// 「只给分支」如果不补 origin，就会变成 git push main，git 去找一个叫 main 的远端然后失败。
///
/// branch/remote 来自外部（agent 经 MCP 传入），直入 argv 就是注入面：
/// --force / --delete 会被当选项，ext::sh -c … 会触发外部传输协议执行任意命令。
/// </summary>
public class GitWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wb-git-write-" + Guid.NewGuid().ToString("N"));

    public GitWriterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    // ---------- 正常拼装 ----------

    [Fact]
    public void 只给分支时补上origin()
    {
        // refspec 前有 -- 与选项隔开（git push <remote> -- <branch> 是合法语法）
        Assert.Equal(new[] { "push", "origin", "--", "main" }, GitWriter.BuildPushArgs("main", null));
    }

    [Fact]
    public void 远端与分支都给出时保持顺序()
    {
        Assert.Equal(new[] { "push", "upstream", "--", "dev" }, GitWriter.BuildPushArgs("dev", "upstream"));
    }

    [Fact]
    public void 只给远端时只推远端()
    {
        Assert.Equal(new[] { "push", "upstream" }, GitWriter.BuildPushArgs(null, "upstream"));
    }

    [Fact]
    public void 都不给时用默认推送()
    {
        Assert.Equal(new[] { "push" }, GitWriter.BuildPushArgs(null, null));
    }

    [Theory]
    [InlineData("main:refs/heads/main")]  // 正常 refspec（单冒号）放行
    [InlineData("HEAD")]
    [InlineData("feature/x-1.2")]
    [InlineData("release_2026")]
    public void 正常分支名放行(string branch)
    {
        var args = GitWriter.BuildPushArgs(branch, "origin");
        Assert.Equal(branch, args[^1]);
    }

    // ---------- 注入拒绝 ----------

    [Theory]
    [InlineData("--force")]
    [InlineData("--delete")]
    [InlineData("-f")]
    [InlineData("ext::sh -c touch /tmp/pwned")]
    [InlineData("main branch")]      // 空格
    [InlineData("a\tb")]             // 制表符
    [InlineData("main\nx")]          // 换行
    [InlineData("+main")]            // 强推 refspec，等价 --force
    [InlineData(":main")]            // 删除 refspec，等价 --delete
    public void 注入分支被拒绝(string branch)
    {
        Assert.Throws<ArgumentException>(() => GitWriter.BuildPushArgs(branch, null));
    }

    [Theory]
    [InlineData("--force")]
    [InlineData("ext::sh")]
    [InlineData("https://evil.example/repo")]  // URL 含冒号，不是远端名
    [InlineData("a b")]
    [InlineData("origin;rm")]
    [InlineData("origin\n")]
    public void 注入远端被拒绝(string remote)
    {
        Assert.Throws<ArgumentException>(() => GitWriter.BuildPushArgs("main", remote));
    }

    [Fact]
    public async Task PushAsync遇到注入返回结构化错误而不是抛异常()
    {
        var repo = NewRepo();

        var result = await new GitWriter().PushAsync(repo, "--force");

        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Contains("选项", result.Error);
    }

    // ---------- 真实仓库 ----------

    [Fact]
    public async Task 推送到本地裸仓库仍然能成功()
    {
        var remote = Path.Combine(_root, "remote.git");
        RunGit(_root, "init", "--bare", remote);

        var repo = NewRepo();
        File.WriteAllText(Path.Combine(repo, "a.cs"), "x\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-m", "init");
        RunGit(repo, "remote", "add", "origin", remote);

        var branch = RunGit(repo, "rev-parse", "--abbrev-ref", "HEAD").Trim();
        var result = await new GitWriter().PushAsync(repo, branch);

        Assert.True(result.Ok, result.Error);
        // 远端真的收到了提交
        var head = RunGit(remote, "rev-parse", branch).Trim();
        Assert.Equal(RunGit(repo, "rev-parse", "HEAD").Trim(), head);
    }

    [Fact]
    public async Task 提交信息以连字符开头仍是字面信息()
    {
        // -m 的值是独立 argv，git 无条件把它当值消费，不会解析成选项
        var repo = NewRepo();
        File.WriteAllText(Path.Combine(repo, "a.cs"), "x\n");

        var result = await new GitWriter().CommitAsync(repo, "--amend");

        Assert.True(result.Ok, result.Error);
        Assert.Equal("--amend", RunGit(repo, "log", "-1", "--format=%s").Trim());
    }

    // ---------- 辅助 ----------

    private string NewRepo()
    {
        var repo = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(repo);

        RunGit(repo, "init");
        RunGit(repo, "config", "user.email", "test@example.com");
        RunGit(repo, "config", "user.name", "test");

        return repo;
    }

    /// <summary>跑一条 git 命令（不走 shell，参数用 ArgumentList 传）。</summary>
    private static string RunGit(string workingDir, params string[] args)
    {
        var info = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(15_000);

        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} 失败: {stderr}");

        return stdout;
    }
}