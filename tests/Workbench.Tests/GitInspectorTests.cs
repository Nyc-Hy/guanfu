using System.Diagnostics;
using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>
/// Git 检视：porcelain 路径解析、porcelain=v2 的分支/HEAD、真实仓库、超时。
///
/// 回归点：git 默认把非 ASCII 路径转义成 "\346\226\207\346\241\243.md"，
/// 解析出来是转义串而不是真实文件名，评审按路径读文件必然找不到（中文名仓库全中招）。
/// </summary>
public class GitInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wb-git-inspect-" + Guid.NewGuid().ToString("N"));

    public GitInspectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    // ---------- porcelain（v1）路径解析 ----------

    [Theory]
    // git 把中文名转义成 UTF-8 字节的八进制，解回来才是真实文件名
    [InlineData("?? \"\\346\\226\\207\\346\\241\\243.md\"", "文档.md")]
    // 前导空格不能被吃掉（AGENTS.md #13）：" M src/x.cs" 的路径是 src/x.cs
    [InlineData(" M src/x.cs", "src/x.cs")]
    [InlineData("?? src/y.cs", "src/y.cs")]
    [InlineData("M  src/z.cs", "src/z.cs")]
    // 重命名取新路径
    [InlineData("R  old.cs -> new.cs", "new.cs")]
    // 带引号的路径
    [InlineData("?? \"a b.txt\"", "a b.txt")]
    public void porcelain路径解析(string line, string expected)
    {
        Assert.Equal(expected, GitInspector.ParsePorcelainPath(line));
    }

    [Fact]
    public void 空行和太短的行当空处理()
    {
        Assert.Equal(string.Empty, GitInspector.ParsePorcelainPath(""));
        Assert.Equal(string.Empty, GitInspector.ParsePorcelainPath(" M "));
        Assert.Equal(string.Empty, GitInspector.ParsePorcelainPath("?? "));
    }

    // ---------- porcelain=v2（--branch 一次拿全） ----------

    [Fact]
    public void v2能解析出分支HEAD和变更文件()
    {
        var output = string.Join('\n',
            "# branch.oid 8d0ac8a900948779b01980c2720eb9b0f9433a38",
            "# branch.head main",
            "1 .M N... 100644 100644 100644 c1b0730 c1b0730 src/x.cs",
            "? 新文件.md");

        var (branch, head, paths) = GitInspector.ParseStatusV2(output);

        Assert.Equal("main", branch);
        Assert.Equal("8d0ac8a900948779b01980c2720eb9b0f9433a38", head);
        Assert.Equal(new[] { "src/x.cs", "新文件.md" }, paths);
    }

    [Fact]
    public void v2游离HEAD映射成HEAD()
    {
        // 老实现 `git rev-parse --abbrev-ref HEAD` 给的是 HEAD，v2 给的是 (detached)
        var (branch, head, paths) = GitInspector.ParseStatusV2(
            "# branch.oid 8d0ac8a900948779b01980c2720eb9b0f9433a38\n# branch.head (detached)\n");

        Assert.Equal("HEAD", branch);
        Assert.Equal("8d0ac8a900948779b01980c2720eb9b0f9433a38", head);
        Assert.Empty(paths);
    }

    [Fact]
    public void v2重命名取新路径()
    {
        var line = "2 R. N... 100644 100644 100644 c1b0730 c1b0730 R100 new.cs\told.cs";

        Assert.Equal("new.cs", GitInspector.ParseStatusV2Path(line));
    }

    [Fact]
    public void v2路径含空格不被切错()
    {
        var line = "1 .M N... 100644 100644 100644 c1b0730 c1b0730 a b.txt";

        Assert.Equal("a b.txt", GitInspector.ParseStatusV2Path(line));
    }

    // ---------- 真实仓库 ----------

    [Fact]
    public void 真实仓库里中文文件名不是转义串()
    {
        var repo = NewRepo();
        File.WriteAllText(Path.Combine(repo, "文档.md"), "# v1\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-m", "init");

        // 提交之后工作区是干净的，文件不会进变更列表；改一下才会
        File.WriteAllText(Path.Combine(repo, "文档.md"), "# v2\n");

        var state = new GitInspector().Inspect(repo);

        Assert.Null(state.Error);
        Assert.Contains("文档.md", state.ChangedFilePaths);
        Assert.DoesNotContain(state.ChangedFilePaths, p => p.Contains("\\346"));
    }

    [Fact]
    public void 真实仓库里未跟踪的中文文件名也是真实文件名()
    {
        var repo = NewRepo();
        File.WriteAllText(Path.Combine(repo, "a.cs"), "x\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-m", "init");

        // 还没纳入版本控制的中文名文件
        File.WriteAllText(Path.Combine(repo, "需求文档.md"), "x\n");

        var state = new GitInspector().Inspect(repo);

        Assert.Null(state.Error);
        Assert.Contains("需求文档.md", state.ChangedFilePaths);
        Assert.DoesNotContain(state.ChangedFilePaths, p => p.Contains("\\346"));
    }

    [Fact]
    public void 分支与HEAD和git一致()
    {
        var repo = NewRepo();
        File.WriteAllText(Path.Combine(repo, "a.cs"), "x\n");
        RunGit(repo, "add", "-A");
        RunGit(repo, "commit", "-m", "init");

        var expectedBranch = RunGit(repo, "rev-parse", "--abbrev-ref", "HEAD").Trim();
        var expectedHead = RunGit(repo, "rev-parse", "HEAD").Trim();

        var state = new GitInspector().Inspect(repo);

        Assert.Null(state.Error);
        Assert.Equal(expectedBranch, state.Branch);
        Assert.Equal(expectedHead, state.HeadSha);
    }

    [Fact]
    public void 还没有提交的仓库返回错误状态()
    {
        var repo = NewRepo();

        var state = new GitInspector().Inspect(repo);

        Assert.NotNull(state.Error);
        Assert.Null(state.Branch);
        Assert.Null(state.HeadSha);
    }

    // ---------- 写操作（GitWriter 共用同一个 runner） ----------

    [Fact]
    public async Task 写操作仍然能提交()
    {
        var repo = NewRepo();
        File.WriteAllText(Path.Combine(repo, "a.cs"), "x\n");

        var result = await new GitWriter().CommitAsync(repo, "init");

        Assert.True(result.Ok, result.Error);
        Assert.Equal(0, new GitInspector().Inspect(repo).ChangedFiles);
    }

    [Fact]
    public async Task 调用方取消原样抛出而不是报成超时()
    {
        var repo = NewRepo();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // 超时和调用方取消是两回事：调用方取消就该原样抛 OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new GitWriter().CommitAsync(repo, "init", cancelled.Token));
    }

    // ---------- 超时 ----------

    /// <summary>
    /// 超时是真的超时：命令挂着不退时到点就抛 TimeoutException。
    /// 老写法把 WaitForExit(10s) 放在两次同步 ReadToEnd 之后，git 一挂住就永远等不到那个超时。
    ///
    /// 这里用 sh 造一个挂住的子进程 —— 产品代码里没有 shell 路径
    /// （git 命令一律 ProcessStartInfo + ArgumentList，AGENTS.md #6/#7）。
    /// </summary>
    [Fact]
    public async Task 命令挂住时到点超时()
    {
        if (OperatingSystem.IsWindows())
        {
            return;  // CI 只跑 ubuntu/macos
        }

        var started = DateTime.UtcNow;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => GitProcessRunner.RunAsync(
            "/bin/sh",
            ["-c", "sleep 30"],
            _root,
            TimeSpan.FromSeconds(1),
            CancellationToken.None));

        var elapsed = DateTime.UtcNow - started;

        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"超时没生效，拖了 {elapsed.TotalSeconds:0.0} 秒");
        Assert.Contains("超时", ex.Message);
    }

    /// <summary>
    /// 超时后整棵进程树都得死：git 挂住时不杀，子进程会继续占着 .git/index.lock，
    /// 之后所有 git 命令都报 "Unable to create '.git/index.lock': File exists"。
    /// 让孙进程在 2 秒后写个标记文件 —— 标记没出现就说明它确实被杀掉了。
    /// </summary>
    [Fact]
    public async Task 超时会杀掉整棵进程树()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var marker = Path.Combine(_root, "still-alive.txt");

        await Assert.ThrowsAsync<TimeoutException>(() => GitProcessRunner.RunAsync(
            "/bin/sh",
            ["-c", $"(sleep 2; touch '{marker}') & wait"],
            _root,
            TimeSpan.FromSeconds(1),
            CancellationToken.None));

        await Task.Delay(TimeSpan.FromSeconds(3));

        Assert.False(File.Exists(marker), "超时后孙进程还活着 —— 进程树没杀干净。");
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
    private static string RunGit(string repoPath, params string[] args)
    {
        var info = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = repoPath,
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
