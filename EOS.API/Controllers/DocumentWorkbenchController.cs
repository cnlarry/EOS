using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EOS.API.Controllers;

[ApiController, Authorize, Route("api/document-workbench/{moduleId:int}")]
public sealed class DocumentWorkbenchController(DocumentWorkbenchRepository repository, LegacyRightsRepository rightsRepository, EOS.API.Security.CurrentUserContext userContext, IOptions<UnifiedFormEditorSettings> formSettings, ILogger<DocumentWorkbenchController> logger) : ControllerBase
{
    [HttpGet("definition")]
    public async Task<IActionResult> Definition(int moduleId,CancellationToken token)=>await AuthorizedDefinition(moduleId,token) is { } definition?Ok(definition):NotFound();

    [HttpGet("records")]
    public async Task<IActionResult> Records(int moduleId,[FromQuery]int page=1,[FromQuery]int pageSize=20,[FromQuery]string? keyword=null,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,[FromQuery]string? sortFields=null,[FromQuery]string? sortDirections=null,[FromQuery]int? groupIndex=null,[FromQuery]string? groupValue=null,CancellationToken token=default){var definition=await AuthorizedDefinition(moduleId,token);if(definition is null)return NotFound();return Ok(await repository.GetRowsAsync(definition,false,new Dictionary<string,string>(),page,pageSize,token,null,keyword,sortFields??sortField,sortDirections??sortDirection,groupIndex,groupValue));}

    [HttpPost("query")]
    public async Task<IActionResult> Query(int moduleId,[FromBody]WorkbenchQuery query,[FromQuery]int page=1,[FromQuery]int pageSize=20,[FromQuery]string? keyword=null,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,[FromQuery]string? sortFields=null,[FromQuery]string? sortDirections=null,[FromQuery]int? groupIndex=null,[FromQuery]string? groupValue=null,CancellationToken token=default){var definition=await AuthorizedDefinition(moduleId,token);if(definition is null)return NotFound();return Ok(await repository.GetRowsAsync(definition,false,new Dictionary<string,string>(),page,pageSize,token,query,keyword,sortFields??sortField,sortDirections??sortDirection,groupIndex,groupValue));}

    [HttpGet("details")]
    public async Task<IActionResult> Details(int moduleId,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,CancellationToken token=default){var definition=await AuthorizedDefinition(moduleId,token);if(definition is null)return NotFound();var keys=Request.Query.ToDictionary(item=>item.Key,item=>item.Value.ToString(),StringComparer.OrdinalIgnoreCase);return Ok(await repository.GetRowsAsync(definition,true,keys,1,100,token,null,null,sortField,sortDirection));}

    [HttpPost("export")]
    public async Task<IActionResult> Export(int moduleId,[FromBody]WorkbenchQuery? query,[FromQuery]string? keyword=null,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,[FromQuery]string? sortFields=null,[FromQuery]string? sortDirections=null,[FromQuery]int? groupIndex=null,[FromQuery]string? groupValue=null,CancellationToken token=default){var definition=await AuthorizedDefinition(moduleId,token);if(definition is null)return NotFound();var rows=await repository.GetExportRowsAsync(definition,query,keyword,token,sortFields??sortField,sortDirections??sortDirection,groupIndex,groupValue);return File(BuildCsv(definition.MasterFields,rows),"text/csv; charset=utf-8","export.csv");}

    [HttpPost("export-selected")]
    public async Task<IActionResult> ExportSelected(int moduleId,[FromBody]ExportSelectedRequest request,[FromQuery]int? groupIndex=null,[FromQuery]string? groupValue=null,CancellationToken token=default)
    {
        var definition=await AuthorizedDefinition(moduleId,token);
        if(definition is null)return NotFound();
        if(request.Keys.Count==0||request.Keys.Count>500)return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_EXPORT_KEYS","导出行数需在 1~500 之间。"));
        if(request.Keys.Any(row=>row.Count!=definition.MasterPkOrder.Count))return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_EXPORT_KEYS","导出主键数量与模块定义不一致。"));
        var rows=await repository.GetExportRowsByKeysAsync(definition,request.Keys,token,groupIndex,groupValue);
        return File(BuildCsv(definition.MasterFields,rows),"text/csv; charset=utf-8","export.csv");
    }

    [HttpGet("form-definition")]
    public async Task<IActionResult> FormDefinition(int moduleId,[FromQuery]string mode="new",CancellationToken token=default)
    {
        var definition=await AuthorizedDefinition(moduleId,token);
        var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if(definition is null||userId is null)return NotFound();
        if(!formSettings.Value.EnabledModuleIds.Contains(moduleId))return NotFound();
        var normalized=mode.Trim().ToLowerInvariant();
        if(normalized is not ("new" or "edit" or "view"))return BadRequest(new{code="INVALID_FORM_MODE",message="mode 仅支持 new、edit 或 view。"});
        var rights=await rightsRepository.GetAsync(userId,moduleId,token);
        if(normalized=="new"&&!rights.CanAddNew)return Forbid();
        if(normalized=="edit"&&!rights.CanEdit)return Forbid();
        // view 模式仅需浏览权限（对齐旧系统 state=brow 只读查看）
        if(normalized=="view"&&!rights.CanBrowse)return Forbid();
        if(normalized=="new"&&!definition.HasAdd)return NotFound();
        if(normalized=="edit"&&!definition.HasEdit)return NotFound();
        var form=await repository.GetFormDefinitionAsync(definition,userId,normalized,rights.CanViewCost,rights.CanViewSecrecy,
            rights.DeniedMasterFields,rights.DeniedDetailFields,
            rights.DenyNewMasterFields,rights.DenyNewDetailFields,
            rights.DenyModiMasterFields,rights.DenyModiDetailFields,token);
        return Ok(form);
    }

    [HttpGet("record")]
    public async Task<IActionResult> Record(int moduleId,[FromQuery]string key,CancellationToken token=default)
    {
        // 查看优先（浏览权限即可），编辑为回退（编辑权限可看可改）
        var access=await FormAccess(moduleId,"view",token) ?? await FormAccess(moduleId,"edit",token);
        if(access is null)return NotFound();
        var keyValues=ParseKey(key);
        if(keyValues is null)return BadRequest(new{code="INVALID_RECORD_KEY",message="key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"});
        var result=await repository.GetRecordAsync(access.Value.Definition,access.Value.Form,keyValues,access.Value.Rights.DataFilter,token);
        return MapReadResult(result);
    }

    [HttpPost("record")]
    public async Task<IActionResult> CreateRecord(int moduleId,[FromBody]SaveRecordRequest request,CancellationToken token=default)
    {
        var access=await FormAccess(moduleId,"new",token);
        if(access is null)return NotFound();
        logger.LogDebug("统一表单保存请求 module={ModuleId} mode=new fields={Fields} details={DetailCount}",moduleId,string.Join(',',request.Values.Keys),request.Details?.Count??0);
        var result=await repository.CreateRecordAsync(access.Value.Definition,access.Value.Form,request,userContext.EmployeeName,userContext.UserId,access.Value.Rights.DataFilter,token);
        LogValidationFailure(moduleId,result);
        return MapSaveResult(result);
    }

    [HttpPut("record")]
    public async Task<IActionResult> UpdateRecord(int moduleId,[FromQuery]string key,[FromBody]SaveRecordRequest request,CancellationToken token=default)
    {
        var access=await FormAccess(moduleId,"edit",token);
        if(access is null)return NotFound();
        var keyValues=ParseKey(key);
        if(keyValues is null)return BadRequest(new{code="INVALID_RECORD_KEY",message="key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"});
        logger.LogDebug("统一表单保存请求 module={ModuleId} mode=edit key={Key} fields={Fields} details={DetailCount}",moduleId,string.Join(',',keyValues),string.Join(',',request.Values.Keys),request.Details?.Count??0);
        var result=await repository.UpdateRecordAsync(access.Value.Definition,access.Value.Form,keyValues,request,userContext.EmployeeName,userContext.UserId,access.Value.Rights.DataFilter,token);
        LogValidationFailure(moduleId,result);
        return MapSaveResult(result);
    }

    [HttpDelete("record")]
    public async Task<IActionResult> DeleteRecord(int moduleId,[FromQuery]string key,CancellationToken token=default)
    {
        var access=await FormAccess(moduleId,"edit",token);
        if(access is null)return NotFound();
        if(!access.Value.Rights.CanDelete)return Forbid();
        var keyValues=ParseKey(key);
        if(keyValues is null)return BadRequest(new{code="INVALID_RECORD_KEY",message="key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"});
        var result=await repository.DeleteRecordAsync(access.Value.Definition,access.Value.Form,keyValues,userContext.UserId,access.Value.Rights.DataFilter,token);
        LogValidationFailure(moduleId,result);
        return MapSaveResult(result);
    }

    [HttpPost("approve")]
    public async Task<IActionResult> Approve(int moduleId,[FromBody]ApproveWorkflowRequest request,CancellationToken token=default)
        => await RunWorkflow(moduleId,true,request,token);

    [HttpPost("deapprove")]
    public async Task<IActionResult> Deapprove(int moduleId,[FromBody]ApproveWorkflowRequest request,CancellationToken token=default)
        => await RunWorkflow(moduleId,false,request,token);

    private async Task<IActionResult> RunWorkflow(int moduleId,bool approve,ApproveWorkflowRequest request,CancellationToken token)
    {
        var definition=await AuthorizedDefinition(moduleId,token);
        if(definition is null)return NotFound();
        var keyValues=ParseKey(request.Key);
        if(keyValues is null)return BadRequest(new{code="INVALID_RECORD_KEY",message="key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"});
        var result=await repository.WorkflowAsync(definition,keyValues,approve,userContext.EmployeeName,userContext.UserId,token);
        return MapSaveResult(result);
    }

    private void LogValidationFailure(int moduleId,RecordSaveResult result)
    {
        if(result.Status==RecordAccessStatus.ValidationFailed)
            logger.LogWarning("统一表单保存校验失败 module={ModuleId} code={Code} errors={Errors}",moduleId,result.ErrorCode,result.FieldErrors);
    }

    [HttpGet("form-chooser/{fieldKey}")]
    public async Task<IActionResult> FormChooser(int moduleId,string fieldKey,[FromQuery]string? keyword=null,[FromQuery]string? filterField=null,[FromQuery]string? master=null,[FromQuery]string? detail=null,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,[FromQuery]int page=1,[FromQuery]int pageSize=50,CancellationToken token=default)
    {
        var access=await FormAccess(moduleId,"new",token) ?? await FormAccess(moduleId,"edit",token);
        if(access is null)return NotFound();
        var (definition,form,rights)=access.Value;
        var field=form.MasterFields.Concat(form.DetailFields).FirstOrDefault(item=>item.Key.Equals(fieldKey,StringComparison.OrdinalIgnoreCase));
        if(field is null)return NotFound();
        var source=field.Choosers.FirstOrDefault(item=>item.Active&&!string.IsNullOrWhiteSpace(item.Table));
        if(source is null||source.Table is null)return NotFound();
        var chooserRights=rights;
        if(source.ModuleId is int moduleIndex&&moduleIndex>0)
        {
            var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if(userId is not null)chooserRights=await rightsRepository.GetAsync(userId,moduleIndex,token);
        }
        // {module} 为旧系统模板占位符，服务端替换为当前模块号（常量，安全）后再受控解析
        var chooseFilter=string.IsNullOrWhiteSpace(source.Filter)
            ? null
            : source.Filter.Replace("{module}",moduleId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        IReadOnlyDictionary<string,string>? masterValues=null;
        if(!string.IsNullOrWhiteSpace(master))
        {
            try
            {
                masterValues=System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>(master,
                    new System.Text.Json.JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            }
            catch
            {
                masterValues=null;
            }
        }
        IReadOnlyDictionary<string,string>? detailValues=null;
        if(!string.IsNullOrWhiteSpace(detail))
        {
            try
            {
                detailValues=System.Text.Json.JsonSerializer.Deserialize<Dictionary<string,string>>(detail,
                    new System.Text.Json.JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            }
            catch
            {
                detailValues=null;
            }
        }
        var result=await repository.GetChooserOptionsAsync(source.Table,keyword,filterField,source.ReturnMapping,masterValues,detailValues,chooserRights.CanViewCost,chooserRights.CanViewSecrecy,chooserRights.DeniedMasterFields,chooserRights.DataFilter,chooseFilter,sortField,sortDirection,page,pageSize,token);
        return result is null?NotFound():Ok(result);
    }

    private async Task<(WorkbenchDefinition Definition,FormDefinition Form,LegacyModuleRights Rights)?> FormAccess(int moduleId,string mode,CancellationToken token)
    {
        var definition=await AuthorizedDefinition(moduleId,token);
        var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if(definition is null||userId is null)return null;
        if(!formSettings.Value.EnabledModuleIds.Contains(moduleId))return null;
        var rights=await rightsRepository.GetAsync(userId,moduleId,token);
        if(mode=="new"&&!rights.CanAddNew)return null;
        if(mode=="edit"&&!rights.CanEdit)return null;
        if(mode=="view"&&!rights.CanBrowse)return null;
        if(mode=="new"&&!definition.HasAdd)return null;
        if(mode=="edit"&&!definition.HasEdit)return null;
        if(mode=="view"&&!definition.HasEdit)return null;
        var form=await repository.GetFormDefinitionAsync(definition,userId,mode,rights.CanViewCost,rights.CanViewSecrecy,
            rights.DeniedMasterFields,rights.DeniedDetailFields,
            rights.DenyNewMasterFields,rights.DenyNewDetailFields,
            rights.DenyModiMasterFields,rights.DenyModiDetailFields,token);
        return form is null?null:(definition,form,rights);
    }

    private static IReadOnlyList<string>? ParseKey(string? key)
    {
        if(string.IsNullOrWhiteSpace(key))return null;
        try
        {
            var values=System.Text.Json.JsonSerializer.Deserialize<string[]>(key);
            return values is null?null:(IReadOnlyList<string>)values;
        }
        catch
        {
            return null;
        }
    }

    private IActionResult MapReadResult(RecordReadResult result)=>result.Status switch
    {
        RecordAccessStatus.Ok=>Ok(result.Bundle),
        RecordAccessStatus.NotFound=>NotFound(),
        RecordAccessStatus.OutOfScope=>StatusCode(403,new{code="RECORD_OUT_OF_SCOPE",message="目标记录不在当前用户数据范围内。"}),
        RecordAccessStatus.FilterUnsupported=>StatusCode(403,new{code="DATA_FILTER_UNSUPPORTED",message="当前数据过滤条件尚不支持，已拒绝执行。"}),
        _=>BadRequest(new{code="RECORD_KEY_MISMATCH",message="主键数量与模块主键不匹配。"}),
    };

    private IActionResult MapSaveResult(RecordSaveResult result)=>result.Status switch
    {
        RecordAccessStatus.Ok=>Ok(new{key=result.Key,flowStarted=result.FlowStarted}),
        RecordAccessStatus.NotFound=>NotFound(),
        RecordAccessStatus.OutOfScope=>StatusCode(403,new{code="RECORD_OUT_OF_SCOPE",message="目标记录不在当前用户数据范围内。"}),
        RecordAccessStatus.FilterUnsupported=>StatusCode(403,new{code="DATA_FILTER_UNSUPPORTED",message="当前数据过滤条件尚不支持，已拒绝执行。"}),
        RecordAccessStatus.ConcurrentModified=>BadRequest(new{code="CONCURRENT_MODIFIED",message="字段内容已被他人修改，请刷新后重试！",fieldErrors=result.FieldErrors??Array.Empty<FieldError>()}),
        _=>BadRequest(new{code=result.ErrorCode??"VALIDATION_FAILED",message=result.ErrorMessage??"数据校验未通过。",fieldErrors=result.FieldErrors??Array.Empty<FieldError>()}),
    };

    [HttpGet("columns")]
    public async Task<IActionResult> Columns(int moduleId,CancellationToken token){var definition=await AuthorizedDefinition(moduleId,token);var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;if(definition is null||userId is null)return NotFound();return Ok(await repository.GetColumnSettingsAsync(definition,userId,token));}

    [HttpGet("column-editor")]
    public async Task<IActionResult> ColumnEditor(int moduleId,CancellationToken token){var definition=await AuthorizedDefinition(moduleId,token);var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;if(definition is null||userId is null)return NotFound();var settings=await repository.GetColumnEditorSettingsAsync(definition,userId,token);return Ok(new {current=settings.Current,defaults=settings.Defaults});}

    [HttpPut("columns")]
    public async Task<IActionResult> SaveColumns(int moduleId,[FromBody]SaveWorkbenchColumns settings,CancellationToken token){var definition=await AuthorizedDefinition(moduleId,token);var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;if(definition is null||userId is null)return NotFound();await repository.SaveColumnSettingsAsync(definition,userId,settings,token);return NoContent();}

    [HttpDelete("columns")]
    public async Task<IActionResult> ResetColumns(int moduleId,CancellationToken token){var definition=await AuthorizedDefinition(moduleId,token);var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;if(definition is null||userId is null)return NotFound();await repository.ResetColumnSettingsAsync(definition,userId,token);return NoContent();}

    [HttpGet("field-settings/lookups/{kind}")]
    public async Task<IActionResult> FieldSettingsLookups(int moduleId,string kind,CancellationToken token){if(await SetupDefinition(moduleId,token) is null)return Forbid();return kind.ToLowerInvariant() switch{"tables"=>Ok(await repository.GetFieldSetupTablesAsync(token)),"modules"=>Ok(await repository.GetFieldSetupModulesAsync(token)),_=>NotFound()};}

    [HttpGet("field-settings")]
    public async Task<IActionResult> FieldSettingsList(int moduleId,[FromQuery]bool detail=false,CancellationToken token=default){var access=await SetupDefinition(moduleId,token);return access is null?Forbid():Ok(await repository.GetFieldSummariesAsync(access,detail,token));}

    [HttpGet("field-settings/{fieldKey}")]
    public async Task<IActionResult> FieldSettings(int moduleId,string fieldKey,[FromQuery]bool detail=false,CancellationToken token=default){var access=await SetupDefinition(moduleId,token);if(access is null)return Forbid();var field=await repository.GetFieldMetadataAsync(access,detail,fieldKey,token);return field is null?NotFound():Ok(field);}

    [HttpPut("field-settings/{fieldKey}")]
    public async Task<IActionResult> UpdateFieldSettings(int moduleId,string fieldKey,[FromBody]UpdateWorkbenchFieldMetadata update,[FromQuery]bool detail=false,CancellationToken token=default){var access=await SetupDefinition(moduleId,token);if(access is null)return Forbid();await repository.UpdateFieldMetadataAsync(access,detail,fieldKey,update,userContext.EmployeeName,token);return NoContent();}

    [HttpPut("column-widths")]
    public async Task<IActionResult> UpdateColumnWidths(int moduleId,[FromBody]UpdateColumnWidthsRequest request,CancellationToken token=default){var access=await SetupDefinition(moduleId,token);if(access is null)return Forbid();await repository.UpdateColumnWidthsAsync(access,request,userContext.EmployeeName,token);return NoContent();}

    private async Task<WorkbenchDefinition?> SetupDefinition(int moduleId,CancellationToken token){var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;if(userId is null)return null;var rights=await rightsRepository.GetAsync(userId,moduleId,token);return rights.CanBrowse&&rights.CanSetup?await repository.GetDefinitionAsync(moduleId,userId,rights.ExecuteTag,rights.CanViewCost,rights.CanViewSecrecy,rights.DeniedMasterFields,rights.DeniedDetailFields,token):null;}

    private async Task<WorkbenchDefinition?> AuthorizedDefinition(int moduleId,CancellationToken token){var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;if(userId is null)return null;var rights=await rightsRepository.GetAsync(userId,moduleId,token);return rights.CanBrowse?await repository.GetDefinitionAsync(moduleId,userId,rights.ExecuteTag,rights.CanViewCost,rights.CanViewSecrecy,rights.DeniedMasterFields,rights.DeniedDetailFields,token):null;}

    private static byte[] BuildCsv(IReadOnlyList<WorkbenchField> fields,IReadOnlyList<Dictionary<string,object?>> rows)
    {
        using var writer=new StringWriter();
        writer.Write('\uFEFF');
        WriteCsvRow(writer,fields.Select(field=>field.Label));
        foreach(var row in rows)WriteCsvRow(writer,fields.Select(field=>FormatCsvValue(row.GetValueOrDefault(field.Key),field.DataType)));
        return System.Text.Encoding.UTF8.GetBytes(writer.ToString());
    }

    private static void WriteCsvRow(StringWriter writer,IEnumerable<string> values)=>writer.WriteLine(string.Join(',',values.Select(value=>EscapeCsv(value))));

    private static string EscapeCsv(string? value)
    {
        if(value is null)return "";
        return value.IndexOfAny([',','"','\r','\n'])>=0?"\""+value.Replace("\"","\"\"")+"\"":value;
    }

    private static string FormatCsvValue(object? value,string dataType)
    {
        if(value is null)return "";
        if(value is bool flag)return flag?"是":"否";
        if(value is DateTime date)return date.ToString("yyyy-MM-dd HH:mm:ss");
        if(value is DateTimeOffset offset)return offset.ToString("yyyy-MM-dd HH:mm:ss");
        return value.ToString()??"";
    }
}
