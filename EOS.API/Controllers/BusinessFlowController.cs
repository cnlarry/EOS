using EOS.API.Data;
using EOS.API.Features.BusinessFlow;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 业务流程图（模块 2314，根 23 系统管理 / 父 2311 数据表维护）：只读元数据浏览。
/// 数据取自 FIELD_DATASOURCE（字段数据来源），不新增任何写路径。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/admin/business-flow")]
public sealed class BusinessFlowController(
    BusinessFlowRepository repository,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext) : ControllerBase
{
    private const int FlowModuleId = 2314;

    /// <summary>业务域总览：域清单与域间引用计数（权限门 CanBrowse 2314）。</summary>
    [HttpGet("overview")]
    public async Task<IActionResult> Overview(CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        return Ok(await repository.GetOverviewAsync(token));
    }

    /// <summary>某个业务域内的表间明细图（权限门 CanBrowse 2314）。</summary>
    [HttpGet("domains/{rootIdx:int}")]
    public async Task<IActionResult> Domain(int rootIdx, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var graph = await repository.GetDomainGraphAsync(rootIdx, token);
        return graph is null ? NotFound() : Ok(graph);
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, FlowModuleId, token)).CanBrowse;
}
