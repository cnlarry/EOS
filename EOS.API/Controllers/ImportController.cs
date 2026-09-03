using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/import")]
public sealed class ImportController(
    ImportService service,
    ModuleRightsRepository rightsRepository,
    EOS.API.Security.CurrentUserContext userContext) : ControllerBase
{
    [HttpGet("tables")]
    public async Task<IActionResult> Tables(CancellationToken token)
    {
        if(!await CanImportAsync(token))return Forbid();
        return Ok(await service.GetTablesAsync(token));
    }

    [HttpGet("{table}/definition")]
    public async Task<IActionResult> Definition(string table,CancellationToken token)
    {
        if(!await CanImportAsync(token))return Forbid();
        var definition=await service.GetDefinitionAsync(table,token);
        return definition is null?NotFound():Ok(definition);
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody]string csvText,CancellationToken token)
    {
        if(!await CanImportAsync(token))return Forbid();
        return Ok(ImportService.ParseCsv(csvText));
    }

    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromBody]ImportExecuteRequest request,CancellationToken token)
    {
        if(!await CanImportAsync(token))return Forbid();
        if(request.Rows.Count>5000)return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"IMPORT_TOO_MANY_ROWS","单次导入不能超过 5000 行。"));
        var result=await service.ExecuteAsync(request,userContext.EmployeeName,token);
        return Ok(result);
    }

    private async Task<bool> CanImportAsync(CancellationToken token)
    {
        var userId=User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if(userId is null)return false;
        var rights=await rightsRepository.GetAsync(userId,230902,token);
        return rights.CanBrowse&&rights.CanSetup;
    }
}
