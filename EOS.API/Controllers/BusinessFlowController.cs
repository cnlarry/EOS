using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Features.BusinessFlow;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 业务流程图（模块 2314，23 系统管理直下）：只读元数据浏览。
/// 节点是模块（单据/业务页），数据取自 FIELD_DATASOURCE 与 FIELD_RELATION，不新增任何写路径。
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

    /// <summary>某个业务域内的模块关系图（权限门 CanBrowse 2314）。</summary>
    [HttpGet("domains/{rootIdx:int}")]
    public async Task<IActionResult> Domain(int rootIdx, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        var graph = await repository.GetDomainGraphAsync(rootIdx, token);
        return graph is null ? NotFound() : Ok(graph);
    }

    /// <summary>
    /// 两个业务域之间的关系明细（权限门 CanBrowse 2314）：总览图上那条带数字的连线点进来的第三级视图。
    /// 两端必须是不同的域——同一域内的关系在域内图里看，这里不重复提供入口。
    /// </summary>
    [HttpGet("domains/{fromRootIdx:int}/links/{toRootIdx:int}")]
    public async Task<IActionResult> DomainLink(int fromRootIdx, int toRootIdx, CancellationToken token)
    {
        if (!await CanBrowse(token)) return Forbid();
        if (fromRootIdx == toRootIdx)
            return BadRequest(ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                "INVALID_DOMAIN_LINK",
                "两端必须是不同的业务域，同一域内的关系请进入该域查看。"));
        var graph = await repository.GetDomainLinkGraphAsync(fromRootIdx, toRootIdx, token);
        return graph is null ? NotFound() : Ok(graph);
    }

    private async Task<bool> CanBrowse(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, FlowModuleId, token)).CanBrowse;
}
