using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Workbench.Infrastructure;

/// <summary>
/// 抽出一条只读 SQL 读到的表。
///
/// 表级规则的 <c>AllowRead=false</c> 以前只存不读 —— 规则配了「这张表不许读」，
/// 但 db_query 压根不问规则，读直通就过去了。这个类把「读到的表」列出来，
/// 让调用方逐张表问一句 <c>IsReadAllowedAsync</c>。
///
/// 只认具名表（<c>NamedTableReference</c>）。CTE 名、临时表、表变量不是真实表，
/// 直接跳过 —— 否则 CTE 名撞上规则名会误伤。
/// </summary>
public static class SqlReadTargetExtractor
{
    /// <summary>
    /// 读到的表（schema.table，schema 缺省按 dbo）。
    /// 解析失败返回空列表 —— 调用方要按「认不出来」处理，别猜。
    /// </summary>
    public static IReadOnlyList<string> Extract(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return [];
        }

        IList<ParseError> errors;
        TSqlFragment fragment;

        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(sql);
            fragment = parser.Parse(reader, out errors);
        }
        catch (Exception)
        {
            return [];
        }

        if (errors is { Count: > 0 } || fragment is not TSqlScript script)
        {
            return [];
        }

        var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in script.Batches)
        {
            foreach (var statement in batch.Statements)
            {
                CollectCteNames(statement, cteNames);
            }
        }

        var collector = new TableCollector(cteNames);
        collector.Visit(script);

        return collector.Tables;
    }

    private static void CollectCteNames(TSqlStatement statement, HashSet<string> names)
    {
        switch (statement)
        {
            case SelectStatement select:
                CollectCteNames(select.WithCtesAndXmlNamespaces, names);
                break;
            case UpdateStatement update:
                CollectCteNames(update.WithCtesAndXmlNamespaces, names);
                break;
            case DeleteStatement delete:
                CollectCteNames(delete.WithCtesAndXmlNamespaces, names);
                break;
            case InsertStatement insert:
                CollectCteNames(insert.WithCtesAndXmlNamespaces, names);
                break;
        }
    }

    private static void CollectCteNames(WithCtesAndXmlNamespaces clause, HashSet<string> names)
    {
        if (clause is null)
        {
            return;
        }

        foreach (var cte in clause.CommonTableExpressions)
        {
            names.Add(cte.ExpressionName.Value);
        }
    }

    private sealed class TableCollector(HashSet<string> cteNames) : TSqlFragmentVisitor
    {
        public List<string> Tables { get; } = [];

        public override void ExplicitVisit(NamedTableReference node)
        {
            var schema = node.SchemaObject.SchemaIdentifier?.Value;
            var name = node.SchemaObject.BaseIdentifier?.Value;

            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            // CTE 名不是真实表；临时表（#x）也不在规则表的管辖范围里。
            if (name.StartsWith('#') || cteNames.Contains(name))
            {
                return;
            }

            var qualified = string.IsNullOrEmpty(schema) ? $"dbo.{name}" : $"{schema}.{name}";

            if (!Tables.Contains(qualified, StringComparer.OrdinalIgnoreCase))
            {
                Tables.Add(qualified);
            }
        }
    }
}
