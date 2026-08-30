using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/reports/{moduleId:int}")]
public sealed class ReportController(
    ReportRepository repository,
    PrintSettingsRepository printSettingsRepository,
    LegacyRightsRepository rightsRepository,
    IPermissionService permissions,
    WorkbenchAuditWriter auditWriter,
    ReportPdfService reportPdfService) : ControllerBase
{
    [HttpGet("definition")]
    public async Task<IActionResult> Definition(int moduleId, [FromQuery] string? reportId, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var rights = (await permissions.GetAsync(userId, moduleId, token)).Rights;
        if (!rights.CanBrowse) return Forbid();
        var definition = await repository.GetDefinitionAsync(moduleId, userId, rights.CanViewCost, rights.CanViewSecrecy,
            rights.DeniedMasterFields, reportId, token);
        return definition is null ? NotFound() : Ok(definition);
    }

    [HttpPost("query")]
    public async Task<IActionResult> Query(int moduleId,
        [FromQuery] int page, [FromQuery] int pageSize, [FromQuery] string? reportId,
        [FromBody] ReportQueryRequest request, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var rights = (await permissions.GetAsync(userId, moduleId, token)).Rights;
        if (!rights.CanBrowse) return Forbid();
        var definition = await repository.GetDefinitionAsync(moduleId, userId, rights.CanViewCost, rights.CanViewSecrecy,
            rights.DeniedMasterFields, reportId, token);
        if (definition is null) return NotFound();
        return Ok(await repository.QueryAsync(definition, request, page, pageSize, rights.DataFilter, token));
    }

    [HttpGet("condition-options/{serialNo:int}")]
    public async Task<IActionResult> ConditionOptions(int moduleId, int serialNo, CancellationToken token)
    {
        var definition = await AuthorizedDefinition(moduleId, null, token);
        if (definition is null) return NotFound();
        return Ok(await repository.GetConditionOptionsAsync(definition, serialNo, token));
    }

    /// <summary>打印面板设置：权限内报表 + 页头/表尾 + 排序方案 + 用户最近设置（SYSQR）。</summary>
    [HttpGet("print-settings")]
    public async Task<IActionResult> PrintSettings(int moduleId, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var rights = (await permissions.GetAsync(userId, moduleId, token)).Rights;
        if (!rights.CanBrowse) return Forbid();
        return Ok(await printSettingsRepository.GetAsync(moduleId, userId, token));
    }

    /// <summary>保存打印面板设置（写入 SYSQR，IS_LAST=1 语义）。</summary>
    [HttpPost("print-settings")]
    public async Task<IActionResult> SavePrintSettings(int moduleId,
        [FromBody] ReportPrintSettingsRequest request, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var rights = (await permissions.GetAsync(userId, moduleId, token)).Rights;
        if (!rights.CanBrowse) return Forbid();
        await printSettingsRepository.SaveAsync(moduleId, userId, request, token);
        return NoContent();
    }

    /// <summary>
    /// 报表 PDF（覆盖 RptList/RptList2/RptDirect/RptInteg）：
    /// 报表清单按预览权限过滤，生成前校验打印权；页头/表尾可覆盖报表默认；
    /// 排序/分组字段来自 REPORT_SORT 白名单；数据由 QueryPdfAsync 受控查询。
    /// </summary>
    [HttpPost("pdf")]
    public async Task<IActionResult> Pdf(int moduleId, [FromBody] ReportPdfRequest request, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var moduleRights = (await permissions.GetAsync(userId, moduleId, token)).Rights;
        if (!moduleRights.CanBrowse) return Forbid();
        var definition = await repository.GetDefinitionAsync(
            moduleId, userId, moduleRights.CanViewCost, moduleRights.CanViewSecrecy,
            moduleRights.DeniedMasterFields, request.ReportId, token);
        if (definition is null) return NotFound();

        var settings = await printSettingsRepository.GetAsync(moduleId, userId, token);
        var report = settings.Reports.FirstOrDefault(item => !string.IsNullOrWhiteSpace(request.ReportId) && item.ReportId == request.ReportId.Trim())
            ?? settings.Reports.FirstOrDefault(item => item.IsDefault)
            ?? settings.Reports.FirstOrDefault();
        if (report is null) return NotFound();
        var reportRights = await rightsRepository.GetReportAsync(userId, moduleId, report.ReportId, token);
        if (!reportRights.CanPrint) return Forbid();

        var meta = await printSettingsRepository.GetPdfMetaAsync(moduleId, report.ReportId, token);
        if (meta is null) return NotFound();

        var headerId = string.IsNullOrWhiteSpace(request.HeaderId) ? meta.HeaderId : request.HeaderId.Trim();
        var tailId = string.IsNullOrWhiteSpace(request.TailId) ? meta.TailId : request.TailId.Trim();
        var header = settings.Headers.FirstOrDefault(item => item.HeaderId == headerId) ?? meta.Header;
        var tail = settings.Tails.FirstOrDefault(item => item.TailId == tailId);
        var tailText = tail?.TailText ?? meta.TailText;

        IReadOnlyList<string> sortFields = definition.SortFields;
        IReadOnlyList<string> groupFields = new List<string>();
        var scheme = meta.SortSchemes.FirstOrDefault(item => request.SortSerialNo is not null && item.SerialNo == request.SortSerialNo);
        if (scheme is not null)
        {
            sortFields = SplitFields(scheme.SortFields);
            groupFields = SplitFields(scheme.GroupFields);
        }

        var query = await repository.QueryPdfAsync(
            definition,
            new ReportQueryRequest(request.Values ?? new Dictionary<int, string?>(), request.ValuesTo ?? new Dictionary<int, string?>()),
            definition.ModuleFilter,
            meta.ReportFilter,
            CombineDataFilters(moduleRights.DataFilter, reportRights.DataFilter),
            sortFields,
            groupFields,
            token);

        var pdf = reportPdfService.Generate(new ReportPdfRenderInput(
            meta, definition, query, BuildConditionDescription(definition, request), userId,
            groupFields, request.ShowGroup, request.ShowDetail, header, tailText));
        await auditWriter.WriteBestEffortAsync(moduleId, report.ReportId, "PRINT", $"报表打印 {definition.Title}", userId, "REPORT_PRINT", result: 1, null, token);
        return File(pdf, "application/pdf", $"{definition.Title}.pdf");
    }

    /// <summary>CSV 导出：除模块浏览权外，报表级 EXPORT_TAG 必须为真（对齐旧 RptView 导出权限）。</summary>
    [HttpPost("export")]
    public async Task<IActionResult> Export(int moduleId, [FromQuery] string? reportId,
        [FromBody] ReportQueryRequest request, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return Unauthorized();
        var rights = (await permissions.GetAsync(userId, moduleId, token)).Rights;
        if (!rights.CanBrowse) return Forbid();
        var definition = await repository.GetDefinitionAsync(moduleId, userId, rights.CanViewCost, rights.CanViewSecrecy,
            rights.DeniedMasterFields, reportId, token);
        if (definition is null) return NotFound();
        var settings = await printSettingsRepository.GetAsync(moduleId, userId, token);
        var report = settings.Reports.FirstOrDefault(item => item.IsDefault) ?? settings.Reports.FirstOrDefault();
        if (report is null) return Forbid();
        var reportRights = await rightsRepository.GetReportAsync(userId, moduleId, report.ReportId, token);
        if (!reportRights.CanExport) return Forbid();

        var result = await repository.QueryAsync(
            definition, request, 1, 200,
            CombineDataFilters(rights.DataFilter, reportRights.DataFilter), token);
        using var writer = new StringWriter();
        writer.Write('\uFEFF');
        writer.WriteLine(string.Join(',', definition.Columns.Select(column => Escape(column.Label))));
        foreach (var row in result.Rows)
            writer.WriteLine(string.Join(',', definition.Columns.Select(column => Escape(Convert.ToString(row.GetValueOrDefault(column.Key)) ?? ""))));
        await auditWriter.WriteBestEffortAsync(moduleId, report.ReportId, "EXPORT", $"报表导出 {definition.Title}", userId, "REPORT_EXPORT", result: 1, null, token);
        return File(System.Text.Encoding.UTF8.GetBytes(writer.ToString()), "text/csv; charset=utf-8", "report.csv");
    }

    private async Task<ReportDefinition?> AuthorizedDefinition(int moduleId, string? reportId, CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return null;
        var rights = (await permissions.GetAsync(userId, moduleId, token)).Rights;
        if (!rights.CanBrowse) return null;
        return await repository.GetDefinitionAsync(moduleId, userId, rights.CanViewCost, rights.CanViewSecrecy,
            rights.DeniedMasterFields, reportId, token);
    }

    private static IReadOnlyList<string> SplitFields(string? raw) =>
        (raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(field => field.Contains('.'))
            .ToList();

    private static string BuildConditionDescription(ReportDefinition definition, ReportPdfRequest request)
    {
        var values = request.Values ?? new Dictionary<int, string?>();
        var valuesTo = request.ValuesTo ?? new Dictionary<int, string?>();
        var parts = new List<string>();
        foreach (var condition in definition.Conditions)
        {
            var value = values.GetValueOrDefault(condition.SerialNo);
            var valueTo = valuesTo.GetValueOrDefault(condition.SerialNo);
            if (condition.Type == 1)
            {
                if (!string.IsNullOrWhiteSpace(value) || !string.IsNullOrWhiteSpace(valueTo))
                    parts.Add($"{condition.Desc} 从 {value} 到 {valueTo}");
            }
            else if (!string.IsNullOrWhiteSpace(value))
            {
                var label = condition.Options.FirstOrDefault(option => option.Value == value)?.Label ?? value;
                parts.Add($"{condition.Desc} = {label}");
            }
        }
        return parts.Count == 0 ? string.Empty : "条件：" + string.Join("；", parts);
    }

    private static string Escape(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    /// <summary>合并多个 DATA_FILTER 表达式（交集收紧，§13.3 作用顺序：AND 组合）。</summary>
    private static string CombineDataFilters(params string?[] filters)
    {
        var parts = filters
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => $"({f!.Trim()})")
            .ToList();
        return parts.Count == 0 ? string.Empty : string.Join(" AND ", parts);
    }
}
