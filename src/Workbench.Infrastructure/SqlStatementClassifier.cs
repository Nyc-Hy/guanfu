using Microsoft.SqlServer.TransactSql.ScriptDom;
using Workbench.Domain;

namespace Workbench.Infrastructure;

/// <summary>
/// T-SQL 语句分类器。
///
/// db_query 声称是「只读查询」，但 ADO.NET 的 <c>ExecuteReader</c> 对 DELETE / UPDATE /
/// DROP 照样**真正执行** —— 所以在语句发到数据库之前必须先判定它的性质。
///
/// 判定方式是**解析成 AST 再看语句类型**，不是「剥掉注释看首关键字」。
/// 差别在这些地方：
/// - <c>WITH c AS (...) SELECT ...</c> 与 <c>WITH c AS (...) UPDATE ...</c>
///   首关键字都是 WITH，只有看 AST 的根节点才能分开；
/// - <c>SELECT * INTO t FROM s</c> 首关键字是 SELECT，但它会建表；
/// - <c>BEGIN SELECT 1 END</c>、<c>IF ... SELECT ...</c> 里的语句藏在控制流里。
///
/// 分类只有三档：
/// - <see cref="SqlStatementKind.Read"/> —— 确认是只读，直接放行；
/// - <see cref="SqlStatementKind.Write"/> —— 确认是写，走审批；
/// - <see cref="SqlStatementKind.Unknown"/> —— **认不出来，一律走审批**。
///   存储过程、解析失败、没见过的语句都归这一档：静态分析答不了的问题，
///   就别猜，交给人。
/// </summary>
public static class SqlStatementClassifier
{
    /// <summary>判定一条 SQL 是读还是写。认不出来时返回 <see cref="SqlStatementKind.Unknown"/>。</summary>
    public static SqlStatementAnalysis Classify(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return new SqlStatementAnalysis(SqlStatementKind.Unknown, "SQL 不能为空。", "Empty");
        }

        IList<ParseError> errors;
        TSqlFragment fragment;

        try
        {
            // 解析器不是线程安全的，每次 new 一个 —— 构造很便宜，别为省这点开销去赌。
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(sql);
            fragment = parser.Parse(reader, out errors);
        }
        catch (Exception ex)
        {
            return new SqlStatementAnalysis(
                SqlStatementKind.Unknown, $"SQL 解析失败：{ex.Message}", "Unparseable");
        }

        if (errors is { Count: > 0 })
        {
            return new SqlStatementAnalysis(
                SqlStatementKind.Unknown, $"SQL 解析失败：{errors[0].Message}", "Unparseable");
        }

        if (fragment is not TSqlScript script)
        {
            return new SqlStatementAnalysis(SqlStatementKind.Unknown, "无法识别的 SQL 结构。", "Unknown");
        }

        var statements = script.Batches.SelectMany(b => b.Statements).ToList();
        if (statements.Count == 0)
        {
            return new SqlStatementAnalysis(SqlStatementKind.Unknown, "SQL 里没有可执行的语句。", "Empty");
        }

        // OPENROWSET / OPENQUERY 能把语句甩到别的服务器上执行 ——
        // 外层看着是 SELECT，实质能跑任意 SQL，静态判定不了它到底干什么。
        var remote = FindRemoteExecution(script);
        if (remote is not null)
        {
            return new SqlStatementAnalysis(
                SqlStatementKind.Unknown,
                $"{remote} 能在远端执行任意语句，无法确认是否只读。",
                remote);
        }

        var findings = new List<Finding>();
        foreach (var statement in statements)
        {
            ClassifyStatement(statement, findings);
        }

        if (findings.Count == 0)
        {
            return new SqlStatementAnalysis(SqlStatementKind.Unknown, "无法识别的 SQL 结构。", "Unknown");
        }

        var write = findings.Find(f => f.Kind == SqlStatementKind.Write);
        if (write is not null)
        {
            return new SqlStatementAnalysis(
                SqlStatementKind.Write, $"检测到写操作：{write.Type}", write.Type);
        }

        var unknown = findings.Find(f => f.Kind == SqlStatementKind.Unknown);
        if (unknown is not null)
        {
            return new SqlStatementAnalysis(
                SqlStatementKind.Unknown, $"无法确认是否只读：{unknown.Type}", unknown.Type);
        }

        return new SqlStatementAnalysis(
            SqlStatementKind.Read,
            "只读查询。",
            string.Join(" + ", findings.Select(f => f.Type).Distinct()));
    }

    /// <summary>
    /// 只读通道的拒绝消息。
    ///
    /// 要说清两件事：**为什么拒**、**接下来该干什么**。
    /// agent 拿到「请改用 db_execute」才知道该走哪条路；只说「不允许」它只会瞎试。
    /// </summary>
    public static string BuildRejection(SqlStatementAnalysis analysis, string sql)
    {
        var snippet = Snippet(sql);

        return analysis.Kind == SqlStatementKind.Write
            ? $"db_query 只放行只读查询，这条是写操作（{analysis.StatementType}）：{snippet}。" +
              "请改用 db_execute（写操作会走人工审批）。"
            : $"db_query 只放行只读查询，这条无法确认是否只读（{analysis.Reason}）：{snippet}。" +
              "按规矩一律要人工审批，请改用 db_execute。";
    }

    /// <summary>错误消息里用的语句摘要：压掉空白，太长就截断。</summary>
    private static string Snippet(string sql)
    {
        var collapsed = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 120 ? collapsed : string.Concat(collapsed.AsSpan(0, 117), "...");
    }

    private sealed record Finding(SqlStatementKind Kind, string Type);

    /// <summary>
    /// 递归判定一条语句。控制流（IF / WHILE / BEGIN…END / TRY…CATCH）要往里看 ——
    /// 写操作藏在里面，外层是控制流，照样会改数据。
    /// </summary>
    private static void ClassifyStatement(TSqlStatement statement, List<Finding> findings)
    {
        switch (statement)
        {
            case SelectStatement select:
                // SELECT ... INTO 会建表 —— 形态上是 SELECT，实质是写
                if (select.Into is not null)
                {
                    findings.Add(new Finding(SqlStatementKind.Write, "SELECT INTO"));
                    return;
                }

                findings.Add(new Finding(SqlStatementKind.Read, "SELECT"));
                return;

            case InsertStatement:
                findings.Add(new Finding(SqlStatementKind.Write, "INSERT"));
                return;

            case UpdateStatement:
                findings.Add(new Finding(SqlStatementKind.Write, "UPDATE"));
                return;

            case DeleteStatement:
                findings.Add(new Finding(SqlStatementKind.Write, "DELETE"));
                return;

            case MergeStatement:
                findings.Add(new Finding(SqlStatementKind.Write, "MERGE"));
                return;

            case TruncateTableStatement:
                findings.Add(new Finding(SqlStatementKind.Write, "TRUNCATE"));
                return;

            case ExecuteStatement:
                // 存储过程做什么，静态分析答不了 —— 一律归「认不出来」
                findings.Add(new Finding(SqlStatementKind.Unknown, "EXEC"));
                return;

            case BeginEndBlockStatement block:
                foreach (var inner in block.StatementList.Statements)
                {
                    ClassifyStatement(inner, findings);
                }

                return;

            case IfStatement ifStatement:
                if (ifStatement.ThenStatement is not null)
                {
                    ClassifyStatement(ifStatement.ThenStatement, findings);
                }

                if (ifStatement.ElseStatement is not null)
                {
                    ClassifyStatement(ifStatement.ElseStatement, findings);
                }

                return;

            case WhileStatement whileStatement:
                if (whileStatement.Statement is not null)
                {
                    ClassifyStatement(whileStatement.Statement, findings);
                }

                return;

            case TryCatchStatement tryCatch:
                foreach (var inner in tryCatch.TryStatements.Statements)
                {
                    ClassifyStatement(inner, findings);
                }

                foreach (var inner in tryCatch.CatchStatements.Statements)
                {
                    ClassifyStatement(inner, findings);
                }

                return;

            default:
                // 其余语句：DDL 一律是写，别的（SET / DECLARE / 权限 / 没见过的）归「认不出来」。
                // 用**节点类型名**判断而不是用户文本 —— 类型名是解析器给的，可信。
                var friendly = FriendlyName(statement);
                findings.Add(new Finding(
                    IsMutation(friendly) ? SqlStatementKind.Write : SqlStatementKind.Unknown,
                    friendly));
                return;
        }
    }

    /// <summary>
    /// 会改东西的语句动词。
    ///
    /// 判的是**节点类型名**（解析器给的），不是用户写的文本 —— 后者能被伪装，前者不能。
    /// </summary>
    private static readonly string[] MutationVerbs =
    [
        "CREATE", "ALTER", "DROP", "RENAME", "GRANT", "DENY", "REVOKE",
        "TRUNCATE", "INSERT", "UPDATE", "DELETE", "MERGE", "BACKUP", "RESTORE",
        "ATTACH", "DETACH", "REFRESH"
    ];

    private static bool IsMutation(string friendlyName)
    {
        foreach (var verb in MutationVerbs)
        {
            if (friendlyName.StartsWith(verb, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 把 AST 节点类型名变成人能读的说法。
    /// <c>DropTableStatement</c> → <c>DROP TABLE</c>，<c>CreateOrAlterProcedureStatement</c> → <c>CREATE OR ALTER PROCEDURE</c>。
    /// </summary>
    internal static string FriendlyName(TSqlStatement statement)
    {
        var name = statement.GetType().Name;

        const string suffix = "Statement";
        if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
        {
            name = name[..^suffix.Length];
        }

        var builder = new System.Text.StringBuilder(name.Length + 8);

        for (var i = 0; i < name.Length; i++)
        {
            var ch = name[i];

            if (char.IsUpper(ch) && builder.Length > 0 && builder[^1] != ' ')
            {
                builder.Append(' ');
            }

            builder.Append(builder.Length == 0 ? char.ToUpperInvariant(ch) : ch);
        }

        return builder.ToString().ToUpperInvariant();
    }

    /// <summary>找出会把语句甩到远端执行的表引用（OPENROWSET / OPENQUERY）。</summary>
    private static string? FindRemoteExecution(TSqlScript script)
    {
        var visitor = new RemoteExecutionVisitor();
        script.Accept(visitor);
        return visitor.Found;
    }

    private sealed class RemoteExecutionVisitor : TSqlFragmentVisitor
    {
        public string? Found { get; private set; }

        public override void ExplicitVisit(OpenRowsetTableReference node) =>
            Found ??= "OPENROWSET";

        public override void ExplicitVisit(OpenQueryTableReference node) =>
            Found ??= "OPENQUERY";
    }
}
