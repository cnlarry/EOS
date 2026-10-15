using System.Text;
using EOS.API.Features.Diagnostics;
using EOS.API.Telemetry;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 运行日志读取：日志管理（2313）靠它把文件内容读成表格，因此"读不到"必须能被发现，
/// 不能被当成"没有日志"。本组用例钉住两条不变量：
/// ① 写入流打开期间仍要读得到（共享模式不能与写入方冲突）；
/// ② 真的读不到时要上报 unavailable，而不是静默返回 0 条。
/// </summary>
public sealed class LogFileReaderTests
{
    [Fact]
    public void Read_WhenWriterHoldsTheFile_StillReturnsEntries()
    {
        var path = CreateTempLog(
            Line("Error", "first", "c1", 0),
            Line("Warning", "second", "c2", 1));

        // provider 构造即以 FileAccess.Write + FileShare.ReadWrite 打开写流并保持，
        // 与线上"日志 provider 正在写文件"完全一致——用 FileShare.Read 读取会在这里失败
        var provider = new JsonFileLoggerProvider(path, maxBytes: 1024 * 1024, maxFiles: 3);
        var reader = new LogFileReader(provider);

        var result = reader.Read(new LogQuery(10, null, null, null, null, null));

        Assert.Equal(2, result.Entries.Count);
        Assert.Empty(result.UnavailableFiles);
        // 文件内自后向前读：最新的排在最前
        Assert.Equal("second", result.Entries[0].Message);
        Assert.Equal("c2", result.Entries[0].CorrelationId);
    }

    [Fact]
    public void Read_WhenFileIsUnavailable_ReportsItInsteadOfPretendingEmpty()
    {
        var path = CreateTempLog(Line("Error", "only", "c1", 0));
        // 独占打开制造"读不到"：此时 Read 不得把它当成"没有日志"
        using var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var provider = new JsonFileLoggerProvider(path, maxBytes: 1024 * 1024, maxFiles: 3);
        var reader = new LogFileReader(provider);

        var result = reader.Read(new LogQuery(10, null, null, null, null, null));

        Assert.Empty(result.Entries);
        Assert.Contains("api-json.log", result.UnavailableFiles);
    }

    [Fact]
    public void FilterLines_AppliesLevelAndKeyword()
    {
        var lines = new[] { Line("Error", "boom happened", "c1", 0), Line("Warning", "slow request", "c2", 1) };

        var byLevel = LogFileReader.FilterLines(lines, new LogQuery(10, "Error", null, null, null, null));
        Assert.Single(byLevel);
        Assert.Equal("boom happened", byLevel[0].Message);

        var byKeyword = LogFileReader.FilterLines(lines, new LogQuery(10, null, "SLOW", null, null, null));
        Assert.Single(byKeyword);
        Assert.Equal("c2", byKeyword[0].CorrelationId);
    }

    // 拼接而非 string.Format / 插值：日志行本身就是 JSON，花括号是内容不是占位符，
    // 走 Format 就得把每一对都写成 {{ }}，漏一处就拼出非法 JSON。
    private static string Line(string level, string message, string correlationId, int minute) =>
        "{\"level\":\"" + level + "\""
        + ",\"category\":\"EOS.API.Middleware.RequestLoggingMiddleware\""
        + ",\"message\":\"" + message + "\""
        + ",\"fields\":{\"CorrelationId\":\"" + correlationId + "\",\"ModuleId\":2306}"
        + ",\"exception\":null"
        + ",\"ts\":\"2026-10-15T10:0" + minute + ":00+08:00\""
        + ",\"event\":\"http_request\"}";

    private static string CreateTempLog(params string[] lines)
    {
        var directory = Path.Combine(Path.GetTempPath(), "eos-logreader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "api-json.log");
        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
