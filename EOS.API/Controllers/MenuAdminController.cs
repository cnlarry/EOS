using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 菜单管理（模块 2301，
/// 读要求 CanBrowse，写要求 CanSetup；编号变更自动级联子级与权限引用。
/// </summary>
[ApiController, Authorize, Route("api/v1/admin/menus")]
public sealed class MenuAdminController(
    MenuAdminRepository repository,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext,
    IConfiguration configuration) : ControllerBase
{
    private const int MenuAdminModuleId = 2301;

    [HttpGet]
    public async Task<IActionResult> Modules([FromQuery] string? keyword = null, CancellationToken token = default)
    {
        if (!await CanBrowse(token)) return Forbid();
        var result = await repository.GetModulesAsync(keyword, token);
        var overrides = configuration.GetSection("NavigationIcons").GetChildren()
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);
        var byId = result.Modules.ToDictionary(module => module.M_IDX);
        var modules = result.Modules
            .Select(module => module with { Icon = ResolveIcon(module, byId, overrides) })
            .ToList();
        return Ok(new MenuAdminList(result.Total, modules));
    }

    /// <summary>表选择器候选（操作主表/副表选择，CanBrowse 2301 即可读取）。</summary>
    [HttpGet("tables")]
    public async Task<IActionResult> Tables(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetTablesAsync(token));
    }

    /// <summary>字段选择器候选（排序字段/必需字段/不可解批字段/过滤条件选择，CanBrowse 2301 即可读取）。</summary>
    [HttpGet("fields")]
    public async Task<IActionResult> Fields([FromQuery] string table, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetTableFieldsAsync(table, token));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Module(int id, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var module = await repository.GetModuleAsync(id, token);
        return module is null ? NotFound() : Ok(module);
    }

    [HttpPost]
    public async Task<IActionResult> Create(MenuAdminModule input, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        var id = await repository.SaveAsync(input, null, userContext.EmployeeName, token);
        return Created($"/api/admin/menus/{id}", new { id });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, MenuAdminModule input, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.SaveAsync(input, id, userContext.EmployeeName, token);
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.DeleteAsync(id, token);
        return NoContent();
    }

    /// <summary>
    /// 菜单同级排序：top=同级顶部、up=向上一位、down=向下一位、bottom=同级底部。
    /// 只改写 SORT_IDX，全部事务内完成。
    /// </summary>
    [HttpPut("{id:int}/sort")]
    public async Task<IActionResult> Reorder(int id, MenuReorderRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.ReorderAsync(id, request.Action, userContext.EmployeeName, token);
        return NoContent();
    }

    /// <summary>
    /// 菜单拖拽移动：parentId=目标父节点（null/0=根级），beforeId=插入到该同级节点之前
    /// （null=追加到同级末尾）。重写相关同级 SORT_IDX，根变化时归一化子树 M_ROOT_IDX。
    /// </summary>
    [HttpPut("{id:int}/move")]
    public async Task<IActionResult> Move(int id, MenuMoveRequest request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.MoveAsync(id, request.ParentId, request.BeforeId, userContext.EmployeeName, token);
        return NoContent();
    }

    /// <summary>菜单默认查询列：table=master|detail。</summary>
    [HttpGet("{id:int}/default-columns")]
    public async Task<IActionResult> DefaultColumns(int id, [FromQuery] string table = "master", CancellationToken token = default)
    {
        if (!await CanBrowse(token)) return Forbid();
        if (table is not ("master" or "detail"))
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_TABLE_KIND", "table 仅支持 master 或 detail。"));
        return Ok(await repository.GetDefaultColumnsAsync(id, table, token));
    }

    [HttpPut("{id:int}/default-columns")]
    public async Task<IActionResult> SaveDefaultColumns(int id, SaveMenuDefaultColumns request, CancellationToken token)
    {
        if (!await CanSetup(token)) return Forbid();
        await repository.SaveDefaultColumnsAsync(id, request, token);
        return NoContent();
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, MenuAdminModuleId, token)).CanBrowse;

    private async Task<bool> CanSetup(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, MenuAdminModuleId, token)).CanSetup;

    /// <summary>沿 M_P_IDX 链定位所在根菜单，解析其侧栏图标名（与 /api/app/bootstrap 一致）。</summary>
    private static string? ResolveIcon(
        MenuAdminModule module,
        IReadOnlyDictionary<int, MenuAdminModule> byId,
        IReadOnlyDictionary<string, string?> overrides)
    {
        var root = module;
        var visited = new HashSet<int>();
        while (root.M_P_IDX is { } parent && parent > 0 && visited.Add(root.M_IDX))
        {
            if (!byId.TryGetValue(parent, out var parentModule))
                break;
            root = parentModule;
        }
        // 用户自选图标优先，其次配置/关键字规则
        if (!string.IsNullOrWhiteSpace(root.M_ICON))
            return root.M_ICON.Trim();
        return MenuIconResolver.Resolve(root.M_IDX, root.M_DESC, overrides);
    }
}
