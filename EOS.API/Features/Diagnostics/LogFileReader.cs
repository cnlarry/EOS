using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EOS.API.Telemetry;
using Microsoft.Data.SqlClient;

namespace EOS.API.Features.Diagnostics;

/// <summary>日志文件描述（供「日志管理」页选择文件与打包白名单使用）。</summary>
public sealed record LogFileInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("lastWriteTime")] string LastWriteTime);

/// <summary>一条日志记录（JSONL 一行的投影；fields/exception 保持原样，已过脱敏）。</summary>
public sealed record LogEntry(
    [property: JsonPropertyName("ts")] string? Timestamp,
    [property: JsonPropertyName("level")] string? Level,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("correlationId")] string? CorrelationId,
    [property: JsonPropertyName("moduleId")] string? ModuleId,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("exception")] string? Exception,
    [property: JsonPropertyName("file")] string File);

/// <summary>读取过滤器：服务端只按这些字段筛选，不接受任意表达式。</summary>
public sealed record LogQuery(
    int Take,
    string? Level,
    string? Keyword,
    string? CorrelationId,
    string? Event,
    DateTimeOffset? From);

/// <summary>
/// 运行日志读取：文件枚举、按字段过滤、以及按白名单打包。
/// **权限与脱敏的边界**：文件内容在写入时已过 `LogRedactor`（见 ADR-025/027），
/// 本类只读不写、不做二次解析改写；目录与文件名一律来自日志 provider 的自身配置，
/// 不接受调用方传入路径（避免绕过白名单读任意文件）。
/// </summary>
public sealed class LogFileReader(JsonFileLoggerProvider provider)
{
    private static readonly string[] Levels = ["Trace", "Debug", "Information", "Warning", "Error", "Critical"];

    /// <summary>当前写文件 + 轮转副本，按最后写入时间倒序（最新的排最前）。</summary>
    public IReadOnlyList<FileInfo> ListFiles()
    {
        var path = provider.Path;
        var directory = Path.GetDirectoryName(path) ?? ".";
        var baseName = Path.GetFileName(path);
        if (!Directory.Exists(directory)) return [];

        return Directory.EnumerateFiles(directory, baseName + "*")
            .Select(candidate => new FileInfo(candidate))
            .Where(file => file.Exists && file.Length >= 0)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
    }

    public IReadOnlyList<LogFileInfo> ListFileInfo() =>
        ListFiles()
            .Select(file => new LogFileInfo(
                file.Name,
                file.Length,
                file.LastWriteTime.ToString("yyyy-MM-ddTHH:mm:sszzz")))
            .ToList();

    /// <summary>总占用体积（用于日志管理页显示与打包体积预估）。</summary>
    public long TotalBytes() => ListFiles().Sum(file => file.Length);

    /// <summary>
    /// 读取日志条目：最新文件优先、文件内自后向前，命中 take 即停。
    /// 单行读取失败（正在写入的半行）跳过而不使整个查询失败。
    /// </summary>
    public (IReadOnlyList<LogEntry> Entries, bool Truncated) Read(LogQuery query)
    {
        var files = ListFiles();
        var results = new List<LogEntry>();
        var take = Math.Clamp(query.Take, 1, 2000);

        for (var fileIndex = 0; fileIndex < files.Count; fileIndex++)
        {
            var file = files[fileIndex];
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file.FullName, Encoding.UTF8);
            }
            catch (IOException)
            {
                // 轮转窗口内文件可能瞬时不可用：跳过该文件，不让整次查询失败
                continue;
            }

            for (var index = lines.Length - 1; index >= 0; index--)
            {
                var entry = TryParse(lines[index], file.Name);
                if (entry is null || !Matches(entry, query)) continue;
                results.Add(entry);
                if (results.Count < take) continue;
                // 还有更旧的内容没读完 ⇒ 明确告知被截断，避免前端把"前 N 条"当成全量
                var truncated = index > 0 || fileIndex < files.Count - 1;
                return (results, truncated);
            }
        }
        return (results, false);
    }

    /// <summary>纯函数式的过滤/解析入口：给定文件内容与过滤器返回条目（供单测直接断言，不碰磁盘）。</summary>
    public static IReadOnlyList<LogEntry> FilterLines(IEnumerable<string> lines, LogQuery query, string fileName = "test.log")
    {
        var take = Math.Clamp(query.Take, 1, 2000);
        var results = new List<LogEntry>();
        foreach (var line in lines)
        {
            var entry = TryParse(line, fileName);
            if (entry is null || !Matches(entry, query)) continue;
            results.Add(entry);
            if (results.Count >= take) break;
        }
        return results;
    }

    /// <summary>把日志文件按白名单写入 zip 流（打包用，不落中间文件）。</summary>
    public IReadOnlyList<(string Name, long SizeBytes)> WriteToZip(System.IO.Compression.ZipArchive archive)
    {
        var written = new List<(string, long)>();
        foreach (var file in ListFiles())
        {
            try
            {
                var entry = archive.CreateEntry(file.Name, System.IO.Compression.CompressionLevel.Optimal);
                using var target = entry.Open();
                using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                source.CopyTo(target);
                written.Add((file.Name, file.Length));
            }
            catch (IOException)
            {
                // 打包瞬间文件被轮转/删除：跳过并在清单里如实反映（调用方据 written 生成 manifest）
                continue;
            }
        }
        return written;
    }

    private static LogEntry? TryParse(string line, string fileName)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            string? fields(string name) =>
                root.TryGetProperty("fields", out var fieldsElement)
                && fieldsElement.ValueKind == JsonValueKind.Object
                && fieldsElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            return new LogEntry(
                Text(root, "ts"),
                Text(root, "level"),
                Text(root, "category"),
                Text(root, "event"),
                Text(root, "message"),
                fields("CorrelationId"),
                fields("ModuleId"),
                fields("ErrorCode"),
                Text(root, "exception"),
                fileName);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Matches(LogEntry entry, LogQuery query)
    {
        if (query.Level is { Length: > 0 } level && !string.Equals(entry.Level, level, StringComparison.OrdinalIgnoreCase)) return false;
        if (query.Event is { Length: > 0 } @event && !Contains(entry.Event, @event)) return false;
        if (query.CorrelationId is { Length: > 0 } correlation && !Contains(entry.CorrelationId, correlation)) return false;
        if (query.Keyword is { Length: > 0 } keyword
            && !Contains(entry.Message, keyword) && !Contains(entry.Exception, keyword) && !Contains(entry.Category, keyword))
        {
            return false;
        }
        if (query.From is { } from && DateTimeOffset.TryParse(entry.Timestamp, out var timestamp) && timestamp < from) return false;
        return true;
    }

    private static bool Contains(string? value, string needle) =>
        value is not null && value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>级别清单：前端下拉只从服务端取，避免两边各写一份。</summary>
    public static IReadOnlyList<string> KnownLevels => Levels;

    /// <summary>迁移台账摘要：只报数量与最后一个脚本名，不导出内容。</summary>
    public static async Task<(int AppliedCount, string? LastScript)> ReadJournalAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT_BIG(1), MAX(SCRIPTNAME)
            FROM dbo.ERP_SCHEMA_JOURNAL;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (0, null);
        var count = (int)Convert.ToInt64(reader.GetValue(0));
        var last = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (count, last);
    }
}
