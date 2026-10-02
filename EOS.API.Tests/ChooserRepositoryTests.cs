using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 统一选择器数据源注册与参数白名单纯逻辑测试（不依赖数据库）。
/// 覆盖：sourceKey 注册解析、权限门、排序列白名单、分页钳制、args 校验。
/// </summary>
public sealed class ChooserRepositoryTests
{
    [Theory]
    [InlineData("menu-admin.tables", true)]
    [InlineData("menu-admin.fields", true)]
    [InlineData("menu-admin.sprocs", true)]
    [InlineData("menu-admin.columns", true)]
    [InlineData("menu-admin.modules", true)]
    [InlineData("report-admin.fields", true)]
    [InlineData("report-admin.modules", true)]
    [InlineData("user-admin.employees", true)]
    [InlineData("form-designer.fields", true)]
    [InlineData("assistant-admin.modules", true)]
    [InlineData("MENU-ADMIN.TABLES", true)]
    [InlineData("unknown.source", false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData(null, false)]
    public void IsRegistered_ResolvesKnownSources(string? sourceKey, bool expected)
    {
        Assert.Equal(expected, ChooserRepository.IsRegistered(sourceKey ?? string.Empty));
    }

    [Theory]
    [InlineData("menu-admin.tables", 2301)]
    [InlineData("menu-admin.fields", 2301)]
    [InlineData("menu-admin.sprocs", 2301)]
    [InlineData("menu-admin.columns", 2301)]
    [InlineData("menu-admin.modules", 2301)]
    [InlineData("report-admin.fields", 2201)]
    [InlineData("report-admin.modules", 2201)]
    [InlineData("user-admin.employees", 2306)]
    // 助手作用域的模块候选集：门挂 3105（助手设置），与写覆盖的端点同一道门
    [InlineData("assistant-admin.modules", 3105)]
    [InlineData("unknown.source", null)]
    public void PermissionModuleId_ReturnsPermissionGate(string sourceKey, int? expected)
    {
        Assert.Equal(expected, ChooserRepository.PermissionModuleId(sourceKey));
    }

    [Theory]
    [InlineData("menu-admin.tables", null, null, "T_DESC", "ASC")]
    [InlineData("menu-admin.tables", "T_ID", "desc", "T_ID", "DESC")]
    [InlineData("menu-admin.tables", "NOT_A_COLUMN", "desc", "T_DESC", "DESC")]
    [InlineData("menu-admin.tables", "t_kind", null, "T_KIND", "ASC")]
    [InlineData("menu-admin.fields", null, null, "F_ID", "ASC")]
    [InlineData("menu-admin.fields", "F_DESC", "DESC", "F_DESC", "DESC")]
    [InlineData("menu-admin.fields", "F_ID", "sideways", "F_ID", "ASC")]
    [InlineData("menu-admin.columns", null, null, "COLUMN_NAME", "ASC")]
    [InlineData("menu-admin.columns", "COLUMN_NAME", "desc", "COLUMN_NAME", "DESC")]
    [InlineData("menu-admin.columns", "NOT_A_COLUMN", null, "COLUMN_NAME", "ASC")]
    [InlineData("menu-admin.modules", null, null, "M_IDX", "ASC")]
    [InlineData("menu-admin.modules", "M_DESC", "desc", "M_DESC", "DESC")]
    [InlineData("menu-admin.sprocs", null, null, "SP_NAME", "ASC")]
    [InlineData("menu-admin.sprocs", "SP_NAME", "desc", "SP_NAME", "DESC")]
    [InlineData("menu-admin.sprocs", "NOT_A_COLUMN", null, "SP_NAME", "ASC")]
    [InlineData("report-admin.fields", null, null, "T_ID", "ASC")]
    [InlineData("report-admin.fields", "F_ID", "desc", "F_ID", "DESC")]
    [InlineData("report-admin.fields", "NOT_A_COLUMN", null, "T_ID", "ASC")]
    [InlineData("assistant-admin.modules", null, null, "M_IDX", "ASC")]
    [InlineData("assistant-admin.modules", "M_DESC", "desc", "M_DESC", "DESC")]
    public void ResolveSort_UsesWhitelistWithFallback(string sourceKey, string? sortField, string? sortDirection, string expectedColumn, string expectedDirection)
    {
        var (column, direction) = ChooserRepository.ResolveSort(sourceKey, sortField, sortDirection);
        Assert.Equal(expectedColumn, column);
        Assert.Equal(expectedDirection, direction);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(-5, 1)]
    [InlineData(42, 42)]
    public void NormalizePage_ClampsToAtLeastOne(int page, int expected)
    {
        Assert.Equal(expected, ChooserRepository.NormalizePage(page));
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(50, 50)]
    [InlineData(500, 100)]
    [InlineData(-1, 10)]
    public void NormalizePageSize_ClampsBetweenTenAndHundred(int pageSize, int expected)
    {
        Assert.Equal(expected, ChooserRepository.NormalizePageSize(pageSize));
    }

    [Theory]
    [InlineData("COMPANY", "COMPANY")]
    [InlineData("  COMPANY  ", "COMPANY")]
    [InlineData("PRODUCT_J", "PRODUCT_J")]
    [InlineData("a; DROP TABLE dbo.FIELDS--", null)]
    [InlineData("带空格 表", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ResolveFieldsTableId_OnlyAcceptsWhitelistedIdentifiers(string? tableId, string? expected)
    {
        var args = tableId is null ? null : new Dictionary<string, string> { ["tableId"] = tableId };
        Assert.Equal(expected, ChooserRepository.ResolveFieldsTableId(args));
    }

    [Fact]
    public void ResolveFieldsTableId_RejectsMissingKey()
    {
        Assert.Null(ChooserRepository.ResolveFieldsTableId(new Dictionary<string, string> { ["other"] = "COMPANY" }));
    }

    [Theory]
    [InlineData("1404", 1404)]
    [InlineData(" 1404 ", 1404)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ResolveReportModuleId_RequiresPositiveInteger(string? raw, int? expected)
    {
        var args = raw is null ? null : new Dictionary<string, string> { ["moduleId"] = raw };
        Assert.Equal(expected, ChooserRepository.ResolveReportModuleId(args));
    }

    [Fact]
    public void ResolveReportModuleId_RejectsMissingKey()
    {
        Assert.Null(ChooserRepository.ResolveReportModuleId(new Dictionary<string, string> { ["tableId"] = "COMPANY" }));
    }

    [Theory]
    [InlineData("master", "master")]
    [InlineData("detail", "detail")]
    [InlineData("DETAIL", "detail")]
    [InlineData("  master  ", "master")]
    [InlineData("other", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ResolveDesignerTable_OnlyAcceptsMasterOrDetail(string? table, string? expected)
    {
        var args = table is null ? null : new Dictionary<string, string> { ["table"] = table };
        Assert.Equal(expected, ChooserRepository.ResolveDesignerTable(args));
    }

    [Fact]
    public void ResolveDesignerTable_RejectsMissingKey()
    {
        Assert.Null(ChooserRepository.ResolveDesignerTable(new Dictionary<string, string> { ["moduleId"] = "1405" }));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("ORDER_NO", "ORDER_NO")]
    [InlineData("ORDER_NO,CLIENT_ID", "ORDER_NO,CLIENT_ID")]
    [InlineData(" ORDER_NO , CLIENT_ID ", "ORDER_NO,CLIENT_ID")]
    [InlineData("ORDER_NO,,CLIENT_ID,", "ORDER_NO,CLIENT_ID")]
    [InlineData("ORDER_NO,order_no", "ORDER_NO")]
    [InlineData("ORDER_NO,BAD KEY,CLIENT_ID", "ORDER_NO,CLIENT_ID")]
    [InlineData("ORDER_NO;DROP TABLE dbo.FIELDS--", "")]
    public void ResolveExcludeKeys_KeepsOnlyIdentifierShapedKeys(string? raw, string expected)
    {
        var args = raw is null ? null : new Dictionary<string, string> { ["exclude"] = raw };
        Assert.Equal(expected, ChooserRepository.ResolveExcludeKeys(args));
    }

    [Fact]
    public void ResolveExcludeKeys_RejectsMissingKeyAndCapsTheList()
    {
        Assert.Equal(string.Empty, ChooserRepository.ResolveExcludeKeys(new Dictionary<string, string> { ["table"] = "master" }));

        var many = string.Join(',', Enumerable.Range(0, 600).Select(index => $"COL_{index}"));
        var kept = ChooserRepository.ResolveExcludeKeys(new Dictionary<string, string> { ["exclude"] = many }).Split(',');
        Assert.Equal(500, kept.Length);
        Assert.Equal("COL_0", kept[0]);
        Assert.Equal("COL_499", kept[^1]);
    }

    [Theory]
    [InlineData("form-designer.fields", true)]
    [InlineData("FORM-DESIGNER.FIELDS", true)]
    [InlineData("menu-admin.fields", false)]
    public void IsFormDesignerFieldPool_OnlyMatchesDesignerPoolSource(string sourceKey, bool expected)
    {
        Assert.Equal(expected, ChooserRepository.IsFormDesignerFieldPool(sourceKey));
    }

    /// <summary>
    /// 新增数据源要在四张表里登记齐（列清单 / 排序白名单 / 关键字白名单 / 列表达式）。
    /// 漏一处的表现是运行期才炸，而这四张表都是按 sourceKey 直接取下标用的。
    /// </summary>
    [Theory]
    [InlineData("assistant-admin.modules")]
    public void RegisteredSource_IsCompleteAcrossTheFourTables(string sourceKey)
    {
        Assert.Empty(ChooserRepository.MissingRegistrations(sourceKey));
    }
}

/// <summary>
/// 选择器回填映射（RETURN_ITEMS）的读写口径：读取大小写不敏感，写入固定小驼峰。
/// 写入若产出 PascalCase，按小写键读取的一方会静默得到空映射（选择器选完不回填、不报错）。
/// </summary>
public sealed class ChooserReturnItemsTests
{
    [Fact]
    public void ToJson_WritesCamelCaseKeys()
    {
        var json = ChooserReturnItems.ToJson([new ChooserReturnItem("CLIENT_ID", "CLIENT_ID")]);

        Assert.Equal("[{\"target\":\"CLIENT_ID\",\"column\":\"CLIENT_ID\"}]", json);
    }

    [Fact]
    public void ToJson_RoundTripsThroughParse()
    {
        var items = new[] { new ChooserReturnItem("CLIENT_ID", "CLIENT_ID"), new ChooserReturnItem("CLIENT_NAME", "CLIENT_NAME") };

        Assert.Equal(items, ChooserReturnItems.Parse(ChooserReturnItems.ToJson(items)));
    }

    [Theory]
    [InlineData("[{\"target\":\"CLIENT_ID\",\"column\":\"CLIENT_ID\"}]")]
    [InlineData("[{\"Target\":\"CLIENT_ID\",\"Column\":\"CLIENT_ID\"}]")]
    [InlineData("[{\"TARGET\":\"CLIENT_ID\",\"COLUMN\":\"CLIENT_ID\"}]")]
    public void Parse_AcceptsAnyKeyCasing(string json)
    {
        var expected = new[] { new ChooserReturnItem("CLIENT_ID", "CLIENT_ID") };

        Assert.Equal(expected, ChooserReturnItems.Parse(json));
    }

    [Fact]
    public void Parse_TreatsBlankAsEmptyAndInvalidAsNull()
    {
        Assert.Empty(ChooserReturnItems.Parse(null)!);
        Assert.Empty(ChooserReturnItems.Parse("   ")!);
        Assert.Null(ChooserReturnItems.Parse("{oops"));
    }
}
