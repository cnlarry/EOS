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
    [InlineData("report-admin.fields", true)]
    [InlineData("report-admin.modules", true)]
    [InlineData("user-admin.employees", true)]
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
    [InlineData("report-admin.fields", 2201)]
    [InlineData("report-admin.modules", 2201)]
    [InlineData("user-admin.employees", 2306)]
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
    [InlineData("menu-admin.sprocs", null, null, "SP_NAME", "ASC")]
    [InlineData("menu-admin.sprocs", "SP_NAME", "desc", "SP_NAME", "DESC")]
    [InlineData("menu-admin.sprocs", "NOT_A_COLUMN", null, "SP_NAME", "ASC")]
    [InlineData("report-admin.fields", null, null, "T_ID", "ASC")]
    [InlineData("report-admin.fields", "F_ID", "desc", "F_ID", "DESC")]
    [InlineData("report-admin.fields", "NOT_A_COLUMN", null, "T_ID", "ASC")]
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
}
