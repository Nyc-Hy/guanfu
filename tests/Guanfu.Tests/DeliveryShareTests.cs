using Guanfu.Application.Services;
using Guanfu.Domain;

namespace Guanfu.Tests;

/// <summary>
/// 交付包的分享链接。
///
/// **交付的最后一公里**：包打好了，项管没有观复账号 ——
/// 他要的是一个能点的链接。链接本身就是那把钥匙，所以：
/// - 库里只存哈希（明文只在签发那一次出现）；
/// - 有有效期（默认 7 天）；
/// - 能撤销；
/// - 认不出 / 过期 / 被撤销 —— 一律当「没有」（不解释是哪一种）。
/// </summary>
public sealed class DeliveryShareTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "wb-share-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

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

    private async Task<(GuanfuTestHost Host, DeliveryPackageService Service, Guid PackageId)> NewPackageAsync()
    {
        var host = new GuanfuTestHost();
        var project = await host.Projects.CreateAsync("交付项目");
        var milestone = await host.Milestones.CreateAsync(project.Id, "12 月版本");
        var task = await host.Tasks.CreateAsync(project.Id, "写脚本的活");

        await host.Milestones.AssignTaskAsync(task.Id, milestone.Id);
        await host.Tasks.AddArtifactAsync(task.Id, ArtifactType.SqlScript, "建表.sql", "CREATE TABLE t(id int);");

        var service = new DeliveryPackageService(
            host.Db, new TempDocumentStore(_root), new TempPackageStore(_root), host.Clock);

        var candidates = await service.ListCandidatesAsync(milestone.Id);
        var package = await service.CreateAsync(
            milestone.Id, candidates.Candidates.Select(c => c.ItemId).ToList(), "测试");

        return (host, service, package.Id);
    }

    [Fact]
    public async Task 签发分享链接后能凭令牌找到包()
    {
        var (host, service, packageId) = await NewPackageAsync();
        using var _ = host;

        var share = await service.CreateShareAsync(packageId, validDays: 7);

        Assert.False(string.IsNullOrWhiteSpace(share.Token));
        Assert.NotNull(share.ExpiresAt);

        var resolved = await service.ResolveShareAsync(share.Token);

        Assert.NotNull(resolved);
        Assert.Equal(packageId, resolved!.Id);
    }

    [Fact]
    public async Task 库里只有令牌的哈希没有明文()
    {
        var (host, service, packageId) = await NewPackageAsync();
        using var _ = host;

        var share = await service.CreateShareAsync(packageId);

        var stored = host.Db.DeliveryPackages.Single(p => p.Id == packageId);

        Assert.NotNull(stored.ShareTokenHash);
        Assert.DoesNotContain(share.Token, stored.ShareTokenHash!);
    }

    [Fact]
    public async Task 乱猜的令牌认不出()
    {
        var (host, service, packageId) = await NewPackageAsync();
        using var _ = host;

        await service.CreateShareAsync(packageId);

        Assert.Null(await service.ResolveShareAsync("不对的令牌"));
        Assert.Null(await service.ResolveShareAsync(null));
    }

    [Fact]
    public async Task 撤销之后链接就失效了()
    {
        var (host, service, packageId) = await NewPackageAsync();
        using var _ = host;

        var share = await service.CreateShareAsync(packageId);

        Assert.NotNull(await service.ResolveShareAsync(share.Token));

        Assert.True(await service.RevokeShareAsync(packageId));

        Assert.Null(await service.ResolveShareAsync(share.Token));
    }

    [Fact]
    public async Task 过期之后链接就失效了()
    {
        var (host, service, packageId) = await NewPackageAsync();
        using var _ = host;

        var share = await service.CreateShareAsync(packageId, validDays: 1);

        // 把时钟拨到过期之后
        host.Clock.Advance(TimeSpan.FromDays(2));

        Assert.Null(await service.ResolveShareAsync(share.Token));
    }
}
