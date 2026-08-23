namespace EOS.API.Logging;

/// <summary>结构化日志条目（从 JSONL 文件解析，输出前统一脱敏）。</summary>
public sealed record LogEntry(
    DateTimeOffset Ts,
    string Level,
    string Category,
    int? EventId,
    string? Event,
    string Message,
    IReadOnlyDictionary<string, object?>? Fields,
    string? Exception);

public sealed record LogSearchRequest(
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? Level = null,
    string? TraceId = null,
    string? CorrelationId = null,
    string? UserId = null,
    int? ModuleId = null,
    string? Action = null,
    string? ErrorCode = null,
    string? Keyword = null,
    int Limit = 200);

public sealed record LogSummaryRow(string ErrorCode, string Module, string Action, int Count);

public sealed record SlowRequestReport(
    string CorrelationId,
    double ElapsedMs,
    double? DbElapsedMs,
    string? Status,
    string? Action,
    string? ModuleId,
    string? Path,
    IReadOnlyList<LogEntry> Trace);
