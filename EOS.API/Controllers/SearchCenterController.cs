using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/search-center")]
public sealed class SearchCenterController(
    SearchCenterRepository repository,
    ModuleRightsRepository rightsRepository) : ControllerBase
{
    [HttpGet("modules")]
    public async Task<IActionResult> Modules(CancellationToken token)
        => Ok(await repository.GetModulesAsync(token));

    [HttpGet("{moduleId:int}/definition")]
    public async Task<IActionResult> Definition(int moduleId,[FromQuery]string table,CancellationToken token)
    {
        var rights=await AuthorizedRights(moduleId,token);
        if(rights?.CanBrowse!=true)return Forbid();
        var definition=await repository.GetDefinitionAsync(moduleId,table,rights.CanViewCost,
            rights.CanViewSecrecy,rights.DeniedMasterFields,token);
        return definition is null?NotFound():Ok(definition);
    }

    [HttpPost("{moduleId:int}/query")]
    public async Task<IActionResult> Query(int moduleId,[FromBody]SearchQueryRequest request,
        [FromQuery]int page,[FromQuery]int pageSize,CancellationToken token)
    {
        var rights=await AuthorizedRights(moduleId,token);
        if(rights?.CanBrowse!=true)return Forbid();
        var definition=await repository.GetDefinitionAsync(moduleId,request.Table,rights.CanViewCost,
            rights.CanViewSecrecy,rights.DeniedMasterFields,token);
        if(definition is null)return NotFound();
        return Ok(await repository.QueryAsync(definition,request,page,pageSize,token));
    }

    private async Task<ModuleRights?> AuthorizedRights(int moduleId,CancellationToken token)
    {
        var userId=User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return userId is null?null:await rightsRepository.GetAsync(userId,moduleId,token);
    }
}
