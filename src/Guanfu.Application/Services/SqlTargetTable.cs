namespace Guanfu.Application.Services;

/// <summary>
/// 从写操作 SQL 里认出「动的是哪张表」。
///
/// 用途：表级规则（<see cref="Domain.DbTableRule"/>）能配「这张表免审批写」，
/// 所以得知道 SQL 打到哪张表上。
///
/// 认不出来时返回 null —— 调用方必须按「要审批」处理。
/// 宁可多问一次审批，也不能因为认错表就放行。
/// </summary>
public static class SqlTargetTable
{
    /// <summary>认出写操作的目标表。认不出来返回 null。</summary>
    public static string? TryExtract(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        var tokens = Tokenize(sql);
        if (tokens.Count == 0)
        {
            return null;
        }

        var keyword = tokens[0].Text.ToUpperInvariant();

        return keyword switch
        {
            "UPDATE" => ReadIdentifier(tokens, 1),
            "INSERT" => ReadAfterInsert(tokens),
            "DELETE" => ReadAfterDelete(tokens),
            "MERGE" => ReadAfterMerge(tokens),
            "TRUNCATE" => ReadAfterTruncate(tokens),
            _ => null
        };
    }

    private static string? ReadAfterInsert(List<Token> tokens)
    {
        // INSERT INTO [dbo].[Orders] (...) / INSERT [dbo].[Orders] (...)
        for (var i = 1; i < tokens.Count; i++)
        {
            if (tokens[i].Text.Equals("INTO", StringComparison.OrdinalIgnoreCase))
            {
                return ReadIdentifier(tokens, i + 1);
            }
        }

        // INSERT dbo.Orders ...（INTO 可省）
        return ReadIdentifier(tokens, 1);
    }

    private static string? ReadAfterDelete(List<Token> tokens)
    {
        // DELETE FROM tbl / DELETE alias FROM tbl / DELETE tbl
        for (var i = 1; i < tokens.Count; i++)
        {
            if (tokens[i].Text.Equals("FROM", StringComparison.OrdinalIgnoreCase))
            {
                return ReadIdentifier(tokens, i + 1);
            }
        }

        return ReadIdentifier(tokens, 1);
    }

    private static string? ReadAfterMerge(List<Token> tokens)
    {
        // MERGE INTO target USING src ON ... WHEN MATCHED THEN UPDATE ...
        for (var i = 1; i < tokens.Count; i++)
        {
            if (tokens[i].Text.Equals("INTO", StringComparison.OrdinalIgnoreCase))
            {
                return ReadIdentifier(tokens, i + 1);
            }
        }

        return null;
    }

    private static string? ReadAfterTruncate(List<Token> tokens)
    {
        // TRUNCATE TABLE tbl
        for (var i = 1; i < tokens.Count; i++)
        {
            if (tokens[i].Text.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
            {
                return ReadIdentifier(tokens, i + 1);
            }
        }

        return null;
    }

    /// <summary>
    /// 读一个表标识符。可能是 <c>Orders</c>、<c>dbo.Orders</c>、
    /// <c>[dbo].[Orders]</c>、<c>"dbo"."Orders"</c>。
    /// </summary>
    private static string? ReadIdentifier(List<Token> tokens, int index)
    {
        if (index < 0 || index >= tokens.Count)
        {
            return null;
        }

        var parts = new List<string>();
        var i = index;

        while (i < tokens.Count)
        {
            var token = tokens[i];

            if (token.IsIdentifier)
            {
                parts.Add(token.Text);
                i++;

                // a.b.c —— 继续吃掉点号后面的段
                if (i + 1 < tokens.Count && tokens[i].Text == "." && tokens[i + 1].IsIdentifier)
                {
                    i++;
                    continue;
                }

                break;
            }

            break;
        }

        return parts.Count == 0 ? null : string.Join(".", parts);
    }

    private sealed record Token(string Text, bool IsIdentifier);

    /// <summary>
    /// 分词：只区分「标识符」和「其它」。
    /// 方括号 / 双引号包起来的是标识符，其余按空白与符号切。
    /// </summary>
    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < sql.Length)
        {
            var ch = sql[i];

            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            // 行注释
            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            // 块注释
            if (ch == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(sql.Length, i + 2);
                continue;
            }

            // 字符串
            if (ch == '\'')
            {
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'')
                        {
                            i += 2;
                            continue;
                        }

                        i++;
                        break;
                    }

                    i++;
                }

                continue;
            }

            // 带引号的标识符：[dbo].[Orders] / "dbo"."Orders"
            if (ch is '[' or '"')
            {
                var close = ch == '[' ? ']' : '"';
                var start = i + 1;
                i++;

                while (i < sql.Length && sql[i] != close)
                {
                    i++;
                }

                var text = sql[start..Math.Min(i, sql.Length)];
                i = Math.Min(sql.Length, i + 1);
                tokens.Add(new Token(text, true));
                continue;
            }

            // 裸标识符
            if (char.IsLetter(ch) || ch == '_' || ch == '#')
            {
                var start = i;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '#' or '@'))
                {
                    i++;
                }

                tokens.Add(new Token(sql[start..i], true));
                continue;
            }

            // 标点（尤其是点号，用来拼 schema.table）
            var symbol = ch.ToString();
            tokens.Add(new Token(symbol, false));
            i++;
        }

        return tokens;
    }
}
