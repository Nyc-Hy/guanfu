namespace Workbench.Infrastructure;

/// <summary>
/// 工作台运行时配置。全部路径相对 <see cref="HomeDirectory"/>，
/// 换电脑时把整个目录搬过去即可。
/// </summary>
public sealed class WorkbenchOptions
{
    public const string HomeEnvironmentVariable = "WORKBENCH_HOME";

    public string HomeDirectory { get; set; } = DefaultHomeDirectory();

    public string DatabasePath => Path.Combine(HomeDirectory, "workbench.db");

    public string DocumentsDirectory => Path.Combine(HomeDirectory, "documents");

    public string KeyFilePath => Path.Combine(HomeDirectory, "secret.key");

    /// <summary>
    /// 外部 agent 的运行日志目录。换机时也要带走，否则新机器上看不到之前的运行记录。
    /// </summary>
    public string RunsDirectory => Path.Combine(HomeDirectory, "runs");

    /// <summary>
    /// 审计链头的外部归档路径（文件或目录）。
    ///
    /// 必须**在工作台数据目录之外** —— 放在里面等于没归档：
    /// 拿到库文件的人连归档文件一起改就白搭。配成目录时，
    /// 链头文件放 <c>workbench-audit-chain.jsonl</c>。
    ///
    /// 不配就不归档：链只在库内可验，拿到库文件的人连哈希一起重算就挡不住。
    /// 这不是缺陷，是这套机制的本分 —— 工作台只管诚实地归档与对比，
    /// 归档位置能不能被同样的人改，是部署方的选择。
    /// </summary>
    public string? AuditArchivePath { get; set; }

    public static string DefaultHomeDirectory()
    {
        var fromEnv = Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return Path.GetFullPath(fromEnv);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".workbench");
    }
}
