using Workbench.Infrastructure;

namespace Workbench.Tests;

/// <summary>
/// 写目标分析器（免审批写的判据）。
///
/// 这套分析的结论直接决定「这次写要不要人工审批」——
/// 认错表就等于开了扇不要审批的门。
/// 所以这里把**曾经真的能绕过去**的几种写法都钉住：
/// 别名、跨库、INSERT … EXEC、派生表。
/// </summary>
public class SqlWriteTargetAnalyzerTests
{
    [Fact]
    public void 别名指向另一张表时要解析回真实表()
    {
        // UPDATE t … FROM Secret AS t —— 写的是 Secret，不是 t。
        var analysis = SqlWriteTargetAnalyzer.Analyze(
            "UPDATE Exempt SET Exempt.v = s.v FROM Secret AS Exempt JOIN Other s ON s.id = Exempt.id");

        Assert.True(analysis.AllIdentifiableWrites);
        Assert.Equal(["dbo.Secret"], analysis.AllTargetTables);
    }

    [Fact]
    public void 删除语句的别名同样要解析()
    {
        var analysis = SqlWriteTargetAnalyzer.Analyze(
            "DELETE FROM Exempt FROM Secret AS Exempt WHERE Exempt.id > 0");

        Assert.True(analysis.AllIdentifiableWrites);
        Assert.Equal(["dbo.Secret"], analysis.AllTargetTables);
    }

    [Fact]
    public void 跨库写认不出来()
    {
        // OtherDb.dbo.Exempt 写的是别的库 —— 本地规则管不到，一律要审批。
        var analysis = SqlWriteTargetAnalyzer.Analyze("UPDATE OtherDb.dbo.Exempt SET v = 1");

        Assert.False(analysis.AllIdentifiableWrites);
        Assert.Empty(analysis.AllTargetTables);
    }

    [Fact]
    public void 插入执行存储过程认不出来()
    {
        // INSERT … EXEC：存储过程里写什么表，静态分析答不了。
        var analysis = SqlWriteTargetAnalyzer.Analyze("INSERT INTO Exempt EXEC dbo.AnyProc");

        Assert.False(analysis.AllIdentifiableWrites);
    }

    [Fact]
    public void 派生表别名认不出来()
    {
        var analysis = SqlWriteTargetAnalyzer.Analyze("UPDATE t SET t.v = 1 FROM (SELECT 1 AS v) t");

        Assert.False(analysis.AllIdentifiableWrites);
    }

    [Fact]
    public void 合并语句的目标撞上源表别名时认不出来()
    {
        // MERGE 的目标不是别名 —— 名字撞了就不猜。
        var analysis = SqlWriteTargetAnalyzer.Analyze(
            "MERGE INTO Exempt USING Secret AS Exempt ON Exempt.id = Secret.id WHEN MATCHED THEN UPDATE SET v = 1;");

        Assert.False(analysis.AllIdentifiableWrites);
    }

    [Theory]
    [InlineData("UPDATE dbo.Orders SET Amount = 1")]
    [InlineData("UPDATE Orders SET Amount = 1")]
    [InlineData("DELETE FROM dbo.Orders WHERE Id = 1")]
    [InlineData("INSERT INTO dbo.Orders (Id) VALUES (1)")]
    [InlineData("TRUNCATE TABLE dbo.Orders")]
    public void 常规写法仍然认得出来(string sql)
    {
        var analysis = SqlWriteTargetAnalyzer.Analyze(sql);

        Assert.True(analysis.AllIdentifiableWrites, sql);
        Assert.Equal(["dbo.Orders"], analysis.AllTargetTables);
    }

    [Fact]
    public void 别名指回同一张表时认得出来()
    {
        var analysis = SqlWriteTargetAnalyzer.Analyze(
            "UPDATE Orders SET Orders.Amount = 1 FROM dbo.Orders Orders");

        Assert.True(analysis.AllIdentifiableWrites);
        Assert.Equal(["dbo.Orders"], analysis.AllTargetTables);
    }

    [Fact]
    public void 输出到另一张表时两张表都要算上()
    {
        var analysis = SqlWriteTargetAnalyzer.Analyze(
            "UPDATE dbo.Orders SET Amount = 1 OUTPUT INSERTED.Id INTO dbo.Archive");

        Assert.True(analysis.AllIdentifiableWrites);
        Assert.Equal(["dbo.Orders", "dbo.Archive"], analysis.AllTargetTables);
    }
}
