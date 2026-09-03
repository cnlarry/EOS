using System.Security.Claims;
using EOS.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/app")]
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
            new { id = "dashboard", label = "首页", route = "/dashboard", icon = "dashboard", children = (object?)null },
            new { id = "report-center", label = "报表中心", route = "/report-center", icon = "report", children = (object?)null }
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
    /// 递归构建完整菜单树。
    /// 叶子携带 moduleId / masterTable / groups，供分组（第 4 级）与搜索使用。
    /// 中间层不再被跳过（此前实现把二级压平成叶子，丢失层级）。
    /// </summary>
    private static List<object> BuildChildren(int parentId, IReadOnlyList<NavigationModule> modules, string rootIcon)
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

    private static object MenuLeaf(NavigationModule module, string rootIcon) => new
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

    // MODULES.M_URL 即模块页面链接，代码只做安全校验不做翻译。
    private static string RouteFor(NavigationModule module) =>
        ModuleRouteValidator.Resolve(module.SourceUrl, module.Id);

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
