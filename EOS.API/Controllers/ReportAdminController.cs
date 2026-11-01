using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 报表排序汇总设置（2201，旧名报表定义维护 / 229801 报表设置明细）：
/// REPORT + REPORT_SORT 主子表 CRUD，字段选择器选项来自模块主/明细表白名单；
/// 排序/分组字段串必须命中字段白名单；值全部参数化。
/// 权限门：读要求 CanBrowse(2201)，写要求 CanSetup(2201)。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/report-admin")]
public sealed class ReportAdminController(
    ReportAdminRepository repository,
    ReportFormatRepository reportFormats,
    ModuleRightsRepository rightsRepository,
    ReportMetricAdminRepository metrics,
    Features.Assistant.Metrics.ReportMetricAdminValidator metricValidator) : ControllerBase
{
    /// <summary>
    /// 可绑定的打印版式（内置格式包）。版式自带数据契约（列清单），只能绑到同模块的报表上——
    /// 跨模块绑定会让版式引用到报表没有的列。
    /// </summary>
    [HttpGet("formats")]
    public async Task<IActionResult> Formats([FromQuery] int? moduleId, CancellationToken token)
    {
        if (!await CanBrowseAsync(token)) return Forbid();
        var options = reportFormats.ListTemplates()
            .Where(item => moduleId is null || item.ModuleId == moduleId.Value)
            .Select(item => new ReportFormatOption(item.FormatId, item.Title, item.ModuleId))
            .ToList();
        return Ok(options);
    }

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
        ValidateFormat(draft.FormatId, draft.ModuleId!.Value);
        await ValidateCompanyAsync(draft.CompanyId, token);
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
        var moduleId = await repository.GetReportModuleAsync(id, token);
        if (moduleId is null) return NotFound();
        ValidateFormat(draft.FormatId, moduleId.Value);
        await ValidateCompanyAsync(draft.CompanyId, token);
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

    [HttpGet("companies")]
    public async Task<IActionResult> Companies(CancellationToken token)
    {
        if (!await CanBrowseAsync(token)) return Forbid();
        return Ok(await repository.CompanyOptionsAsync(token));
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

    /// <summary>
    /// 版式绑定校验（fail-closed）：空 = 沿用模块默认版式；非空必须是**存在**的格式包，
    /// 且归属于同一模块——绑一个不存在的编号会让打印静默回落到默认版式（看起来"配了不生效"），
    /// 绑一个别的模块的版式会引用到本报表没有的列。
    /// </summary>
    private void ValidateFormat(string? formatId, int moduleId)
    {
        if (string.IsNullOrWhiteSpace(formatId)) return;
        var package = reportFormats.GetPackage(formatId.Trim())
            ?? throw new ArgumentException($"打印版式 '{formatId.Trim()}' 不存在，请从可选版式中选择。");
        if (package.Format.ModuleId != moduleId)
            throw new ArgumentException($"打印版式 '{formatId.Trim()}' 属于模块 {package.Format.ModuleId}，不能绑定到模块 {moduleId} 的报表。");
    }

    /// <summary>
    /// 公司绑定校验（fail-closed）：必须显式选择公司（系统默认公司编号 DEFAULT），且必须真实存在——
    /// 不选会让打印页头失去公司名，库层也已用 NOT NULL + CHECK 拒空（迁移 449）。
    /// </summary>
    private async Task ValidateCompanyAsync(string? companyId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(companyId))
            throw new ArgumentException("必须选择开单公司（系统默认公司编号为 DEFAULT）。");
        var id = companyId.Trim();
        if (!await repository.CompanyExistsAsync(id, token))
            throw new ArgumentException($"公司 '{id}' 不存在，请从 110101 公司基本资料中选择。");
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

    // ---------- 指标口径（REPORT_METRIC）维护 ----------
    // 权限门用 **2317 指标口径**（独立能力，不挂在报表设置或元数据管理上）：读 = CanBrowse，写 = CanSetup。
    // 与报表（2201）分开的理由：同一条口径被报表与助手共同消费，把它绑在某张报表的权限上
    // 会让"谁能改口径"取决于"谁能改报表"。

    /// <summary>口径清单（含候选口径）。</summary>
    [HttpGet("metrics")]
    public async Task<IActionResult> Metrics(CancellationToken token)
    {
        if (!await CanStewardBrowseAsync(token)) return Forbid();
        return Ok(await metrics.ListAsync(token));
    }

    /// <summary>新增口径。标识是身份（新增后不可改），表达式与维度必须过受控校验。</summary>
    [HttpPost("metrics")]
    public async Task<IActionResult> CreateMetric([FromBody] ReportMetricAdminDraft draft, CancellationToken token)
    {
        if (!await CanStewardSetupAsync(token)) return Forbid();
        if (draft is null) throw new ArgumentException("缺少口径内容。");
        await metricValidator.ValidateAsync(draft, token);
        if (await metrics.ExistsAsync(draft.MetricId.Trim(), token)) return Conflict("口径标识已存在。");
        await metrics.CreateAsync(draft, CurrentUserId, token);
        return NoContent();
    }

    /// <summary>修改口径：版本号自动递增；改成 CONFIRMED 记批核人/时间，改回候选清空。</summary>
    [HttpPut("metrics/{metricId}")]
    public async Task<IActionResult> UpdateMetric(
        string metricId, [FromBody] ReportMetricAdminDraft draft, CancellationToken token)
    {
        if (!await CanStewardSetupAsync(token)) return Forbid();
        if (draft is null) throw new ArgumentException("缺少口径内容。");
        if (!string.Equals(metricId.Trim(), draft.MetricId?.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("口径标识是身份，不允许修改；如需新标识请新建一条口径。");
        }
        await metricValidator.ValidateAsync(draft, token);
        return await metrics.UpdateAsync(metricId.Trim(), draft, CurrentUserId, token) ? NoContent() : NotFound();
    }

    /// <summary>删除口径：只允许候选；已确认的先改回候选（那一步本身就是一次显式决策）。</summary>
    [HttpDelete("metrics/{metricId}")]
    public async Task<IActionResult> DeleteMetric(string metricId, CancellationToken token)
    {
        if (!await CanStewardSetupAsync(token)) return Forbid();
        return await metrics.DeleteAsync(metricId.Trim(), token)
            ? NoContent()
            : Conflict("只能删除候选口径：已确认口径可能被报表与结论引用，请先改回候选。");
    }

    private string CurrentUserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private async Task<bool> CanStewardBrowseAsync(CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return false;
        return (await rightsRepository.GetAsync(userId, PermissionModules.MetricCatalog, token)).CanBrowse;
    }

    private async Task<bool> CanStewardSetupAsync(CancellationToken token)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null) return false;
        return (await rightsRepository.GetAsync(userId, PermissionModules.MetricCatalog, token)).CanSetup;
    }
}
