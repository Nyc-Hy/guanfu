using System.Text;
using Guanfu.Application.Contracts;
using static Guanfu.Cli.CliArgs;

namespace Guanfu.Cli;

/// <summary>
/// 交付打包 —— 本地与 CI 侧的那一端。
///
/// 观复里「按迭代版本勾选 → 打包 → 分享链接」本来只能在界面点，
/// 但交付往往发生在**交付那一侧**：CI 打完包要把链接交给项管。
/// 这个命令让同一件事能在命令行做完。
///
/// 注意：打包与分享**要人工凭证**（东西要发出去，得有人点头）——
/// 用 <c>--token</c> 或 <c>GUANFU_HUMAN_TOKEN</c> 带上。
/// </summary>
internal static class DeliveryCommand
{
    public static async Task<int> RunAsync(string url, List<string> cliArgs)
    {
        var client = new GuanfuClient(url, CliOptions.Token);
        var sub = cliArgs.Count > 0 ? Arg(cliArgs, 0).ToLowerInvariant() : "help";

        switch (sub)
        {
            case "candidates":
            {
                if (cliArgs.Count < 2)
                {
                    Console.Error.WriteLine("用法: guanfu delivery candidates <里程碑ID>");
                    return 1;
                }

                var milestoneId = await CliResolve.MilestoneIdAsync(client, Arg(cliArgs, 1));

                if (milestoneId is null)
                {
                    Console.Error.WriteLine($"找不到迭代版本：{Arg(cliArgs, 1)}");
                    return 1;
                }

                var candidates = await client.GetAsync<DeliveryCandidatesDto>(
                    $"/api/milestones/{milestoneId}/delivery/candidates");

                if (candidates.Candidates.Count == 0)
                {
                    Console.WriteLine("这个迭代版本下面没有可交付的内容。");
                    return 0;
                }

                Console.WriteLine($"{candidates.MilestoneName}（{candidates.ProjectName}）· {candidates.Candidates.Count} 项");
                Console.WriteLine($"{"ID",-38} {"类别",-10} {"名称",-24} 来自任务");
                foreach (var item in candidates.Candidates)
                {
                    Console.WriteLine($"{item.ItemId,-38} {KindLabel(item),-10} {Truncate(item.Title, 24),-24} {item.TaskKey}");
                }

                return 0;
            }

            case "pack":
            {
                if (cliArgs.Count < 2)
                {
                    Console.Error.WriteLine(
                        "用法: guanfu delivery pack <里程碑ID> [--all] [--item 条目ID ...] [--share]");
                    return 1;
                }

                var milestoneId = await CliResolve.MilestoneIdAsync(client, Arg(cliArgs, 1));

                if (milestoneId is null)
                {
                    Console.Error.WriteLine($"找不到迭代版本：{Arg(cliArgs, 1)}");
                    return 1;
                }

                var items = ItemIds(cliArgs);

                if (items.Count == 0)
                {
                    // 没点名就全打 —— 打包本来就是「这个版本要发出去的东西」。
                    var candidates = await client.GetAsync<DeliveryCandidatesDto>(
                        $"/api/milestones/{milestoneId}/delivery/candidates");

                    items = candidates.Candidates.Select(c => c.ItemId).ToList();

                    if (items.Count == 0)
                    {
                        Console.WriteLine("这个迭代版本下面没有可交付的内容。");
                        return 0;
                    }
                }

                var package = await client.PostAsync<DeliveryPackageDto>(
                    $"/api/milestones/{milestoneId}/delivery/packages", new { itemIds = items });

                Console.WriteLine($"已打包 {package.ItemCount} 项 · {package.FileName} · {package.SizeBytes} 字节");

                // 布尔开关：看它在不在参数里（不看它的「值」——
                // 开关后面没有值，GetOption 读不到东西）。
                if (cliArgs.Contains("--share", StringComparer.Ordinal))
                {
                    var share = await client.PostAsync<DeliveryShareDto>(
                        $"/api/delivery-packages/{package.Id}/share", new { });

                    Console.WriteLine();
                    Console.WriteLine("分享链接（发给项管，他不需要观复账号）：");
                    Console.WriteLine($"  {url.TrimEnd('/')}/delivery/{share.Token}");
                    Console.WriteLine($"  有效期到 {share.ExpiresAt?.ToLocalTime():yyyy-MM-dd HH:mm}");
                }

                return 0;
            }

            case "list":
            {
                if (cliArgs.Count < 2)
                {
                    Console.Error.WriteLine("用法: guanfu delivery list <里程碑ID>");
                    return 1;
                }

                var milestoneId = await CliResolve.MilestoneIdAsync(client, Arg(cliArgs, 1));

                if (milestoneId is null)
                {
                    Console.Error.WriteLine($"找不到迭代版本：{Arg(cliArgs, 1)}");
                    return 1;
                }

                var packages = await client.GetAsync<List<DeliveryPackageDto>>(
                    $"/api/milestones/{milestoneId}/delivery/packages");

                if (packages.Count == 0)
                {
                    Console.WriteLine("还没打过包。");
                    return 0;
                }

                Console.WriteLine($"{"包",-40} {"条目",-6} {"大小",-10} 打包人");
                foreach (var package in packages)
                {
                    Console.WriteLine($"{Truncate(package.FileName, 40),-40} {package.ItemCount,-6} {package.SizeBytes,-10} {package.CreatedBy}");
                }

                return 0;
            }

            default:
                Console.Error.WriteLine("""
                    用法:
                      guanfu delivery candidates <里程碑ID>      这个版本下能打包什么
                      guanfu delivery pack <里程碑ID> [--all] [--item 条目ID ...] [--share]
                      guanfu delivery list <里程碑ID>            打过哪些包

                    打包与分享要人工凭证（--token 或 GUANFU_HUMAN_TOKEN）。
                    """);
                return 1;
        }
    }

    private static List<Guid> ItemIds(List<string> cliArgs)
    {
        var ids = new List<Guid>();

        foreach (var raw in cliArgs)
        {
            if (Guid.TryParse(raw, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static string KindLabel(DeliveryCandidateDto item) =>
        item.Kind == "document"
            ? UiText.DocumentKind(item.KindLabel)
            : (item.KindLabel ?? "其它");

    private static string Truncate(string value, int length) =>
        string.IsNullOrEmpty(value) ? value : value.Length <= length ? value : value[..(length - 1)] + "…";
}
