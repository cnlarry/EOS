using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// CAS 谓词的判别性用例（L104）：乐观并发过去是「先 ReadRow 比对、再 UPDATE」，
/// 读与写之间没有条件，两个重叠事务都能读到旧状态而同时通过 → 后写覆盖先写（丢失更新）。
/// 现在把期望值下推成 UPDATE 的 WHERE 条件，这几条用例钉住"下推得对、且不误伤"。
/// </summary>
public sealed class WorkbenchCasPredicateTests
{
    private static readonly string[] Pk = ["ORDER_TYPE", "ORDER_NO"];

    [Fact]
    public void PushesBackOrdinaryColumns()
    {
        var predicate = WorkbenchCommandHandler.BuildCasPredicate(
            [("REMARK", "X", "X"), ("QTY", 5m, 5m)], Pk);

        Assert.Equal(" AND [REMARK]=@c0 AND [QTY]=@c1", predicate.Sql);
        Assert.Equal(2, predicate.Parameters.Count);
        Assert.Equal("X", predicate.Parameters[0].Value);
        Assert.Equal(5m, predicate.Parameters[1].Value);
        Assert.Equal(["REMARK", "QTY"], predicate.Keys);
    }

    [Fact]
    public void SkipsPrimaryKeyColumns()
    {
        // 键条件已由 WHERE 的键部分负责；重复下推只会多一个恒真的参数
        var predicate = WorkbenchCommandHandler.BuildCasPredicate(
            [("ORDER_NO", "DD01", "DD01"), ("REMARK", "X", "X")], Pk);

        Assert.Equal(" AND [REMARK]=@c0", predicate.Sql);
        Assert.DoesNotContain("ORDER_NO", predicate.Keys);
    }

    [Fact]
    public void NullExpectedBecomesIsNullNotEquals()
    {
        // 拼成 `= NULL` 恒为 UNKNOWN，永远不匹配 —— 那会让每一次保存都报"已被他人修改"
        var predicate = WorkbenchCommandHandler.BuildCasPredicate([("REMARK", null, null)], Pk);

        Assert.Equal(" AND [REMARK] IS NULL", predicate.Sql);
        Assert.Empty(predicate.Parameters);
        Assert.Single(predicate.Keys);
    }

    [Fact]
    public void SkipsColumnsRewrittenByDisplayResolution()
    {
        // 选择器同组展示列：客户端看到的是解析后的值，物理列是另一回事（L102）。
        // 拿物理值下推会跟客户端展示值比，等于给正常保存制造假冲突。
        var predicate = WorkbenchCommandHandler.BuildCasPredicate(
            [("ORDER_NO", "DD13010001", "DD18010079"), ("REMARK", "X", "X")], Pk);

        Assert.Equal(" AND [REMARK]=@c0", predicate.Sql);
        Assert.Equal(["REMARK"], predicate.Keys);
    }

    [Fact]
    public void ParameterNamesStayUniqueAcrossSkippedColumns()
    {
        // 跳过列不能让参数序号与列名错位（@c0 必须还是第一个"实际下推"的列）
        var predicate = WorkbenchCommandHandler.BuildCasPredicate(
            [("A", "1", "1"), ("ORDER_TYPE", "DD", "DD"), ("B", "2", "2")], Pk);

        Assert.Equal(" AND [A]=@c0 AND [B]=@c1", predicate.Sql);
        Assert.Equal(2, predicate.Parameters.Count);
    }
}
