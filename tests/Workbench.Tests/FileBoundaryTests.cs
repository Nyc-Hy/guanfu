using System.Text;
using Microsoft.Extensions.Options;
using Workbench.Application.Services;
using Workbench.Domain;
using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>
/// 路径边界回归测试。
/// 核心用例：同前缀兄弟目录（/tmp/wb-root 与 /tmp/wb-root-secrets）不能互相穿越，
/// 软链也不能把文件读出根目录 —— 这两种都能绕过「以 root 开头」的粗糙判断。
/// </summary>
public class FileBoundaryTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _root;
    private readonly string _home;

    public FileBoundaryTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "wb-boundary-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_tmp, "wb-root");
        _home = Path.Combine(_tmp, "wb-home");

        Directory.CreateDirectory(Path.Combine(_root, "src"));
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_root, "src", "app.cs"), "class App { }");
    }

    public void Dispose()
    {
        // 先把软链删掉，再删目录 —— 递归删除遇到指向外部的软链时行为不够稳
        foreach (var link in Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
                     .Where(f => new FileInfo(f).LinkTarget is not null)
                     .ToList())
        {
            File.Delete(link);
        }

        foreach (var link in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories)
                     .Where(d => new DirectoryInfo(d).LinkTarget is not null)
                     .ToList())
        {
            Directory.Delete(link);
        }

        if (Directory.Exists(_tmp))
        {
            Directory.Delete(_tmp, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private static string FullRoot(string root) => Path.GetFullPath(root);

    private FileDocumentStore NewStore() =>
        new(Options.Create(new WorkbenchOptions { HomeDirectory = _home }));

    // ---------- 文件系统工具的路径边界 ----------

    [Fact]
    public void 根目录本身允许()
    {
        Assert.Equal(FullRoot(_root), FileSystemService.ResolveInside(_root, null));
        Assert.Equal(FullRoot(_root), FileSystemService.ResolveInside(_root, ""));
        Assert.Equal(FullRoot(_root), FileSystemService.ResolveInside(_root, "."));
    }

    [Fact]
    public void 根下的文件允许()
    {
        var resolved = FileSystemService.ResolveInside(_root, "src/app.cs");

        Assert.Equal(Path.Combine(FullRoot(_root), "src", "app.cs"), resolved);
    }

    [Fact]
    public void 根外的相对路径被拒绝()
    {
        Assert.Throws<WorkbenchException>(() => FileSystemService.ResolveInside(_root, "../outside.txt"));
    }

    /// <summary>核心回归用例：同前缀兄弟目录。</summary>
    [Fact]
    public void 同前缀兄弟目录被拒绝()
    {
        var secrets = Path.Combine(_tmp, "wb-root-secrets");
        Directory.CreateDirectory(secrets);
        File.WriteAllText(Path.Combine(secrets, "secret.txt"), "top secret");

        var ex = Assert.Throws<WorkbenchException>(
            () => FileSystemService.ResolveInside(_root, "../wb-root-secrets/secret.txt"));

        // 报错要说清楚是哪个路径越界，agent 才能自我纠正
        Assert.Contains("wb-root-secrets", ex.Message);
    }

    [Fact]
    public void 多级上跳被拒绝()
    {
        Assert.Throws<WorkbenchException>(() => FileSystemService.ResolveInside(_root, "../../etc/passwd"));
    }

    [Fact]
    public void 绝对路径被拒绝()
    {
        Assert.Throws<WorkbenchException>(() => FileSystemService.ResolveInside(_root, "/etc/passwd"));
    }

    [Fact]
    public void 根目录带尾斜杠不误伤()
    {
        var withSlash = _root + Path.DirectorySeparatorChar;

        Assert.Equal(FullRoot(_root), FileSystemService.ResolveInside(withSlash, null));
        Assert.Equal(Path.Combine(FullRoot(_root), "src", "app.cs"), FileSystemService.ResolveInside(withSlash, "src/app.cs"));

        // 带斜杠也不能变成绕过
        Assert.Throws<WorkbenchException>(
            () => FileSystemService.ResolveInside(withSlash, "../wb-root-secrets/secret.txt"));
    }

    [Fact]
    public void 根目录是斜杠时不误伤()
    {
        // root = "/" 时任何绝对路径都在根内，不该被拦
        var resolved = FileSystemService.ResolveInside("/", "etc/hosts");

        Assert.Equal(Path.GetFullPath("/etc/hosts"), resolved);
    }

    [Fact]
    public void 软链指向根外被拒绝()
    {
        var outside = Path.Combine(_tmp, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "top secret");

        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), outside);
        File.CreateSymbolicLink(Path.Combine(_root, "secret-link.txt"), Path.Combine(outside, "secret.txt"));

        Assert.Throws<WorkbenchException>(() => FileSystemService.ResolveInside(_root, "link/secret.txt"));
        Assert.Throws<WorkbenchException>(() => FileSystemService.ResolveInside(_root, "secret-link.txt"));
    }

    [Fact]
    public void 软链指向根内不误伤()
    {
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "inside.txt"), "ok");
        Directory.CreateSymbolicLink(Path.Combine(_root, "link"), other);

        var resolved = FileSystemService.ResolveInside(_root, "link/inside.txt");

        Assert.Equal(Path.Combine(FullRoot(_root), "link", "inside.txt"), resolved);
    }

    [Fact]
    public void 根目录本身是软链时不误伤()
    {
        var real = Path.Combine(_tmp, "real-root");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "a.txt"), "ok");
        var link = Path.Combine(_tmp, "linked-root");
        Directory.CreateSymbolicLink(link, real);

        var resolved = FileSystemService.ResolveInside(link, "a.txt");

        Assert.Equal(Path.Combine(Path.GetFullPath(link), "a.txt"), resolved);
    }

    // ---------- 文档存储的路径边界 ----------

    [Fact]
    public void 文档存储不允许逃出HOME()
    {
        var store = NewStore();

        Assert.Throws<UnauthorizedAccessException>(() => store.ResolveAbsolutePath("../wb-home-secrets/x.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => store.ResolveAbsolutePath("../../etc/passwd"));
        Assert.Throws<UnauthorizedAccessException>(() => store.ResolveAbsolutePath("/etc/passwd"));
    }

    [Fact]
    public void 文档存储的同前缀兄弟目录被拒绝()
    {
        var store = NewStore();

        Assert.Throws<UnauthorizedAccessException>(
            () => store.ResolveAbsolutePath("../wb-home-secrets/secret.txt"));
    }

    [Fact]
    public void 越界路径Exists返回false而不是抛异常()
    {
        var store = NewStore();

        Assert.False(store.Exists("../outside.txt"));
    }

    // ---------- 文档存储的写入行为 ----------

    [Fact]
    public async Task 写入后内容完整且没有残留临时文件()
    {
        var store = NewStore();
        var taskId = Guid.NewGuid();
        var content = "hello 原件内容";

        var stored = await store.SaveAsync(taskId, "a.txt", new MemoryStream(Encoding.UTF8.GetBytes(content)));

        var absolute = store.ResolveAbsolutePath(stored.RelativePath);
        Assert.Equal(content, await File.ReadAllTextAsync(absolute));
        Assert.Equal(stored.SizeBytes, new FileInfo(absolute).Length);

        // 临时文件必须已经清掉，不能留下半成品
        var dir = Path.GetDirectoryName(absolute)!;
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public async Task 同名文件不覆盖原件()
    {
        var store = NewStore();
        var taskId = Guid.NewGuid();

        var first = await store.SaveAsync(taskId, "doc.txt", new MemoryStream("第一个"u8.ToArray()));
        var second = await store.SaveAsync(taskId, "doc.txt", new MemoryStream("第二个"u8.ToArray()));

        Assert.NotEqual(first.RelativePath, second.RelativePath);
        Assert.Equal("第一个", await File.ReadAllTextAsync(store.ResolveAbsolutePath(first.RelativePath)));
        Assert.Equal("第二个", await File.ReadAllTextAsync(store.ResolveAbsolutePath(second.RelativePath)));
    }

    /// <summary>核心回归用例：同名文件超过上限时必须报错，而不是把最早那份冲掉。</summary>
    [Fact]
    public async Task 同名文件超过上限时抛异常且不覆盖任何已有内容()
    {
        var store = NewStore();
        var taskId = Guid.NewGuid();
        var dir = Path.Combine(_home, "documents", taskId.ToString("N"));
        Directory.CreateDirectory(dir);

        // 造满所有候选名：doc.txt 加 doc(2)..doc(999)，一共 999 个
        var original = Path.Combine(dir, "doc.txt");
        await File.WriteAllTextAsync(original, "最早的内容");
        for (var i = 2; i < 1000; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, $"doc({i}).txt"), $"副本{i}");
        }

        await Assert.ThrowsAsync<WorkbenchException>(
            () => store.SaveAsync(taskId, "doc.txt", new MemoryStream("新来的"u8.ToArray())));

        // 最早那份原件必须完好
        Assert.Equal("最早的内容", await File.ReadAllTextAsync(original));
        // 文件数不变：既没覆盖，也没留下半成品
        Assert.Equal(999, Directory.GetFiles(dir).Length);
    }

    [Fact]
    public async Task 超过体积上限被拒绝()
    {
        var store = NewStore();
        var taskId = Guid.NewGuid();

        // 已知长度：直接拒
        await Assert.ThrowsAsync<WorkbenchException>(
            () => store.SaveAsync(taskId, "big.bin", new GeneratedStream(FileDocumentStore.MaxFileBytes + 1, seekable: true)));

        // 边读边超限：写了一半也要清干净
        await Assert.ThrowsAsync<WorkbenchException>(
            () => store.SaveAsync(taskId, "big2.bin", new GeneratedStream(FileDocumentStore.MaxFileBytes + 1, seekable: false)));

        var dir = Path.Combine(_home, "documents", taskId.ToString("N"));
        Assert.Empty(Directory.GetFiles(dir));
    }

    /// <summary>按需生成内容的流 —— 测体积上限时不用真造 50MB 内存。</summary>
    private sealed class GeneratedStream(long length, bool seekable) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => false;
        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var remaining = length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            var read = (int)Math.Min(count, remaining);
            Array.Clear(buffer, offset, read);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => length + offset
            };
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
