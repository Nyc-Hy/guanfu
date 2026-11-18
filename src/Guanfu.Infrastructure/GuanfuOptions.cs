namespace Guanfu.Infrastructure;

/// <summary>
/// 观复运行时配置。全部路径相对 <see cref="HomeDirectory"/>，
/// 换电脑时把整个目录搬过去即可。
/// </summary>
public sealed class GuanfuOptions
{
    /// <summary>新的环境变量名。</summary>
    public const string HomeEnvironmentVariable = "GUANFU_HOME";

    /// <summary>
    /// 旧的环境变量名（改名前的）。**继续认** ——
    /// 用户的启动脚本里多半写着它，改名不该让人以为数据没了。
    /// </summary>
    public const string LegacyHomeEnvironmentVariable = "WORKBENCH_HOME";

    public string HomeDirectory { get; set; } = DefaultHomeDirectory();

    /// <summary>
    /// 库文件的位置。改名之前建的库文件叫 workbench.db ——
    /// 它还在就用它（不搬、不删），新库才叫 guanfu.db。
    /// </summary>
    public string DatabasePath => ResolveDatabasePath();

    private string ResolveDatabasePath()
    {
        var current = Path.Combine(HomeDirectory, "guanfu.db");
        var legacy = Path.Combine(HomeDirectory, "workbench.db");

        return !File.Exists(current) && File.Exists(legacy) ? legacy : current;
    }

    public string DocumentsDirectory => Path.Combine(HomeDirectory, "documents");

    public string KeyFilePath => Path.Combine(HomeDirectory, "secret.key");

    /// <summary>
    /// 外部 agent 的运行日志目录。换机时也要带走，否则新机器上看不到之前的运行记录。
    /// </summary>
    public string RunsDirectory => Path.Combine(HomeDirectory, "runs");

    /// <summary>
    /// 审计链头的外部归档路径（文件或目录）。
    ///
    /// 必须**在观复的数据目录之外** —— 放在里面等于没归档：
    /// 拿到库文件的人连归档文件一起改就白搭。配成目录时，
    /// 链头文件放 <c>guanfu-audit-chain.jsonl</c>。
    ///
    /// 不配就不归档：链只在库内可验，拿到库文件的人连哈希一起重算就挡不住。
    /// 这不是缺陷，是这套机制的本分 —— 观复只管诚实地归档与对比，
    /// 归档位置能不能被同样的人改，是部署方的选择。
    /// </summary>
    public string? AuditArchivePath { get; set; }

    /// <summary>
    /// 数据目录的默认位置。
    ///
    /// 改名之前是 <c>~/.workbench</c>。改名不该让人「打开一看数据没了」——
    /// 所以：新的环境变量优先；旧的环境变量仍然认；
    /// 都没配的话，**旧目录还在就继续用它**，否则用新目录。
    /// </summary>
    public static string DefaultHomeDirectory()
    {
        foreach (var variable in new[] { HomeEnvironmentVariable, LegacyHomeEnvironmentVariable })
        {
            var fromEnv = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return Path.GetFullPath(fromEnv);
            }
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var current = Path.Combine(profile, ".guanfu");
        var legacy = Path.Combine(profile, ".workbench");

        return Directory.Exists(legacy) && !Directory.Exists(current) ? legacy : current;
    }
}
