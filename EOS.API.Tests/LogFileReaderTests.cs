using EOS.API.Features.Diagnostics;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 运行日志读取与过滤：日志管理页与诊断包都走同一段解析逻辑，
/// 这里钉住"能按级别/关键字/报障编号筛"、"坏行不炸整次查询"、"take 上限生效"。
/// 纯函数入口（FilterLines）不碰磁盘，因此不依赖真实日志文件。
/// </summary>
public sealed class LogFileReaderTests
{
    private const string WarningLine = """
        {"ts":"2026-09-29T10:00:00.000+08:00","level":"Warning","category":"EOS.API.Middleware.RequestLoggingMiddleware","eventId":0,"event":"http_request","message":"HTTP 请求结束 http_request /api/v1/x -> 200 耗时 1200ms","fields":{"ElapsedMs":1200.5,"CorrelationId":"corr-1","ModuleId":1406,"ErrorCode":null}}
        """;

    private const string ErrorLine = """
        {"ts":"2026-09-29T10:01:00.000+08:00","level":"Error","category":"EOS.API.Middleware.GlobalExceptionHandler","eventId":0,"event":null,"message":"未处理异常 SqlException","fields":{"CorrelationId":"corr-2","ErrorCode":"INTERNAL_ERROR"},"exception":"System.Exception: boom"}
        """;

    private static LogQuery Query(int take = 100, string? level = null, string? keyword = null, string? correlation = null, string? @event = null) =>
        new(take, level, keyword, correlation, @event, null);

    [Fact]
    public void 按级别过滤_只返回命中级别()
    {
        var entries = LogFileReader.FilterLines([WarningLine, ErrorLine], Query(level: "Error"));
        var entry = Assert.Single(entries);
        Assert.Equal("Error", entry.Level);
        Assert.Equal("INTERNAL_ERROR", entry.ErrorCode);
    }

    [Fact]
    public void 按关键字过滤_命中消息或异常()
    {
        Assert.Single(LogFileReader.FilterLines([WarningLine, ErrorLine], Query(keyword: "耗时")));
        Assert.Single(LogFileReader.FilterLines([WarningLine, ErrorLine], Query(keyword: "boom")));
        Assert.Empty(LogFileReader.FilterLines([WarningLine, ErrorLine], Query(keyword: "不存在的内容")));
    }

    [Fact]
    public void 按报障编号过滤_取到该请求的日志()
    {
        var entry = Assert.Single(LogFileReader.FilterLines([WarningLine, ErrorLine], Query(correlation: "corr-2")));
        Assert.Equal("corr-2", entry.CorrelationId);
        Assert.Contains("未处理异常", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void 坏行与空行被跳过_不影响整次查询()
    {
        var entries = LogFileReader.FilterLines(["", "   ", "{ not json", WarningLine, ErrorLine], Query());
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void take上限生效_按给出顺序截断()
    {
        var entries = LogFileReader.FilterLines([WarningLine, ErrorLine, WarningLine], Query(take: 2));
        Assert.Equal(2, entries.Count);
        Assert.Equal("Warning", entries[0].Level);
    }

    [Fact]
    public void 级别清单来自服务端_前端下拉不再各写一份()
    {
        Assert.Contains("Warning", LogFileReader.KnownLevels);
        Assert.Contains("Error", LogFileReader.KnownLevels);
    }
}
