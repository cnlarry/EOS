using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/reports/{moduleId:int}")]
public sealed class ReportController(
    ReportRepository repository,
    LegacyRightsRepository rightsRepository) : ControllerBase
{
    [HttpGet("definition")]
    public async Task<IActionResult> Definition(int moduleId,CancellationToken token)
    {
        var userId=User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if(userId is null)return Unauthorized();
        var rights=await rightsRepository.GetAsync(userId,moduleId,token);
        if(!rights.CanBrowse)return Forbid();
        var definition=await repository.GetDefinitionAsync(moduleId,userId,rights.CanViewCost,rights.CanViewSecrecy,
            rights.DeniedMasterFields,token);
        return definition is null?NotFound():Ok(definition);
    }

    [HttpPost("query")]
    public async Task<IActionResult> Query(int moduleId,
        [FromQuery]int page,[FromQuery]int pageSize,[FromBody]ReportQueryRequest request,CancellationToken token)
    {
        var definition=await AuthorizedDefinition(moduleId,token);
        if(definition is null)return NotFound();
        return Ok(await repository.QueryAsync(definition,request,page,pageSize,token));
    }

    [HttpGet("condition-options/{serialNo:int}")]
    public async Task<IActionResult> ConditionOptions(int moduleId,int serialNo,CancellationToken token)
    {
        var definition=await AuthorizedDefinition(moduleId,token);
        if(definition is null)return NotFound();
        return Ok(await repository.GetConditionOptionsAsync(definition,serialNo,token));
    }

    [HttpPost("export")]
    public async Task<IActionResult> Export(int moduleId,[FromBody]ReportQueryRequest request,CancellationToken token)
    {
        var definition=await AuthorizedDefinition(moduleId,token);
        if(definition is null)return NotFound();
        var result=await repository.QueryAsync(definition,request,1,200,token);
        using var writer=new StringWriter();
        writer.Write('\uFEFF');
        writer.WriteLine(string.Join(',',definition.Columns.Select(column=>Escape(column.Label))));
        foreach(var row in result.Rows)
            writer.WriteLine(string.Join(',',definition.Columns.Select(column=>Escape(Convert.ToString(row.GetValueOrDefault(column.Key))??""))));
        return File(System.Text.Encoding.UTF8.GetBytes(writer.ToString()),"text/csv; charset=utf-8","report.csv");
    }

    private async Task<ReportDefinition?> AuthorizedDefinition(int moduleId,CancellationToken token)
    {
        var userId=User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if(userId is null)return null;
        var rights=await rightsRepository.GetAsync(userId,moduleId,token);
        if(!rights.CanBrowse)return null;
        return await repository.GetDefinitionAsync(moduleId,userId,rights.CanViewCost,rights.CanViewSecrecy,
            rights.DeniedMasterFields,token);
    }

    private static string Escape(string value)=>
        value.IndexOfAny([',','"','\r','\n'])>=0?"\""+value.Replace("\"","\"\"")+"\"":value;
}
