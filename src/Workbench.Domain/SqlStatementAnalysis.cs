namespace Workbench.Domain;

/// <summary>
/// SQL 语句的读写性质。
///
/// 分类的目的只有一个：**分清「可以直接跑」和「必须人工点头」**。
/// 认不出来的那一类不是「放行」，而是「归到需要审批的那边」——
/// 宁可多问一次，也不能因为猜错就放过去。
/// </summary>
public enum SqlStatementKind
{
    /// <summary>纯读。可以在只读通道里直接执行。</summary>
    Read = 0,

    /// <summary>写（增删改 / DDL）。必须走审批。</summary>
    Write = 1,

    /// <summary>认不出来（解析失败、存储过程、控制流…）。一律走审批。</summary>
    Unknown = 2
}

/// <summary>SQL 语句的分类结果。</summary>
/// <param name="Kind">读写性质。</param>
/// <param name="Reason">判定依据，写给人与 agent 看。</param>
/// <param name="StatementType">识别出的语句类型（SELECT / INSERT / EXEC …）。</param>
public sealed record SqlStatementAnalysis(
    SqlStatementKind Kind,
    string Reason,
    string StatementType);
