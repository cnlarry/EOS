namespace EOS.API.Data;

/// <summary>
/// 模块路由契约：**只有 M_URL 一个字段**（2026-10-06 收敛，迁移 321 删掉了 NEW_URL / MODI_URL / HELP_URL）。
///
/// M_URL = 模块承载页：
/// - **空**：没声明承载页。单据模块（有主表）按"默认统一工作台"落 /workbench/{id}；
///   无主表的节点是目录（有下级）或未接线模块（落占位页）——菜单侧另有"有无子模块"的判定。
/// - **参数化承载页**（/workbench、/reports、/search-center）：菜单渲染时追加 /{moduleId}。
/// - **精确路径白名单**（特殊页，如 /admin/menus）：原样返回。
/// - **非法值**（`~/`、协议头、相对路径、反斜杠、以及已退场的动作模板）一律落占位页，
///   让界面明确说"未接线"，而不是给一个点进去必错的外链。
///
/// 也提供"这个模块是不是统一工作台模块"的统一判定（承载页是 /workbench，或没声明承载页但有主表），
/// 取代各处私有前缀判断——它同时是工作台定义、列表、浏览解析与快照重建的共同入口。
///
/// 曾经的 NEW_URL / MODI_URL（新增/修改路由）与 HELP_URL（帮助页）已物理删除：能不能新增/编辑
/// 由统一表单名单（<see cref="EOS.API.Models.UnifiedFormEditorSettings"/>）与权限裁决，
/// **不再由路由字段表达**——这正是本次收敛要消掉的那层"同一件事两个真源"。
/// </summary>
internal static class ModuleRouteValidator
{
    /// <summary>参数化承载页：M_URL 为该路径时追加 /{moduleId}。</summary>
    private static readonly string[] ParameterizedBases =
    [
        "/reports",
        "/workbench",
        "/search-center",
    ];

    private static readonly string[] ExactRoutes =
    [
        "/dashboard",
        "/admin/menus", "/admin/tables", "/admin/field-audit", "/admin/users",
        "/admin/fields", "/admin/depot-stock-policy", "/admin/logs", "/admin/business-flow",
        "/admin/assistant/sessions", "/admin/assistant/mechanism", "/admin/assistant/kb", "/admin/assistant/models",
        "/admin/assistant/settings",
        "/admin/groups", "/admin/report-setup",
        "/import", "/settings/system", "/settings/hr-setup", "/settings/hrm-setup",
        "/bom-expand", "/jobs", "/my-tasks", "/car-summary",
        "/workflow/design", "/workflow/monitor",
    ];

    private static readonly System.Text.RegularExpressions.Regex PlaceholderRoutePattern =
        new(@"^/fallback/modules/\d+$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// 解析模块承载页。<paramref name="masterTable"/> 只用于"没声明承载页"的默认落点判断：
    /// 有主表 ⇒ 这是单据模块，默认走统一工作台；没有主表 ⇒ 未接线，落占位页。
    /// </summary>
    public static string Resolve(string? rawUrl, int moduleId, string? masterTable = null)
    {
        var url = (rawUrl ?? string.Empty).Trim();
        if (url.Length == 0)
            return string.IsNullOrWhiteSpace(masterTable) ? Placeholder(moduleId) : $"/workbench/{moduleId}";
        if (IsForbiddenUrl(url)) return Placeholder(moduleId);
        if (ParameterizedBases.Contains(url, StringComparer.OrdinalIgnoreCase)) return $"{url}/{moduleId}";
        if (ExactRoutes.Contains(url, StringComparer.OrdinalIgnoreCase)) return url;
        // 已退场的动作模板（/workbench/{moduleId}/new|edit|view）与任何未知形态一样落占位页
        return Placeholder(moduleId);
    }

    /// <summary>M_URL 契约校验（菜单管理保存用）：空值（未声明）、承载页、精确路径或迁移占位页。</summary>
    public static bool IsValidHostUrl(string? rawUrl)
    {
        var url = (rawUrl ?? string.Empty).Trim();
        if (url.Length == 0 || IsForbiddenUrl(url) || !url.StartsWith('/')) return url.Length == 0;
        return ParameterizedBases.Contains(url, StringComparer.OrdinalIgnoreCase)
            || ExactRoutes.Contains(url, StringComparer.OrdinalIgnoreCase)
            || PlaceholderRoutePattern.IsMatch(url);
    }

    /// <summary>
    /// 统一工作台模块：承载页声明为 /workbench，或**没声明承载页但有主表**（默认落统一工作台）。
    /// 这是"工作台定义能不能装配 / 列表能不能查 / 快照要不要重建"的共同判据。
    /// </summary>
    public static bool IsWorkbenchModule(string? rawUrl, string? masterTable)
        => IsWorkbenchUrl((rawUrl ?? string.Empty).Trim())
           || (string.IsNullOrWhiteSpace(rawUrl) && !string.IsNullOrWhiteSpace(masterTable));

    /// <summary>工作台承载判定（工作台定义、统一表单、单据打印共用）。</summary>
    public static bool IsWorkbenchUrl(string url)
    {
        var value = url.Trim().Replace('\\', '/');
        return value.Equals("/workbench", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/workbench/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForbiddenUrl(string url) =>
        !url.StartsWith('/')
        || url.StartsWith("//", StringComparison.Ordinal)
        || url.Contains("://", StringComparison.Ordinal)
        || url.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
        || url.Contains("data:", StringComparison.OrdinalIgnoreCase)
        || url.Contains('\\');

    private static string Placeholder(int moduleId) => $"/fallback/modules/{moduleId}";
}
