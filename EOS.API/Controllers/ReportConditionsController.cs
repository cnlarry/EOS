using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 报表过滤条件设置（2205，旧 RPT/SysqrDft.aspx 的受控等价，2026-08-28 自统一表单
/// 白名单归类定制页）：SYSQR_DA + SYSQR_DEFAULT 按模块维护报表查看器条件面板的
/// 默认条件定义（字段/类型/选项 DSL/数据源语句/默认值/参数名）。
/// 写侧校验与报表运行时解析规则镜像（ReportConditionsValidator），坏配置保存时拦截；
/// F_TYPE 3/5 数据源表/列做物理存在校验（fail-closed）。
/// 权限门：读要求 CanBrowse(2205)，写要求 CanSetup(2205)（对齐 2201 双门口径）。
/// SYSQR_DA 主档仅作模块行存在性与审计承载，批核（CONFIRM_TAG）沿用旧系统样板列，
/// 不作为本页业务功能暴露。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/report-conditions")]
public sealed class ReportConditionsController(
    ReportConditionsRepository repository,
    ReportAdminRepository reportAdminRepository,
    LegacyRightsRepository rightsRepository) : ControllerBase
{
    /// <summary>可维护过滤条件的模块：与 2201 同口径（有 RPT 入口或已有报表定义的模块）。</summary>
    [HttpGet("modules")]
    public async Task<IActionResult> Modules(CancellationToken token)
    {
        if (!await CanBrowseAsync(token)) return Forbid();
        return Ok(await reportAdminRepository.ListModulesAsync(token));
    }

    [HttpGet("conditions")]
    public async Task<IActionResult> Conditions([FromQuery] int moduleId, CancellationToken token)
    {
        if (!await CanBrowseAsync(token)) return Forbid();
        ReportConditionsValidator.ValidateModuleId(moduleId);
        return Ok(await repository.ListConditionsAsync(moduleId, token));
    }

    /// <summary>全部条件行（含模块编号/名称，2026-08-29 单表改版——孤儿模块行保留展示）。</summary>
    [HttpGet("conditions/all")]
    public async Task<IActionResult> AllConditions(CancellationToken token)
    {
        if (!await CanBrowseAsync(token)) return Forbid();
        return Ok(await repository.ListAllConditionsAsync(token));
    }

    [HttpPost("conditions")]
    public async Task<IActionResult> CreateCondition([FromQuery] int moduleId, [FromBody] ReportConditionDraft draft, CancellationToken token)
    {
        if (!await CanSetupAsync(token)) return Forbid();
        ReportConditionsValidator.ValidateModuleId(moduleId);
        ReportConditionsValidator.ValidateSerialNo(draft.SerialNo);
        ReportConditionsValidator.ValidateType(draft.Type);
        var masterTable = await repository.GetMasterTableAsync(moduleId, token);
        if (masterTable is null) return NotFound();
        var normalized = await ValidateAsync(moduleId, masterTable, draft, token);
        var existing = await repository.ListConditionsAsync(moduleId, token);
        if (existing.Any(item => item.SerialNo == draft.SerialNo)) return Conflict("条件序号已存在。");
        await repository.CreateConditionAsync(moduleId, normalized, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, token);
        return NoContent();
    }

    [HttpPut("conditions/{serialNo:int}")]
    public async Task<IActionResult> UpdateCondition(int serialNo, [FromQuery] int moduleId, [FromBody] ReportConditionDraft draft, CancellationToken token)
    {
        if (!await CanSetupAsync(token)) return Forbid();
        ReportConditionsValidator.ValidateModuleId(moduleId);
        ReportConditionsValidator.ValidateSerialNo(serialNo);
        ReportConditionsValidator.ValidateType(draft.Type);
        var masterTable = await repository.GetMasterTableAsync(moduleId, token);
        if (masterTable is null) return NotFound();
        var normalized = await ValidateAsync(moduleId, masterTable, draft, token);
        var updated = await repository.UpdateConditionAsync(moduleId, serialNo, normalized, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, token);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("conditions/{serialNo:int}")]
    public async Task<IActionResult> DeleteCondition(int serialNo, [FromQuery] int moduleId, CancellationToken token)
    {
        if (!await CanSetupAsync(token)) return Forbid();
        ReportConditionsValidator.ValidateModuleId(moduleId);
        ReportConditionsValidator.ValidateSerialNo(serialNo);
        var deleted = await repository.DeleteConditionAsync(moduleId, serialNo, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, token);
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>写路径校验：字段白名单/条件语句 DSL/文本长度 + F_TYPE 3/5 数据源物理存在（模块存在性与类型已在端点前置）。</summary>
    private async Task<ReportConditionDraft> ValidateAsync(int moduleId, string masterTable, ReportConditionDraft draft, CancellationToken token)
    {
        var fieldOptions = (await reportAdminRepository.FieldOptionsAsync(moduleId, token))
            .Where(option => option.Table.Equals(masterTable, StringComparison.OrdinalIgnoreCase))
            .Select(option => (option.Table, option.Column))
            .ToHashSet();
        var field = ReportConditionsValidator.ValidateField(draft.Field, masterTable, fieldOptions);
        var expression = ReportConditionsValidator.ValidateExpression(draft.Type, draft.Expression);
        if (draft.Type is 3 or 5 && expression is not null)
        {
            var match = ReportConditionsValidator.MatchSelectSource(expression);
            if (match is null
                || !await repository.SelectSourceExistsAsync(match.Groups[3].Value, match.Groups[1].Value, match.Groups[2].Value, token))
                throw new ArgumentException("数据源表/列不存在或不可访问（仅支持 dbo 表/视图）。", nameof(draft));
        }
        return new ReportConditionDraft(
            draft.SerialNo, draft.Type, field, expression,
            ReportConditionsValidator.ValidateText(draft.Description, 50, "条件描述"),
            ReportConditionsValidator.ValidateText(draft.DefaultValue, 500, "默认查询值"),
            ReportConditionsValidator.ValidateText(draft.ParameterName, 50, "参数名称"),
            ReportConditionsValidator.ValidateText(draft.Remark, 500, "备注"));
    }

    private async Task<bool> CanBrowseAsync(CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return false;
        return (await rightsRepository.GetAsync(userId, 2205, token)).CanBrowse;
    }

    private async Task<bool> CanSetupAsync(CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return false;
        return (await rightsRepository.GetAsync(userId, 2205, token)).CanSetup;
    }
}
