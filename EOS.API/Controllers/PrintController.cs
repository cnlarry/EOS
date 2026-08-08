using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/print")]
public sealed class PrintController(
    PrintService service,
    LegacyRightsRepository rightsRepository) : ControllerBase
{
    [HttpPost("{moduleId:int}")]
    public async Task<IActionResult> Print(int moduleId,[FromBody]PrintRequest request,CancellationToken token)
    {
        var userId=User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if(userId is null)return Unauthorized();
        var rights=await rightsRepository.GetAsync(userId,moduleId,token);
        if(!rights.CanBrowse)return Forbid();
        var data=await service.GetPrintDataAsync(moduleId,request.Key,rights.CanViewCost,rights.CanViewSecrecy,
            rights.DeniedMasterFields,rights.DeniedDetailFields,token);
        return data is null?NotFound():Ok(data);
    }
}

public sealed record PrintRequest(IReadOnlyList<string> Key);
