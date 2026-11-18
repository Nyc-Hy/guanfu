using System.Text;
using Guanfu.Application;
using Guanfu.Application.Contracts;
using Guanfu.Application.Services;
using Guanfu.Domain;
using Guanfu.Documents;
using Guanfu.Mcp;

namespace Guanfu.Tests;

/// <summary>
/// 文档批量导入。
///
/// 批量入口的价值全在一句话：**一个文件不行，不该把整批打回来**。
/// 需求文档常是「PRD + 接口文档 + 用例」一起给的，
/// 里面混进一个不允许的扩展名是常事 —— 整批打回就得人一个一个试。
/// </summary>
public sealed class DocumentBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wb-batch-" + Guid.NewGuid().ToString("N"));

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

    private (GuanfuTestHost Host, DocumentService Documents) NewService()
    {
        var host = new GuanfuTestHost();
        var registry = new DocumentParserRegistry(
        [
            new MarkdownDocumentParser(),
            new DocxDocumentParser()
        ]);

        return (host, new DocumentService(host.Db, registry, new TempDocumentStore(_root), host.Clock));
    }

    private static Stream Text(string value) => new MemoryStream(Encoding.UTF8.GetBytes(value));

    [Fact]
    public async Task 一个文件失败不牵连其它的()
    {
        var (host, documents) = NewService();
        using var _ = host;

        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "测试任务");

        var result = await documents.UploadBatchAsync(task.Id,
        [
            new DocumentUploadInput("需求.md", Text("# 需求"), null),
            new DocumentUploadInput("坏文件.exe", Text("x"), null),
            new DocumentUploadInput("接口文档.md", Text("# 接口"), null)
        ]);

        Assert.Equal(2, result.Uploaded.Count);
        Assert.Single(result.Failed);
        Assert.Equal("坏文件.exe", result.Failed[0].FileName);
        Assert.Contains("不支持的文件类型", result.Failed[0].Error);

        // 成功的确实落库了
        var listed = await documents.ListAsync(task.Id);
        Assert.Equal(2, listed.Count);
    }

    [Fact]
    public async Task 任务不存在时整批都算不上失败()
    {
        var (host, documents) = NewService();
        using var _ = host;

        await Assert.ThrowsAsync<NotFoundException>(() => documents.UploadBatchAsync(
            Guid.NewGuid(),
            [new DocumentUploadInput("需求.md", Text("# 需求"), null)]));
    }

    [Fact]
    public async Task 一次传太多文件被拒()
    {
        var (host, documents) = NewService();
        using var _ = host;

        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "测试任务");

        var items = Enumerable.Range(0, 51)
            .Select(i => new DocumentUploadInput($"文件{i}.md", Text("# x"), null))
            .ToList();

        var ex = await Assert.ThrowsAsync<GuanfuException>(
            () => documents.UploadBatchAsync(task.Id, items));

        Assert.Contains("最多上传 50 个文件", ex.Message);
    }

    [Fact]
    public async Task MCP一次能传多个文件()
    {
        var (host, documents) = NewService();
        using var _ = host;

        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "测试任务");

        var tools = new DocumentMcpTools(documents);
        var result = await tools.UploadAsync(
            task.Id,
            documents:
            [
                new DocUploadItem("需求.md", "# 需求"),
                new DocUploadItem("坏文件.exe", "x")
            ]);

        // 一个成功一个失败 —— agent 要能同时看到两边
        Assert.False(result.Ok);
        Assert.NotNull(result.Error);
        Assert.Single(result.Documents!);
        Assert.Single(result.Failures!);
        Assert.Equal("坏文件.exe", result.Failures![0].FileName);
    }

    [Fact]
    public async Task MCP单个文件的老用法照旧()
    {
        var (host, documents) = NewService();
        using var _ = host;

        var project = await host.Projects.CreateAsync("测试项目");
        var task = await host.Tasks.CreateAsync(project.Id, "测试任务");

        var tools = new DocumentMcpTools(documents);
        var result = await tools.UploadAsync(task.Id, "需求.md", content: "# 需求");

        Assert.True(result.Ok);
        Assert.NotNull(result.Document);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("PRD-需求.md", "prd")]
    [InlineData("测试用例.md", "testcase")]
    [InlineData("接口文档.md", "apidoc")]
    [InlineData("随便.md", "other")]
    public void 文档类别按文件名猜(string fileName, string expected)
    {
        Assert.Equal(expected, DocumentService.GuessKind(fileName));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
