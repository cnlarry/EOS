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

    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly int _retentionDays;
    private readonly LogLevel _minimumLevel;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private FileStream? _stream;
    private long _currentBytes;

    /// <summary>
    /// Directory unusable (read-only or inaccessible): the provider silently degrades to
    /// "enabled but never writes" instead of throwing at startup — a broken log directory
    /// must not prevent the process from serving requests.
    /// </summary>
    public bool IsDisabled { get; private set; }

    /// <summary>Path this provider writes to (for diagnostics/health reporting).</summary>
    public string Path => _path;

    public JsonFileLoggerProvider(
        string path,
        long maxBytes,
        int maxFiles,
        LogLevel minimumLevel = LogLevel.Warning,
        int retentionDays = DefaultRetentionDays)
    {
        _path = path;
        _maxBytes = maxBytes > 0 ? maxBytes : 50L * 1024 * 1024;
        _maxFiles = Math.Max(1, maxFiles);
        _retentionDays = Math.Max(1, retentionDays);
        _minimumLevel = minimumLevel;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            OpenStream();
            DeleteExpiredRotatedFiles();
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
        record["ts"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz");
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

    private void OpenStream()
    {
        _stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _currentBytes = _stream.Length;
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        if (_currentBytes + incomingBytes <= _maxBytes)
        {
            return;
        }
        _stream?.Dispose();
        for (var i = _maxFiles - 1; i >= 1; i--)
        {
            var source = $"{_path}.{i - 1}";
            var target = $"{_path}.{i}";
            if (File.Exists(source))
            {
                File.Copy(source, target, overwrite: true);
                File.Delete(source);
            }
        }
        if (File.Exists(_path))
        {
            File.Copy(_path, $"{_path}.1", overwrite: true);
            File.Delete(_path);
        }
        OpenStream();
        DeleteExpiredRotatedFiles();
    }

    /// <summary>
    /// Age gate: rotated parts older than the window are removed on startup and after every
    /// rotation, so a quiet deployment that never reaches MaxBytes still stops retaining
    /// logs past the configured window.
    /// </summary>
    private void DeleteExpiredRotatedFiles()
    {
        var cutoff = DateTime.Now.AddDays(-_retentionDays);
        try
        {
            // 轮转副本与"当前文件"分属两种命运：副本按年龄清，当前文件永不被本方法删除
            // （否则会在没有任何新日志时把正在使用的文件删掉）。
            foreach (var candidate in Directory.EnumerateFiles(
                         System.IO.Path.GetDirectoryName(_path) ?? ".",
                         System.IO.Path.GetFileName(_path) + ".*"))
            {
                if (File.GetLastWriteTime(candidate) >= cutoff)
                {
                    continue;
                }
                File.Delete(candidate);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响写入：下次轮转或下次启动再试。
        }
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
