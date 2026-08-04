using EOS.API.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController, Authorize, Route("api/document-workbench/{moduleId:int}")]
public sealed class DocumentWorkbenchController(DocumentWorkbenchRepository repository, LegacyRightsRepository rightsRepository, EOS.API.Security.CurrentUserContext userContext) : ControllerBase
{
    [HttpGet("definition")]
    public async Task<IActionResult> Definition(int moduleId,CancellationToken token)=>await AuthorizedDefinition(moduleId,token) is { } definition?Ok(definition):NotFound();

    [HttpGet("records")]
    public async Task<IActionResult> Records(int moduleId,[FromQuery]int page=1,[FromQuery]int pageSize=20,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,CancellationToken token=default){var definition=await AuthorizedDefinition(moduleId,token);if(definition is null)return NotFound();try{return Ok(await repository.GetRowsAsync(definition,false,new Dictionary<string,string>(),page,pageSize,token,null,sortField,sortDirection));}catch(ArgumentException error){return BadRequest(new {message=error.Message});}}

    [HttpPost("query")]
    public async Task<IActionResult> Query(int moduleId,[FromBody]WorkbenchQuery query,[FromQuery]int page=1,[FromQuery]int pageSize=20,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,CancellationToken token=default){var definition=await AuthorizedDefinition(moduleId,token);if(definition is null)return NotFound();try{return Ok(await repository.GetRowsAsync(definition,false,new Dictionary<string,string>(),page,pageSize,token,query,sortField,sortDirection));}catch(ArgumentException error){return BadRequest(new { message=error.Message });}}

    [HttpGet("details")]
    public async Task<IActionResult> Details(int moduleId,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,CancellationToken token=default){var definition=await AuthorizedDefinition(moduleId,token);if(definition is null)return NotFound();var keys=Request.Query.ToDictionary(item=>item.Key,item=>item.Value.ToString(),StringComparer.OrdinalIgnoreCase);try{return Ok(await repository.GetRowsAsync(definition,true,keys,1,100,token,null,sortField,sortDirection));}catch(ArgumentException error){return BadRequest(new{message=error.Message});}}

    [HttpGet("columns")]
    public async Task<IActionResult> Columns(int moduleId,CancellationToken token){var definition=await AuthorizedDefinition(moduleId,token);var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;if(definition is null||userId is null)return NotFound();return Ok(await repository.GetColumnSettingsAsync(definition,userId,token));}

    [HttpGet("column-editor")]
    public async Task<IActionResult> ColumnEditor(int moduleId,CancellationToken token){var definition=await AuthorizedDefinition(moduleId,token);var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;if(definition is null||userId is null)return NotFound();var settings=await repository.GetColumnEditorSettingsAsync(definition,userId,token);return Ok(new {current=settings.Current,defaults=settings.Defaults});}

    [HttpPut("columns")]
    public async Task<IActionResult> SaveColumns(int moduleId,[FromBody]SaveWorkbenchColumns settings,CancellationToken token){var definition=await AuthorizedDefinition(moduleId,token);var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;if(definition is null||userId is null)return NotFound();try{await repository.SaveColumnSettingsAsync(definition,userId,settings,token);return NoContent();}catch(ArgumentException error){return BadRequest(new {message=error.Message});}}

    [HttpDelete("columns")]
    public async Task<IActionResult> ResetColumns(int moduleId,CancellationToken token){var definition=await AuthorizedDefinition(moduleId,token);var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;if(definition is null||userId is null)return NotFound();await repository.ResetColumnSettingsAsync(definition,userId,token);return NoContent();}

    [HttpGet("field-settings/lookups/{kind}")]
    public async Task<IActionResult> FieldSettingsLookups(int moduleId,string kind,CancellationToken token){if(await SetupDefinition(moduleId,token) is null)return Forbid();return kind.ToLowerInvariant() switch{"tables"=>Ok(await repository.GetFieldSetupTablesAsync(token)),"modules"=>Ok(await repository.GetFieldSetupModulesAsync(token)),_=>NotFound()};}

    [HttpGet("field-settings")]
    public async Task<IActionResult> FieldSettingsList(int moduleId,[FromQuery]bool detail=false,CancellationToken token=default){var access=await SetupDefinition(moduleId,token);return access is null?Forbid():Ok(await repository.GetFieldSummariesAsync(access,detail,token));}

    [HttpGet("field-settings/{fieldKey}")]
    public async Task<IActionResult> FieldSettings(int moduleId,string fieldKey,[FromQuery]bool detail=false,CancellationToken token=default){var access=await SetupDefinition(moduleId,token);if(access is null)return Forbid();var field=await repository.GetFieldMetadataAsync(access,detail,fieldKey,token);return field is null?NotFound():Ok(field);}

    [HttpPut("field-settings/{fieldKey}")]
    public async Task<IActionResult> UpdateFieldSettings(int moduleId,string fieldKey,[FromBody]UpdateWorkbenchFieldMetadata update,[FromQuery]bool detail=false,CancellationToken token=default){var access=await SetupDefinition(moduleId,token);if(access is null)return Forbid();try{await repository.UpdateFieldMetadataAsync(access,detail,fieldKey,update,userContext.EmployeeName,token);return NoContent();}catch(ArgumentException error){return BadRequest(new {message=error.Message});}catch(KeyNotFoundException error){return NotFound(new {message=error.Message});}}

    private async Task<WorkbenchDefinition?> SetupDefinition(int moduleId,CancellationToken token){var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;if(userId is null)return null;var rights=await rightsRepository.GetAsync(userId,moduleId,token);return rights.CanBrowse&&rights.CanSetup?await repository.GetDefinitionAsync(moduleId,userId,rights.CanViewCost,rights.CanViewSecrecy,rights.DeniedMasterFields,rights.DeniedDetailFields,token):null;}

    private async Task<WorkbenchDefinition?> AuthorizedDefinition(int moduleId,CancellationToken token){var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;if(userId is null)return null;var rights=await rightsRepository.GetAsync(userId,moduleId,token);return rights.CanBrowse?await repository.GetDefinitionAsync(moduleId,userId,rights.CanViewCost,rights.CanViewSecrecy,rights.DeniedMasterFields,rights.DeniedDetailFields,token):null;}
}
