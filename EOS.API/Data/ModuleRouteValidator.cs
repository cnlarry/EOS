namespace EOS.API.Data;

/// <summary>
/// 模块页面链接校验（M_URL 即模块链接，代码不做旧路径翻译）：
/// M_URL 只存承载页路径（如 /reports、/document-workbench），菜单渲染时自动追加
/// 模块号；精确路径白名单（特殊页）原样返回；外部/脚本链接一律拒绝并回退占位页。
/// </summary>
internal static class ModuleRouteValidator
{
    /// <summary>参数化承载页：M_URL 为该路径时追加 /{moduleId}。</summary>
    private static readonly string[] ParameterizedBases =
    [
        "/reports",
        "/document-workbench",
        "/search-center",
        "/detail-query",
    ];

    private static readonly string[] ExactRoutes =
    [
        "/dashboard",
        "/admin/menus", "/admin/tables", "/admin/field-audit", "/admin/users", "/admin/table-data",
        "/admin/report-setup", "/admin/print-setup/headers", "/admin/print-setup/footers", "/admin/print-setup/tails",
        "/import", "/settings/system", "/settings/hr-setup", "/settings/hrm-setup",
        "/bom-expand", "/jobs", "/my-tasks", "/car-summary",
    ];

    public static string Resolve(string? rawUrl, int moduleId)
    {
        var url = (rawUrl ?? string.Empty).Trim();
        if (url.Length == 0 || !url.StartsWith('/')) return Placeholder(moduleId);
        if (url.Contains("://", StringComparison.Ordinal)
            || url.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
            || url.Contains("data:", StringComparison.OrdinalIgnoreCase)
            || url.Contains('\\')
            || url.StartsWith("//", StringComparison.Ordinal))
            return Placeholder(moduleId);
        if (ParameterizedBases.Contains(url, StringComparer.OrdinalIgnoreCase))
            return $"{url}/{moduleId}";
        if (ExactRoutes.Contains(url, StringComparer.OrdinalIgnoreCase)) return url;
        return Placeholder(moduleId);
    }

    private static string Placeholder(int moduleId) => $"/legacy/modules/{moduleId}";
}
