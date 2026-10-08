using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class ModuleRouteValidatorTests
{
    [Theory]
    [InlineData("/reports", 129801, "/reports/129801")]
    [InlineData("/workbench", 1406, "/workbench/1406")]
    [InlineData("/search-center", 1405, "/search-center/1405")]
    public void ParameterizedBases_AppendModuleId(string url, int moduleId, string expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.Resolve(url, moduleId));
    }

    [Theory]
    [InlineData("/admin/menus", 2301)]
    [InlineData("/admin/report-setup", 2201)]
    [InlineData("/admin/groups", 2305)]
    [InlineData("/admin/logs", 2313)]
    [InlineData("/settings/system", 110111)]
    [InlineData("/my-tasks", 2102)]
    [InlineData("/workflow/design", 2101)]
    [InlineData("/workflow/monitor", 2103)]
    [InlineData("/jobs", 230901)]
    public void ExactRoutes_ReturnAsIs(string url, int moduleId)
    {
        Assert.Equal(url, ModuleRouteValidator.Resolve(url, moduleId));
    }

    /// <summary>
    /// 定制页的路由必须同时满足两件事：① 后端把它当"精确路径"原样下发（否则菜单会退化成
    /// `/fallback/modules/{id}` 兜底页——这是实测踩到的：新增定制页只登记了模块与前端路由、
    /// 漏登记后端的 `ExactRoutes`，菜单点进去是回落页）；② 前端的 `workspaceRoutes.tsx`
    /// 里确有同名路由。两边任一缺失都会表现为"菜单点不开"，而编译与单测都不会报。
    /// </summary>
    [Theory]
    [InlineData("/admin/logs", 2313)]
    [InlineData("/admin/field-audit", 2303)]
    [InlineData("/admin/depot-stock-policy", 110310)]
    [InlineData("/admin/business-flow", 2314)]
    public void 定制页路由_后端精确路径与前端路由表一致(string url, int moduleId)
    {
        Assert.Equal(url, ModuleRouteValidator.Resolve(url, moduleId));
        var frontendRoute = Path.Combine(FindRepoRoot(), "EOS.Web", "src", "app", "workspaceRoutes.tsx");
        Assert.True(File.Exists(frontendRoute), $"找不到前端路由表：{frontendRoute}");
        Assert.Contains($"path: '{url.TrimStart('/')}'", File.ReadAllText(frontendRoute), StringComparison.Ordinal);
    }

    /// <summary>仓库根优先取构建期注入的 <c>RepoRoot</c> 元数据（与运行输出目录无关），失败再向上找。</summary>
    private static string FindRepoRoot()
    {
        var declared = typeof(ModuleRouteValidatorTests).Assembly
            .GetCustomAttributes(inherit: false)
            .OfType<System.Reflection.AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepoRoot")?.Value;
        if (!string.IsNullOrWhiteSpace(declared))
        {
            var resolved = Path.GetFullPath(declared);
            if (Directory.Exists(Path.Combine(resolved, "EOS.Web"))) return resolved;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EOS.API", "EOS.API.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
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

    /// <summary>
    /// 已退场的动作模板（NEW_URL / MODI_URL 时代）不得再解析成表单路由：一律落占位页，
    /// 保存期也被 <see cref="ModuleRouteValidator.IsValidHostUrl"/> 拒掉（迁移 321 删了那三个字段）。
    /// </summary>
    [Theory]
    [InlineData("/workbench/{moduleId}/edit")]
    [InlineData("/workbench/{moduleId}/new")]
    [InlineData("/workbench/{moduleId}/view")]
    [InlineData("/workbench/{moduleId}/edit?key=x")]
    public void Resolve_RetiredActionTemplates_FallToPlaceholder(string url)
    {
        Assert.Equal("/fallback/modules/1406", ModuleRouteValidator.Resolve(url, 1406));
        Assert.False(ModuleRouteValidator.IsValidHostUrl(url));
    }

    /// <summary>
    /// 没声明承载页时的默认落点：单据模块（有主表）走统一工作台；没有主表的节点落占位页
    /// （菜单侧另有"有无子模块"的目录判定，见 ApplicationController.HasOwnPage）。
    /// </summary>
    [Theory]
    [InlineData(null, "PRODUCT", "/workbench/1201")]
    [InlineData("", "PRODUCT", "/workbench/1201")]
    [InlineData("  ", "COMPANY", "/workbench/1201")]
    [InlineData(null, null, "/fallback/modules/1201")]
    [InlineData("", "", "/fallback/modules/1201")]
    public void Resolve_EmptyUrl_DefaultsToWorkbenchForDocumentModules(string? url, string? masterTable, string expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.Resolve(url, 1201, masterTable));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("/workbench", true)]
    [InlineData("/reports", true)]
    [InlineData("/search-center", true)]
    [InlineData("/admin/tables", true)]
    [InlineData("/settings/system", true)]
    [InlineData("/fallback/modules/2307", true)]
    [InlineData("/workbench/1406", false)]
    [InlineData("/reports/129801", false)]
    [InlineData("/workbench/{moduleId}/new", false)]   // 动作模板已退场（迁移 321）
    [InlineData("~/BOM/Product", false)]
    [InlineData("Comm/unknown_path", false)]
    [InlineData("https://evil.example/x", false)]
    [InlineData("/admin/nope", false)]
    public void IsValidHostUrl_Contract(string? url, bool expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.IsValidHostUrl(url));
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
    /// 工作台模块判定：承载页是 /workbench，或**没声明承载页但有主表**（默认落统一工作台）。
    /// 这是"能不能装配工作台定义 / 进模块列表 / 重建快照"的共同入口。
    /// </summary>
    [Theory]
    [InlineData("/workbench", "PRODUCT", true)]
    [InlineData("/workbench/1201", "PRODUCT", true)]
    [InlineData(null, "PRODUCT", true)]              // 没声明承载页的单据模块
    [InlineData("", "PRODUCT", true)]
    [InlineData("  ", "COMPANY", true)]
    [InlineData(null, null, false)]                  // 纯目录节点
    [InlineData("", "", false)]
    [InlineData("/admin/menus", "PRODUCT", false)]   // 自定义承载页不是工作台模块
    [InlineData("/reports", "PRODUCT", false)]
    public void IsWorkbenchModule_Classification(string? url, string? masterTable, bool expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.IsWorkbenchModule(url, masterTable));
    }

    /// <summary>
    /// 节点形态三分（SQL 视图 `dbo.V_MODULE_NODE` 的代码侧同源实现）：先判工作台，
    /// 不是工作台且承载页留空即目录节点，其余是自定义承载页。2301 按它决定露出哪些配置项与页签，
    /// SQL 侧按同一规则过滤——两侧不一致会出现"界面藏了、后端还在按它办事"。
    /// </summary>
    [Theory]
    [InlineData("/workbench", "PRODUCT", ModuleNodeKind.Workbench)]
    [InlineData("/workbench/1201", null, ModuleNodeKind.Workbench)]
    [InlineData(null, "PRODUCT", ModuleNodeKind.Workbench)]
    [InlineData("  ", "COMPANY", ModuleNodeKind.Workbench)]
    [InlineData(null, null, ModuleNodeKind.Directory)]
    [InlineData("", "", ModuleNodeKind.Directory)]
    [InlineData("  ", null, ModuleNodeKind.Directory)]
    [InlineData("/admin/menus", "MODULES", ModuleNodeKind.CustomPage)]
    [InlineData("/settings/system", null, ModuleNodeKind.CustomPage)]
    [InlineData("/reports", null, ModuleNodeKind.CustomPage)]
    public void ResolveKind_Classification(string? url, string? masterTable, ModuleNodeKind expected)
    {
        Assert.Equal(expected, ModuleRouteValidator.ResolveKind(url, masterTable));
    }

    /// <summary>
    /// 形态的线上名必须与视图定义里的取值逐字一致：两侧共用一套名字（`NODE_KIND`），
    /// 改一边而不改另一边会让 2301 的页签显隐整体错位，而编译、单测都不会报。
    /// </summary>
    [Fact]
    public void WireName_MatchesViewDefinition()
    {
        Assert.Equal("WORKBENCH", ModuleRouteValidator.WireName(ModuleNodeKind.Workbench));
        Assert.Equal("CUSTOMPAGE", ModuleRouteValidator.WireName(ModuleNodeKind.CustomPage));
        Assert.Equal("DIRECTORY", ModuleRouteValidator.WireName(ModuleNodeKind.Directory));

        var migrations = Path.Combine(FindRepoRoot(), "EOS.API", "Data", "Migrations");
        var viewSql = Directory.EnumerateFiles(migrations, "*.sql")
            .Select(File.ReadAllText)
            .FirstOrDefault(text => text.Contains("CREATE VIEW dbo.V_MODULE_NODE", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(viewSql);
        foreach (var name in new[] { "WORKBENCH", "CUSTOMPAGE", "DIRECTORY" })
        {
            Assert.Contains($"N'{name}'", viewSql, StringComparison.Ordinal);
        }
    }
}
