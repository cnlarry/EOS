using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 菜单管理（模块 2301，对齐旧 Admin/MenuBuilder.aspx）。
/// 读要求 CanBrowse，写要求 CanSetup；编号变更自动级联子级与权限引用。
/// </summary>
[ApiController, Authorize, Route("api/admin/menus")]
public sealed class MenuAdminController(
    MenuAdminRepository repository,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private const int MenuAdminModuleId = 2301;

    [HttpGet]
    public async Task<IActionResult> Modules([FromQuery] string? keyword = null, CancellationToken token = default)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetModulesAsync(keyword, token));
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

    /// <summary>菜单默认查询列（对齐旧 MenuBuilder「默认查询」）：table=master|detail。</summary>
    [HttpGet("{id:int}/default-columns")]
    public async Task<IActionResult> DefaultColumns(int id, [FromQuery] string table = "master", CancellationToken token = default)
    {
        if (!await CanBrowse(token)) return Forbid();
        if (table is not ("master" or "detail"))
            return BadRequest(new { code = "INVALID_TABLE_KIND", message = "table 仅支持 master 或 detail。" });
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
}
