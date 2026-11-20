using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 归档交付线。
///
/// 三件事：
/// ① 产物能改，改完留版本（人改 AI 写的脚本是常态）；
/// ② 产物与文档的类别说人话（SQL 脚本、交付文件）；
/// ③ 迭代版本能勾选打包，交付物是**一个 zip + 一份清单**。
/// </summary>
public sealed class ArchiveDeliveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wb-delivery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>文档原件的落盘（测试用真实文件，好让打包能真的读到字节）。</summary>
    private sealed class TempDocumentStore(string root) : IDocumentStore
    {
        public async Task<StoredFile> SaveAsync(
            Guid taskId,
            string fileName,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            var dir = Path.Combine(root, taskId.ToString());
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, fileName);
            await using (var target = File.Create(path))
            {
                await content.CopyToAsync(target, cancellationToken);
            }

            return new StoredFile(Path.Combine(taskId.ToString(), fileName), new FileInfo(path).Length);
        }

        public string ResolveAbsolutePath(string relativePath) => Path.Combine(root, relativePath);

        public bool Exists(string relativePath) => File.Exists(Path.Combine(root, relativePath));
    }

    /// <summary>交付包的落盘（测试用真实目录，打出来的 zip 能解开验）。</summary>
    private sealed class TempPackageStore(string root) : IDeliveryPackageStore
    {
        public async Task<StoredFile> SaveAsync(
            Guid milestoneId,
            string fileName,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            var dir = Path.Combine(root, milestoneId.ToString());
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, fileName);
            await using (var target = File.Create(path))
            {
                await content.CopyToAsync(target, cancellationToken);
            }

            return new StoredFile(Path.Combine(milestoneId.ToString(), fileName), new FileInfo(path).Length);
        }

        public string ResolveAbsolutePath(string relativePath) => Path.Combine(root, relativePath);
    }

    private static Stream Text(string value) => new MemoryStream(Encoding.UTF8.GetBytes(value));

    // ---- 产物：能改，改完留版本 ----

    [Fact]
    public async Task 改了产物会多一版并留下痕迹()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写脚本的活");

        await host.Tasks.AddArtifactAsync(
            task.Id, ArtifactType.SqlScript, "建表.sql", "SELECT 1;");

        var artifacts = new ArtifactService(host.Db, host.Clock);

        var updated = await artifacts.UpdateAsync(
            task.Id,
            (await artifacts.ListAsync(task.Id)).Single().Id,
            "建表.sql",
            "SELECT 2;",
            "张三",
            "生产库要改字段名");

        Assert.Equal(2, updated.Revision);
        Assert.Equal("张三", updated.EditedBy);
        Assert.NotNull(updated.UpdatedAt);

        // 原来的那一版还在历史里 —— 改不能抹掉过去
        var revisions = await artifacts.ListRevisionsAsync(task.Id, updated.Id);
        Assert.Equal(2, revisions.Count);
        Assert.Contains(revisions, r => r.Version == 1 && r.Content == "SELECT 1;");
        Assert.Contains(revisions, r => r.Version == 2 && r.Content == "SELECT 2;");

        // 流水里也留了一笔
        var events = host.Db.StatusEvents
            .Where(e => e.TaskId == task.Id && e.EventType == TaskEventTypes.ArtifactUpdated)
            .ToList();

        var evt = Assert.Single(events);
        Assert.Equal("张三", evt.Actor);
        Assert.Contains("建表.sql", evt.Payload ?? string.Empty);
    }

    [Fact]
    public async Task 内容没变就不多一版()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写脚本的活");

        await host.Tasks.AddArtifactAsync(task.Id, ArtifactType.SqlScript, "a.sql", "SELECT 1;");

        var artifacts = new ArtifactService(host.Db, host.Clock);
        var artifact = (await artifacts.ListAsync(task.Id)).Single();

        var updated = await artifacts.UpdateAsync(task.Id, artifact.Id, "a.sql", "SELECT 1;", "张三", null);

        Assert.Equal(1, updated.Revision);
    }

    [Fact]
    public async Task 内存里拿到的旧值不会把版本号算错()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "写脚本的活");

        await host.Tasks.AddArtifactAsync(task.Id, ArtifactType.SqlScript, "a.sql", "SELECT 1;");

        // 模拟「页面早就加载过、实体一直在跟踪里」：
        // 库里已经是第 5 版，内存里那份还是第 1 版。
        await host.Db.Database.ExecuteSqlRawAsync(
            "UPDATE artifacts SET Revision = 5, Content = 'SELECT 5;'");

        var artifacts = new ArtifactService(host.Db, host.Clock);
        var artifact = (await artifacts.ListAsync(task.Id)).Single();

        var updated = await artifacts.UpdateAsync(
            task.Id, artifact.Id, "a.sql", "SELECT 6;", "张三", null);

        // 版本号要接着库里那份走（5 → 6），不能从旧值接着算
        Assert.Equal(6, updated.Revision);
        Assert.Equal("SELECT 6;", updated.Content);

        var revisions = await artifacts.ListRevisionsAsync(task.Id, artifact.Id);
        Assert.Single(revisions, r => r.Version == 5 && r.Content == "SELECT 5;");
    }

    // ---- 类别说人话 ----

    [Fact]
    public void 产物类型认中文说法()
    {
        Assert.True(UiText.TryParseArtifactType("SQL 脚本", out var type));
        Assert.Equal(ArtifactType.SqlScript, type);

        Assert.True(UiText.TryParseArtifactType("SqlScript", out var named));
        Assert.Equal(ArtifactType.SqlScript, named);

        Assert.False(UiText.TryParseArtifactType("不存在的东西", out _));
        Assert.Equal("SQL 脚本", UiText.ArtifactTypeLabel(ArtifactType.SqlScript));
    }

    [Fact]
    public void 脚本文件被归成交付文件()
    {
        Assert.Equal("delivery", DocumentService.GuessKind("变更脚本.sql"));
        Assert.Equal("delivery", DocumentService.GuessKind("deploy.sh"));
        Assert.Equal("prd", DocumentService.GuessKind("订单 PRD.md"));
    }

    [Fact]
    public void 交付文件是文档类别之一()
    {
        Assert.Contains(DocumentKinds.Delivery, DocumentKinds.All);
        Assert.Equal("交付文件", UiText.DocumentKind(DocumentKinds.Delivery));
    }

    // ---- 打包交付 ----

    [Fact]
    public async Task 勾选之后打出一个包和一份清单()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12 月版本");

        var task = await host.Tasks.CreateAsync(project.Id, "写脚本的活");
        await host.Milestones.AssignTaskAsync(task.Id, milestone.Id);

        await host.Tasks.AddArtifactAsync(
            task.Id, ArtifactType.SqlScript, "建表.sql", "CREATE TABLE t(id int);");

        var registry = new Documents.DocumentParserRegistry([new Documents.MarkdownDocumentParser()]);
        var documents = new DocumentService(host.Db, registry, new TempDocumentStore(_root), host.Clock);
        await documents.UploadAsync(task.Id, "发布说明.md", Text("# 发布说明\n\n- 上线时间\n"));

        var delivery = new DeliveryPackageService(
            host.Db, new TempDocumentStore(_root), new TempPackageStore(_root), host.Clock);

        // 候选池：文档与产物都在
        var candidates = await delivery.ListCandidatesAsync(milestone.Id);
        Assert.Equal(2, candidates.Candidates.Count);
        Assert.Contains(candidates.Candidates, c => c.Kind == "document");
        Assert.Contains(candidates.Candidates, c => c.Kind == "artifact" && c.Title == "建表.sql");

        // 勾两项打包
        var package = await delivery.CreateAsync(
            milestone.Id,
            candidates.Candidates.Select(c => c.ItemId).ToList(),
            "李四");

        Assert.Equal(2, package.ItemCount);
        Assert.Equal("李四", package.CreatedBy);
        Assert.NotNull(package.Manifest);
        Assert.Contains("12 月版本", package.Manifest);
        Assert.Contains("建表.sql", package.Manifest);

        // 包真的落盘了，能解开
        var path = await delivery.ResolvePathAsync(package);
        Assert.True(File.Exists(path));

        using (var archive = ZipFile.OpenRead(path))
        {
            var names = archive.Entries.Select(e => e.FullName).ToList();

            Assert.Contains("清单.md", names);
            Assert.Contains("manifest.json", names);
            Assert.Contains(names, n => n.EndsWith("建表.sql", StringComparison.Ordinal));
            Assert.Contains(names, n => n.EndsWith("发布说明.md", StringComparison.Ordinal));

            var manifest = archive.GetEntry("清单.md")!;
            using var reader = new StreamReader(manifest.Open());
            var text = await reader.ReadToEndAsync();
            Assert.Contains("李四", text);
        }

        // 打包记录落在账上
        var stored = host.Db.DeliveryPackages.Single();
        Assert.Equal(2, stored.ItemCount);
        Assert.Equal(milestone.Id, stored.MilestoneId);

        // 涉及的活都留了一笔痕迹
        var events = host.Db.StatusEvents
            .Where(e => e.EventType == TaskEventTypes.DeliveryPacked)
            .ToList();

        Assert.Single(events);
        Assert.Equal(task.Id, events[0].TaskId);
    }

    [Fact]
    public async Task 没勾东西打不出包()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12 月版本");

        var delivery = new DeliveryPackageService(
            host.Db, new TempDocumentStore(_root), new TempPackageStore(_root), host.Clock);

        await Assert.ThrowsAsync<GuanfuException>(
            () => delivery.CreateAsync(milestone.Id, [], "李四"));
    }

    [Fact]
    public async Task 发版勾选的SQL原件缺失时不生成交付包()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12 月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "迁移任务", milestoneId: milestone.Id);
        var document = new Document { TaskId = task.Id, FileName = "迁移.sql", StoredPath = "缺失.sql" };
        host.Db.Documents.Add(document);
        await host.Db.SaveChangesAsync();
        var delivery = new DeliveryPackageService(
            host.Db, new TempDocumentStore(_root), new TempPackageStore(_root), host.Clock);

        var error = await Assert.ThrowsAsync<GuanfuException>(() =>
            delivery.CreateAsync(milestone.Id, [document.Id], release: new ReleasePackageOptions(
                [new ReleaseSqlStep(document.Id, "deploy", "测试库")])));

        Assert.Contains("迁移.sql", error.Message);
        Assert.Empty(await host.Db.DeliveryPackages.ToListAsync());
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task 别的迭代版本的东西进不来()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12 月版本");
        var other = await host.Milestones.CreateAsync(project.Id, "1 月版本");

        var task = await host.Tasks.CreateAsync(project.Id, "写脚本的活");
        await host.Milestones.AssignTaskAsync(task.Id, other.Id);

        await host.Tasks.AddArtifactAsync(task.Id, ArtifactType.SqlScript, "建表.sql", "SELECT 1;");

        var delivery = new DeliveryPackageService(
            host.Db, new TempDocumentStore(_root), new TempPackageStore(_root), host.Clock);

        var candidates = await delivery.ListCandidatesAsync(milestone.Id);
        Assert.Empty(candidates.Candidates);

        var artifactId = host.Db.Artifacts.Single().Id;

        await Assert.ThrowsAsync<GuanfuException>(
            () => delivery.CreateAsync(milestone.Id, [artifactId], "李四"));
    }
}
