namespace Guanfu.Cli;

/// <summary>全局参数。CLI 只有一个入口，静态存着最省事。</summary>
internal static class CliOptions
{
    /// <summary>
    /// 人工凭证。优先级：--token 显式 &gt; GUANFU_HUMAN_TOKEN 环境变量 &gt;
    /// 数据目录/.human-token 文件（由 <c>GuanfuClient</c> 解析）。
    /// 没有 token 时，敏感命令（裁决审批、改配置、启动 agent、搬迁工作区）会 401。
    /// </summary>
    public static string? Token { get; set; }
}
