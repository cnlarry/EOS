using EOS.API.Telemetry;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 运行日志按天切分：一个文件对应一天（单日体量超上限时再带当日序号），
/// 于是单个文件不会无上限地涨，文件名自带日期、界面可按天切换。
/// 切分与保留都走注入的时钟，测试不必真等一天。
/// </summary>
public sealed class JsonFileLoggerProviderTests
{
    [Fact]
    public void Write_CreatesFileNamedByDay()
    {
        var now = new DateTime(2026, 10, 15, 10, 0, 0);
        var directory = CreateTempDirectory();
        using var provider = new JsonFileLoggerProvider(directory, maxBytes: 1024 * 1024, maxFiles: 30, clock: () => now);

        provider.Write("C", LogLevel.Warning, default, "hello", [], null);

        var expected = Path.Combine(directory, "api-20261015.log");
        Assert.True(File.Exists(expected), "当天日志应落在 api-20261015.log");
        Assert.Equal(expected, provider.Path);
    }

    [Fact]
    public void Write_OnNextDay_SwitchesToANewFile()
    {
        var now = new DateTime(2026, 10, 15, 10, 0, 0);
        var directory = CreateTempDirectory();
        using var provider = new JsonFileLoggerProvider(directory, maxBytes: 1024 * 1024, maxFiles: 30, clock: () => now);

        provider.Write("C", LogLevel.Warning, default, "day one", [], null);
        now = now.AddDays(1);
        provider.Write("C", LogLevel.Warning, default, "day two", [], null);

        Assert.True(File.Exists(Path.Combine(directory, "api-20261015.log")));
        Assert.True(File.Exists(Path.Combine(directory, "api-20261016.log")));
        Assert.Contains("day two", ReadAllTextShared(Path.Combine(directory, "api-20261016.log")));
    }

    [Fact]
    public void Write_WhenSingleDayExceedsMaxBytes_ContinuesWithSequenceFile()
    {
        var now = new DateTime(2026, 10, 15, 10, 0, 0);
        var directory = CreateTempDirectory();
        // 阈值故意极小：第二条必然超限，且仍在同一天——验证的是体积兜底而不是跨天
        using var provider = new JsonFileLoggerProvider(directory, maxBytes: 60, maxFiles: 30, clock: () => now);

        provider.Write("C", LogLevel.Warning, default, "first line that is definitely longer than sixty bytes", [], null);
        provider.Write("C", LogLevel.Warning, default, "second line that is also longer than sixty bytes", [], null);

        Assert.True(File.Exists(Path.Combine(directory, "api-20261015.log")));
        Assert.True(File.Exists(Path.Combine(directory, "api-20261015-2.log")));
    }

    [Fact]
    public void TryParseLogFileName_ReadsDateAndSequence()
    {
        Assert.True(JsonFileLoggerProvider.TryParseLogFileName("api-20261015.log", "api", out var date, out var part));
        Assert.Equal(new DateTime(2026, 10, 15), date);
        Assert.Equal(1, part);

        Assert.True(JsonFileLoggerProvider.TryParseLogFileName("api-20261015-2.log", "api", out var secondDate, out var secondPart));
        Assert.Equal(new DateTime(2026, 10, 15), secondDate);
        Assert.Equal(2, secondPart);

        // 旧命名（不含日期）必须解析不出来：否则保留清理会把它当未知文件永久放过
        Assert.False(JsonFileLoggerProvider.TryParseLogFileName("api-json.log", "api", out _, out _));
    }

    [Fact]
    public void Construction_RemovesLegacyNamedFile_ThatHasNoDateInName()
    {
        var now = new DateTime(2026, 10, 15, 10, 0, 0);
        var directory = CreateTempDirectory();
        // 按天切分之前的旧命名：名字里没有日期，只能靠写入时间判定是否过期
        var legacy = Path.Combine(directory, "api-json.log");
        File.WriteAllText(legacy, "old content");
        File.SetLastWriteTime(legacy, now.AddDays(-60));

        using var provider = new JsonFileLoggerProvider(
            directory, maxBytes: 1024 * 1024, maxFiles: 30, retentionDays: 14, clock: () => now);

        Assert.False(File.Exists(legacy), "旧命名文件超期后必须被清理，否则它永远进不了清理名单、一直占着磁盘");
    }

    /// <summary>provider 正持有写流，读取必须用同样的共享模式（File.ReadAllText 会因要求独占写而失败）。</summary>
    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eos-logprovider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
