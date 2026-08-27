using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EOS.API.Controllers;

[ApiController, Authorize, Route("api/v1/document-workbench/{moduleId:int}")]
public sealed class DocumentWorkbenchController(DocumentWorkbenchRepository repository, IPermissionService permissions, WorkbenchAuditWriter auditWriter, EOS.API.Security.CurrentUserContext userContext, IOptions<UnifiedFormEditorSettings> formSettings, ILogger<DocumentWorkbenchController> logger) : ControllerBase
{
    [HttpGet("definition")]
    public async Task<IActionResult> Definition(int moduleId,CancellationToken token)=>await AuthorizedDefinition(moduleId,token) is { } definition?Ok(definition):NotFound();

    [HttpGet("records")]
    public async Task<IActionResult> Records(int moduleId,[FromQuery]int page=1,[FromQuery]int pageSize=20,[FromQuery]string? keyword=null,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,[FromQuery]string? sortFields=null,[FromQuery]string? sortDirections=null,[FromQuery]int? groupIndex=null,[FromQuery]string? groupValue=null,CancellationToken token=default){var definition=await AuthorizedDefinition(moduleId,token);if(definition is null)return NotFound();var dataFilter=await EffectiveDataFilter(moduleId,token);return Ok(await repository.GetRowsAsync(definition,false,new Dictionary<string,string>(),page,pageSize,token,null,keyword,sortFields??sortField,sortDirections??sortDirection,groupIndex,groupValue,dataFilter));}

    [HttpPost("query")]
    public async Task<IActionResult> Query(int moduleId,[FromBody]WorkbenchQuery query,[FromQuery]int page=1,[FromQuery]int pageSize=20,[FromQuery]string? keyword=null,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,[FromQuery]string? sortFields=null,[FromQuery]string? sortDirections=null,[FromQuery]int? groupIndex=null,[FromQuery]string? groupValue=null,CancellationToken token=default){var definition=await AuthorizedDefinition(moduleId,token);if(definition is null)return NotFound();var dataFilter=await EffectiveDataFilter(moduleId,token);return Ok(await repository.GetRowsAsync(definition,false,new Dictionary<string,string>(),page,pageSize,token,query,keyword,sortFields??sortField,sortDirections??sortDirection,groupIndex,groupValue,dataFilter));}

    [HttpGet("details")]
    public async Task<IActionResult> Details(int moduleId,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,CancellationToken token=default){var definition=await AuthorizedDefinition(moduleId,token);if(definition is null)return NotFound();var keys=Request.Query.ToDictionary(item=>item.Key,item=>item.Value.ToString(),StringComparer.OrdinalIgnoreCase);var dataFilter=await EffectiveDataFilter(moduleId,token);return Ok(await repository.GetRowsAsync(definition,true,keys,1,100,token,null,null,sortField,sortDirection,dataFilter:dataFilter));}
    [HttpPost("export")]
    public async Task<IActionResult> Export(int moduleId,[FromBody]WorkbenchQuery? query,[FromQuery]string? keyword=null,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,[FromQuery]string? sortFields=null,[FromQuery]string? sortDirections=null,[FromQuery]int? groupIndex=null,[FromQuery]string? groupValue=null,[FromQuery]string? format=null,[FromQuery]string? columns=null,CancellationToken token=default){var definition=await AuthorizedDefinition(moduleId,token);if(definition is null)return NotFound();var dataFilter=await EffectiveDataFilter(moduleId,token);var exportFields=DocumentWorkbenchRepository.ResolveExportFields(definition.MasterFields,ParseColumnKeys(columns));var rows=await repository.GetExportRowsAsync(definition,query,keyword,token,sortFields??sortField,sortDirections??sortDirection,groupIndex,groupValue,exportFields,dataFilter);await auditWriter.WriteBestEffortAsync(moduleId,$"export {format} rows={rows.Count}","EXPORT",$"导出 {definition.Title}",userContext.UserId,"EXPORT",1,null,token);return ExportFile(exportFields,rows,format,definition.Title);}

    [HttpPost("export-selected")]
    public async Task<IActionResult> ExportSelected(int moduleId,[FromBody]ExportSelectedRequest request,[FromQuery]int? groupIndex=null,[FromQuery]string? groupValue=null,[FromQuery]string? format=null,[FromQuery]string? columns=null,CancellationToken token=default)
    {
        var definition=await AuthorizedDefinition(moduleId,token);
        if(definition is null)return NotFound();
        if(request.Keys.Count==0||request.Keys.Count>500)return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_EXPORT_KEYS","导出行数需在 1~500 之间。"));
        if(request.Keys.Any(row=>row.Count!=definition.MasterPkOrder.Count))return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_EXPORT_KEYS","导出主键数量与模块定义不一致。"));
        var exportFields=DocumentWorkbenchRepository.ResolveExportFields(definition.MasterFields,ParseColumnKeys(columns));
        var dataFilter=await EffectiveDataFilter(moduleId,token);
        var rows=await repository.GetExportRowsByKeysAsync(definition,request.Keys,token,groupIndex,groupValue,exportFields,dataFilter);
        await auditWriter.WriteBestEffortAsync(moduleId,$"export-selected rows={rows.Count}","EXPORT",$"导出所选 {definition.Title}",userContext.UserId,"EXPORT",1,null,token);
        return ExportFile(exportFields,rows,format,definition.Title);
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
        var rights=(await permissions.GetAsync(userId,moduleId,token)).Rights;
        if(normalized=="new"&&!rights.CanAddNew)return Forbid();
        if(normalized=="edit"&&!rights.CanEdit)return Forbid();
        // view 模式仅需浏览权限（对齐旧系统 state=brow 只读查看）
        if(normalized=="view"&&!rights.CanBrowse)return Forbid();
        if(normalized=="new"&&!definition.HasAdd)return NotFound();
        if(normalized=="edit"&&!definition.HasEdit)return NotFound();
var form=await repository.GetFormDefinitionAsync(definition,userId,normalized,rights.CanViewCost,rights.CanViewSecrecy,
            rights.DeniedMasterFields,rights.DeniedDetailFields,
            rights.DenyNewMasterFields,rights.DenyNewDetailFields,
            rights.DenyModiMasterFields,rights.DenyModiDetailFields,token,
            rights.CanAddNew,rights.CanEdit,rights.CanDelete,rights.CanApprove,rights.CanDeapprove,rights.CanEndCase,rights.CanUnEndCase,
            rights.CanFileView,rights.CanFileUpda,rights.CanFileEdit,rights.CanFileDele);
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
    public async Task<IActionResult> CreateRecord(int moduleId,[FromBody]SaveRecordRequest request,[FromHeader(Name="X-Idempotency-Key")]string? headerIdempotencyKey=null,CancellationToken token=default)
    {
        var access=await FormAccess(moduleId,"new",token);
        if(access is null)return NotFound();
        // ADR-006 决策 2.1：统一表单写路径强制幂等键（请求体 IdempotencyKey 或 X-Idempotency-Key 头）
        if(IdempotencyProblem(request.IdempotencyKey??headerIdempotencyKey) is { } idempotencyProblem)return idempotencyProblem;
        request=request with{IdempotencyKey=request.IdempotencyKey??headerIdempotencyKey};
        logger.LogDebug("统一表单保存请求 module={ModuleId} mode=new fields={Fields} details={DetailCount}",moduleId,string.Join(',',request.Values.Keys),request.Details?.Count??0);
        var result=await repository.CreateRecordAsync(access.Value.Definition,access.Value.Form,request,userContext.EmployeeName,userContext.UserId,access.Value.Rights.DataFilter,token);
        LogValidationFailure(moduleId,result);
        return MapSaveResult(result);
    }

    [HttpPut("record")]
    public async Task<IActionResult> UpdateRecord(int moduleId,[FromQuery]string key,[FromBody]SaveRecordRequest request,[FromHeader(Name="X-Idempotency-Key")]string? headerIdempotencyKey=null,CancellationToken token=default)
    {
        var access=await FormAccess(moduleId,"edit",token);
        if(access is null)return NotFound();
        var keyValues=ParseKey(key);
        if(keyValues is null)return BadRequest(new{code="INVALID_RECORD_KEY",message="key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"});
        if(IdempotencyProblem(request.IdempotencyKey??headerIdempotencyKey) is { } idempotencyProblem)return idempotencyProblem;
        request=request with{IdempotencyKey=request.IdempotencyKey??headerIdempotencyKey};
        logger.LogDebug("统一表单保存请求 module={ModuleId} mode=edit key={Key} fields={Fields} details={DetailCount}",moduleId,string.Join(',',keyValues),string.Join(',',request.Values.Keys),request.Details?.Count??0);
        var result=await repository.UpdateRecordAsync(access.Value.Definition,access.Value.Form,keyValues,request,userContext.EmployeeName,userContext.UserId,access.Value.Rights.DataFilter,token);
        LogValidationFailure(moduleId,result);
        return MapSaveResult(result);
    }

    [HttpDelete("record")]
    public async Task<IActionResult> DeleteRecord(int moduleId,[FromQuery]string key,[FromHeader(Name="X-Idempotency-Key")]string? idempotencyKey=null,CancellationToken token=default)
    {
        var access=await FormAccess(moduleId,"edit",token);
        if(access is null)return NotFound();
        await permissions.RequireAsync(userContext.UserId,moduleId,PermissionAction.Delete,token);
        var keyValues=ParseKey(key);
        if(keyValues is null)return BadRequest(new{code="INVALID_RECORD_KEY",message="key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"});
        if(IdempotencyProblem(idempotencyKey) is { } idempotencyProblem)return idempotencyProblem;
        var result=await repository.DeleteRecordAsync(access.Value.Definition,access.Value.Form,keyValues,userContext.UserId,access.Value.Rights.DataFilter,token,idempotencyKey!.Trim());
        LogValidationFailure(moduleId,result);
        return MapSaveResult(result);
    }

    [HttpPost("approve")]
    public async Task<IActionResult> Approve(int moduleId,[FromBody]ApproveWorkflowRequest request,[FromHeader(Name="X-Idempotency-Key")]string? headerIdempotencyKey=null,CancellationToken token=default)
        => await RunWorkflow(moduleId,true,request,headerIdempotencyKey,token);

[HttpPost("deapprove")]
    public async Task<IActionResult> Deapprove(int moduleId,[FromBody]ApproveWorkflowRequest request,[FromHeader(Name="X-Idempotency-Key")]string? headerIdempotencyKey=null,CancellationToken token=default)
        => await RunWorkflow(moduleId,false,request,headerIdempotencyKey,token);

    [HttpPost("endcase")]
    public async Task<IActionResult> EndCase(int moduleId,[FromBody]ApproveWorkflowRequest request,[FromHeader(Name="X-Idempotency-Key")]string? headerIdempotencyKey=null,CancellationToken token=default)
        => await RunFinish(moduleId,true,request,headerIdempotencyKey,token);

    [HttpPost("unendcase")]
    public async Task<IActionResult> UnEndCase(int moduleId,[FromBody]ApproveWorkflowRequest request,[FromHeader(Name="X-Idempotency-Key")]string? headerIdempotencyKey=null,CancellationToken token=default)
        => await RunFinish(moduleId,false,request,headerIdempotencyKey,token);

private async Task<IActionResult> RunWorkflow(int moduleId,bool approve,ApproveWorkflowRequest request,string? headerIdempotencyKey,CancellationToken token)
    {
        var definition=await AuthorizedDefinition(moduleId,token);
        if(definition is null)return NotFound();
        var keyValues=ParseKey(request.Key);
        if(keyValues is null)return BadRequest(new{code="INVALID_RECORD_KEY",message="key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"});
        if(IdempotencyProblem(request.IdempotencyKey??headerIdempotencyKey) is { } idempotencyProblem)return idempotencyProblem;
        var result=await repository.WorkflowAsync(definition,keyValues,approve,userContext.EmployeeName,userContext.UserId,token,request.IdempotencyKey??headerIdempotencyKey,request.Message);
        return MapSaveResult(result);
    }

    private async Task<IActionResult> RunFinish(int moduleId,bool finish,ApproveWorkflowRequest request,string? headerIdempotencyKey,CancellationToken token)
    {
        var definition=await AuthorizedDefinition(moduleId,token);
        if(definition is null)return NotFound();
        var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if(userId is null)return Unauthorized();
        await permissions.RequireAsync(userId,moduleId,finish?PermissionAction.EndCase:PermissionAction.UnEndCase,token);
        var keyValues=ParseKey(request.Key);
        if(keyValues is null)return BadRequest(new{code="INVALID_RECORD_KEY",message="key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"});
        if(IdempotencyProblem(request.IdempotencyKey??headerIdempotencyKey) is { } idempotencyProblem)return idempotencyProblem;
        var result=await repository.FinishAsync(definition,keyValues,finish,userContext.EmployeeName,userContext.UserId,token,request.IdempotencyKey??headerIdempotencyKey);
        return MapSaveResult(result);
    }

    /// <summary>ADR-006 决策 2.1：统一表单写路径幂等键强制（缺失或超 128 字符返回 400）。</summary>
    private IActionResult? IdempotencyProblem(string? idempotencyKey)
        => string.IsNullOrWhiteSpace(idempotencyKey)||idempotencyKey.Trim().Length>128
            ? BadRequest(new{code="IDEMPOTENCY_KEY_REQUIRED",message="写操作缺少有效幂等键（请求体 idempotencyKey 或 X-Idempotency-Key 请求头，≤128 字符）。"})
            :null;

    private void LogValidationFailure(int moduleId,RecordSaveResult result)
    {
        if(result.Status==RecordAccessStatus.ValidationFailed)
            logger.LogWarning("统一表单保存校验失败 module={ModuleId} code={Code} errors={Errors}",moduleId,result.ErrorCode,result.FieldErrors);
    }

    [HttpGet("form-chooser/{fieldKey}")]
    public async Task<IActionResult> FormChooser(int moduleId,string fieldKey,[FromQuery]string? keyword=null,[FromQuery]string? filterField=null,[FromQuery]string? master=null,[FromQuery]string? detail=null,[FromQuery]string? conditions=null,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,[FromQuery]int page=1,[FromQuery]int pageSize=50,CancellationToken token=default)
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
            if(userId is not null)chooserRights=(await permissions.GetAsync(userId,moduleIndex,token)).Rights;
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
        IReadOnlyList<UnifiedChooserCondition>? chooserConditions=null;
        if(!string.IsNullOrWhiteSpace(conditions))
        {
            try
            {
                chooserConditions=System.Text.Json.JsonSerializer.Deserialize<List<UnifiedChooserCondition>>(conditions,
                    new System.Text.Json.JsonSerializerOptions{PropertyNameCaseInsensitive=true});
            }
            catch
            {
                chooserConditions=null;
            }
        }
        var chooserUserId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (chooserUserId is null)
        {
            return Unauthorized();
        }
        var result=await repository.GetChooserOptionsAsync(source.Table,keyword,filterField,source.ReturnMapping,masterValues,detailValues,chooserConditions,chooserRights.CanViewCost,chooserRights.CanViewSecrecy,chooserRights.DeniedMasterFields,chooserRights.DataFilter,chooseFilter,sortField,sortDirection,page,pageSize,chooserRights.ExecuteTag,source.ModuleId ?? moduleId,chooserUserId,token);
        return result is null?NotFound():Ok(result);
    }

    private async Task<(WorkbenchDefinition Definition,FormDefinition Form,LegacyModuleRights Rights)?> FormAccess(int moduleId,string mode,CancellationToken token)
    {
        var definition=await AuthorizedDefinition(moduleId,token);
        var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if(definition is null||userId is null)return null;
        if(!formSettings.Value.EnabledModuleIds.Contains(moduleId))return null;
        var rights=(await permissions.GetAsync(userId,moduleId,token)).Rights;
        if(mode=="new"&&!rights.CanAddNew)return null;
        if(mode=="edit"&&!rights.CanEdit)return null;
        if(mode=="view"&&!rights.CanBrowse)return null;
        if(mode=="new"&&!definition.HasAdd)return null;
        if(mode=="edit"&&!definition.HasEdit)return null;
        if(mode=="view"&&!definition.HasEdit)return null;
var form=await repository.GetFormDefinitionAsync(definition,userId,mode,rights.CanViewCost,rights.CanViewSecrecy,
            rights.DeniedMasterFields,rights.DeniedDetailFields,
            rights.DenyNewMasterFields,rights.DenyNewDetailFields,
            rights.DenyModiMasterFields,rights.DenyModiDetailFields,token,
            rights.CanAddNew,rights.CanEdit,rights.CanDelete,rights.CanApprove,rights.CanDeapprove,rights.CanEndCase,rights.CanUnEndCase,
            rights.CanFileView,rights.CanFileUpda,rights.CanFileEdit,rights.CanFileDele);
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
        RecordAccessStatus.Ok=>Ok(new{master=result.Bundle!.Master,details=result.Bundle.Details,flowState=result.FlowState.ToString()}),
        RecordAccessStatus.NotFound=>NotFound(),
        RecordAccessStatus.OutOfScope=>StatusCode(403,new{code="RECORD_OUT_OF_SCOPE",message="目标记录不在当前用户数据范围内。"}),
        RecordAccessStatus.FilterUnsupported=>StatusCode(403,new{code="DATA_FILTER_UNSUPPORTED",message="当前数据过滤条件尚不支持，已拒绝执行。"}),
        _=>BadRequest(new{code="RECORD_KEY_MISMATCH",message="主键数量与模块主键不匹配。"}),
    };

    private IActionResult MapSaveResult(RecordSaveResult result)=>result.Status switch
    {
        // ADR-006 决策 2.7：warnings 随保存响应回传（如自动批核失败），前端在浏览态 banner 展示
        RecordAccessStatus.Ok=>Ok(new{key=result.Key,flowStarted=result.FlowStarted,warnings=result.Warnings}),
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

    private async Task<WorkbenchDefinition?> SetupDefinition(int moduleId,CancellationToken token){var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;if(userId is null)return null;var rights=(await permissions.GetAsync(userId,moduleId,token)).Rights;return rights.CanBrowse&&rights.CanSetup?await repository.GetDefinitionAsync(moduleId,userId,rights.ExecuteTag,rights.CanViewCost,rights.CanViewSecrecy,rights.DeniedMasterFields,rights.DeniedDetailFields,token):null;}

    private async Task<WorkbenchDefinition?> AuthorizedDefinition(int moduleId,CancellationToken token)
    {
        var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if(userId is null)return null;
        var rights=(await permissions.GetAsync(userId,moduleId,token)).Rights;
        if(!rights.CanBrowse)return null;
        var definition=await repository.GetDefinitionAsync(moduleId,userId,rights.ExecuteTag,rights.CanViewCost,rights.CanViewSecrecy,rights.DeniedMasterFields,rights.DeniedDetailFields,token);
        if(definition is null)return null;
        // 路由契约（M86）：NEW_URL/MODI_URL 有值即自定义路由；无值时按统一表单白名单
        // 回退（显示按钮走统一表单）或隐藏按钮。
var formEnabled=formSettings.Value.EnabledModuleIds.Contains(moduleId);
        return definition with
        {
            HasAdd=definition.NewUrl is not null||formEnabled,
            HasEdit=definition.ModiUrl is not null||formEnabled,
            CanDelete=rights.CanDelete,
        };
    }

    /// <summary>当前用户对该模块生效的数据范围（SYSDD/SYSDH DATA_FILTER，个人覆盖组、组 OR 已组合）。</summary>
    private async Task<string?> EffectiveDataFilter(int moduleId,CancellationToken token)
    {
        var userId=User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if(userId is null)return null;
        var rights=(await permissions.GetAsync(userId,moduleId,token)).Rights;
        return rights.DataFilter;
    }

    private static byte[] BuildCsv(IReadOnlyList<WorkbenchField> fields,IReadOnlyList<Dictionary<string,object?>> rows)
    {
        using var writer=new StringWriter();
        writer.Write('\uFEFF');
        WriteCsvRow(writer,fields.Select(field=>field.Label));
        foreach(var row in rows)WriteCsvRow(writer,fields.Select(field=>FormatCsvValue(row.GetValueOrDefault(field.Key),field.DataType)));
        return System.Text.Encoding.UTF8.GetBytes(writer.ToString());
    }

    /// <summary>导出文件响应：CSV（默认）或 Excel 2003 XML（对齐旧 DataTableToExcel）。</summary>
    private static IActionResult ExportFile(IReadOnlyList<WorkbenchField> fields,IReadOnlyList<Dictionary<string,object?>> rows,string? format,string title)
    {
        var fileName=SanitizeFileName(title);
        if(string.Equals(format,"xls",StringComparison.OrdinalIgnoreCase)||string.Equals(format,"excel",StringComparison.OrdinalIgnoreCase))
            return new FileContentResult(BuildExcel(fields,rows),"application/vnd.ms-excel"){FileDownloadName=$"{fileName}.xls"};
        return new FileContentResult(BuildCsv(fields,rows),"text/csv; charset=utf-8"){FileDownloadName=$"{fileName}.csv"};
    }

    /// <summary>逗号分隔的列 key 白名单解析：仅保留定义内字段、保持顺序、上限 30 列。</summary>
    private static IReadOnlyList<string>? ParseColumnKeys(string? columns)
    {
        if(string.IsNullOrWhiteSpace(columns))return null;
        return columns.Split(',',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToArray();
    }

    private static string SanitizeFileName(string title)
    {
        var invalid=System.IO.Path.GetInvalidFileNameChars();
        var safe=new string(title.Where(character=>!invalid.Contains(character)).ToArray()).Trim();
        return safe.Length==0?"export":safe;
    }

    private static byte[] BuildExcel(IReadOnlyList<WorkbenchField> fields,IReadOnlyList<Dictionary<string,object?>> rows)
    {
        var builder=new System.Text.StringBuilder();
        builder.Append("<?xml version=\"1.0\"?>\n");
        builder.Append("<?mso-application progid=\"Excel.Sheet\"?>\n");
        builder.Append("<Workbook xmlns=\"urn:schemas-microsoft-com:office:spreadsheet\" ");
        builder.Append("xmlns:ss=\"urn:schemas-microsoft-com:office:spreadsheet\">");
        builder.Append("<Worksheet ss:Name=\"Sheet1\"><Table>");
        builder.Append("<Row>");
        foreach(var field in fields)
            builder.Append("<Cell><Data ss:Type=\"String\">").Append(XmlEncode(field.Label)).Append("</Data></Cell>");
        builder.Append("</Row>");
        foreach(var row in rows)
        {
            builder.Append("<Row>");
            foreach(var field in fields)
            {
                var value=row.GetValueOrDefault(field.Key);
                var text=FormatCsvValue(value,field.DataType);
                var numeric=value is not null && value is not DBNull && IsNumericValue(value);
                builder.Append("<Cell><Data ss:Type=\"").Append(numeric?"Number":"String").Append("\">")
                    .Append(XmlEncode(numeric&&text.Length>0?text.Replace(",",""):text)).Append("</Data></Cell>");
            }
            builder.Append("</Row>");
        }
        builder.Append("</Table></Worksheet></Workbook>");
        return System.Text.Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static bool IsNumericValue(object value)=>value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static string XmlEncode(string? value)=>
        string.IsNullOrEmpty(value)?string.Empty:
        System.Security.SecurityElement.Escape(value)??string.Empty;

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
