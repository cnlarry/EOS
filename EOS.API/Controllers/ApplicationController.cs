using System.Security.Claims;
using EOS.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/app")]
public sealed class ApplicationController(NavigationRepository navigationRepository) : ControllerBase
{
    [HttpGet("bootstrap")]
    public async Task<IActionResult> Bootstrap(CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;
        var user = new {
            id = userId, username = userId,
            displayName = User.FindFirstValue(ClaimTypes.Name) ?? userId,
            avatarText = GetAvatarText(User.FindFirstValue(ClaimTypes.Name) ?? userId),
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
        foreach (var root in roots)
        {
            var children = BuildChildren(root.Id, modules, moduleIds);
            if (children.Count == 0) continue;
            navigation.Add(new { id = $"module-{root.Id}", label = root.Label, route = (string?)null, icon = IconFor(root.Id), children = (object?)children });
        }
        navigation.Add(new { id = "settings", label = "个人设置", route = "/settings/profile", icon = "settings", children = (object?)null });
        var permissions = modules.Where(module => module.Enabled).Select(module => $"legacy-module.{module.Id}.read").ToList();
        return Ok(new { user, permissions, navigation });
    }

    private static List<object> BuildChildren(int rootId, IReadOnlyList<LegacyNavigationModule> modules, IReadOnlySet<int> included)
    {
        var direct = modules.Where(module => module.ParentId == rootId && module.Id != rootId)
            .OrderBy(module => module.SortIndex).ThenBy(module => module.Id).ToList();
        var result = new List<object>();
        foreach (var module in direct)
        {
            var descendants = modules.Where(child => child.ParentId == module.Id && included.Contains(child.Id))
                .OrderBy(child => child.SortIndex).ThenBy(child => child.Id).ToList();
            if (descendants.Count > 0)
                result.AddRange(descendants.Select(child => MenuLeaf(child, rootId)));
            else if (module.Enabled) result.Add(MenuLeaf(module, rootId));
        }
        return result;
    }

    private static object MenuLeaf(LegacyNavigationModule module, int rootId) => new {
        id = $"module-{module.Id}", label = module.Label, route = RouteFor(module), icon = IconFor(rootId), children = (object?)null
    };

    private static string RouteFor(LegacyNavigationModule module) => IsDocumentWorkbenchUrl(module.LegacyUrl)
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

    private static string IconFor(int rootId) => rootId switch { 13 => "inventory", 14 => "sales", 15 => "procurement", _ => "settings" };
    private static string GetAvatarText(string name) => string.Concat(name.Trim().TakeLast(Math.Min(2, name.Trim().Length)));
}
