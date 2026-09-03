using System.Security.Claims;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// 报表中心调度订阅 + Inbox。
/// 调度订阅：用户可订阅报表按周期（DAILY/WEEKLY/MONTHLY）自动生成 PDF 进 Inbox。
/// 权限：订阅/Inbox 的报表必须用户有可见性（REPORT_TAG），由调度服务以订阅者身份取数。
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/report-center")]
public sealed class ReportInboxController(
    ReportInboxRepository repository,
    IPermissionService permissions) : ControllerBase
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new UnauthorizedAccessException();

    private static string ValidateReportId(string? reportId)
    {
        if (string.IsNullOrWhiteSpace(reportId))
            throw new ArgumentException("报表编号不能为空。", nameof(reportId));
        return reportId.Trim();
    }

    private static string ValidateScheduleType(string? scheduleType)
    {
        var type = (scheduleType ?? string.Empty).Trim().ToUpperInvariant();
        if (type is not ("DAILY" or "WEEKLY" or "MONTHLY"))
            throw new ArgumentException("调度周期无效（DAILY/WEEKLY/MONTHLY）。", nameof(scheduleType));
        return type;
    }

    [HttpGet("subscriptions")]
    public async Task<IActionResult> ListSubscriptions(CancellationToken token)
        => Ok(await repository.ListSubscriptionsAsync(UserId, token));

    [HttpPost("subscriptions")]
    public async Task<IActionResult> CreateSubscription([FromBody] CreateSubscriptionRequest request, CancellationToken token)
    {
        var reportId = ValidateReportId(request.ReportId);
        var scheduleType = ValidateScheduleType(request.ScheduleType);
        // 权限门：目标模块报表可见性（REPORT_TAG）
        var permission = await permissions.GetAsync(UserId, request.ModuleId, token);
        if (!permission.Rights.CanBrowse) return Forbid();
        var id = await repository.CreateSubscriptionAsync(
            UserId, request.ModuleId, reportId, scheduleType,
            request.RunHour, request.RunMinute, request.Weekday, request.MonthDay, request.Enabled,
            UserId, token);
        return Ok(new { id });
    }

    [HttpPut("subscriptions/{id:int}")]
    public async Task<IActionResult> UpdateSubscription(int id, [FromBody] CreateSubscriptionRequest request, CancellationToken token)
    {
        var reportId = ValidateReportId(request.ReportId);
        var scheduleType = ValidateScheduleType(request.ScheduleType);
        var permission = await permissions.GetAsync(UserId, request.ModuleId, token);
        if (!permission.Rights.CanBrowse) return Forbid();
        return await repository.UpdateSubscriptionAsync(
            id, UserId, reportId, scheduleType,
            request.RunHour, request.RunMinute, request.Weekday, request.MonthDay, request.Enabled,
            UserId, token) ? NoContent() : NotFound();
    }

    [HttpDelete("subscriptions/{id:int}")]
    public async Task<IActionResult> DeleteSubscription(int id, CancellationToken token)
        => await repository.DeleteSubscriptionAsync(id, UserId, token) ? NoContent() : NotFound();

    [HttpGet("inbox")]
    public async Task<IActionResult> ListInbox(CancellationToken token, [FromQuery] int limit = 20)
        => Ok(await repository.ListInboxAsync(UserId, limit, token));

    [HttpPost("inbox/{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken token)
        => await repository.MarkReadAsync(id, UserId, token) ? NoContent() : NotFound();

    [HttpGet("inbox/{id:int}/download")]
    public async Task<IActionResult> Download(int id, CancellationToken token)
    {
        var inbox = await repository.ListInboxAsync(UserId, 1000, token);
        var item = inbox.FirstOrDefault(i => i.Id == id);
        if (item is null) return NotFound();
        var path = repository.ResolveInboxPath(item.PdfPath);
        if (!System.IO.File.Exists(path)) return NotFound();
        return PhysicalFile(path, "application/pdf", $"{item.Title}.pdf");
    }
}

public sealed record CreateSubscriptionRequest(
    int ModuleId, string? ReportId, string? ScheduleType,
    int RunHour = 8, int RunMinute = 0, int? Weekday = null, int? MonthDay = null, bool Enabled = true);