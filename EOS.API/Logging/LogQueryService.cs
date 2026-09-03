using System.Globalization;
using System.Text.Json;

namespace EOS.API.Logging;

/// <summary>
/// 结构化日志查询：从 JSONL 滚动文件读取并按时间/级别/traceId/
/// correlationId/userId/moduleId/action/error code 检索；输出统一脱敏、限行数与字节数。
/// 只读服务，不提供修改/删除日志能力。
/// </summary>
public sealed class LogQueryService(IConfiguration configuration)
{
    public const int MaxLines = 500;
    public const int MaxOutputBytes = 256 * 1024;

    private static readonly string[] LevelOrder = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];
    private readonly string _path = ResolvePath(configuration);

    public async Task<IReadOnlyList<LogEntry>> SearchAsync(LogSearchRequest request, CancellationToken token)
    {
        var entries = await ReadEntriesAsync(token);
        var rank = LevelRank(request.Level);
        var result = entries
            .Where(e => request.From is null || e.Ts >= request.From.Value)
            .Where(e => request.To is null || e.Ts <= request.To.Value)
            .Where(e => rank is null || LevelRank(e.Level) >= rank)
            .Where(e => request.TraceId is null || FieldString(e, "TraceId") == request.TraceId)
            .Where(e => request.CorrelationId is null || FieldString(e, "CorrelationId") == request.CorrelationId)
            .Where(e => request.UserId is null || FieldString(e, "User") == request.UserId)
            .Where(e => request.ModuleId is null || FieldInt(e, "ModuleId") == request.ModuleId)
            .Where(e => request.Action is null || string.Equals(FieldString(e, "Action"), request.Action, StringComparison.OrdinalIgnoreCase))
            .Where(e => request.ErrorCode is null || FieldString(e, "ErrorCode") == request.ErrorCode)
            .Where(e => request.Keyword is null
                || (e.Message ?? string.Empty).Contains(request.Keyword, StringComparison.OrdinalIgnoreCase)
                || (e.Category ?? string.Empty).Contains(request.Keyword, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Clamp(request.Limit, 1, MaxLines))
            .ToList();
        return RedactEntries(result);
    }

    public Task<IReadOnlyList<LogEntry>> GetTraceAsync(string correlationId, CancellationToken token)
        => SearchAsync(new LogSearchRequest(CorrelationId: correlationId, Limit: MaxLines), token);

    public async Task<IReadOnlyList<LogSummaryRow>> SummarizeErrorsAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken token)
    {
        var entries = await ReadEntriesAsync(token);
        var rows = entries
            .Where(e => (e.Level == "Error" || e.Level == "Critical" || FieldString(e, "ErrorCode") is { Length: > 0 }))
            .Where(e => from is null || e.Ts >= from.Value)
            .Where(e => to is null || e.Ts <= to.Value)
            .GroupBy(e => (
                ErrorCode: string.IsNullOrEmpty(FieldString(e, "ErrorCode")) ? "UNKNOWN" : FieldString(e, "ErrorCode")!,
                Module: FieldString(e, "ModuleId") ?? string.Empty,
                Action: FieldString(e, "Action") ?? string.Empty))
            .Select(g => new LogSummaryRow(g.Key.ErrorCode, g.Key.Module, g.Key.Action, g.Count()))
            .OrderByDescending(row => row.Count)
            .Take(MaxLines)
            .ToList();
        return rows.Select(row => row with { ErrorCode = LogRedactor.Redact(row.ErrorCode) }).ToList();
    }

    public async Task<SlowRequestReport?> ExplainSlowAsync(string correlationId, CancellationToken token)
    {
        var entries = await GetTraceAsync(correlationId, token);
        var http = entries.FirstOrDefault(e => string.Equals(e.Event, "http_request", StringComparison.OrdinalIgnoreCase));
        if (http is null)
        {
            return null;
        }
        return new SlowRequestReport(
            correlationId,
            FieldDouble(http, "ElapsedMs") ?? 0,
            FieldDouble(http, "DbElapsedMs"),
            FieldString(http, "Status"),
            FieldString(http, "Action"),
            FieldString(http, "ModuleId"),
            FieldString(http, "Path"),
            entries);
    }

    public async Task<IReadOnlyList<LogEntry>> TailAsync(string? level, int limit, CancellationToken token)
    {
        var entries = await ReadEntriesAsync(token);
        var rank = LevelRank(level);
        var result = entries
            .Where(e => rank is null || LevelRank(e.Level) >= rank)
            .TakeLast(Math.Clamp(limit, 1, MaxLines))
            .Reverse()
            .ToList();
        return RedactEntries(result);
    }

    public string RedactText(string text) => LogRedactor.Redact(text);

    private async Task<IReadOnlyList<LogEntry>> ReadEntriesAsync(CancellationToken token)
    {
        var files = new List<string>();
        if (File.Exists(_path))
        {
            files.Add(_path);
        }
        var rotated = $"{_path}.1";
        if (File.Exists(rotated))
        {
            files.Add(rotated);
        }
        var entries = new List<LogEntry>();
        foreach (var file in files)
        {
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync(token)) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                var entry = Parse(line);
                if (entry is not null)
                {
                    entries.Add(entry);
                }
            }
        }
        return entries.OrderBy(entry => entry.Ts).ToList();
    }

    private static LogEntry? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (root.TryGetProperty("fields", out var fieldsElement) && fieldsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in fieldsElement.EnumerateObject())
                {
                    fields[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.Number when property.Value.TryGetInt64(out var l) => l,
                        JsonValueKind.Number when property.Value.TryGetDouble(out var d) => d,
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        JsonValueKind.Null => null,
                        _ => property.Value.GetString(),
                    };
                }
            }
            var ts = root.TryGetProperty("ts", out var tsElement) && DateTimeOffset.TryParse(tsElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedTs)
                ? parsedTs
                : DateTimeOffset.MinValue;
            var level = root.TryGetProperty("level", out var levelElement) ? levelElement.GetString() ?? "Information" : "Information";
            var category = root.TryGetProperty("category", out var categoryElement) ? categoryElement.GetString() ?? string.Empty : string.Empty;
            var eventId = root.TryGetProperty("eventId", out var eventIdElement) && eventIdElement.TryGetInt32(out var parsedEventId) ? parsedEventId : (int?)null;
            var eventName = root.TryGetProperty("event", out var eventElement) ? eventElement.GetString() : null;
            var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? string.Empty : string.Empty;
            var exception = root.TryGetProperty("exception", out var exceptionElement) ? exceptionElement.GetString() : null;
            return new LogEntry(ts, level, category, eventId, eventName, message, fields, exception);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<LogEntry> RedactEntries(IEnumerable<LogEntry> entries)
    {
        var result = new List<LogEntry>();
        var totalBytes = 0;
        foreach (var entry in entries)
        {
            var fields = entry.Fields?
                .ToDictionary(pair => pair.Key, pair => pair.Value is string s ? (object?)LogRedactor.Redact(s) : pair.Value, StringComparer.Ordinal);
            var redacted = entry with
            {
                Message = LogRedactor.Redact(entry.Message),
                Exception = LogRedactor.Redact(entry.Exception),
                Fields = fields,
            };
            totalBytes += (redacted.Message?.Length ?? 0) + (redacted.Exception?.Length ?? 0);
            if (totalBytes > MaxOutputBytes)
            {
                break;
            }
            result.Add(redacted);
        }
        return result;
    }

    private static int? LevelRank(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            return null;
        }
        var index = Array.FindIndex(LevelOrder, item => item.Equals(level.Trim(), StringComparison.OrdinalIgnoreCase));
        return index < 0 ? null : index;
    }

    private static string FieldString(LogEntry entry, string key) =>
        entry.Fields is not null && entry.Fields.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;

    private static int? FieldInt(LogEntry entry, string key) =>
        int.TryParse(FieldString(entry, key), out var value) ? value : null;

    private static double? FieldDouble(LogEntry entry, string key) =>
        double.TryParse(FieldString(entry, key), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string ResolvePath(IConfiguration configuration)
    {
        var configured = configuration["Logging:File:Path"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "logs", "api-json.log"));
    }
}
