using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace EOS.API.Telemetry;

/// <summary>
/// JSONL 结构化日志文件提供程序（ADR-005 §5.1/§5.4）：把结构化日志逐行写入滚动文件
/// （默认 logs/api-json.log），供 LogQueryService / 日志 MCP 检索与排障。
/// 每行一个 JSON 对象：ts/level/category/eventId/event/message/fields（结构化状态）/exception。
/// </summary>
public sealed class JsonFileLoggerProvider : ILoggerProvider
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly object _gate = new();
    private readonly JsonSerializerOptions _jsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private FileStream? _stream;
    private long _currentBytes;

    public JsonFileLoggerProvider(string path, long maxBytes, int maxFiles)
    {
        _path = path;
        _maxBytes = maxBytes;
        _maxFiles = Math.Max(1, maxFiles);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        OpenStream();
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
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in state)
        {
            if (key == "{OriginalFormat}")
            {
                continue;
            }
            fields[key] = value;
        }
        var record = new Dictionary<string, object?>
        {
            ["ts"] = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"),
            ["level"] = level.ToString(),
            ["category"] = categoryName,
            ["eventId"] = eventId.Id,
            ["event"] = fields.GetValueOrDefault("Event"),
            ["message"] = message,
            ["fields"] = fields.Count > 0 ? fields : null,
            ["exception"] = exception?.ToString(),
        };
        var line = JsonSerializer.Serialize(record, _jsonOptions) + Environment.NewLine;
        var bytes = Encoding.UTF8.GetBytes(line);
        lock (_gate)
        {
            RotateIfNeeded(bytes.Length);
            _stream!.Write(bytes, 0, bytes.Length);
            _stream.Flush();
            _currentBytes += bytes.Length;
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
    }

    private sealed class FileLogger(JsonFileLoggerProvider provider, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

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
