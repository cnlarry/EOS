using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class ModuleRouteValidatorTests
{
    [Theory]
    [InlineData("/reports", 129801, "/reports/129801")]
    [InlineData("/document-workbench", 1406, "/document-workbench/1406")]
    [InlineData("/search-center", 2501, "/search-center/2501")]
    [InlineData("/detail-query", 14996, "/detail-query/14996")]
    public void ParameterizedBases_AppendModuleId(string url, int moduleId, string expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.Resolve(url, moduleId));
    }

    [Theory]
    [InlineData("/admin/menus", 2301)]
    [InlineData("/admin/report-setup", 2201)]
    [InlineData("/admin/print-setup/headers", 2202)]
    [InlineData("/admin/groups", 2305)]
    [InlineData("/settings/system", 110111)]
    [InlineData("/my-tasks", 2102)]
    [InlineData("/jobs", 230901)]
    public void ExactRoutes_ReturnAsIs(string url, int moduleId)
    {
        Assert.Equal(url, ModuleRouteValidator.Resolve(url, moduleId));
    }

    [Theory]
    [InlineData("/reports/129801", 129801)]          // 编号不应写进 M_URL
    [InlineData("/document-workbench/1406", 1406)]
    [InlineData("/reports?x=1", 129801)]
    [InlineData("/admin/nope", 2301)]
    [InlineData("https://evil.example/x", 129801)]
    [InlineData("javascript:alert(1)", 129801)]
    [InlineData("data:text/html;base64,xx", 129801)]
    [InlineData("//evil.example/x", 129801)]
    [InlineData("~/RPT/RptList2.aspx", 129801)]
    [InlineData("Comm/view_frame.aspx", 1406)]
    [InlineData("", 129801)]
    [InlineData(null, 129801)]
    public void InvalidOrLegacyRoutes_FallBackToPlaceholder(string? url, int moduleId)
    {
        Assert.Equal($"/legacy/modules/{moduleId}", ModuleRouteValidator.Resolve(url, moduleId));
    }

    [Theory]
    [InlineData("/document-workbench/{moduleId}/new", 1209, "/document-workbench/1209/new")]
    [InlineData("/document-workbench/{moduleId}/edit", 1406, "/document-workbench/1406/edit")]
    [InlineData("/document-workbench/{moduleId}/view", 1305, "/document-workbench/1305/view")]
    [InlineData("/document-workbench/{moduleId}/edit?key=x", 1406, "/document-workbench/1406/edit?key=x")]
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
    [InlineData("~/BOM/Product.aspx")]
    [InlineData("~/COP/Return.aspx?m=1")]
    [InlineData("/admin/nope")]
    [InlineData("/document-workbench/1406")]
    [InlineData("https://evil.example/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//evil.example/x")]
    public void ResolveActionUrl_InvalidOrLegacy_ReturnsNull(string? url)
    {
        Assert.Null(ModuleRouteValidator.ResolveActionUrl(url, 1406));
    }

    [Theory]
    [InlineData("/document-workbench/{moduleId}/edit", 1406, "/document-workbench/1406/edit")]
    [InlineData("/document-workbench/{moduleId}/new", 1209, "/document-workbench/1209/new")]
    public void Resolve_ActionTemplates_MapToFormRoute(string url, int moduleId, string expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.Resolve(url, moduleId));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("/document-workbench", true)]
    [InlineData("/reports", true)]
    [InlineData("/search-center", true)]
    [InlineData("/detail-query", true)]
    [InlineData("/admin/tables", true)]
    [InlineData("/settings/system", true)]
    [InlineData("/document-workbench/{moduleId}/new", true)]
    [InlineData("/legacy/modules/2307", true)]
    [InlineData("/document-workbench/1406", false)]
    [InlineData("/reports/129801", false)]
    [InlineData("~/BOM/Product.aspx", false)]
    [InlineData("Comm/view_frame.aspx", false)]
    [InlineData("https://evil.example/x", false)]
    [InlineData("/admin/nope", false)]
    public void IsValidHostUrl_Contract(string? url, bool expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.IsValidHostUrl(url));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("/document-workbench/{moduleId}/new", true)]
    [InlineData("/document-workbench/{moduleId}/edit", true)]
    [InlineData("/admin/tables?table=PRODUCT", true)]
    [InlineData("/admin/menus", true)]
    [InlineData("~/BOM/Product.aspx", false)]
    [InlineData("/document-workbench/1406/edit", false)]
    [InlineData("/admin/nope", false)]
    [InlineData("javascript:alert(1)", false)]
    public void IsValidActionUrl_Contract(string? url, bool expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.IsValidActionUrl(url));
    }

    [Theory]
    [InlineData("/document-workbench", true)]
    [InlineData("/document-workbench/1406", true)]
    [InlineData("/document-workbench/1406/edit", true)]
    [InlineData("/reports", false)]
    [InlineData("/admin/menus", false)]
    public void IsWorkbenchUrl_Classification(string url, bool expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.IsWorkbenchUrl(url));
    }

    [Theory]
    [InlineData("/reports", true)]
    [InlineData("/reports/129801", true)]
    [InlineData("/document-workbench", false)]
    [InlineData("/search-center", false)]
    public void IsReportUrl_Classification(string url, bool expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.IsReportUrl(url));
    }
}
