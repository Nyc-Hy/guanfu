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
