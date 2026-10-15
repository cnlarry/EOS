using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EOS.API.Logging;
using Microsoft.Extensions.Logging;

namespace EOS.API.Telemetry;

/// <summary>
/// JSONL file log provider: appends structured logs to rolling files
/// (default logs/api-json.log) for file-based troubleshooting
/// (tail/grep by Agents, mssql for AUDIT_EVENT).
/// Each line is one JSON object: ts/level/category/eventId/event/message/fields/exception.
/// Only events at or above MinimumLevel reach the file: routine API requests
/// stay on the console pipeline, while warnings, errors and slow requests
/// are the persisted troubleshooting source.
/// Retention is enforced on two axes — size (MaxBytes per file, MaxFiles parts)
/// and age (RetentionDays): whichever expires first wins. Every string written
/// passes LogRedactor so credentials never reach disk; redaction failures are
/// not tolerated (the text is masked, not dropped).
/// </summary>
public sealed class JsonFileLoggerProvider : ILoggerProvider
{
    /// <summary>Retention default when the caller does not configure one.</summary>
    public const int DefaultRetentionDays = 14;

    /// <summary>日志文件名前缀：文件名形如 <c>api-20261015.log</c>。</summary>
    public const string DefaultFilePrefix = "api";

    private readonly string _directory;
    private readonly string _prefix;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly int _retentionDays;
    private readonly LogLevel _minimumLevel;
    /// <summary>时间源：切分与时间戳都走它，测试才能模拟跨天而不必真等一天。</summary>
    private readonly Func<DateTime> _clock;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private FileStream? _stream;
    private long _currentBytes;
    private string _currentPath;
    private DateTime _currentDate;
    private int _currentPart;

    /// <summary>
    /// Directory unusable (read-only or inaccessible): the provider silently degrades to
    /// "enabled but never writes" instead of throwing at startup — a broken log directory
    /// must not prevent the process from serving requests.
    /// </summary>
    public bool IsDisabled { get; private set; }

    /// <summary>当前正在写的日志文件完整路径（按天切分后会随之变化）。</summary>
    public string Path => _currentPath;

    /// <summary>日志目录。</summary>
    public string LogDirectory => _directory;

    /// <summary>
    /// 枚举全部日志文件用的通配符（形如 <c>api-*.log</c>）。
    /// 读取方一律按它枚举，不要拿 <see cref="Path"/> 的文件名去拼——那是"今天"这一份。
    /// </summary>
    public string FilePattern => $"{_prefix}-*.log";

    /// <param name="directory">日志目录，文件名由本类按天生成。</param>
    public JsonFileLoggerProvider(
        string directory,
        long maxBytes,
        int maxFiles,
        LogLevel minimumLevel = LogLevel.Warning,
        int retentionDays = DefaultRetentionDays,
        string filePrefix = DefaultFilePrefix,
        Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.Now);
        _directory = directory;
        _prefix = string.IsNullOrWhiteSpace(filePrefix) ? DefaultFilePrefix : filePrefix.Trim();
        _maxBytes = maxBytes > 0 ? maxBytes : 50L * 1024 * 1024;
        _maxFiles = Math.Max(1, maxFiles);
        _retentionDays = Math.Max(1, retentionDays);
        _minimumLevel = minimumLevel;
        _currentDate = _clock().Date;
        _currentPart = 1;
        _currentPath = BuildPath(_currentDate, _currentPart);
        try
        {
            Directory.CreateDirectory(_directory);
            OpenCurrent(_currentDate, _currentPart);
            DeleteExpiredFiles();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            IsDisabled = true;
            _stream = null;
        }
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Write(
        string categoryName,
        LogLevel level,
        EventId eventId,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>> state,
        Exception? exception)
    {
        if (IsDisabled)
        {
            return;
        }
        var record = BuildRecord(categoryName, level, eventId, message, state, exception);
        record["ts"] = new DateTimeOffset(_clock()).ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
        record["event"] = record["fields"] is Dictionary<string, object?> fields
            ? fields.GetValueOrDefault("Event")
            : null;
        var line = JsonSerializer.Serialize(record, _jsonOptions) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(line);
        lock (_gate)
        {
            if (_stream is null || IsDisabled)
            {
                return;
            }
            try
            {
                RotateIfNeeded(bytes.Length);
                _stream!.Write(bytes, 0, bytes.Length);
                _stream.Flush();
                _currentBytes += bytes.Length;
            }
            catch (Exception writeException) when (writeException is IOException or ObjectDisposedException)
            {
                // 运行期磁盘故障（空间满、目录被删、介质离线）：停写而不是抛出，避免日志把请求打挂。
                IsDisabled = true;
                _stream?.Dispose();
                _stream = null;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private static object? RedactValue(object? value) =>
        value is string text ? LogRedactor.Redact(text) : value;

    /// <summary>
    /// 序列化前的脱敏出口（供单测直接断言，不经文件）：消息、字段字符串值与异常文本
    /// 都必须先过这里再落盘。返回的字典内容与写入文件的一致。
    /// </summary>
    internal static Dictionary<string, object?> BuildRecord(
        string categoryName,
        LogLevel level,
        EventId eventId,
        string message,
        IReadOnlyList<KeyValuePair<string, object?>> state,
        Exception? exception)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in state)
        {
            if (key == "{OriginalFormat}") continue;
            fields[key] = RedactValue(value);
        }
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["level"] = level.ToString(),
            ["category"] = categoryName,
            ["eventId"] = eventId.Id,
            ["message"] = LogRedactor.Redact(message),
            ["fields"] = fields.Count > 0 ? fields : null,
            ["exception"] = exception is null ? null : LogRedactor.Redact(exception.ToString()),
        };
    }

    /// <summary>文件名：<c>api-20261015.log</c>；同一天里再切则带序号 <c>api-20261015-2.log</c>。</summary>
    internal string BuildPath(DateTime date, int part) =>
        System.IO.Path.Combine(_directory, part <= 1
            ? $"{_prefix}-{date:yyyyMMdd}.log"
            : $"{_prefix}-{date:yyyyMMdd}-{part}.log");

    private void OpenCurrent(DateTime date, int part)
    {
        _currentPath = BuildPath(date, part);
        _currentDate = date;
        _currentPart = part;
        _stream = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _currentBytes = _stream.Length;
    }

    /// <summary>
    /// 换文件的两种时机：① 跨天——新的一天写进新文件；② 单日体量超阈值——
    /// 异常刷屏时一天也能写满，故按当天序号继续切，不让单个文件无上限地涨。
    /// </summary>
    private void RotateIfNeeded(int incomingBytes)
    {
        var today = _clock().Date;
        if (today == _currentDate && _currentBytes + incomingBytes <= _maxBytes)
        {
            return;
        }
        _stream?.Dispose();
        OpenCurrent(today, today == _currentDate ? _currentPart + 1 : 1);
        DeleteExpiredFiles();
    }

    /// <summary>
    /// Age gate: rotated parts older than the window are removed on startup and after every
    /// rotation, so a quiet deployment that never reaches MaxBytes still stops retaining
    /// logs past the configured window.
    /// </summary>
    private void DeleteExpiredFiles()
    {
        var cutoff = _clock().Date.AddDays(-_retentionDays);
        try
        {
            var candidates = new List<(string Path, DateTime Date, int Part)>();
            // 解析不出日期的文件（如按天切分之前的 api-json.log）：改按最后写入时间判定，
            // 否则它们既看不出日期、又永远进不了清理名单，会一直占着磁盘。
            var undated = new List<(string Path, DateTime Time)>();
            foreach (var file in Directory.EnumerateFiles(_directory, FilePattern))
            {
                // 当前文件分属另一种命运：它正在被写，永不清理
                if (string.Equals(file, _currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                if (TryParseLogFileName(System.IO.Path.GetFileName(file), _prefix, out var date, out var part))
                {
                    candidates.Add((file, date, part));
                }
                else
                {
                    undated.Add((file, File.GetLastWriteTime(file)));
                }
            }

            foreach (var expired in candidates.Where(item => item.Date < cutoff))
            {
                File.Delete(expired.Path);
            }
            foreach (var expired in undated.Where(item => item.Time < cutoff))
            {
                File.Delete(expired.Path);
            }

            var keep = candidates
                .Where(item => item.Date >= cutoff)
                .OrderByDescending(item => item.Date)
                .ThenByDescending(item => item.Part)
                .ToList();
            // 当前文件不在候选里，独占一份额度
            foreach (var excess in keep.Skip(Math.Max(0, _maxFiles - 1)))
            {
                File.Delete(excess.Path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响写入：下次轮转或下次启动再试。
        }
    }

    /// <summary>解析 <c>api-20261015.log</c> / <c>api-20261015-2.log</c> 的日期与当日序号，供排序与保留判定。</summary>
    internal static bool TryParseLogFileName(string fileName, string prefix, out DateTime date, out int part)
    {
        date = default;
        part = 1;
        if (!fileName.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)) return false;
        if (!fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return false;
        var core = fileName[(prefix.Length + 1)..^4];
        var segments = core.Split('-');
        if (!DateTime.TryParseExact(segments[0], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            return false;
        }
        if (segments.Length == 1) return true;
        return segments.Length == 2 && int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out part);
    }

    private sealed class FileLogger(JsonFileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => !provider.IsDisabled && logLevel >= provider._minimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var values = state as IEnumerable<KeyValuePair<string, object?>>;
            var pairs = values?.ToList() ?? [];
            provider.Write(categoryName, logLevel, eventId, formatter(state, exception), pairs, exception);
        }
    }
}
