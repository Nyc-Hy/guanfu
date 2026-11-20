namespace Guanfu.Cli;

/// <summary>全局参数。CLI 只有一个入口，静态存着最省事。</summary>
internal static class CliOptions
{
    /// <summary>
    /// 本次命令的显式凭证，优先于对应入口的环境变量。
    /// </summary>
    public static string? Token { get; set; }
}
