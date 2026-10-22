using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Data.Workbench;
using EOS.API.Errors;
using EOS.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 查询中心（跨模块检索）：
/// - 模块清单 = `SEARCH_1/SEARCH_2` 检索位 ∩ 当前用户可浏览模块；
/// - 授权与定义装配走 <see cref="WorkbenchAccessPolicy"/>，与统一工作台同一入口；
/// - 条件/关键字/排序复用工作台的字段白名单与算子渲染；
/// - 行范围（模块 FILTER + DATA_FILTER + EXEC_TAG）在仓库侧经 <see cref="WorkbenchScopeFilter"/> 落地。
/// 探测口径与工作台一致：不可浏览一律 404，不区分"不存在"与"无权限"。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/search-center")]
public sealed class SearchCenterController(
    SearchCenterRepository repository,
    WorkbenchAccessPolicy accessPolicy) : ControllerBase
{
    [HttpGet("modules")]
    public async Task<IActionResult> Modules(CancellationToken token)
    {
        var userId = CurrentUserId();
        return userId is null ? Unauthorized() : Ok(await repository.GetModulesAsync(userId, token));
    }

    [HttpGet("{moduleId:int}/definition")]
    public async Task<IActionResult> Definition(int moduleId, [FromQuery] string table, CancellationToken token)
    {
        var decision = await accessPolicy.AuthorizeDefinitionAsync(CurrentUserId(), moduleId, token);
        if (decision.Denial is not null) return NotFound();
        var access = decision.Value!;
        var rights = access.Rights;
        var definition = await repository.GetDefinitionAsync(access.Definition, table, rights.CanViewCost,
            rights.CanViewSecrecy, rights.DeniedMasterFields, token);
        return definition is null ? NotFound() : Ok(definition);
    }

    [HttpPost("{moduleId:int}/query")]
    public async Task<IActionResult> Query(int moduleId, [FromBody] SearchQueryRequest request,
        [FromQuery] int page, [FromQuery] int pageSize, CancellationToken token)
    {
        var decision = await accessPolicy.AuthorizeDefinitionAsync(CurrentUserId(), moduleId, token);
        if (decision.Denial is not null) return NotFound();
        var access = decision.Value!;
        var rights = access.Rights;
        var definition = await repository.GetDefinitionAsync(access.Definition, request.Table, rights.CanViewCost,
            rights.CanViewSecrecy, rights.DeniedMasterFields, token);
        if (definition is null) return NotFound();
        try
        {
            return Ok(await repository.QueryAsync(definition, access.Definition, rights.DataFilter, request, page, pageSize, token));
        }
        catch (ArgumentException exception)
        {
            // 条件字段不在白名单、算子非法、条件过多：这是请求本身的问题，给 400 而不是 500。
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "SEARCH_QUERY_INVALID", exception.Message));
        }
    }

    private string? CurrentUserId() => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
}
