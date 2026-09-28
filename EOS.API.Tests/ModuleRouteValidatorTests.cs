using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class ModuleRouteValidatorTests
{
    [Theory]
    [InlineData("/reports", 129801, "/reports/129801")]
    [InlineData("/workbench", 1406, "/workbench/1406")]
    [InlineData("/search-center", 2501, "/search-center/2501")]
    [InlineData("/detail-query", 14996, "/detail-query/14996")]
    public void ParameterizedBases_AppendModuleId(string url, int moduleId, string expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.Resolve(url, moduleId));
    }

    [Theory]
    [InlineData("/admin/menus", 2301)]
    [InlineData("/admin/report-setup", 2201)]
    [InlineData("/admin/groups", 2305)]
    [InlineData("/settings/system", 110111)]
    [InlineData("/my-tasks", 2102)]
    [InlineData("/workflow/design", 2101)]
    [InlineData("/workflow/monitor", 2103)]
    [InlineData("/jobs", 230901)]
    public void ExactRoutes_ReturnAsIs(string url, int moduleId)
    {
        Assert.Equal(url, ModuleRouteValidator.Resolve(url, moduleId));
    }

    [Theory]
    [InlineData("/reports/129801", 129801)]          // 编号不应写进 M_URL
    [InlineData("/workbench/1406", 1406)]
    [InlineData("/reports?x=1", 129801)]
    [InlineData("/admin/nope", 2301)]
    [InlineData("https://evil.example/x", 129801)]
    [InlineData("javascript:alert(1)", 129801)]
    [InlineData("data:text/html;base64,xx", 129801)]
    [InlineData("//evil.example/x", 129801)]
    [InlineData("~/RPT/RptList2", 129801)]
    [InlineData("Comm/unknown_path", 1406)]
    [InlineData("", 129801)]
    [InlineData(null, 129801)]
    public void InvalidOrPlaceholderRoutes_FallBackToPlaceholder(string? url, int moduleId)
    {
        Assert.Equal($"/fallback/modules/{moduleId}", ModuleRouteValidator.Resolve(url, moduleId));
    }

    [Theory]
    [InlineData("/workbench/{moduleId}/new", 1209, "/workbench/1209/new")]
    [InlineData("/workbench/{moduleId}/edit", 1406, "/workbench/1406/edit")]
    [InlineData("/workbench/{moduleId}/view", 1305, "/workbench/1305/view")]
    [InlineData("/workbench/{moduleId}/edit?key=x", 1406, "/workbench/1406/edit?key=x")]
    public void ResolveActionUrl_Templates_SubstituteModuleId(string url, int moduleId, string expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.ResolveActionUrl(url, moduleId));
    }

    [Theory]
    [InlineData("/admin/tables?table=PRODUCT", 2302)]
    [InlineData("/admin/menus", 2301)]
    [InlineData("/import", 230902)]
    public void ResolveActionUrl_ExactRoutes_ReturnAsIs(string url, int moduleId)
    {
        Assert.Equal(url, ModuleRouteValidator.ResolveActionUrl(url, moduleId));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("~/BOM/Product")]
    [InlineData("~/COP/Return?m=1")]
    [InlineData("/admin/nope")]
    [InlineData("/workbench/1406")]
    [InlineData("https://evil.example/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example/x")]
    public void ResolveActionUrl_InvalidOrUnknown_ReturnsNull(string? url)
    {
        Assert.Null(ModuleRouteValidator.ResolveActionUrl(url, 1406));
    }

    [Theory]
    [InlineData("/workbench/{moduleId}/edit", 1406, "/workbench/1406/edit")]
    [InlineData("/workbench/{moduleId}/new", 1209, "/workbench/1209/new")]
    public void Resolve_ActionTemplates_MapToFormRoute(string url, int moduleId, string expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.Resolve(url, moduleId));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("/workbench", true)]
    [InlineData("/reports", true)]
    [InlineData("/search-center", true)]
    [InlineData("/detail-query", true)]
    [InlineData("/admin/tables", true)]
    [InlineData("/settings/system", true)]
    [InlineData("/workbench/{moduleId}/new", true)]
    [InlineData("/fallback/modules/2307", true)]
    [InlineData("/workbench/1406", false)]
    [InlineData("/reports/129801", false)]
    [InlineData("~/BOM/Product", false)]
    [InlineData("Comm/unknown_path", false)]
    [InlineData("https://evil.example/x", false)]
    [InlineData("/admin/nope", false)]
    public void IsValidHostUrl_Contract(string? url, bool expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.IsValidHostUrl(url));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("/workbench/{moduleId}/new", true)]
    [InlineData("/workbench/{moduleId}/edit", true)]
    [InlineData("/admin/tables?table=PRODUCT", true)]
    [InlineData("/admin/menus", true)]
    [InlineData("~/BOM/Product", false)]
    [InlineData("/workbench/1406/edit", false)]
    [InlineData("/admin/nope", false)]
    [InlineData("javascript:alert(1)", false)]
    public void IsValidActionUrl_Contract(string? url, bool expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.IsValidActionUrl(url));
    }

    [Theory]
    [InlineData("/workbench", true)]
    [InlineData("/workbench/1406", true)]
    [InlineData("/workbench/1406/edit", true)]
    [InlineData("/reports", false)]
    [InlineData("/admin/menus", false)]
    public void IsWorkbenchUrl_Classification(string url, bool expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.IsWorkbenchUrl(url));
    }

    /// <summary>
    /// 统一表单动作路由的判定：只有本模块的 new/edit/view 命中。名单外的模块靠它把
    /// "指向统一表单的 MODI_URL" 当无值处理（否则列表双击会开到一个必 404 的表单）。
    /// </summary>
    [Theory]
    [InlineData("/workbench/1303/edit", 1303, true)]
    [InlineData("/workbench/1303/view", 1303, true)]
    [InlineData("/workbench/1303/new", 1303, true)]
    [InlineData("/workbench/1303/edit?key=x", 1303, true)]
    [InlineData("/workbench/1303/edit", 1302, false)]      // 别的模块的编号不算
    [InlineData("/admin/depot-stock-policy", 110310, false)] // 自定义承载页不是统一表单路由
    [InlineData("/workbench/1303", 1303, false)]            // 列表页本身不是表单动作
    [InlineData("/workbench/{moduleId}/edit", 1303, false)] // 未解析的模板：判定发生在解析之后
    [InlineData("", 1303, false)]
    [InlineData(null, 1303, false)]
    public void IsUnifiedFormRoute_Classification(string? url, int moduleId, bool expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.IsUnifiedFormRoute(url, moduleId));
    }
}
