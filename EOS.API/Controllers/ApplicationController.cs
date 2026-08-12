using System.Security.Claims;
using EOS.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/app")]
public sealed class ApplicationController(NavigationRepository navigationRepository, IConfiguration configuration) : ControllerBase
{
    [HttpGet("bootstrap")]
    public async Task<IActionResult> Bootstrap(CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var displayName = User.FindFirstValue(ClaimTypes.Name) ?? userId;
        var user = new {
            id = userId, username = userId,
            displayName,
            employeeId = User.FindFirstValue("employee_id") ?? "",
            avatarText = GetAvatarText(displayName),
            avatarUrl = (string?)null,
            roleName = "ERP 用户",
            organization = new { id = User.FindFirstValue("department_id") ?? "", name = User.FindFirstValue("department_name") ?? "" }
        };
        var modules = await navigationRepository.GetForUserAsync(userId, token);
        var roots = modules.Where(module => module.ParentId == 0)
            .OrderBy(module => module.SortIndex).ThenBy(module => module.Id).ToList();
        var navigation = new List<object> {
            new { id = "dashboard", label = "工作台", route = "/dashboard", icon = "dashboard", children = (object?)null }
        };
        var iconOverrides = configuration.GetSection("NavigationIcons").GetChildren()
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var rootIcon = IconFor(root.Id, root.Label, iconOverrides);
            var children = BuildChildren(root.Id, modules, rootIcon);
            if (children.Count == 0) continue;
            navigation.Add(new { id = $"module-{root.Id}", label = root.Label, route = (string?)null, icon = IconFor(root.Id, root.Label, iconOverrides), children = (object?)children });
        }
        navigation.Add(new { id = "settings", label = "个人设置", route = "/settings/profile", icon = "settings", children = (object?)null });
        var permissions = modules.Where(module => module.Enabled).Select(module => $"legacy-module.{module.Id}.read").ToList();
        return Ok(new { user, permissions, navigation });
    }

    /// <summary>
    /// 递归构建完整菜单树（旧系统为「无限级」，实际数据三级：根 → 组 → 叶子）。
    /// 叶子携带 moduleId / masterTable / groups，供分组（第 4 级）与搜索使用。
    /// 中间层不再被跳过（此前实现把二级压平成叶子，丢失层级）。
    /// </summary>
    private static List<object> BuildChildren(int parentId, IReadOnlyList<LegacyNavigationModule> modules, string rootIcon)
    {
        var result = new List<object>();
        var direct = modules.Where(module => module.ParentId == parentId && module.Id != parentId)
            .OrderBy(module => module.SortIndex).ThenBy(module => module.Id).ToList();
        foreach (var module in direct)
        {
            var descendants = BuildChildren(module.Id, modules, rootIcon);
            if (descendants.Count > 0)
                result.Add(new { id = $"module-{module.Id}", label = module.Label, route = (string?)null, icon = rootIcon, children = (object?)descendants });
            else if (module.Enabled) result.Add(MenuLeaf(module, rootIcon));
        }
        return result;
    }

    private static object MenuLeaf(LegacyNavigationModule module, string rootIcon) => new
    {
        id = $"module-{module.Id}",
        label = module.Label,
        alias = module.Alias,
        route = RouteFor(module),
        icon = rootIcon,
        moduleId = module.Id,
        masterTable = module.MasterTable,
        groups = module.Groups
            .Where(group => group.Enabled && !string.IsNullOrWhiteSpace(group.Description))
            .Select(group => new { index = group.Index, description = group.Description })
            .ToList()
    };

    private static readonly Dictionary<int, string> ModernRoutes = new()
    {
        [2301] = "/admin/menus",
        [2302] = "/admin/tables",
        [2303] = "/admin/field-audit",
        [2306] = "/admin/users",
        [2310] = "/admin/table-data",
        [2312] = "/admin/table-data",
        [230902] = "/import",
        [110111] = "/settings/system",
        [129802] = "/bom-expand",
        [230901] = "/jobs",
        [180654] = "/jobs",
        [180659] = "/jobs",
        [180505] = "/jobs",
        [2102] = "/my-tasks",
        [199901] = "/car-summary",
        [14996] = "/detail-query/14996",
        [14998] = "/detail-query/14998",
        [170297] = "/detail-query/170297",
        [209805] = "/reports/209805",
        [180213] = "/settings/hr-setup",
        [180662] = "/settings/hrm-setup",
    };

    private static string RouteFor(LegacyNavigationModule module) =>
        ModernRoutes.TryGetValue(module.Id, out var modern)
            ? modern
            : IsSearchCenterUrl(module.LegacyUrl)
                ? $"/search-center/{module.Id}"
            : IsReportUrl(module.LegacyUrl)
                ? $"/reports/{module.Id}"
            : IsDocumentWorkbenchUrl(module.LegacyUrl)
                ? $"/document-workbench/{module.Id}"
                : $"/legacy/modules/{module.Id}";

    private static bool IsSearchCenterUrl(string? legacyUrl)
    {
        if (string.IsNullOrWhiteSpace(legacyUrl)) return false;
        var normalized = legacyUrl.Trim().Replace('\\', '/').ToLowerInvariant();
        return normalized.Contains("search_frame") || normalized.EndsWith("comm/searchcenter.aspx");
    }

    private static bool IsReportUrl(string? legacyUrl)
    {
        if (string.IsNullOrWhiteSpace(legacyUrl)) return false;
        var normalized = legacyUrl.Trim().Replace('\\', '/');
        if (normalized.StartsWith("~/", StringComparison.Ordinal)) normalized = normalized[2..];
        return normalized.StartsWith("RPT/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDocumentWorkbenchUrl(string? legacyUrl)
    {
        if (string.IsNullOrWhiteSpace(legacyUrl)) return false;
        var normalized = legacyUrl.Trim().Replace('\\', '/');
        if (normalized.StartsWith("~/", StringComparison.Ordinal)) normalized = normalized[2..];
        normalized = normalized.TrimStart('/').Split('?', '#')[0].ToLowerInvariant();
        return normalized is "comm/view_frame.aspx"
            or "comm/m_view_frame.aspx"
            or "hr/hr_view_frame.aspx"
            or "hrm/hr_view_frame.aspx"
            or "comm/sysdept_view_frame.aspx"
            or "admin/menubuilder.aspx"
            or "hr/diarytoother.aspx"
            or "hrm/diary.aspx";
    }

    private static string IconFor(int rootId, string rootLabel, IReadOnlyDictionary<string, string?> iconOverrides)
        => MenuIconResolver.Resolve(rootId, rootLabel, iconOverrides);
    private static string GetAvatarText(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return "?";
        var first = trimmed[..1];
        // 中文姓名取首字；拉丁姓名取前两个字符（大写）
        return first[0] is >= '\u4E00' and <= '\u9FFF'
            ? first
            : trimmed.Length >= 2 ? trimmed[..2].ToUpperInvariant() : trimmed.ToUpperInvariant();
    }
}
