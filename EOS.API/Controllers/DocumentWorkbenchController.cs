using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.Workbench;
using EOS.API.Errors;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController, Authorize, Route("api/v1/document-workbench/{moduleId:int}")]
public sealed class DocumentWorkbenchController(DocumentWorkbenchRepository repository, WorkbenchChooserService chooser, WorkbenchAccessPolicy policy, WorkbenchAuditWriter auditWriter, DocumentActionExecutor documentActions, EOS.API.Security.CurrentUserContext userContext, ILogger<DocumentWorkbenchController> logger) : ControllerBase
{
    /// <summary>当前请求的用户号（授权判定与数据装配的主体）。</summary>
    private string? UserId => User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

    /// <summary>把策略层的拒绝结果映射为响应形态（404 / 403 / 400），口径由策略层给出。</summary>
    private IActionResult Denied(WorkbenchDenial denial) => denial.Kind switch
    {
        WorkbenchDenialKind.Forbidden=>Forbid(),
        WorkbenchDenialKind.InvalidRequest=>BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,denial.Code,denial.Message??denial.Code)),
        _=>NotFound(),
    };

    [HttpGet("definition")]
    public async Task<IActionResult> Definition(int moduleId,CancellationToken token)
    {
        var decision=await policy.AuthorizeDefinitionAsync(UserId,moduleId,token);
        return decision.Allowed?Ok(decision.Value!.Definition):Denied(decision.Denial!);
    }

    [HttpGet("records")]
    public async Task<IActionResult> Records(int moduleId,[FromQuery]int page=1,[FromQuery]int pageSize=20,[FromQuery]string? keyword=null,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,[FromQuery]string? sortFields=null,[FromQuery]string? sortDirections=null,[FromQuery]int? groupIndex=null,[FromQuery]string? groupValue=null,CancellationToken token=default){var decision=await policy.AuthorizeDefinitionAsync(UserId,moduleId,token);if(!decision.Allowed)return Denied(decision.Denial!);var dataFilter=await policy.GetDataFilterAsync(UserId,moduleId,token);return Ok(await repository.GetRowsAsync(decision.Value!.Definition,false,new Dictionary<string,string>(),page,pageSize,token,null,keyword,sortFields??sortField,sortDirections??sortDirection,groupIndex,groupValue,dataFilter));}

    [HttpPost("query")]
    public async Task<IActionResult> Query(int moduleId,[FromBody]WorkbenchQuery query,[FromQuery]int page=1,[FromQuery]int pageSize=20,[FromQuery]string? keyword=null,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,[FromQuery]string? sortFields=null,[FromQuery]string? sortDirections=null,[FromQuery]int? groupIndex=null,[FromQuery]string? groupValue=null,CancellationToken token=default){var decision=await policy.AuthorizeDefinitionAsync(UserId,moduleId,token);if(!decision.Allowed)return Denied(decision.Denial!);var dataFilter=await policy.GetDataFilterAsync(UserId,moduleId,token);return Ok(await repository.GetRowsAsync(decision.Value!.Definition,false,new Dictionary<string,string>(),page,pageSize,token,query,keyword,sortFields??sortField,sortDirections??sortDirection,groupIndex,groupValue,dataFilter));}

    [HttpGet("details")]
    public async Task<IActionResult> Details(int moduleId,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,CancellationToken token=default){var decision=await policy.AuthorizeDefinitionAsync(UserId,moduleId,token);if(!decision.Allowed)return Denied(decision.Denial!);var keys=Request.Query.ToDictionary(item=>item.Key,item=>item.Value.ToString(),StringComparer.OrdinalIgnoreCase);var dataFilter=await policy.GetDataFilterAsync(UserId,moduleId,token);return Ok(await repository.GetRowsAsync(decision.Value!.Definition,true,keys,1,100,token,null,null,sortField,sortDirection,dataFilter:dataFilter));}
    [HttpPost("export")]
    public async Task<IActionResult> Export(int moduleId,[FromBody]WorkbenchQuery? query,[FromQuery]string? keyword=null,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,[FromQuery]string? sortFields=null,[FromQuery]string? sortDirections=null,[FromQuery]int? groupIndex=null,[FromQuery]string? groupValue=null,[FromQuery]string? format=null,[FromQuery]string? columns=null,CancellationToken token=default){var decision=await policy.AuthorizeDefinitionAsync(UserId,moduleId,token);if(!decision.Allowed)return Denied(decision.Denial!);var definition=decision.Value!.Definition;var dataFilter=await policy.GetDataFilterAsync(UserId,moduleId,token);var exportFields=DocumentWorkbenchRepository.ResolveExportFields(definition.MasterFields,ParseColumnKeys(columns));var rows=await repository.GetExportRowsAsync(definition,query,keyword,token,sortFields??sortField,sortDirections??sortDirection,groupIndex,groupValue,exportFields,dataFilter);await auditWriter.WriteBestEffortAsync(moduleId,$"export {format} rows={rows.Count}","EXPORT",$"导出 {definition.Title}",userContext.UserId,"EXPORT",1,null,token);return ExportFile(exportFields,rows,format,definition.Title);}

    [HttpPost("export-selected")]
    public async Task<IActionResult> ExportSelected(int moduleId,[FromBody]ExportSelectedRequest request,[FromQuery]int? groupIndex=null,[FromQuery]string? groupValue=null,[FromQuery]string? format=null,[FromQuery]string? columns=null,CancellationToken token=default)
    {
        var decision=await policy.AuthorizeDefinitionAsync(UserId,moduleId,token);
        if(!decision.Allowed)return Denied(decision.Denial!);
        var definition=decision.Value!.Definition;
        if(request.Keys.Count==0||request.Keys.Count>500)return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_EXPORT_KEYS","导出行数需在 1~500 之间。"));
        if(request.Keys.Any(row=>row.Count!=definition.MasterPkOrder.Count))return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_EXPORT_KEYS","导出主键数量与模块定义不一致。"));
        var exportFields=DocumentWorkbenchRepository.ResolveExportFields(definition.MasterFields,ParseColumnKeys(columns));
        var dataFilter=await policy.GetDataFilterAsync(UserId,moduleId,token);
        var rows=await repository.GetExportRowsByKeysAsync(definition,request.Keys,token,groupIndex,groupValue,exportFields,dataFilter);
        await auditWriter.WriteBestEffortAsync(moduleId,$"export-selected rows={rows.Count}","EXPORT",$"导出所选 {definition.Title}",userContext.UserId,"EXPORT",1,null,token);
        return ExportFile(exportFields,rows,format,definition.Title);
    }

    [HttpGet("form-definition")]
    public async Task<IActionResult> FormDefinition(int moduleId,[FromQuery]string mode="new",CancellationToken token=default)
    {
        var decision=await policy.AuthorizeFormDefinitionAsync(UserId,moduleId,mode,token);
        return decision.Allowed?Ok(decision.Value!.Form):Denied(decision.Denial!);
    }

    [HttpGet("record")]
    public async Task<IActionResult> Record(int moduleId,[FromQuery]string key,CancellationToken token=default)
    {
        // 查看优先（浏览权限即可），编辑为回退（编辑权限可看可改）
        var access=await policy.AuthorizeFormAsync(UserId,moduleId,"view",token);
        if(!access.Allowed)access=await policy.AuthorizeFormAsync(UserId,moduleId,"edit",token);
        if(!access.Allowed)return NotFound();
        var keyValues=ParseKey(key);
        if(keyValues is null)return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_RECORD_KEY","key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"));
        var granted=access.Value!;
        var result=await repository.GetRecordAsync(granted.Definition,granted.Form,keyValues,granted.Rights.DataFilter,token);
        return MapReadResult(result);
    }

    [HttpPost("record")]
    public async Task<IActionResult> CreateRecord(int moduleId,[FromBody]SaveRecordRequest request,[FromHeader(Name="X-Idempotency-Key")]string? headerIdempotencyKey=null,CancellationToken token=default)
    {
        var access=await policy.AuthorizeFormAsync(UserId,moduleId,"new",token);
        if(!access.Allowed)return NotFound();
        // Form write path requires an idempotency key (request body or X-Idempotency-Key header)
        if(WorkbenchAccessPolicy.CheckIdempotencyKey(request.IdempotencyKey??headerIdempotencyKey) is { } idempotencyProblem)return Denied(idempotencyProblem);
        request=request with{IdempotencyKey=request.IdempotencyKey??headerIdempotencyKey};
        logger.LogDebug("统一表单保存请求 module={ModuleId} mode=new fields={Fields} details={DetailCount}",moduleId,string.Join(',',request.Values.Keys),request.Details?.Count??0);
        var granted=access.Value!;
        var result=await repository.CreateRecordAsync(granted.Definition,granted.Form,request,userContext.EmployeeName,userContext.UserId,granted.Rights.DataFilter,token);
        LogValidationFailure(moduleId,result);
        return MapSaveResult(result);
    }

    [HttpPut("record")]
    public async Task<IActionResult> UpdateRecord(int moduleId,[FromQuery]string key,[FromBody]SaveRecordRequest request,[FromHeader(Name="X-Idempotency-Key")]string? headerIdempotencyKey=null,CancellationToken token=default)
    {
        var access=await policy.AuthorizeFormAsync(UserId,moduleId,"edit",token);
        if(!access.Allowed)return NotFound();
        var keyValues=ParseKey(key);
        if(keyValues is null)return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_RECORD_KEY","key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"));
        if(WorkbenchAccessPolicy.CheckIdempotencyKey(request.IdempotencyKey??headerIdempotencyKey) is { } idempotencyProblem)return Denied(idempotencyProblem);
        request=request with{IdempotencyKey=request.IdempotencyKey??headerIdempotencyKey};
        logger.LogDebug("统一表单保存请求 module={ModuleId} mode=edit key={Key} fields={Fields} details={DetailCount}",moduleId,string.Join(',',keyValues),string.Join(',',request.Values.Keys),request.Details?.Count??0);
        var granted=access.Value!;
        var result=await repository.UpdateRecordAsync(granted.Definition,granted.Form,keyValues,request,userContext.EmployeeName,userContext.UserId,granted.Rights.DataFilter,token);
        LogValidationFailure(moduleId,result);
        return MapSaveResult(result);
    }

    [HttpDelete("record")]
    public async Task<IActionResult> DeleteRecord(int moduleId,[FromQuery]string key,[FromHeader(Name="X-Idempotency-Key")]string? idempotencyKey=null,CancellationToken token=default)
    {
        var access=await policy.AuthorizeDeleteAsync(UserId,moduleId,token);
        if(!access.Allowed)return NotFound();
        var keyValues=ParseKey(key);
        if(keyValues is null)return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_RECORD_KEY","key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"));
        if(WorkbenchAccessPolicy.CheckIdempotencyKey(idempotencyKey) is { } idempotencyProblem)return Denied(idempotencyProblem);
        var granted=access.Value!;
        var result=await repository.DeleteRecordAsync(granted.Definition,granted.Form,keyValues,userContext.UserId,granted.Rights.DataFilter,token,idempotencyKey!.Trim());
        LogValidationFailure(moduleId,result);
        return MapSaveResult(result);
    }

    /// <summary>
    /// 单据操作（自定义按钮）：用户主动触发，服务端就这一张单据做一次动作。
    /// 操作只作用于已落库的单据状态——它不接收界面上的未保存改动（前端在有待保存改动时禁用按钮）。
    /// 权限、数据范围、幂等、审计与事务全部在服务端复核，前端按钮显隐只是体验。
    /// </summary>
    [HttpPost("action/{actionKey}")]
    public async Task<IActionResult> RunAction(int moduleId,string actionKey,[FromBody]DocumentActionRequest? request,[FromHeader(Name="X-Idempotency-Key")]string? idempotencyKey=null,CancellationToken token=default)
    {
        var access=await policy.AuthorizeActionAsync(UserId,moduleId,token);
        if(!access.Allowed)return NotFound();
        if(request?.Key is null||request.Key.Count==0)return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_RECORD_KEY","请求体 key 必须是主键值数组。"));
        if(WorkbenchAccessPolicy.CheckIdempotencyKey(idempotencyKey) is { } idempotencyProblem)return Denied(idempotencyProblem);
        var granted=access.Value!;
        var result=await documentActions.ExecuteAsync(granted.Definition,granted.Form,actionKey,request,
            userContext.UserId,userContext.EmployeeName,granted.Rights.DataFilter,idempotencyKey!.Trim(),token);
        return MapActionResult(moduleId,actionKey,result);
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
        // 批核/解批同属写路径，与新增/修改/删除共用同一道闸门：模块必须有**可写的页面入口**
        // （统一表单写名单；迁移 321 之后这是"能写"的唯一真源），且当前用户具备该动作位——
        // 两条判据都在策略层，API 是最终权限边界，前端按钮显隐只改善体验。
        var decision=await policy.AuthorizeWorkflowAsync(UserId,moduleId,approve?PermissionAction.Approve:PermissionAction.Deapprove,token);
        if(!decision.Allowed)return Denied(decision.Denial!);
        var keyValues=ParseKey(request.Key);
        if(keyValues is null)return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_RECORD_KEY","key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"));
        if(WorkbenchAccessPolicy.CheckIdempotencyKey(request.IdempotencyKey??headerIdempotencyKey) is { } idempotencyProblem)return Denied(idempotencyProblem);
        var result=await repository.WorkflowAsync(decision.Value!.Definition,keyValues,approve,userContext.EmployeeName,userContext.UserId,token,request.IdempotencyKey??headerIdempotencyKey,request.Message);
        return MapSaveResult(result);
    }

    private async Task<IActionResult> RunFinish(int moduleId,bool finish,ApproveWorkflowRequest request,string? headerIdempotencyKey,CancellationToken token)
    {
        // 结案/取消结案与批核同一道闸门，理由同 RunWorkflow。
        var decision=await policy.AuthorizeWorkflowAsync(UserId,moduleId,finish?PermissionAction.EndCase:PermissionAction.UnEndCase,token);
        if(!decision.Allowed)return Denied(decision.Denial!);
        var keyValues=ParseKey(request.Key);
        if(keyValues is null)return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"INVALID_RECORD_KEY","key 必须是主键值数组的 JSON 编码（如 [\"A\",\"B\"]）。"));
        if(WorkbenchAccessPolicy.CheckIdempotencyKey(request.IdempotencyKey??headerIdempotencyKey) is { } idempotencyProblem)return Denied(idempotencyProblem);
        var result=await repository.FinishAsync(decision.Value!.Definition,keyValues,finish,userContext.EmployeeName,userContext.UserId,token,request.IdempotencyKey??headerIdempotencyKey);
        return MapSaveResult(result);
    }

    private void LogValidationFailure(int moduleId,RecordSaveResult result)
    {
        if(result.Status==RecordAccessStatus.ValidationFailed)
            logger.LogWarning("统一表单保存校验失败 module={ModuleId} code={Code} errors={Errors}",moduleId,result.ErrorCode,result.FieldErrors);
    }

    [HttpGet("form-chooser/{fieldKey}")]
    public async Task<IActionResult> FormChooser(int moduleId,string fieldKey,[FromQuery]int? serialNo=null,[FromQuery]string? keyword=null,[FromQuery]string? filterField=null,[FromQuery]string? master=null,[FromQuery]string? detail=null,[FromQuery]string? conditions=null,[FromQuery]string? sortField=null,[FromQuery]string? sortDirection=null,[FromQuery]int page=1,[FromQuery]int pageSize=50,CancellationToken token=default)
    {
        var access=await policy.AuthorizeFormAsync(UserId,moduleId,"new",token);
        if(!access.Allowed)access=await policy.AuthorizeFormAsync(UserId,moduleId,"edit",token);
        if(!access.Allowed)return NotFound();
        var (definition,form,rights)=(access.Value!.Definition,access.Value.Form,access.Value.Rights);
        var field=form.MasterFields.Concat(form.DetailFields).FirstOrDefault(item=>item.Key.Equals(fieldKey,StringComparison.OrdinalIgnoreCase));
        if(field is null)return NotFound();
        // When a field has multiple chooser sources, the front end picks by serialNo; default to the first active source
        // 来源定义（FILTER_STRUCT/RETURN_ITEMS）仅服务端持有，不从表单定义 DTO 读取。
        var source=await chooser.GetChooserSourceAsync(definition.MasterTable,definition.DetailTable,fieldKey,serialNo,token);
        if(source is null||!source.Active||string.IsNullOrWhiteSpace(source.Table))return NotFound();
        var chooserRights=rights;
        if(source.ModuleId is int moduleIndex&&moduleIndex>0)
        {
            var moduleRights=await policy.GetRightsAsync(UserId,moduleIndex,token);
            if(moduleRights is not null)chooserRights=moduleRights;
        }
        // FILTER_STRUCT 结构化条件：编译期把 {module} 等模板转为参数占位符，运行期绑定
        // NULL = 存量条件待重建（迁移清单内），fail-closed 返回空选项，绝不退化为「无过滤」放大数据范围
        if (source.Filter is null)
        {
            logger.LogWarning("选择器过滤条件待重建（FILTER_STRUCT 为空）module={ModuleId} field={Field} serial={Serial}", moduleId, fieldKey, source.SerialNo);
            return Ok(new FormChooserResult([], [], 0));
        }
        if (!ChooserFilterStruct.TryParse(source.Filter, out var filterStruct))
        {
            logger.LogWarning("选择器结构化过滤条件非法 module={ModuleId} field={Field} serial={Serial}", moduleId, fieldKey, source.SerialNo);
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "CHOOSER_FILTER_INVALID", "选择器过滤条件不是合法的结构化 JSON，请联系系统管理员在字段设置中重建。"));
        }
        var returnItems = ChooserReturnItems.Parse(source.ReturnMapping);
        if (returnItems is null)
        {
            logger.LogWarning("选择器回填映射非法 module={ModuleId} field={Field} serial={Serial}", moduleId, fieldKey, source.SerialNo);
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "CHOOSER_RETURN_INVALID", "选择器回填映射不是合法的 JSON 数组，请联系系统管理员在字段设置中重建。"));
        }
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
        var result=await chooser.GetChooserOptionsAsync(source.Table,keyword,filterField,returnItems,masterValues,detailValues,chooserConditions,chooserRights.CanViewCost,chooserRights.CanViewSecrecy,chooserRights.DeniedMasterFields,chooserRights.DataFilter,filterStruct,sortField,sortDirection,page,pageSize,chooserRights.ExecuteTag,source.ModuleId ?? moduleId,moduleId,chooserUserId,token);
        return result is null?NotFound():Ok(result);
    }

    /// <summary>
    /// 该用户在本模块上有授权的单据动作名单——自定义承载页用它渲染按钮。
    /// 没有统一表单、但仍有单据级动作的模块（如库存策略配置页）拿不到 FORM 定义，
    /// 走这个只读端点即可；它是 FORM 里那份名单的同一个来源，不含未经授权的操作。
    /// </summary>
    [HttpGet("actions")]
    public async Task<IActionResult> UserActions(int moduleId,CancellationToken token)
    {
        var decision=await policy.AuthorizeUserActionsAsync(UserId,moduleId,token);
        if(!decision.Allowed)return NotFound();
        return Ok(await repository.BuildUserActionsAsync(decision.Value!.Definition,UserId!,token));
    }

    private IActionResult MapActionResult(int moduleId,string actionKey,DocumentActionExecution result)=>result.Status switch
    {
        DocumentActionStatus.Ok=>Ok(new{
            moduleId,
            actionKey,
            outcome=result.Result!.Outcome.ToString().ToLowerInvariant(),
            message=result.Result.Message,
            targetModuleId=result.Result.TargetModuleId,
            targetKey=result.Result.TargetKey,
            warnings=result.Result.Warnings,
            requiresConfirmation=result.RequiresConfirmation}),
        DocumentActionStatus.NotFound=>NotFound(ApiProblem.Create(StatusCodes.Status404NotFound,result.ErrorCode??DocumentActionErrorCodes.NotFound,result.ErrorMessage??"操作不存在。")),
        DocumentActionStatus.Forbidden=>StatusCode(StatusCodes.Status403Forbidden,ApiProblem.Create(StatusCodes.Status403Forbidden,result.ErrorCode??DocumentActionErrorCodes.Forbidden,result.ErrorMessage??"没有该操作的授权。")),
        DocumentActionStatus.OutOfScope=>StatusCode(StatusCodes.Status403Forbidden,ApiProblem.Create(StatusCodes.Status403Forbidden,result.ErrorCode??"RECORD_OUT_OF_SCOPE",result.ErrorMessage??"目标记录不在当前用户数据范围内。")),
        DocumentActionStatus.FilterUnsupported=>StatusCode(StatusCodes.Status403Forbidden,ApiProblem.Create(StatusCodes.Status403Forbidden,result.ErrorCode??"DATA_FILTER_UNSUPPORTED",result.ErrorMessage??"当前数据过滤条件尚不支持，已拒绝执行。")),
        DocumentActionStatus.KeyMismatch=>BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,result.ErrorCode??"RECORD_KEY_MISMATCH",result.ErrorMessage??"主键数量与模块主键不匹配。")),
        _=>BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,result.ErrorCode??DocumentActionErrorCodes.Failed,result.ErrorMessage??"操作未完成。").WithFieldErrors(result.FieldErrors??Array.Empty<FieldError>())),
    };

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
        RecordAccessStatus.OutOfScope=>StatusCode(StatusCodes.Status403Forbidden,ApiProblem.Create(StatusCodes.Status403Forbidden,"RECORD_OUT_OF_SCOPE","目标记录不在当前用户数据范围内。")),
        RecordAccessStatus.FilterUnsupported=>StatusCode(StatusCodes.Status403Forbidden,ApiProblem.Create(StatusCodes.Status403Forbidden,"DATA_FILTER_UNSUPPORTED","当前数据过滤条件尚不支持，已拒绝执行。")),
        _=>BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"RECORD_KEY_MISMATCH","主键数量与模块主键不匹配。")),
    };

    private IActionResult MapSaveResult(RecordSaveResult result)=>result.Status switch
    {
        // Return warnings (e.g. auto-approval failure) with the save response; front end shows them in a banner
        RecordAccessStatus.Ok=>Ok(new{key=result.Key,flowStarted=result.FlowStarted,warnings=result.Warnings}),
        RecordAccessStatus.NotFound=>NotFound(),
        RecordAccessStatus.OutOfScope=>StatusCode(StatusCodes.Status403Forbidden,ApiProblem.Create(StatusCodes.Status403Forbidden,"RECORD_OUT_OF_SCOPE","目标记录不在当前用户数据范围内。")),
        RecordAccessStatus.FilterUnsupported=>StatusCode(StatusCodes.Status403Forbidden,ApiProblem.Create(StatusCodes.Status403Forbidden,"DATA_FILTER_UNSUPPORTED","当前数据过滤条件尚不支持，已拒绝执行。")),
        RecordAccessStatus.ConcurrentModified=>BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,"CONCURRENT_MODIFIED","字段内容已被他人修改，请刷新后重试！").WithFieldErrors(result.FieldErrors??Array.Empty<FieldError>())),
        _=>BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest,result.ErrorCode??"VALIDATION_FAILED",result.ErrorMessage??"数据校验未通过。").WithFieldErrors(result.FieldErrors??Array.Empty<FieldError>())),
    };

    [HttpGet("columns")]
    public async Task<IActionResult> Columns(int moduleId,CancellationToken token){var decision=await policy.AuthorizeDefinitionAsync(UserId,moduleId,token);if(!decision.Allowed)return NotFound();return Ok(await repository.GetColumnSettingsAsync(decision.Value!.Definition,UserId!,token));}

    [HttpGet("column-editor")]
    public async Task<IActionResult> ColumnEditor(int moduleId,CancellationToken token){var decision=await policy.AuthorizeDefinitionAsync(UserId,moduleId,token);if(!decision.Allowed)return NotFound();var settings=await repository.GetColumnEditorSettingsAsync(decision.Value!.Definition,UserId!,token);return Ok(new {current=settings.Current,defaults=settings.Defaults});}

    [HttpPut("columns")]
    public async Task<IActionResult> SaveColumns(int moduleId,[FromBody]SaveWorkbenchColumns settings,CancellationToken token){var decision=await policy.AuthorizeDefinitionAsync(UserId,moduleId,token);if(!decision.Allowed)return NotFound();await repository.SaveColumnSettingsAsync(decision.Value!.Definition,UserId!,settings,token);return NoContent();}

    [HttpDelete("columns")]
    public async Task<IActionResult> ResetColumns(int moduleId,CancellationToken token){var decision=await policy.AuthorizeDefinitionAsync(UserId,moduleId,token);if(!decision.Allowed)return NotFound();await repository.ResetColumnSettingsAsync(decision.Value!.Definition,UserId!,token);return NoContent();}

    [HttpGet("field-settings/lookups/{kind}")]
    public async Task<IActionResult> FieldSettingsLookups(int moduleId,string kind,CancellationToken token){if(!(await policy.AuthorizeSetupAsync(UserId,moduleId,token)).Allowed)return Forbid();return kind.ToLowerInvariant() switch{"tables"=>Ok(await repository.GetFieldSetupTablesAsync(token)),"modules"=>Ok(await repository.GetFieldSetupModulesAsync(token)),_=>NotFound()};}

    [HttpGet("field-settings")]
    public async Task<IActionResult> FieldSettingsList(int moduleId,[FromQuery]bool detail=false,CancellationToken token=default){var decision=await policy.AuthorizeSetupAsync(UserId,moduleId,token);return decision.Allowed?Ok(await repository.GetFieldSummariesAsync(decision.Value!.Definition,detail,token)):Forbid();}

    [HttpGet("field-settings/{fieldKey}")]
    public async Task<IActionResult> FieldSettings(int moduleId,string fieldKey,[FromQuery]bool detail=false,CancellationToken token=default){var decision=await policy.AuthorizeSetupAsync(UserId,moduleId,token);if(!decision.Allowed)return Forbid();var field=await repository.GetFieldMetadataAsync(decision.Value!.Definition,detail,fieldKey,token);return field is null?NotFound():Ok(field);}

    [HttpPut("field-settings/{fieldKey}")]
    public async Task<IActionResult> UpdateFieldSettings(int moduleId,string fieldKey,[FromBody]UpdateWorkbenchFieldMetadata update,[FromQuery]bool detail=false,CancellationToken token=default){var decision=await policy.AuthorizeSetupAsync(UserId,moduleId,token);if(!decision.Allowed)return Forbid();await repository.UpdateFieldMetadataAsync(decision.Value!.Definition,detail,fieldKey,update,userContext.EmployeeName,token);return NoContent();}

    [HttpPut("column-widths")]
    public async Task<IActionResult> UpdateColumnWidths(int moduleId,[FromBody]UpdateColumnWidthsRequest request,CancellationToken token=default){var decision=await policy.AuthorizeSetupAsync(UserId,moduleId,token);if(!decision.Allowed)return Forbid();await repository.UpdateColumnWidthsAsync(decision.Value!.Definition,request,userContext.EmployeeName,token);return NoContent();}

    private static byte[] BuildCsv(IReadOnlyList<WorkbenchField> fields,IReadOnlyList<Dictionary<string,object?>> rows)
    {
        using var writer=new StringWriter();
        writer.Write('\uFEFF');
        WriteCsvRow(writer,fields.Select(field=>field.Label));
        foreach(var row in rows)WriteCsvRow(writer,fields.Select(field=>FormatCsvValue(row.GetValueOrDefault(field.Key),field.DataType)));
        return System.Text.Encoding.UTF8.GetBytes(writer.ToString());
    }

    /// <summary>导出文件响应：CSV（默认）或 Excel 2003 XML。</summary>
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
