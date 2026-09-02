using System.Data;
using System.Diagnostics;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace EOS.API.Controllers;

/// <summary>
/// Batch job entry points (inventory recalc, HR card batch, attendance generation and
/// wage-driven adjustment). This controller authorizes and validates requests; the actual
/// work is delegated to HumanResourceJobsService / AttendanceCalcService.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/jobs")]
public sealed class JobsController(
    DbConnectionFactory connections,
    LegacyRightsRepository rightsRepository,
    CurrentUserContext userContext,
    AttendanceCalcService attendanceCalc,
    HumanResourceJobsService hrJobs) : ControllerBase
{
    /// <summary>
    /// Recalculates available stock (230901) by running the whitelisted procedure
    /// P_UPDATE_PRO_MRP_ALL after verifying it exists.
    /// </summary>
    [HttpPost("mrp-recalc")]
    public async Task<IActionResult> MrpRecalc(CancellationToken token)
    {
        if (!await CanRunAsync(ModuleIds.MrpRecalc, token)) return Forbid();
        const string sproc = "P_UPDATE_PRO_MRP_ALL";
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string existsSql = "SELECT 1 FROM sys.objects WHERE object_id=OBJECT_ID(@Name) AND type='P';";
        await using var existsCommand = new SqlCommand(existsSql, connection);
        existsCommand.Parameters.Add("@Name", SqlDbType.NVarChar, 200).Value = sproc;
        if (await existsCommand.ExecuteScalarAsync(token) is null)
            return BadRequest(new { code = "SPROC_NOT_FOUND", message = "重算存储过程不存在。" });
        var stopwatch = Stopwatch.StartNew();
        await using var command = new SqlCommand(sproc, connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = 600,
        };
        await command.ExecuteNonQueryAsync(token);
        stopwatch.Stop();
        return Ok(new { sproc, elapsedMs = stopwatch.ElapsedMilliseconds });
    }

    /// <summary>Batch upserts employee access cards (1..2000 rows per request).</summary>
    [HttpPost("card-batch")]
    public async Task<IActionResult> CardBatch([FromBody] CardBatchRequest request, CancellationToken token)
    {
        if (!await CanRunAsync(ModuleIds.CardBatch, token)) return Forbid();
        if (request.Cards.Count == 0 || request.Cards.Count > 2000) return BadRequest(new { code = "INVALID_CARDS", message = "发卡数量需在 1~2000 之间。" });
        if (request.StartDate == default) return BadRequest(new { code = "INVALID_DATE", message = "生效日期不能为空。" });
        var executor = User.Identity?.Name ?? "SYSTEM";
        var result = await hrJobs.BatchCardsAsync(request.Cards, request.StartDate, request.EndDate, executor, token);
        return Ok(new { result.Updated, result.Inserted });
    }

    /// <summary>Generates blank attendance rows over a date range and fills them from approved plans.</summary>
    [HttpPost("attendance-generate")]
    public async Task<IActionResult> AttendanceGenerate([FromBody] AttendanceGenerateRequest request, CancellationToken token)
    {
        if (!await CanRunAsync(ModuleIds.AttendanceSimulate, token) && !await CanRunAsync(ModuleIds.AttendanceExtract, token)) return Forbid();
        if (request.StartDate == default || request.EndDate == default || request.EndDate < request.StartDate)
            return BadRequest(new { code = "INVALID_RANGE", message = "日期范围不合法。" });
        var days = (request.EndDate - request.StartDate).Days + 1;
        if (days > 62) return BadRequest(new { code = "RANGE_TOO_LARGE", message = "日期范围不能超过 62 天。" });
        if (request.Mode is not ("simulate" or "extract"))
            return BadRequest(new { code = "INVALID_MODE", message = "mode 仅支持 simulate 或 extract。" });
        var hasTarget = (request.EmpIds ?? []).Any(id => !string.IsNullOrWhiteSpace(id)) || !string.IsNullOrWhiteSpace(request.DeptId);
        if (!hasTarget) return BadRequest(new { code = "NO_TARGET", message = "请指定员工或部门。" });
        var result = await hrJobs.GenerateAttendanceAsync(
            request.StartDate, request.EndDate, request.Mode, request.DeptId, request.EmpIds, token);
        if (result.EmployeeCount == 0) return BadRequest(new { code = "NO_EMPLOYEE", message = "没有符合条件的员工。" });
        return Ok(new { Mode = result.Mode, StartDate = result.StartDate, EndDate = result.EndDate, result.EmployeeCount, result.Inserted, result.Filled });
    }

    /// <summary>Calculates attendance shifts, overtime classification and hours over a date range (idempotent).</summary>
    [HttpPost("attendance-calc")]
    public async Task<IActionResult> AttendanceCalc([FromBody] AttendanceGenerateRequest request, CancellationToken token)
    {
        if (!await CanRunAsync(ModuleIds.AttendanceSimulate, token) && !await CanRunAsync(ModuleIds.AttendanceExtract, token)) return Forbid();
        if (request.StartDate == default || request.EndDate == default || request.EndDate < request.StartDate)
            return BadRequest(new { code = "INVALID_RANGE", message = "日期范围不合法。" });
        var days = (request.EndDate - request.StartDate).Days + 1;
        if (days > 62) return BadRequest(new { code = "RANGE_TOO_LARGE", message = "日期范围不能超过 62 天。" });
        var hasTarget = (request.EmpIds ?? []).Any(id => !string.IsNullOrWhiteSpace(id)) || !string.IsNullOrWhiteSpace(request.DeptId);
        if (!hasTarget) return BadRequest(new { code = "NO_TARGET", message = "请指定员工或部门。" });
        var empIds = (request.EmpIds ?? [])
            .Select(id => (id ?? "").Trim())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
        var result = await attendanceCalc.CalculateAsync(request.StartDate, request.EndDate, empIds, request.DeptId, token);
        return Ok(new
        {
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            EmployeeCount = result.EmployeeCount,
            DiaryRows = result.DiaryRows,
            Updated = result.Updated,
            SkippedNoTimeType = result.SkippedNoTimeType,
            SkippedNotActive = result.SkippedNotActive,
        });
    }

    /// <summary>
    /// Adjusts attendance for wage deductions (WAGE_ADD&lt;0) by clearing overtime/work fields
    /// in priority order after recalculating wages for the month.
    /// </summary>
    [HttpPost("attendance-adjust-wage")]
    public async Task<IActionResult> AttendanceAdjustWage([FromBody] AttendanceAdjustWageRequest request, CancellationToken token)
    {
        if (!await CanRunAsync(ModuleIds.AttendanceAdjustWage, token)) return Forbid();
        var month = (request.Month ?? "").Trim();
        if (!System.Text.RegularExpressions.Regex.IsMatch(month, "^\\d{6}$"))
            return BadRequest(new { code = "INVALID_MONTH", message = "月份格式应为 yyyyMM。" });
        var result = await hrJobs.AdjustWageAttendanceAsync(month, token);
        if (result is null) return BadRequest(new { code = "WAGE_SETUP_MISSING", message = "考勤系统设置错误：未设置薪资表调整项目（HR_SETUP.WAGE_*）。" });
        return Ok(new { Month = month, WageCalcRuns = result.WageCalcRuns, AdjustedEmployees = result.AdjustedEmployees, ClearedDiaryRows = result.ClearedDiaryRows });
    }

    private async Task<bool> CanRunAsync(int moduleId, CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, moduleId, token)).CanSetup;
}
