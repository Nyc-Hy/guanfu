using Guanfu.Domain;
using Guanfu.Infrastructure;

namespace Guanfu.Tests;

/// <summary>
/// SQL 语句分类器。
///
/// 这一层是 db_query 的底线：ADO.NET 的 ExecuteReader 对 DELETE / UPDATE / DROP
/// 照样**真正执行**，所以语句在发到数据库之前必须先被判性质。
///
/// 判定的三档口径：
/// - 确认是只读 → 放行；
/// - 确认是写 → 拦；
/// - **认不出来 → 也拦**（归到需要人工审批的那一边）。
///
/// 关键是用 AST 判，不是看首关键字 —— `WITH c AS (...) SELECT` 与
/// `WITH c AS (...) UPDATE` 的首关键字都是 WITH，只有看根节点才分得开。
/// </summary>
public class SqlStatementClassifierTests
{
    private static SqlStatementKind Kind(string sql) =>
        SqlStatementClassifier.Classify(sql).Kind;

    // ---- 只读放行 ----

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("select 1")]
    [InlineData("WITH c AS (SELECT 1 AS x) SELECT * FROM c")]
    [InlineData("WITH c AS (SELECT 1 AS x), d AS (SELECT 2 AS y) SELECT * FROM c JOIN d ON 1=1")]
    [InlineData("SELECT '-- not a comment'")]
    [InlineData("/* comment */ SELECT 1")]
    [InlineData("SELECT 'DROP TABLE t'")]
    [InlineData("SELECT 1 -- 后面是注释：DROP TABLE t")]
    [InlineData("SELECT * FROM dbo.Orders WHERE Id = 1")]
    [InlineData("SELECT * FROM dbo.Orders ORDER BY Id OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY")]
    public void 只读语句判定为读(string sql)
    {
        Assert.Equal(SqlStatementKind.Read, Kind(sql));
    }

    [Fact]
    public void 多条只读语句仍然是读()
    {
        // AST 看得清楚两条都是 SELECT —— 不像「只看首关键字」那样一律拒
        Assert.Equal(SqlStatementKind.Read, Kind("SELECT 1; SELECT 2"));
    }

    [Fact]
    public void 注释里的分号不算多语句()
    {
        Assert.Equal(SqlStatementKind.Read, Kind("SELECT 1 -- ; DROP TABLE t"));
        Assert.Equal(SqlStatementKind.Read, Kind("SELECT 1 /* ; DROP TABLE t */"));
    }

    [Fact]
    public void 字符串里的写操作不算语句()
    {
        Assert.Equal(SqlStatementKind.Read, Kind("SELECT 'DROP TABLE t; DELETE FROM t'"));
        Assert.Equal(SqlStatementKind.Read, Kind("SELECT '' FROM t"));
    }

    // ---- 写操作 ----

    [Theory]
    [InlineData("DROP TABLE t")]
    [InlineData("DELETE FROM t")]
    [InlineData("UPDATE t SET a=1")]
    [InlineData("INSERT INTO t VALUES(1)")]
    [InlineData("TRUNCATE TABLE t")]
    [InlineData("CREATE TABLE t (a int)")]
    [InlineData("ALTER TABLE t ADD b int")]
    public void 写操作判定为写(string sql)
    {
        Assert.Equal(SqlStatementKind.Write, Kind(sql));
    }

    [Fact]
    public void merge必须带分号结尾()
    {
        // T-SQL 规定 MERGE 必须以分号终结。不带分号是语法错 —— 解析不出来就归「未知」，
        // 照样要人工审批，安全性不受影响。
        Assert.Equal(SqlStatementKind.Write, Kind("MERGE t AS s USING u ON 1=1 WHEN MATCHED THEN UPDATE SET a=1;"));
        Assert.Equal(SqlStatementKind.Unknown, Kind("MERGE t AS s USING u ON 1=1 WHEN MATCHED THEN UPDATE SET a=1"));
    }

    [Fact]
    public void 带CTE的写操作判定为写()
    {
        // 首关键字都是 WITH，只有看 AST 的根节点才能分开
        Assert.Equal(SqlStatementKind.Write, Kind("WITH c AS (SELECT 1 AS x) UPDATE t SET a=1"));
        Assert.Equal(SqlStatementKind.Write, Kind("WITH c AS (SELECT 1 AS x) DELETE FROM t"));
        Assert.Equal(SqlStatementKind.Write, Kind("WITH c AS (SELECT 1 AS x) INSERT INTO t SELECT * FROM c"));
    }

    [Fact]
    public void select_into会建表所以是写()
    {
        // 形态上是 SELECT，实质是写
        Assert.Equal(SqlStatementKind.Write, Kind("SELECT * INTO t2 FROM t"));
    }

    [Fact]
    public void 多语句里混着写操作要拦下来()
    {
        Assert.Equal(SqlStatementKind.Write, Kind("SELECT 1; DROP TABLE t"));
    }

    [Fact]
    public void 控制流里藏着写操作要拦下来()
    {
        Assert.Equal(SqlStatementKind.Write, Kind("IF 1=1 BEGIN UPDATE t SET a=1 END"));
        Assert.Equal(SqlStatementKind.Write, Kind("WHILE 1=1 BEGIN DELETE FROM t END"));
    }

    // ---- 认不出来 → 一律要人工审批 ----

    [Theory]
    [InlineData("EXEC sp_who")]
    [InlineData("EXECUTE dbo.SomeProc @x = 1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- 只有注释")]
    [InlineData("'DROP TABLE t'")]
    [InlineData("\"DROP TABLE t\"")]
    [InlineData("这不是 SQL")]
    public void 认不出来的语句归到未知(string sql)
    {
        // 认不出来不是「放行」，是「归到需要人工审批的那一边」
        Assert.Equal(SqlStatementKind.Unknown, Kind(sql));
    }

    [Fact]
    public void 远端执行一律归到未知()
    {
        // OPENROWSET 能在别的服务器上执行任意语句，外层看着是 SELECT
        Assert.Equal(SqlStatementKind.Unknown, Kind("SELECT * FROM OPENROWSET('a','b','c')"));
        Assert.Equal(SqlStatementKind.Unknown, Kind("SELECT * FROM OPENQUERY(srv, 'SELECT 1')"));
    }

    [Fact]
    public void OPENDATASOURCE与OPENROWSET同口径归到未知()
    {
        // OPENDATASOURCE 也是 ad-hoc 远端通道，外层同样是 SELECT ——
        // 以前漏判成 Read，等于放行了远端任意执行
        const string sql =
            "SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=srv;Integrated Security=SSPI').db.dbo.t";

        var analysis = SqlStatementClassifier.Classify(sql);

        Assert.Equal(SqlStatementKind.Unknown, analysis.Kind);
        Assert.Equal("OPENDATASOURCE", analysis.StatementType);
    }

    [Fact]
    public void OPENXML归到未知()
    {
        // OPENXML 把动态构造的 XML 当行集，常配合 sp_xml_preparedocument，
        // 与远端/动态通道同口径
        Assert.Equal(SqlStatementKind.Unknown,
            Kind("SELECT * FROM OPENXML(@h, '/root', 1) WITH (x int)"));
    }

    [Theory]
    [InlineData("EXECUTE AS USER = 'dbo'")]
    [InlineData("EXECUTE AS LOGIN = 'sa'")]
    public void EXECUTE_AS切换执行上下文归到未知(string sql)
    {
        // 切换身份之后能干什么，静态判定不了
        Assert.Equal(SqlStatementKind.Unknown, Kind(sql));
    }

    [Fact]
    public void 解析失败归到未知而不是报错()
    {
        var analysis = SqlStatementClassifier.Classify("SELECT FROM WHERE ((");

        Assert.Equal(SqlStatementKind.Unknown, analysis.Kind);
        Assert.False(string.IsNullOrWhiteSpace(analysis.Reason));
    }

    // ---- 拒绝消息 ----

    [Fact]
    public void 写操作的拒绝消息要指路()
    {
        var analysis = SqlStatementClassifier.Classify("DROP TABLE Orders");
        var message = SqlStatementClassifier.BuildRejection(analysis, "DROP TABLE Orders");

        Assert.Contains("写操作", message);
        Assert.Contains("DROP TABLE Orders", message);
        Assert.Contains("db_execute", message);
    }

    [Fact]
    public void 认不出来的拒绝消息要说清是一律要审批()
    {
        var analysis = SqlStatementClassifier.Classify("EXEC sp_who");
        var message = SqlStatementClassifier.BuildRejection(analysis, "EXEC sp_who");

        Assert.Contains("人工审批", message);
        Assert.Contains("db_execute", message);
    }

    [Fact]
    public void 拒绝消息里的语句摘要要能看懂()
    {
        var analysis = SqlStatementClassifier.Classify("DROP TABLE Orders");
        var message = SqlStatementClassifier.BuildRejection(analysis, "DROP TABLE Orders");

        Assert.Contains("DROP TABLE Orders", message);
    }
}
