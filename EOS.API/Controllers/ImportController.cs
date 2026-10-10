using System.Text;
using EOS.API.Data;
using EOS.API.Data.Workbench;
using EOS.API.Errors;
using EOS.API.Features.Import;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 基本资料导入（模块 230902）：新建客户的旧系统数据搬家。
///
/// <para>
/// 入口按"目标模块"而不是"表"组织：每一步都在目标模块上重新授权（<c>new</c> 模式，
/// 与统一表单新增同一把门），因此**导入不是旁路**——字段级禁新增、校验、默认值、
/// 效果链、审计与手工录入逐字一致。
/// </para>
/// <para>
/// 预演（<c>dryRun</c>）在真实事务里跑完整条路径后无条件回滚；执行逐行独立提交，
/// 失败行不影响已成功的行，故结果逐行回报。
/// </para>
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/import")]
public sealed class ImportController(
    ImportService service,
    WorkbenchAccessPolicy policy,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext,
    ILogger<ImportController> logger) : ControllerBase
{
    /// <summary>「基本资料导入」自身的模块号：用它的设置权作为功能入口闸。</summary>
    private const int ImportModuleId = 230902;

    [HttpGet("targets")]
    public async Task<IActionResult> Targets(CancellationToken token)
    {
        if (!await CanUseAsync(token))
        {
            return ForbiddenProblem("IMPORT_NOT_PERMITTED",
                $"你没有「基本资料导入」的使用权限（需要模块 {ImportModuleId} 的设置权限）。");
        }
        return Ok(await service.GetTargetsAsync(token));
    }

    [HttpGet("targets/{moduleId:int}/definition")]
    public async Task<IActionResult> Definition(int moduleId, CancellationToken token)
    {
        var access = await AuthorizeTargetAsync(moduleId, token);
        return access.Value is { } granted
            ? Ok(service.GetDefinition(granted))
            : Denied(access.Denial!);
    }

    /// <summary>空模板：列名即导入定义的字段标签，用户填好即可直接上传（列会自动匹配）。</summary>
    [HttpGet("targets/{moduleId:int}/template")]
    public async Task<IActionResult> Template(int moduleId, CancellationToken token)
    {
        var access = await AuthorizeTargetAsync(moduleId, token);
        if (access.Value is not { } granted)
        {
            return Denied(access.Denial!);
        }
        var definition = service.GetDefinition(granted);
        var ordered = definition.Fields
            .OrderBy(field => definition.RequiredKeys.Contains(field.Key, StringComparer.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(field => field.Label, StringComparer.CurrentCulture)
            .Select(field => Csv(field.Label));
        // 带 BOM 的 UTF-8：Excel 双击打开中文表头才不会乱码
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join(',', ordered) + "\r\n")).ToArray();
        var fileName = $"{definition.Title}-导入模板.csv";
        logger.LogInformation("导入模板下发 module={ModuleId} table={Master} fields={Fields}",
            definition.ModuleId, definition.MasterTable, definition.Fields.Count);
        return File(bytes, "text/csv; charset=utf-8", fileName);
    }

    /// <summary>
    /// 该（用户 + 模块）记住的列映射；没记住过返回 `null`，由界面回落到按字段标签自动匹配。
    /// </summary>
    [HttpGet("targets/{moduleId:int}/mapping")]
    public async Task<IActionResult> Mapping(int moduleId, CancellationToken token)
    {
        var access = await AuthorizeTargetAsync(moduleId, token);
        if (access.Value is null)
        {
            return Denied(access.Denial!);
        }
        return Ok(await service.GetMappingAsync(userContext.UserId, moduleId, token));
    }

    /// <summary>
    /// 记住列映射。映射是实施期的资产而不是一次性的临时状态：同一客户同一张表往往要导很多次，
    /// 按列名记住，补导时（Excel 另存换了列序）也不会串位。
    /// </summary>
    [HttpPut("targets/{moduleId:int}/mapping")]
    public async Task<IActionResult> SaveMapping(
        int moduleId,
        [FromBody] ImportMappingSaveRequest request,
        CancellationToken token)
    {
        var access = await AuthorizeTargetAsync(moduleId, token);
        if (access.Value is null)
        {
            return Denied(access.Denial!);
        }
        await service.SaveMappingAsync(userContext.UserId, userContext.EmployeeName, moduleId, request, token);
        return NoContent();
    }

    /// <summary>
    /// 前置资料就绪度：必填字段引用的那张主档还是空表时提前说出来，
    /// 免得"预演全过、落库整行失败（引不到值）"。
    /// </summary>
    [HttpGet("targets/{moduleId:int}/readiness")]
    public async Task<IActionResult> Readiness(int moduleId, CancellationToken token)
    {
        var access = await AuthorizeTargetAsync(moduleId, token);
        if (access.Value is not { } granted)
        {
            return Denied(access.Denial!);
        }
        return Ok(await service.GetReadinessAsync(granted.Definition.MasterTable, token));
    }

    /// <summary>上传文件并解析成表格：只读成格子，不碰业务判定。</summary>
    [HttpPost("parse")]
    [RequestSizeLimit(ImportLimits.MaxFileBytes + 1024 * 1024)]
    public async Task<IActionResult> Parse([FromForm] IFormFile? file, CancellationToken token)
    {
        if (!await CanUseAsync(token))
        {
            return ForbiddenProblem("IMPORT_NOT_PERMITTED",
                $"你没有「基本资料导入」的使用权限（需要模块 {ImportModuleId} 的设置权限）。");
        }
        if (file is null || file.Length == 0)
        {
            return BadRequest(ApiProblem.BadRequest("请选择要导入的文件。", "IMPORT_FILE_REQUIRED"));
        }
        try
        {
            await using var stream = file.OpenReadStream();
            return Ok(TabularFileReader.Read(file.FileName, stream));
        }
        catch (ImportFileException ex)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, ex.Code, ex.Message));
        }
    }

    /// <summary>预演：真实事务内执行后回滚，给出与执行逐字一致的逐行判定，不落任何数据。</summary>
    [HttpPost("targets/{moduleId:int}/preview")]
    public async Task<IActionResult> Preview(int moduleId, [FromBody] ImportRunRequest request, CancellationToken token)
        => await RunAsync(moduleId, request, dryRun: true, token);

    /// <summary>执行：逐行独立提交；结果逐行回报，失败行不影响已成功的行。</summary>
    [HttpPost("targets/{moduleId:int}/execute")]
    public async Task<IActionResult> Execute(int moduleId, [FromBody] ImportRunRequest request, CancellationToken token)
        => await RunAsync(moduleId, request, dryRun: false, token);

    private async Task<IActionResult> RunAsync(int moduleId, ImportRunRequest request, bool dryRun, CancellationToken token)
    {
        var access = await AuthorizeTargetAsync(moduleId, token);
        if (access.Value is not { } granted)
        {
            return Denied(access.Denial!);
        }
        if (request.Rows is null || request.Rows.Count == 0)
        {
            return BadRequest(ApiProblem.BadRequest("没有可导入的数据行。", "IMPORT_NO_ROWS"));
        }
        try
        {
            var result = await service.RunAsync(granted, request, userContext.EmployeeName, userContext.UserId, dryRun, token);
            return Ok(result);
        }
        catch (ImportRequestException ex)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, ex.Code, ex.Message));
        }
    }

    /// <summary>
    /// 目标模块的写权限门：与统一表单新增同一入口（`new` 模式）。
    /// 拒绝时**必须说清是哪一条不满足**——用户问的是"我自己为什么做不了这件事"，
    /// 不是探测别人数据是否存在（那类才按 404 隐藏）。
    /// </summary>
    private async Task<WorkbenchDecision<WorkbenchFormAccess>> AuthorizeTargetAsync(int moduleId, CancellationToken token)
    {
        if (!await CanUseAsync(token))
        {
            return new WorkbenchDecision<WorkbenchFormAccess>(default,
                new WorkbenchDenial(WorkbenchDenialKind.Forbidden, "IMPORT_NOT_PERMITTED",
                    $"你没有「基本资料导入」的使用权限（需要模块 {ImportModuleId} 的设置权限）。"));
        }
        return await policy.AuthorizeFormAsync(userContext.UserId, moduleId, "new", token);
    }

    private IActionResult Denied(WorkbenchDenial denial) => denial.Kind switch
    {
        WorkbenchDenialKind.NotFound => NotFound(),
        WorkbenchDenialKind.InvalidRequest => BadRequest(ApiProblem.Create(
            StatusCodes.Status400BadRequest, denial.Code, denial.Message ?? denial.Code)),
        _ => ForbiddenProblem(denial.Code, DescribeTarget(denial)),
    };

    private static string DescribeTarget(WorkbenchDenial denial) => denial.Code switch
    {
        WorkbenchDenialCodes.ModeNotPermitted => "你对这个模块没有新增权限（缺少 ADDNEW 动作位），无法导入。",
        WorkbenchDenialCodes.AddCapabilityMissing => "这个模块没有开放新增动作，无法导入。",
        WorkbenchDenialCodes.FormNotEnabled => "这个模块不在统一表单写名单里，没有可写入的入口。",
        WorkbenchDenialCodes.DefinitionUnavailable => "这个模块的定义不可用，无法导入。",
        WorkbenchDenialCodes.FormUnavailable => "这个模块的表单定义不可用，无法导入。",
        _ => denial.Message ?? "无法向这个模块导入数据。",
    };

    private IActionResult ForbiddenProblem(string code, string message) =>
        StatusCode(StatusCodes.Status403Forbidden, ApiProblem.Create(StatusCodes.Status403Forbidden, code, message));

    /// <summary>功能入口闸：模块 230902 的设置权（与"产品可用库存重计"这类工具页同一口径）。</summary>
    private async Task<bool> CanUseAsync(CancellationToken token)
    {
        var userId = userContext.UserId;
        if (string.IsNullOrEmpty(userId))
        {
            return false;
        }
        var rights = await rightsRepository.GetAsync(userId, ImportModuleId, token);
        return rights.CanBrowse && rights.CanSetup;
    }

    private static string Csv(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"'
            : value;
}
