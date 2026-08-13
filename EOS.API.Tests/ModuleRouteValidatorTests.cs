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
}
