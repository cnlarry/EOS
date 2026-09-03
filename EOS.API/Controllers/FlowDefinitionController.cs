using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 流程设计器（模块 2101「表单流程设计」）API：模块流程清单/详情/保存/删除 + 人员检索。
/// 权限门）。保存与删除由 FlowDefinitionService 做受控校验（模块能力/
/// 人员存在性/条件预解析/在途实例守卫），绝不拼接用户输入。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/workflow/definitions")]
public sealed class FlowDefinitionController(
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext,
    FlowDefinitionService flowDefinitions) : ControllerBase
{
    public sealed record FlowStepPayload(
        string SortNo,
        string Desc,
        string[] People,
        string? ExecCondition,
        string[]? PersonConditions,
        string[]? ApprovePowers,
        string[]? ForwardPowers,
        string? AutoExecCondition,
        bool IsAutoExec,
        bool IsSign,
        int PassPercent,
        bool IsEffect,
        bool PreMustUnder,
        bool CanSirAgency,
        string[]? MustSigners,
        string? Remark);

    public sealed record SaveFlowRequest(string FlowName, string? Remark, IReadOnlyList<FlowStepPayload> Steps);

    /// <summary>模块流程清单（已配置 flows + 具备批核能力的工作台模块池 eligible）。</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken token)
    {
        if (!(await rightsRepository.GetAsync(userContext.UserId, ModuleIds.FlowDesigner, token)).CanBrowse)
            return Forbid();
        var (flows, eligible) = await flowDefinitions.GetDefinitionsAsync(token);
        return Ok(new { Flows = flows, Eligible = eligible });
    }

    /// <summary>单模块流程详情（未配置返回 404）。</summary>
    [HttpGet("{moduleId:int}")]
    public async Task<IActionResult> Detail(int moduleId, CancellationToken token)
    {
        if (!(await rightsRepository.GetAsync(userContext.UserId, ModuleIds.FlowDesigner, token)).CanBrowse)
            return Forbid();
        var flow = await flowDefinitions.GetFlowAsync(moduleId, token);
        return flow is null
            ? NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "FLOW_NOT_FOUND", "该模块未配置流程。"))
            : Ok(flow);
    }

    /// <summary>活跃用户检索（设计器人员选择数据源）。</summary>
    [HttpGet("people")]
    public async Task<IActionResult> People([FromQuery] string? keyword, CancellationToken token)
    {
        if (!(await rightsRepository.GetAsync(userContext.UserId, ModuleIds.FlowDesigner, token)).CanBrowse)
            return Forbid();
        var people = await flowDefinitions.GetPeopleAsync(keyword, token);
        return Ok(new { People = people });
    }

    /// <summary>保存流程定义（全量重建 WFFORM + WFFORM_FLOW）。</summary>
    [HttpPost("{moduleId:int}")]
    public async Task<IActionResult> Save(int moduleId, [FromBody] SaveFlowRequest request, CancellationToken token)
    {
        if (!(await rightsRepository.GetAsync(userContext.UserId, ModuleIds.FlowDesigner, token)).CanSetup)
            return Forbid();
        if (request is null || request.Steps is null)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_FLOW_PAYLOAD", "请求体不能为空。"));
        var steps = request.Steps.Select(step => new FlowDefinitionService.FlowStepDefinition(
            step.SortNo, step.Desc, step.People ?? [], step.ExecCondition, step.PersonConditions,
            step.ApprovePowers, step.ForwardPowers, step.AutoExecCondition, step.IsAutoExec,
            step.IsSign, step.PassPercent, step.IsEffect, step.PreMustUnder, step.CanSirAgency,
            step.MustSigners, step.Remark)).ToArray();
        var result = await flowDefinitions.SaveFlowAsync(
            moduleId, request.FlowName, request.Remark, steps, userContext.UserId, userContext.EmployeeName, token);
        if (result.Status != RecordAccessStatus.Ok)
            return StatusCode(StatusCodes.Status400BadRequest, ApiProblem.Create(
                StatusCodes.Status400BadRequest,
                result.ErrorCode ?? ApiErrorCodes.InvalidArgument,
                result.ErrorMessage ?? "流程定义保存失败。").WithFieldErrors(result.FieldErrors));
        return Ok(new { Saved = true, message = "流程定义已保存。" });
    }

    /// <summary>删除流程定义（无在途实例时才允许）。</summary>
    [HttpDelete("{moduleId:int}")]
    public async Task<IActionResult> Delete(int moduleId, CancellationToken token)
    {
        if (!(await rightsRepository.GetAsync(userContext.UserId, ModuleIds.FlowDesigner, token)).CanSetup)
            return Forbid();
        var result = await flowDefinitions.DeleteFlowAsync(moduleId, userContext.UserId, userContext.EmployeeName, token);
        if (result.Status != RecordAccessStatus.Ok)
        {
            var status = result.Status == RecordAccessStatus.NotFound
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status400BadRequest;
            return StatusCode(status, ApiProblem.Create(
                status, result.ErrorCode ?? ApiErrorCodes.InvalidArgument, result.ErrorMessage ?? "流程定义删除失败。"));
        }
        return Ok(new { Deleted = true, message = "流程定义已删除。" });
    }
}
