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
        var navigation = new List<object> {
            new { id = "dashboard", label = "首页", route = "/dashboard", icon = "dashboard", children = (object?)null },
            new { id = "report-center", label = "报表中心", route = "/report-center", icon = "report", children = (object?)null }
        };
        var iconOverrides = configuration.GetSection("NavigationIcons").GetChildren()
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        navigation.AddRange(BuildNavigation(modules, iconOverrides));
        navigation.Add(new { id = "settings", label = "个人设置", route = "/settings/profile", icon = "settings", children = (object?)null });
        var permissions = modules.Where(module => module.Enabled).Select(module => $"fallback-module.{module.Id}.read").ToList();
        return Ok(new { user, permissions, navigation });
    }

    /// <summary>
    /// 构建完整菜单树，返回顶层节点。
    /// 节点形态：有下级的作分组（分组自己也有页面时一并带上落点与模块编号）；没有下级的作叶子，
    /// 携带 moduleId / alias / route / masterTable / groups，供导航渲染、面包屑、第 4 级分组与菜单搜索使用。
    /// 落点按「父模块 → 根模块 → 顶层」逐级回退：元数据里父模块被删号（M_P_IDX 指向已不存在的模块）
    /// 或祖先链不完整时，模块改挂最近的可用祖先——挂在树外等于既不在菜单里、也按模块编号搜不到。
    /// </summary>
    private static List<object> BuildNavigation(IReadOnlyList<NavigationModule> modules, IReadOnlyDictionary<string, string?> iconOverrides)
    {
        var byId = modules.ToDictionary(module => module.Id);
        var childrenOf = modules
            .GroupBy(module => Placement(module, byId))
            .ToDictionary(group => group.Key, group => group.OrderBy(module => module.SortIndex).ThenBy(module => module.Id).ToList());
        var placed = new HashSet<int>();
        var navigation = new List<object>();
        foreach (var root in childrenOf.GetValueOrDefault(0, []))
        {
            // 顶层节点自己就是页面（无下级）时按叶子渲染，不再因"没有下级"被整条丢掉
            var node = BuildNode(root, childrenOf, placed, IconFor(root.Id, root.Label, iconOverrides));
            if (node is not null) navigation.Add(node);
        }
        // 父链成环、或根模块自身不在可访问集合里时，模块挂不上任何祖先：
        // 叶子降级到顶层，避免带页面的模块从菜单与搜索里静默消失（有下级的由其下级各自降级）
        foreach (var module in modules)
        {
            if (placed.Contains(module.Id) || childrenOf.ContainsKey(module.Id) || !module.Enabled) continue;
            navigation.Add(MenuLeaf(module, IconFor(module.RootId, module.Label, iconOverrides)));
        }
        return navigation;
    }

    /// <summary>模块在菜单树上的落点：父模块可访问则挂父模块，否则退回根模块，都不可用则落到顶层。</summary>
    private static int Placement(NavigationModule module, IReadOnlyDictionary<int, NavigationModule> byId)
    {
        if (module.ParentId != 0 && module.ParentId != module.Id && byId.ContainsKey(module.ParentId)) return module.ParentId;
        if (module.RootId != module.Id && byId.ContainsKey(module.RootId)) return module.RootId;
        return 0;
    }

    /// <summary>构建单个节点：有下级即分组，没有下级即叶子（停用且无下级的节点不产出）。</summary>
    private static object? BuildNode(
        NavigationModule module,
        IReadOnlyDictionary<int, List<NavigationModule>> childrenOf,
        HashSet<int> placed,
        string icon)
    {
        placed.Add(module.Id);
        var children = childrenOf.GetValueOrDefault(module.Id, [])
            .Select(child => BuildNode(child, childrenOf, placed, icon))
            .OfType<object>()
            .ToList();
        if (children.Count == 0) return module.Enabled ? MenuLeaf(module, icon) : null;
        var hasOwnPage = HasOwnPage(module);
        return new
        {
            id = $"module-{module.Id}",
            label = module.Label,
            alias = module.Alias,
            // 分组自己也有页面时带上落点与编号：否则"有下级"会吞掉这张页面的入口（菜单搜索按编号也就找不到）
            route = hasOwnPage ? RouteFor(module) : (string?)null,
            icon,
            moduleId = hasOwnPage ? module.Id : (int?)null,
            children = (object?)children
        };
    }

    /// <summary>
    /// 模块自己是否承载页面：声明了承载页（M_URL 非空），或是**单据模块**（有主表 ⇒ 默认落统一工作台）。
    /// 两者都没有 = 纯目录节点，只展开不跳转。
    /// </summary>
    private static bool HasOwnPage(NavigationModule module) =>
        module.Enabled
        && (!string.IsNullOrWhiteSpace(module.SourceUrl) || !string.IsNullOrWhiteSpace(module.MasterTable));

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

    // MODULES.M_URL 即模块页面链接，代码只做安全校验不做翻译；留空时单据模块默认走统一工作台。
    private static string RouteFor(NavigationModule module) =>
        ModuleRouteValidator.Resolve(module.SourceUrl, module.Id, module.MasterTable);

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
