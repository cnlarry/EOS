using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 保存后领域规则的语句构造：主表须由调用方按模块显式指定，
/// 采购单与请购单分别回写各自主表，标识符不合法时 fail-closed。
/// </summary>
public sealed class DomainRuleSqlTests
{
    [Theory]
    [InlineData("PUR_PURCHASE_M", "PURCHASE_TYPE", "PURCHASE_NO")]
    [InlineData("PUR_APPLY_M", "APPLY_TYPE", "APPLY_NO")]
    public void BuildDistinctFieldUpdateSql_回写指定主表(string masterTable, string typeColumn, string noColumn)
    {
        var sql = DomainRuleService.BuildDistinctFieldUpdateSql(masterTable, "ORDER_NO", typeColumn, noColumn);

        Assert.Contains($"UPDATE dbo.[{masterTable}] SET [ORDER_NO]=@Value", sql);
        Assert.Contains($"WHERE [{typeColumn}]=@Type AND [{noColumn}]=@No;", sql);
    }

    [Theory]
    [InlineData("PUR_PURCHASE_M; DROP TABLE dbo.X")]
    [InlineData("dbo.PUR_PURCHASE_M")]
    [InlineData("1PUR_PURCHASE_M")]
    [InlineData("")]
    public void BuildDistinctFieldUpdateSql_非法标识符拒绝(string masterTable)
    {
        Assert.Throws<ArgumentException>(() =>
            DomainRuleService.BuildDistinctFieldUpdateSql(masterTable, "ORDER_NO", "PURCHASE_TYPE", "PURCHASE_NO"));
    }

    [Fact]
    public void BuildDistinctFieldUpdateSql_非法列名拒绝()
    {
        Assert.Throws<ArgumentException>(() =>
            DomainRuleService.BuildDistinctFieldUpdateSql("PUR_PURCHASE_M", "ORDER_NO;--", "PURCHASE_TYPE", "PURCHASE_NO"));
    }
}
