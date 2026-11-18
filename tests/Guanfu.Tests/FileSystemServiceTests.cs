using Guanfu.Domain;

namespace Guanfu.Tests;

public class FileSystemServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wb-fs-" + Guid.NewGuid().ToString("N"));

    public FileSystemServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src"));
        File.WriteAllText(Path.Combine(_root, "src", "app.cs"), "class App { }");
        File.WriteAllBytes(Path.Combine(_root, "logo.bin"), [0x00, 0x01, 0x02, 0x00]);
    }

    [Fact]
    public async Task 列出目录内容()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateAsync(project.Id, repoPath: _root);

        var result = await host.Files.ListAsync(project.Id);

        Assert.Contains(result.Entries, e => e.Name == "src" && e.IsDirectory);
    }

    [Fact]
    public async Task 读取文本文件()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateAsync(project.Id, repoPath: _root);

        var result = await host.Files.ReadAsync(project.Id, "src/app.cs");

        Assert.Contains("class App", result.Content);
        Assert.Null(result.Binary);
    }

    [Fact]
    public async Task 二进制文件不返回乱码()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateAsync(project.Id, repoPath: _root);

        var result = await host.Files.ReadAsync(project.Id, "logo.bin");

        Assert.Equal("binary", result.Binary);
        Assert.Empty(result.Content);
    }

    [Fact]
    public async Task 越界路径被拒绝()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");
        await host.Projects.UpdateAsync(project.Id, repoPath: _root);

        await Assert.ThrowsAsync<GuanfuException>(
            () => host.Files.ReadAsync(project.Id, "../../../etc/passwd"));
    }

    [Fact]
    public async Task 项目没配仓库路径时明确报错()
    {
        using var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("测试项目");

        await Assert.ThrowsAsync<GuanfuException>(
            () => host.Files.ListAsync(project.Id));
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
