using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 「未建…」系列报表的筛选口径必须**过得了受控解析**。
///
/// <para>
/// 这些口径落在 <c>REPORT.REPORT_FILTER</c> 上，运行时由 <see cref="DataFilterParser"/> 解析，
/// 解析失败是**拒绝出 PDF**（不是静默忽略）——也就是说一串写错的过滤条件，表现是"这张报表打不开"，
/// 而编译、单测、构建全都照不出来。本用例把 5 条口径逐条钉住，并把"表白名单之外的表"这条
/// fail-closed 边界也钉住（否则以后有人往子查询里塞任意表，没人会知道）。
/// </para>
/// </summary>
public class UnbuiltProductReportFilterTests
{
    /// <summary>报表编号 → 口径原文（与迁移 278 落库的值一致）。</summary>
    private static readonly (string ReportId, string Filter)[] Filters =
    [
        ("Product_List_nomoju",      "{PRODUCT.PRO_NO} NOT IN (SELECT PRO_NO FROM MOU_PRO_M)"),
        ("Product_List_nobom",       "{PRODUCT.PRO_NO} NOT IN (SELECT PRO_NO FROM BOM_STRU_M)"),
        ("Product_List_nokehujijia", "{PRODUCT.PRO_NO} NOT IN (SELECT PRO_NO FROM CLIENT_PRICE_D)"),
        ("Product_List_nochsjijia",  "{PRODUCT.PRO_NO} NOT IN (SELECT PRO_NO FROM SUPPLIER_PRICE_D)"),
        ("Product_List_nosample",    "{PRODUCT.PRO_NO} NOT IN (SELECT PRO_NO FROM SAMPLE_PRO)"),
    ];

    private static readonly HashSet<string> ProductColumns =
        new(StringComparer.OrdinalIgnoreCase) { "PRO_NO" };

    [Theory]
    [InlineData("Product_List_nomoju")]
    [InlineData("Product_List_nobom")]
    [InlineData("Product_List_nokehujijia")]
    [InlineData("Product_List_nochsjijia")]
    [InlineData("Product_List_nosample")]
    public void 未建系列报表的口径可通过受控解析(string reportId)
    {
        var filter = Filters.Single(item => item.ReportId == reportId).Filter;

        var ok = DataFilterParser.TryParse(filter, "PRODUCT", ProductColumns, out var predicate, out _);

        Assert.True(ok, $"{reportId} 的口径解析失败：{filter}");
        Assert.Contains("NOT IN", predicate, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PRO_NO", predicate, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void 子查询表不在白名单时拒绝解析()
    {
        // 白名单是这条链路的边界：不在名单里的表一律拒绝，而不是"尽力而为什么都不加"。
        var ok = DataFilterParser.TryParse(
            "{PRODUCT.PRO_NO} NOT IN (SELECT PRO_NO FROM SOME_RANDOM_TABLE)",
            "PRODUCT", ProductColumns, out _, out _);

        Assert.False(ok, "白名单之外的表必须拒绝解析。");
    }
}
