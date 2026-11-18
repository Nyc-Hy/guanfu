using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Services;
using Guanfu.Domain;
using Guanfu.Infrastructure;

namespace Guanfu.Tests;

/// <summary>
/// 产出文件采集。
///
/// 核心立场：观复自己去 git 里数出来，不是 agent 自报。
/// 所以这里守的是两件事：
/// 1. **解析要对** —— git 的 numstat / name-status 输出有好几种形状
///    （二进制、重命名、引号路径），解析错了采出来的清单就是错的；
/// 2. **要幂等** —— 同一个 commit 采两次不能多出重复行。
/// </summary>
public class FileChangeTests
{
    [Fact]
    public async Task 采集失败要留痕不能静默()
    {
        // 静默的失败最坏：人以为产出记上了，其实账上少了一段。
        using var host = new GuanfuTestHost();

        var project = await host.Projects.CreateAsync("测试项目", repoPath: "/tmp/wb-some-repo");
        var task = await host.Tasks.CreateAsync(project.Id, "采不到产出的活");

        // 仓库读不到 → 采集失败
        host.Git.WorkingTreeError = "git 读不了这个目录";

        var service = new FileChangeService(host.Db, host.Git, host.Clock);
        var captured = await service.CaptureWorkingTreeAsync(task.Id, project.Id, "/tmp/wb-some-repo");

        Assert.Equal(0, captured);

        var events = await host.Tasks.ListEventsAsync(task.Id);
        var failure = Assert.Single(events.Where(e => e.EventType == TaskEventTypes.ArtifactCaptureFailed));
        Assert.Contains("git 读不了这个目录", failure.Payload!);
    }

    // ---------- numstat 解析 ----------

    [Fact]
    public void numstat能解析出路径和增删行数()
    {
        var entries = GitInspector.ParseNumStat("3\t1\tsrc/x.cs\n");

        var entry = Assert.Single(entries);
        Assert.Equal("src/x.cs", entry.Path);
        Assert.Equal(3, entry.LinesAdded);
        Assert.Equal(1, entry.LinesDeleted);
        Assert.Null(entry.OldPath);
    }

    [Fact]
    public void numstat二进制文件的增删行数当零()
    {
        // 二进制文件是 "-\t-\tpath" —— 减号不是数字
        var entries = GitInspector.ParseNumStat("-\t-\tassets/logo.png\n");

        var entry = Assert.Single(entries);
        Assert.Equal("assets/logo.png", entry.Path);
        Assert.Equal(0, entry.LinesAdded);
        Assert.Equal(0, entry.LinesDeleted);
    }

    [Fact]
    public void numstat重命名取新路径()
    {
        var entries = GitInspector.ParseNumStat("0\t0\told name.cs => new name.cs\n");

        var entry = Assert.Single(entries);
        Assert.Equal("new name.cs", entry.Path);
        Assert.Equal("old name.cs", entry.OldPath);
    }

    [Fact]
    public void numstat花括号形式重命名也能拆开()
    {
        // 同目录下的重命名 git 给的是 dir/{old => new}
        var entries = GitInspector.ParseNumStat("0\t0\tsub/dir/{old.cs => new.cs}\n");

        var entry = Assert.Single(entries);
        Assert.Equal("sub/dir/new.cs", entry.Path);
        Assert.Equal("sub/dir/old.cs", entry.OldPath);
    }

    [Fact]
    public void numstat带引号的路径能解码()
    {
        // git 把含特殊字符的路径用 C 风格引号包起来（\346\226\207 是 UTF-8 三字节）
        var entries = GitInspector.ParseNumStat("1\t0\t\"\\346\\226\\207\\346\\241\\243.md\"\n");

        var entry = Assert.Single(entries);
        Assert.Equal("文档.md", entry.Path);
    }

    [Fact]
    public void numstat空行和残缺行当空处理()
    {
        Assert.Empty(GitInspector.ParseNumStat(""));
        Assert.Empty(GitInspector.ParseNumStat("\n\n"));
        Assert.Empty(GitInspector.ParseNumStat("只有一行没有制表符\n"));
        Assert.Empty(GitInspector.ParseNumStat("1\t2\n"));
    }

    [Fact]
    public void numstat多行按顺序都解析出来()
    {
        var entries = GitInspector.ParseNumStat(
            "1\t0\ta.cs\n-\t-\tb.png\n2\t3\tc.cs\n");

        Assert.Equal(3, entries.Count);
        Assert.Equal("a.cs", entries[0].Path);
        Assert.Equal("b.png", entries[1].Path);
        Assert.Equal("c.cs", entries[2].Path);
        Assert.Equal(2, entries[2].LinesAdded);
        Assert.Equal(3, entries[2].LinesDeleted);
    }

    // ---------- name-status 解析 ----------

    [Theory]
    [InlineData("A\tnew.cs", "new.cs", "Added")]
    [InlineData("M\tchanged.cs", "changed.cs", "Modified")]
    [InlineData("D\tgone.cs", "gone.cs", "Deleted")]
    public void nameStatus能解析出类型和路径(string line, string path, string kind)
    {
        var entries = GitInspector.ParseNameStatus(line + "\n");

        var entry = Assert.Single(entries);
        Assert.Equal(path, entry.Path);
        Assert.Equal(kind, entry.ChangeKind);
        Assert.Null(entry.OldPath);
    }

    [Fact]
    public void nameStatus重命名给出旧路径和新路径()
    {
        // R100 是状态字母 + 相似度
        var entries = GitInspector.ParseNameStatus("R100\told.cs\tnew.cs\n");

        var entry = Assert.Single(entries);
        Assert.Equal("new.cs", entry.Path);
        Assert.Equal("old.cs", entry.OldPath);
        Assert.Equal("Renamed", entry.ChangeKind);
    }

    [Fact]
    public void nameStatus带引号的路径能解码()
    {
        var entries = GitInspector.ParseNameStatus("M\t\"\\346\\226\\207\\346\\241\\243.md\"\n");

        var entry = Assert.Single(entries);
        Assert.Equal("文档.md", entry.Path);
    }

    [Fact]
    public void nameStatus空行和残缺行当空处理()
    {
        Assert.Empty(GitInspector.ParseNameStatus(""));
        Assert.Empty(GitInspector.ParseNameStatus("\n"));
        Assert.Empty(GitInspector.ParseNameStatus("没有制表符\n"));
    }

    // ---------- numstat 与 name-status 合并 ----------

    [Fact]
    public void 合并后类型取nameStatus行数取numstat()
    {
        var numstat = GitInspector.ParseNumStat("5\t2\tsrc/x.cs\n");
        var nameStatus = GitInspector.ParseNameStatus("M\tsrc/x.cs\n");

        var changes = GitInspector.MergeChanges(numstat, nameStatus);

        var change = Assert.Single(changes);
        Assert.Equal("src/x.cs", change.Path);
        Assert.Equal("Modified", change.ChangeKind);
        Assert.Equal(5, change.LinesAdded);
        Assert.Equal(2, change.LinesDeleted);
    }

    [Fact]
    public void 合并后重命名的类型和旧路径都保住了()
    {
        var numstat = GitInspector.ParseNumStat("0\t0\told.cs => new.cs\n");
        var nameStatus = GitInspector.ParseNameStatus("R100\told.cs\tnew.cs\n");

        var changes = GitInspector.MergeChanges(numstat, nameStatus);

        var change = Assert.Single(changes);
        Assert.Equal("new.cs", change.Path);
        Assert.Equal("Renamed", change.ChangeKind);
        Assert.Equal("old.cs", change.OldPath);
    }

    [Fact]
    public void 文件名里带箭头的不会被误当成重命名()
    {
        // "has => arrow.txt" 是个普通文件名，不是重命名。
        // name-status 说它是 A（新增），合并后也得是 A，不能变成 Renamed。
        var numstat = GitInspector.ParseNumStat("1\t0\thas => arrow.txt\n");
        var nameStatus = GitInspector.ParseNameStatus("A\thas => arrow.txt\n");

        var changes = GitInspector.MergeChanges(numstat, nameStatus);

        var change = Assert.Single(changes);
        Assert.Equal("has => arrow.txt", change.Path);
        Assert.Equal("Added", change.ChangeKind);
        Assert.Null(change.OldPath);
    }

    [Fact]
    public void 重命名拆分只认跟nameStatus对得上的那一种()
    {
        // git 给的 numstat 是 "old arrow.cs => has => arrow.txt"，
        // 拆点有两个可能。name-status 说旧路径是 "old arrow.cs" ——
        // 只有那一种拆法对得上，要按它来。
        var numstat = GitInspector.ParseNumStat("0\t0\told arrow.cs => has => arrow.txt\n");
        var nameStatus = GitInspector.ParseNameStatus("R100\told arrow.cs\thas => arrow.txt\n");

        var changes = GitInspector.MergeChanges(numstat, nameStatus);

        var change = Assert.Single(changes);
        Assert.Equal("has => arrow.txt", change.Path);
        Assert.Equal("Renamed", change.ChangeKind);
        Assert.Equal("old arrow.cs", change.OldPath);
    }

    [Fact]
    public void 合并提交的合并diff只给一个路径也能认()
    {
        // 合并提交的名字状态是 "RM\tpath" —— 只有一个路径段（旧路径给不出来）。
        var nameStatus = GitInspector.ParseNameStatus("RM\trenamed.txt\n");

        var entry = Assert.Single(nameStatus);
        Assert.Equal("renamed.txt", entry.Path);
        Assert.Equal("Renamed", entry.ChangeKind);
        Assert.Null(entry.OldPath);
    }

    // ---------- 落库与幂等 ----------

    [Fact]
    public async Task 同一个commit采两次不会多出重复行()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        host.Git.CommitFileChanges =
        [
            new GitFileChange("src/a.cs", "Modified", 3, 1),
            new GitFileChange("src/b.cs", "Added", 10, 0)
        ];

        var service = new FileChangeService(host.Db, host.Git, host.Clock);

        var first = await service.CaptureCommitAsync(task.Id, project.Id, "/tmp/repo", "sha-1");
        var second = await service.CaptureCommitAsync(task.Id, project.Id, "/tmp/repo", "sha-1");

        Assert.Equal(2, first);
        Assert.Equal(0, second);

        var rows = await host.Db.TaskFileChanges
            .Where(c => c.TaskId == task.Id)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("sha-1", r.CommitSha));
    }

    [Fact]
    public async Task 不同commit的同一个文件各记一条()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        host.Git.CommitFileChanges = [new GitFileChange("src/a.cs", "Modified", 3, 1)];

        var service = new FileChangeService(host.Db, host.Git, host.Clock);

        await service.CaptureCommitAsync(task.Id, project.Id, "/tmp/repo", "sha-1");
        await service.CaptureCommitAsync(task.Id, project.Id, "/tmp/repo", "sha-2");

        var rows = await host.Db.TaskFileChanges
            .Where(c => c.TaskId == task.Id)
            .ToListAsync();

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task 工作区采集重复执行是更新不是新增()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        host.Git.WorkingTreeFileChanges = [new GitFileChange("src/a.cs", "Modified", 3, 1)];

        var service = new FileChangeService(host.Db, host.Git, host.Clock);

        var first = await service.CaptureWorkingTreeAsync(task.Id, project.Id, "/tmp/repo");
        Assert.Equal(1, first);

        // 改了行数再采一次 —— 应该更新原来那行，不是再插一行
        host.Git.WorkingTreeFileChanges = [new GitFileChange("src/a.cs", "Modified", 8, 2)];

        var second = await service.CaptureWorkingTreeAsync(task.Id, project.Id, "/tmp/repo");
        Assert.Equal(0, second);

        var rows = await host.Db.TaskFileChanges
            .Where(c => c.TaskId == task.Id)
            .ToListAsync();

        var row = Assert.Single(rows);
        Assert.Equal(8, row.LinesAdded);
        Assert.Equal(2, row.LinesDeleted);
        Assert.Null(row.CommitSha);
    }

    [Fact]
    public async Task 采集到空结果时不抛异常()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        var service = new FileChangeService(host.Db, host.Git, host.Clock);

        // git 读不到（没有改动）—— 返回 0，不抛异常
        var count = await service.CaptureCommitAsync(task.Id, project.Id, "/tmp/repo", "sha-1");
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task 列出产出文件按时间倒序()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        host.Git.CommitFileChanges = [new GitFileChange("src/first.cs", "Modified", 1, 0)];

        var service = new FileChangeService(host.Db, host.Git, host.Clock);
        await service.CaptureCommitAsync(task.Id, project.Id, "/tmp/repo", "sha-1");

        host.Clock.Advance(TimeSpan.FromMinutes(1));
        host.Git.CommitFileChanges = [new GitFileChange("src/second.cs", "Modified", 2, 0)];

        await service.CaptureCommitAsync(task.Id, project.Id, "/tmp/repo", "sha-2");

        var list = await service.ListAsync(task.Id);

        Assert.Equal(2, list.Count);
        Assert.Equal("src/second.cs", list[0].Path);
        Assert.Equal("src/first.cs", list[1].Path);
    }

    [Fact]
    public async Task 采集到的记录带上会话与作者()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写登录接口");

        // 会话得是真的 —— TaskFileChange.SessionId 上有外键
        var session = new AgentSession
        {
            ProjectId = project.Id,
            AgentId = "agent-1",
            RepoPath = "/tmp/repo"
        };
        host.Db.AgentSessions.Add(session);
        await host.Db.SaveChangesAsync();

        host.Git.CommitFileChanges = [new GitFileChange("src/a.cs", "Modified", 3, 1)];

        var service = new FileChangeService(host.Db, host.Git, host.Clock);

        await service.CaptureCommitAsync(
            task.Id, project.Id, "/tmp/repo", "sha-1", session.Id, "agent-1");

        var row = Assert.Single(await host.Db.TaskFileChanges.ToListAsync());
        Assert.Equal(session.Id, row.SessionId);
        Assert.Equal("agent-1", row.Author);
        Assert.Equal(project.Id, row.ProjectId);
    }
}
