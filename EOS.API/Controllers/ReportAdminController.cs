using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 报表排序汇总设置（2201，旧名报表定义维护 / 229801 报表设置明细）：
/// REPORT + REPORT_SORT 主子表 CRUD，字段选择器选项来自模块主/明细表白名单；
/// 排序/分组字段串必须命中字段白名单；值全部参数化。
/// 权限门：读要求 CanBrowse(2201)，写要求 CanSetup(2201)（2026-08-28 验收补强，
/// 原全端点仅查 CanBrowse，写操作缺 Setup 门）。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/report-admin")]
public sealed class ReportAdminController(
    ReportAdminRepository repository,
    LegacyRightsRepository rightsRepository) : ControllerBase
{
    [HttpGet("modules")]
    public async Task<IActionResult> Modules(CancellationToken token)
    {
        if (!await CanBrowseAsync(token)) return Forbid();
        return Ok(await repository.ListModulesAsync(token));
    }

    [HttpGet("reports")]
    public async Task<IActionResult> Reports([FromQuery] int? moduleId, CancellationToken token)
    {
        if (!await CanBrowseAsync(token)) return Forbid();
        if (moduleId is not null)
        {
            ReportAdminValidator.ValidateModuleId(moduleId.Value);
            return Ok(await repository.ListReportsAsync(moduleId.Value, token));
        }
        // moduleId 缺省时返回全部模块报表（2201 定制页单一列表）
        return Ok(await repository.ListAllReportsAsync(token));
    }

    [HttpPost("reports")]
    public async Task<IActionResult> CreateReport([FromBody] ReportAdminDraft draft, CancellationToken token)
    {
        if (!await CanSetupAsync(token)) return Forbid();
        ReportAdminValidator.ValidateReportId(draft.ReportId);
        ReportAdminValidator.ValidateModuleId(draft.ModuleId);
        ReportAdminValidator.ValidatePaper(draft.DefaultPaper);
        var existing = await repository.ListReportsAsync(draft.ModuleId!.Value, token);
        if (existing.Any(item => item.ReportId == draft.ReportId.Trim())) return Conflict("报表编号已存在。");
        await repository.CreateReportAsync(draft, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, token);
        return NoContent();
    }

    [HttpPut("reports/{id}")]
    public async Task<IActionResult> UpdateReport(string id, [FromBody] ReportAdminDraft draft, CancellationToken token)
    {
        if (!await CanSetupAsync(token)) return Forbid();
        ReportAdminValidator.ValidateReportId(id);
        ReportAdminValidator.ValidatePaper(draft.DefaultPaper);
        var moduleId = await repository.GetReportModuleAsync(id, token);
        if (moduleId is null) return NotFound();
        var updated = await repository.UpdateReportAsync(id, draft, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty, token);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("reports/{id}")]
    public async Task<IActionResult> DeleteReport(string id, CancellationToken token)
    {
        if (!await CanSetupAsync(token)) return Forbid();
        ReportAdminValidator.ValidateReportId(id);
        await repository.DeleteReportAsync(id, token);
        return NoContent();
    }

    [HttpGet("sorts")]
    public async Task<IActionResult> Sorts([FromQuery] string reportId, CancellationToken token)
    {
        if (!await CanBrowseAsync(token)) return Forbid();
        ReportAdminValidator.ValidateReportId(reportId);
        return Ok(await repository.ListSortsAsync(reportId, token));
    }

    [HttpPost("sorts")]
    public async Task<IActionResult> CreateSort([FromQuery] string reportId, [FromBody] ReportSortDraft draft, CancellationToken token)
    {
        if (!await CanSetupAsync(token)) return Forbid();
        ReportAdminValidator.ValidateReportId(reportId);
        ReportAdminValidator.ValidateSerialNo(draft.SerialNo);
        var moduleId = await repository.GetReportModuleAsync(reportId, token);
        if (moduleId is null) return NotFound();
        var options = await FieldSetAsync(moduleId.Value, token);
        ValidateFieldList(draft, options);
        await repository.CreateSortAsync(reportId, draft, token);
        return NoContent();
    }

    [HttpPut("sorts/{reportId}/{serialNo:int}")]
    public async Task<IActionResult> UpdateSort(string reportId, int serialNo, [FromBody] ReportSortDraft draft, CancellationToken token)
    {
        if (!await CanSetupAsync(token)) return Forbid();
        ReportAdminValidator.ValidateReportId(reportId);
        ReportAdminValidator.ValidateSerialNo(serialNo);
        var moduleId = await repository.GetReportModuleAsync(reportId, token);
        if (moduleId is null) return NotFound();
        var options = await FieldSetAsync(moduleId.Value, token);
        ValidateFieldList(draft, options);
        var updated = await repository.UpdateSortAsync(reportId, serialNo, draft, token);
        return updated ? NoContent() : NotFound();
    }

    [HttpDelete("sorts/{reportId}/{serialNo:int}")]
    public async Task<IActionResult> DeleteSort(string reportId, int serialNo, CancellationToken token)
    {
        if (!await CanSetupAsync(token)) return Forbid();
        ReportAdminValidator.ValidateReportId(reportId);
        await repository.DeleteSortAsync(reportId, serialNo, token);
        return NoContent();
    }

    [HttpGet("field-options")]
    public async Task<IActionResult> FieldOptions([FromQuery] int moduleId, CancellationToken token)
    {
        if (!await CanBrowseAsync(token)) return Forbid();
        ReportAdminValidator.ValidateModuleId(moduleId);
        return Ok(await repository.FieldOptionsAsync(moduleId, token));
    }

    [HttpGet("headers")]
    public async Task<IActionResult> Headers(CancellationToken token)
    {
        if (!await CanBrowseAsync(token)) return Forbid();
        return Ok(await repository.HeaderOptionsAsync(token));
    }

    [HttpGet("tails")]
    public async Task<IActionResult> Tails(CancellationToken token)
    {
        if (!await CanBrowseAsync(token)) return Forbid();
        return Ok(await repository.TailOptionsAsync(token));
    }

    private async Task<HashSet<(string Table, string Column)>> FieldSetAsync(int moduleId, CancellationToken token)
    {
        var options = await repository.FieldOptionsAsync(moduleId, token);
        return options
            .Select(option => (option.Table, option.Column))
            .ToHashSet();
    }

    private static void ValidateFieldList(ReportSortDraft draft, IReadOnlySet<(string Table, string Column)> options)
    {
        ValidateTokens(ReportAdminValidator.ValidateFieldList(draft.SortFields, "SORT_FIELDS"), options, "SORT_FIELDS");
        ValidateTokens(ReportAdminValidator.ValidateFieldList(draft.GroupFields, "GROUP_FIELDS"), options, "GROUP_FIELDS");
    }

    private static void ValidateTokens(
        IReadOnlyList<string> tokens, IReadOnlySet<(string Table, string Column)> options, string fieldName)
    {
        foreach (var token in tokens)
        {
            var parts = token.Split('.');
            if (!options.Contains((parts[0], parts[1])))
                throw new ArgumentException($"{fieldName} 包含非白名单字段：{token}。", fieldName);
        }
    }

    private async Task<bool> CanBrowseAsync(CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return false;
        return (await rightsRepository.GetAsync(userId, 2201, token)).CanBrowse;
    }

    private async Task<bool> CanSetupAsync(CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return false;
        return (await rightsRepository.GetAsync(userId, 2201, token)).CanSetup;
    }
}
