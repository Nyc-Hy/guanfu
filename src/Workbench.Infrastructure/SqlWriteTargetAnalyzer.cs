using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Workbench.Infrastructure;

/// <summary>一条写语句的目标表（已归一到 schema.table，裸表名按 dbo 解释）。</summary>
public sealed record SqlWriteStatementTarget(
    string StatementType,
    IReadOnlyList<string> TargetTables);

/// <summary>
/// 逐语句的写目标分析结果。
/// <see cref="AllIdentifiableWrites"/> 为 true 才可能免审批 ——
/// 任何读 / Unknown / 提取失败 / 混合都会把它置 false。
/// </summary>
public sealed record SqlWriteTargetAnalysis(
    bool AllIdentifiableWrites,
    string? Reason,
    IReadOnlyList<SqlWriteStatementTarget> Statements)
{
    /// <summary>所有语句的目标表去重汇总（审批摘要用）。</summary>
    public IReadOnlyList<string> AllTargetTables =>
        Statements.SelectMany(s => s.TargetTables).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static SqlWriteTargetAnalysis Blocked(string reason) => new(false, reason, []);
}

/// <summary>
/// 把一段 T-SQL 解析成**逐语句**的写目标清单。
///
/// 为什么不能只看首语句：表级规则能配「这张表免审批写」，
/// 旧实现（<c>SqlTargetTable.TryExtract</c>）只分词看第一条语句的目标表，
/// 于是 <c>UPDATE 免批表; DROP TABLE 核心表</c> 整批被当成「免批表」放行 ——
/// 第二条语句根本没被看见。这里用 ScriptDom 把每条语句都拆开，
/// **每一条**都必须是能认出目标表的写，才允许进入免审批判定。
///
/// 与 <see cref="SqlStatementClassifier"/> 同一套解析口径（AST，不看首关键字）：
/// - 控制流（BEGIN…END / IF / WHILE / TRY…CATCH）递归展开成叶子语句；
/// - OUTPUT INTO 会写**另一张表**，那张表也要认出来，认不出就整条判失败；
/// - OPENROWSET / OPENQUERY / OPENDATASOURCE / OPENXML 这类远端/动态通道一律判失败；
/// - 存储过程、SELECT（非 INTO）、认不出的语句一律判失败。
///
/// 认不出来时 <see cref="SqlWriteTargetAnalysis.AllIdentifiableWrites"/> = false，
/// 调用方必须按「要审批」处理 —— 宁可多问一次，也不能认错表就放行。
/// </summary>
public static class SqlWriteTargetAnalyzer
{
    private const string DefaultSchema = "dbo";

    public static SqlWriteTargetAnalysis Analyze(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return SqlWriteTargetAnalysis.Blocked("SQL 不能为空。");
        }

        IList<ParseError> errors;
        TSqlFragment fragment;

        try
        {
            // 解析器不是线程安全的，每次 new 一个（与 SqlStatementClassifier 同口径）。
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(sql);
            fragment = parser.Parse(reader, out errors);
        }
        catch (Exception ex)
        {
            return SqlWriteTargetAnalysis.Blocked($"SQL 解析失败：{ex.Message}");
        }

        if (errors is { Count: > 0 })
        {
            return SqlWriteTargetAnalysis.Blocked($"SQL 解析失败：{errors[0].Message}");
        }

        if (fragment is not TSqlScript script)
        {
            return SqlWriteTargetAnalysis.Blocked("无法识别的 SQL 结构。");
        }

        // 远端/动态执行通道：外层看着是写，实质能把语句甩到别的服务器执行，
        // 静态判定不了它到底动了什么 —— 一律不免审批。
        var remote = FindRemoteExecution(script);
        if (remote is not null)
        {
            return SqlWriteTargetAnalysis.Blocked($"{remote} 属于远端/动态执行通道，无法确认写目标。");
        }

        var leaves = new List<TSqlStatement>();
        foreach (var statement in script.Batches.SelectMany(b => b.Statements))
        {
            Flatten(statement, leaves);
        }

        if (leaves.Count == 0)
        {
            return SqlWriteTargetAnalysis.Blocked("SQL 里没有可执行的语句。");
        }

        var targets = new List<SqlWriteStatementTarget>();
        foreach (var leaf in leaves)
        {
            var target = ExtractLeaf(leaf);
            if (target is null)
            {
                return SqlWriteTargetAnalysis.Blocked(
                    $"第 {targets.Count + 1} 条语句（{FriendlyName(leaf)}）不是可识别目标表的写操作，整批需要人工审批。");
            }

            targets.Add(target);
        }

        return new SqlWriteTargetAnalysis(true, null, targets);
    }

    /// <summary>把控制流展开成叶子语句。写操作藏在 IF / BEGIN…END 里照样会改数据。</summary>
    private static void Flatten(TSqlStatement statement, List<TSqlStatement> leaves)
    {
        switch (statement)
        {
            case BeginEndBlockStatement block:
                foreach (var inner in block.StatementList.Statements)
                {
                    Flatten(inner, leaves);
                }

                return;

            case IfStatement ifStatement:
                if (ifStatement.ThenStatement is not null)
                {
                    Flatten(ifStatement.ThenStatement, leaves);
                }

                if (ifStatement.ElseStatement is not null)
                {
                    Flatten(ifStatement.ElseStatement, leaves);
                }

                return;

            case WhileStatement whileStatement:
                if (whileStatement.Statement is not null)
                {
                    Flatten(whileStatement.Statement, leaves);
                }

                return;

            case TryCatchStatement tryCatch:
                foreach (var inner in tryCatch.TryStatements.Statements)
                {
                    Flatten(inner, leaves);
                }

                foreach (var inner in tryCatch.CatchStatements.Statements)
                {
                    Flatten(inner, leaves);
                }

                return;

            default:
                leaves.Add(statement);
                return;
        }
    }

    /// <summary>
    /// 认出一条叶子写语句的全部目标表。认不出（读 / Unknown / 变量表 / 派生表 /
    /// OUTPUT INTO 他表认不出）返回 null —— 调用方按「要审批」处理。
    /// </summary>
    private static SqlWriteStatementTarget? ExtractLeaf(TSqlStatement statement)
    {
        switch (statement)
        {
            case InsertStatement insert:
                return Build("INSERT", insert.InsertSpecification?.Target, insert.InsertSpecification?.OutputIntoClause);

            case UpdateStatement update:
                return Build("UPDATE", update.UpdateSpecification?.Target, update.UpdateSpecification?.OutputIntoClause);

            case DeleteStatement delete:
                return Build("DELETE", delete.DeleteSpecification?.Target, delete.DeleteSpecification?.OutputIntoClause);

            case MergeStatement merge:
                return Build("MERGE", merge.MergeSpecification?.Target, merge.MergeSpecification?.OutputIntoClause);

            case TruncateTableStatement truncate:
            {
                var table = ToTableName(truncate.TableName);
                return table is null ? null : new SqlWriteStatementTarget("TRUNCATE", [table]);
            }

            case SelectStatement select when select.Into is not null:
            {
                // SELECT … INTO 会建表并写入 —— 形态是 SELECT，实质是写。
                var table = ToTableName(select.Into);
                return table is null ? null : new SqlWriteStatementTarget("SELECT INTO", [table]);
            }

            case CreateTableStatement create:
            {
                var table = ToTableName(create.SchemaObjectName);
                return table is null ? null : new SqlWriteStatementTarget("CREATE TABLE", [table]);
            }

            case AlterTableStatement alter:
            {
                var table = ToTableName(alter.SchemaObjectName);
                return table is null ? null : new SqlWriteStatementTarget("ALTER TABLE", [table]);
            }

            case DropTableStatement drop:
            {
                var tables = new List<string>();
                foreach (var obj in drop.Objects)
                {
                    var table = ToTableName(obj);
                    if (table is null)
                    {
                        return null;
                    }

                    tables.Add(table);
                }

                return tables.Count == 0 ? null : new SqlWriteStatementTarget("DROP TABLE", tables);
            }

            default:
                // SELECT（非 INTO）/ EXEC / SET / 权限 / 没见过的语句：都不是「可识别目标表的写」。
                return null;
        }
    }

    private static SqlWriteStatementTarget? Build(string type, TableReference? target, OutputIntoClause? outputInto)
    {
        var main = ToTableName(target);
        if (main is null)
        {
            return null;
        }

        var tables = new List<string> { main };

        // OUTPUT INTO 把受影响行写进**另一张表** —— 那张表也是写目标，必须一起认。
        // 认不出就整条判失败，不能只盯着主表放行。
        if (outputInto is not null)
        {
            var extra = ToTableName(outputInto.IntoTable);
            if (extra is null)
            {
                return null;
            }

            tables.Add(extra);
        }

        return new SqlWriteStatementTarget(type, tables);
    }

    private static string? ToTableName(TableReference? reference) =>
        reference is NamedTableReference named ? ToTableName(named.SchemaObject) : null;

    /// <summary>
    /// SchemaObjectName → "schema.table"。没写 schema 按 dbo 解释，
    /// 与 DbConnectionService 的规则匹配口径一致（裸表名 = dbo.表）。
    /// </summary>
    private static string? ToTableName(SchemaObjectName? name)
    {
        var table = name?.BaseIdentifier?.Value;
        if (string.IsNullOrWhiteSpace(table))
        {
            return null;
        }

        var schema = name!.SchemaIdentifier?.Value;
        if (string.IsNullOrWhiteSpace(schema))
        {
            schema = DefaultSchema;
        }

        return $"{schema}.{table}";
    }

    private static string FriendlyName(TSqlStatement statement)
    {
        var name = statement.GetType().Name;
        const string suffix = "Statement";
        return name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length
            ? name[..^suffix.Length]
            : name;
    }

    private static string? FindRemoteExecution(TSqlScript script)
    {
        var visitor = new RemoteExecutionVisitor();
        script.Accept(visitor);
        return visitor.Found;
    }

    private sealed class RemoteExecutionVisitor : TSqlFragmentVisitor
    {
        public string? Found { get; private set; }

        public override void ExplicitVisit(OpenRowsetTableReference node) => Found ??= "OPENROWSET";

        public override void ExplicitVisit(OpenQueryTableReference node) => Found ??= "OPENQUERY";

        public override void ExplicitVisit(AdHocTableReference node) => Found ??= "OPENDATASOURCE";

        public override void ExplicitVisit(OpenXmlTableReference node) => Found ??= "OPENXML";
    }
}