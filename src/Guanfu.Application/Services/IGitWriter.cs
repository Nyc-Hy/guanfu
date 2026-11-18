namespace Guanfu.Application.Services;

public sealed record GitCommandResult(bool Ok, string Output, string? Error);

/// <summary>
/// Git 写操作。读操作见 <see cref="IGitInspector"/>；
/// 写操作一律先走审批，由调用方（MCP 工具层）负责审批编排。
/// </summary>
public interface IGitWriter
{
    Task<GitCommandResult> CommitAsync(string repoPath, string message, CancellationToken cancellationToken = default);

    Task<GitCommandResult> PushAsync(string repoPath, string? branch = null, string? remote = null, CancellationToken cancellationToken = default);

    /// <summary>把命令拆成可执行文件 + 参数（不走 shell，避免命令拼接注入）。</summary>
    static IReadOnlyList<string> Tokenize(string command)
    {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var quoteChar = '\0';

        foreach (var c in command)
        {
            if (inQuotes)
            {
                if (c == quoteChar)
                {
                    inQuotes = false;
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                inQuotes = true;
                quoteChar = c;
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }
}
