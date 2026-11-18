using Microsoft.Extensions.Options;
using Guanfu.Application.Services;
using Guanfu.Infrastructure;

namespace Guanfu.Tests;

/// <summary>
/// 密钥文件回归测试：权限窗口 + 首启竞态。
/// 密钥一旦被别的进程覆盖，先启动进程加密的密码就再也解不开——所以两份进程必须拿到同一份密钥。
/// </summary>
public class SecretProtectorTests : IDisposable
{
    private readonly string _home;

    public SecretProtectorTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "wb-secret-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private AesSecretProtector NewProtector() =>
        new(Options.Create(new GuanfuOptions { HomeDirectory = _home }));

    [Fact]
    public void 密钥文件权限是0600()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        NewProtector();

        var path = Path.Combine(_home, "secret.key");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void 再次构造沿用同一份密钥()
    {
        var first = NewProtector();
        var second = NewProtector();

        var cipher = first.Protect("数据库密码");

        Assert.Equal("数据库密码", second.Unprotect(cipher));
    }

    /// <summary>核心回归用例：两个进程同时首启，必须拿到同一份密钥。</summary>
    [Fact]
    public async Task 并发首启不会各拿各的密钥()
    {
        var barrier = new Barrier(2);
        var protectors = new ISecretProtector[2];

        var tasks = new List<Task>
        {
            Task.Run(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                protectors[0] = NewProtector();
            }),
            Task.Run(() =>
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                protectors[1] = NewProtector();
            })
        };

        await Task.WhenAll(tasks);

        var cipher = protectors[0].Protect("数据库密码");
        Assert.Equal("数据库密码", protectors[1].Unprotect(cipher));
    }
}
