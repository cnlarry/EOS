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
        var moduleIds = modules.Select(module => module.Id).ToHashSet();
        var roots = modules.Where(module => module.ParentId == 0 || module.Id == module.RootId)
            .OrderBy(module => module.SortIndex).ThenBy(module => module.Id).ToList();
        var navigation = new List<object> {
            new { id = "dashboard", label = "工作台", route = "/dashboard", icon = "dashboard", children = (object?)null }
        };
        var iconOverrides = configuration.GetSection("NavigationIcons").GetChildren()
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var children = BuildChildren(root.Id, root.Label, modules, moduleIds, iconOverrides);
            if (children.Count == 0) continue;
            navigation.Add(new { id = $"module-{root.Id}", label = root.Label, route = (string?)null, icon = IconFor(root.Id, root.Label, iconOverrides), children = (object?)children });
        }
        navigation.Add(new { id = "settings", label = "个人设置", route = "/settings/profile", icon = "settings", children = (object?)null });
        var permissions = modules.Where(module => module.Enabled).Select(module => $"legacy-module.{module.Id}.read").ToList();
        return Ok(new { user, permissions, navigation });
    }

    private static List<object> BuildChildren(int rootId, string rootLabel, IReadOnlyList<LegacyNavigationModule> modules, IReadOnlySet<int> included, IReadOnlyDictionary<string, string?> iconOverrides)
    {
        var direct = modules.Where(module => module.ParentId == rootId && module.Id != rootId)
            .OrderBy(module => module.SortIndex).ThenBy(module => module.Id).ToList();
        var result = new List<object>();
        foreach (var module in direct)
        {
            var descendants = modules.Where(child => child.ParentId == module.Id && included.Contains(child.Id))
                .OrderBy(child => child.SortIndex).ThenBy(child => child.Id).ToList();
            if (descendants.Count > 0)
                result.AddRange(descendants.Select(child => MenuLeaf(child, rootId, rootLabel, iconOverrides)));
            else if (module.Enabled) result.Add(MenuLeaf(module, rootId, rootLabel, iconOverrides));
        }
        return result;
    }

    private static object MenuLeaf(LegacyNavigationModule module, int rootId, string rootLabel, IReadOnlyDictionary<string, string?> iconOverrides) => new {
        id = $"module-{module.Id}", label = module.Label, route = RouteFor(module), icon = IconFor(rootId, rootLabel, iconOverrides), children = (object?)null
    };

    private static readonly Dictionary<int, string> ModernRoutes = new()
    {
        [2302] = "/admin/tables",
        [2306] = "/admin/users",
    };

    private static string RouteFor(LegacyNavigationModule module) =>
        ModernRoutes.TryGetValue(module.Id, out var modern)
            ? modern
            : IsDocumentWorkbenchUrl(module.LegacyUrl)
                ? $"/document-workbench/{module.Id}"
                : $"/legacy/modules/{module.Id}";

    private static bool IsDocumentWorkbenchUrl(string? legacyUrl)
    {
        if (string.IsNullOrWhiteSpace(legacyUrl)) return false;
        var normalized = legacyUrl.Trim().Replace('\\', '/');
        if (normalized.StartsWith("~/", StringComparison.Ordinal)) normalized = normalized[2..];
        normalized = normalized.TrimStart('/').Split('?', '#')[0].ToLowerInvariant();
        return normalized == "comm/view_frame.aspx";
    }

    private static readonly Dictionary<int, string> LegacyRootIcons = new() { [13] = "inventory", [14] = "sales", [15] = "procurement" };

    private static readonly (string Keyword, string Icon)[] IconKeywordRules =
    {
        ("采购", "procurement"),
        ("销售", "sales"),
        ("生产", "production"),
        ("BOM", "production"),
        ("制造", "production"),
        ("工艺", "production"),
        ("制程", "production"),
        ("工序", "production"),
        ("半成品", "product"),
        ("产品", "product"),
        ("人事", "hr"),
        ("人力资源", "hr"),
        ("考勤", "hr"),
        ("工资", "hr"),
        ("薪资", "hr"),
        ("招聘", "hr"),
        ("财务", "finance"),
        ("应收", "finance"),
        ("应付", "finance"),
        ("会计", "finance"),
        ("海关", "customs"),
        ("报关", "customs"),
        ("质量", "quality"),
        ("质检", "quality"),
        ("品管", "quality"),
        ("品质", "quality"),
        ("品检", "quality"),
        ("报表", "report"),
        ("查询", "query"),
        ("基础资料", "base"),
        ("基本资料", "base"),
        ("资料", "base"),
        ("系统", "settings"),
        ("设置", "settings"),
        ("权限", "settings"),
        ("设备", "equipment"),
        ("机器", "equipment"),
        ("模具", "equipment"),
        ("车辆", "vehicle"),
        ("车队", "vehicle"),
        ("汽车", "vehicle"),
        ("条码", "barcode"),
        ("条形码", "barcode"),
        ("工作流", "workflow"),
        ("流程", "workflow"),
        ("打样", "sample"),
        ("样品", "sample"),
        ("托外", "outsource"),
        ("外发", "outsource"),
        ("客户", "customer"),
        ("供应商", "supplier"),
        ("库存", "inventory"),
        ("仓存", "inventory"),
        ("盘点", "inventory"),
        ("单据", "document"),
        ("订单", "document"),
    };

    private static string IconFor(int rootId, string rootLabel, IReadOnlyDictionary<string, string?> iconOverrides)
    {
        if (iconOverrides.TryGetValue(rootId.ToString(), out var overrideIcon) && !string.IsNullOrWhiteSpace(overrideIcon))
            return overrideIcon;
        foreach (var (keyword, icon) in IconKeywordRules)
            if (rootLabel.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return icon;
        if (LegacyRootIcons.TryGetValue(rootId, out var legacyIcon)) return legacyIcon;
        return "folder";
    }
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
