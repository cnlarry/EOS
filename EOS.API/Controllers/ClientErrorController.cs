using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// Frontend error intake: global JS errors / unhandled rejections / failed
/// requests reported by EOS.Web land here and are logged at Warning
/// (event client_error) into the same JSONL file pipeline, so the log MCP
/// can correlate them by correlationId. Strictly validated, no DB writes,
/// no business data persisted.
/// </summary>
[ApiController, AllowAnonymous, Route("api/v1/client-errors")]
public sealed class ClientErrorController(ILogger<ClientErrorController> logger) : ControllerBase
{
    [HttpPost]
    public IActionResult Report([FromBody] ClientErrorReport report)
    {
        if (report is null || string.IsNullOrWhiteSpace(report.Message))
        {
            return BadRequest();
        }
        if (report.Message.Length > 1000
            || (report.Stack?.Length ?? 0) > 4000
            || (report.Url?.Length ?? 0) > 500
            || (report.CorrelationId?.Length ?? 0) > 128)
        {
            return BadRequest();
        }
        var correlationId = string.IsNullOrWhiteSpace(report.CorrelationId)
            ? HttpContext.TraceIdentifier
            : report.CorrelationId.Trim();
        logger.LogWarning(
            "Frontend error {Event} {Kind} {Message} url={Url} correlation={CorrelationId}",
            "client_error",
            (report.Kind ?? "error").Trim().Length > 20 ? report.Kind!.Trim()[..20] : (report.Kind ?? "error"),
            report.Message.Trim(),
            (report.Url ?? string.Empty).Trim(),
            correlationId);
        return NoContent();
    }
}

/// <summary>Frontend error payload (all fields optional except message).</summary>
public sealed record ClientErrorReport(string? Kind, string? Message, string? Stack, string? Url, string? CorrelationId);
