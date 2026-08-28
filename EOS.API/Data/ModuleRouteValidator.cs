namespace EOS.API.Data;

/// <summary>
/// 模块路由契约（M_URL / NEW_URL / MODI_URL 全量消费）：
/// - M_URL 只存承载页路径（如 /reports、/workbench），菜单渲染时自动追加
///   /{moduleId}；精确路径白名单（特殊页）原样返回；统一表单动作模板
///   （/workbench/{moduleId}/new|edit|view）视为直达表单；外部/脚本链接
///   一律拒绝并回退占位页。
/// - NEW_URL/MODI_URL 决定新增/编辑路由：允许统一表单动作模板（服务端替换
///   {moduleId}）或精确现代路径（特殊页，可带查询串）；空值或非法值视为无值
///   （返回 null），由调用方按统一表单白名单回退或隐藏按钮。
/// - 承载判定（工作台/表单/打印/报表识别）统一由本类提供，替代各处私有前缀判断。
/// - 2026-08-26：浏览器路由前缀由 /document-workbench 收敛为 /workbench（A 档 URL 重构，
///   docs/plans/工作台URL重构.md）；API 路由 /api/v1/document-workbench 不变。
/// </summary>
internal static class ModuleRouteValidator
{
    /// <summary>参数化承载页：M_URL 为该路径时追加 /{moduleId}。</summary>
    private static readonly string[] ParameterizedBases =
    [
        "/reports",
        "/workbench",
        "/search-center",
        "/detail-query",
    ];

    private static readonly string[] ExactRoutes =
    [
        "/dashboard",
        "/admin/menus", "/admin/tables", "/admin/field-audit", "/admin/users",
        "/admin/groups", "/admin/report-setup", "/admin/print-setup/headers", "/admin/print-setup/footers", "/admin/print-setup/tails",
        "/import", "/settings/system", "/settings/hr-setup", "/settings/hrm-setup",
        "/bom-expand", "/jobs", "/my-tasks", "/car-summary",
        "/workflow/design", "/workflow/monitor",
    ];

    /// <summary>统一表单动作模板：NEW_URL/MODI_URL（或 M_URL 直达表单）命中时替换 {moduleId}。</summary>
    private static readonly string[] ActionTemplates =
    [
        "/workbench/{moduleId}/new",
        "/workbench/{moduleId}/edit",
        "/workbench/{moduleId}/view",
    ];

    private static readonly System.Text.RegularExpressions.Regex LegacyPlaceholder =
        new(@"^/legacy/modules/\d+$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string Resolve(string? rawUrl, int moduleId)
    {
        var url = (rawUrl ?? string.Empty).Trim();
        if (url.Length == 0 || IsForbiddenUrl(url)) return Placeholder(moduleId);
        foreach (var template in ActionTemplates)
        {
            if (url.Equals(template, StringComparison.OrdinalIgnoreCase))
                return Substitute(template, moduleId);
        }
        if (ParameterizedBases.Contains(url, StringComparer.OrdinalIgnoreCase))
            return $"{url}/{moduleId}";
        if (ExactRoutes.Contains(url, StringComparer.OrdinalIgnoreCase)) return url;
        return Placeholder(moduleId);
    }

    /// <summary>
    /// 解析新增/编辑动作路由（NEW_URL/MODI_URL）。
    /// 命中统一表单动作模板则替换 {moduleId}；命中特殊页精确路径则原样返回（含查询串）；
    /// 空值或非法值返回 null（视为无值，由调用方回退统一表单或隐藏按钮）。
    /// </summary>
    public static string? ResolveActionUrl(string? rawUrl, int moduleId)
    {
        var url = (rawUrl ?? string.Empty).Trim();
        if (url.Length == 0 || IsForbiddenUrl(url) || !url.StartsWith('/')) return null;
        var queryStart = url.IndexOf('?', StringComparison.Ordinal);
        var path = queryStart >= 0 ? url[..queryStart] : url;
        var query = queryStart >= 0 ? url[queryStart..] : string.Empty;
        foreach (var template in ActionTemplates)
        {
            if (path.Equals(template, StringComparison.OrdinalIgnoreCase))
                return Substitute(template, moduleId) + query;
        }
        return ExactRoutes.Contains(path, StringComparer.OrdinalIgnoreCase) ? url : null;
    }

    /// <summary>M_URL 契约校验（菜单管理保存用）：空值（目录节点）、承载页、精确路径、动作模板或迁移占位页。</summary>
    public static bool IsValidHostUrl(string? rawUrl)
    {
        var url = (rawUrl ?? string.Empty).Trim();
        if (url.Length == 0 || IsForbiddenUrl(url) || !url.StartsWith('/')) return url.Length == 0;
        return ParameterizedBases.Contains(url, StringComparer.OrdinalIgnoreCase)
            || ExactRoutes.Contains(url, StringComparer.OrdinalIgnoreCase)
            || ActionTemplates.Any(template => url.Equals(template, StringComparison.OrdinalIgnoreCase))
            || LegacyPlaceholder.IsMatch(url);
    }

    /// <summary>NEW_URL/MODI_URL 契约校验（菜单管理保存用）：空值或可解析的现代动作路由。</summary>
    public static bool IsValidActionUrl(string? rawUrl)
        => string.IsNullOrWhiteSpace(rawUrl) || ResolveActionUrl(rawUrl, 0) is not null;

    /// <summary>工作台承载判定（工作台定义、统一表单、单据打印共用）。</summary>
    public static bool IsWorkbenchUrl(string url)
    {
        var value = url.Trim().Replace('\\', '/');
        return value.Equals("/workbench", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/workbench/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>报表承载判定（报表查看器/打印体系共用）。</summary>
    public static bool IsReportUrl(string url)
    {
        var value = url.Trim().Replace('\\', '/');
        return value.Equals("/reports", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("/reports/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsForbiddenUrl(string url) =>
        !url.StartsWith('/')
        || url.StartsWith("//", StringComparison.Ordinal)
        || url.Contains("://", StringComparison.Ordinal)
        || url.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
        || url.Contains("data:", StringComparison.OrdinalIgnoreCase)
        || url.Contains('\\');

    private static string Substitute(string template, int moduleId) =>
        template.Replace("{moduleId}", moduleId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string Placeholder(int moduleId) => $"/legacy/modules/{moduleId}";
}
