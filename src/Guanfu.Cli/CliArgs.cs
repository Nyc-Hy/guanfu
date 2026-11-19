namespace Guanfu.Cli;

/// <summary>
/// 命令行参数解析。
///
/// 约定：选项写成 <c>--name value</c>，布尔开关放最后，或用 <c>--name=value</c>。
/// 选项和它的值**不占位置** —— 以前直接用 <c>cliArgs[1]</c>，
/// 选项写在位置参数前面就会读错（<c>guanfu task show --project X REQ-001</c> 读成 "--project"）。
/// </summary>
internal static class CliArgs
{
    /// <summary>
    /// 第 n 个位置参数（0 起）。
    /// </summary>
    public static string Arg(List<string> cliArgs, int index)
    {
        var positionals = Positionals(cliArgs);

        if (index < 0 || index >= positionals.Count)
        {
            throw new GuanfuCliException($"缺少位置参数（第 {index + 1} 个）。");
        }

        return positionals[index];
    }

    /// <summary>全部位置参数（选项和它的值不算）。</summary>
    public static List<string> Positionals(List<string> cliArgs)
    {
        var positionals = new List<string>();

        for (var i = 0; i < cliArgs.Count; i++)
        {
            var arg = cliArgs[i];

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                // 选项的值紧随其后，一并跳过。
                if (i + 1 < cliArgs.Count && !cliArgs[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            positionals.Add(arg);
        }

        return positionals;
    }

    /// <summary>选项的值。</summary>
    public static string? GetOption(List<string> cliArgs, string name)
    {
        var index = cliArgs.IndexOf(name);
        return index >= 0 && index + 1 < cliArgs.Count ? cliArgs[index + 1] : null;
    }
}
