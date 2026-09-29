using EOS.API.Telemetry;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// File log level gate: routine API requests stay on the console pipeline,
/// only Warning and above reach the JSONL file (troubleshooting source).
/// </summary>
public sealed class JsonFileLoggerProviderTests
{
    [Fact]
    public void FileLog_KeepsWarningAndAboveOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eos-logtest-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "test.log");
        var provider = new JsonFileLoggerProvider(path, 1024 * 1024, 2, LogLevel.Warning);
        try
        {
            var logger = provider.CreateLogger("test");
            logger.LogInformation("routine request");
            logger.LogWarning("slow request");
            logger.LogError("failed request");
        }
        finally
        {
            provider.Dispose();
        }
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("routine request", text);
        Assert.Contains("slow request", text);
        Assert.Contains("failed request", text);
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ClientError_PayloadValidation()
    {
        var controller = new EOS.API.Controllers.ClientErrorController(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EOS.API.Controllers.ClientErrorController>.Instance);
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestResult>(
            controller.Report(new EOS.API.Controllers.ClientErrorReport(null, null, null, null, null)));
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestResult>(
            controller.Report(new EOS.API.Controllers.ClientErrorReport("error", new string('x', 1001), null, null, null)));
    }

    /// <summary>
    /// 证据落盘的隐私底线：凭据形态的内容必须在序列化前被打码，普通业务文本不得被改动。
    /// LogRedactor 自身的模式清单由 LogRedactorTests 覆盖，这里钉的是"管道确实调了它"。
    /// </summary>
    [Fact]
    public void FileLog_RedactsCredentialsAndKeepsPlainText()
    {
        var state = new List<KeyValuePair<string, object?>>
        {
            new("Event", "http_request"),
            new("ConnectionString", "Server=db01;Database=EOS.ERP;User ID=sa;Password=P@ssw0rd!;"),
        };
        var record = JsonFileLoggerProvider.BuildRecord(
            "test", LogLevel.Warning, new EventId(1),
            "连接失败 Password=P@ssw0rd!，业务文本应当原样保留",
            state,
            new InvalidOperationException("login failed token=abc.def.ghi"));

        var message = (string)record["message"]!;
        Assert.DoesNotContain("P@ssw0rd!", message);
        Assert.Contains("***", message);
        Assert.Contains("业务文本应当原样保留", message);

        var fields = Assert.IsType<Dictionary<string, object?>>(record["fields"]);
        var connection = (string)fields["ConnectionString"]!;
        Assert.DoesNotContain("P@ssw0rd!", connection);
        Assert.DoesNotContain("User ID=sa", connection);

        var exception = (string)record["exception"]!;
        Assert.DoesNotContain("abc.def.ghi", exception);
    }

    /// <summary>证据落盘走的是同一个序列化出口：文件中不得出现原文。</summary>
    [Fact]
    public void FileLog_WrittenLineIsRedacted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eos-logtest-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "test.log");
        var provider = new JsonFileLoggerProvider(path, 1024 * 1024, 2, LogLevel.Warning);
        try
        {
            provider.CreateLogger("test").LogWarning("凭据 Password=topsecret 不应落盘");
        }
        finally
        {
            provider.Dispose();
        }
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("topsecret", text);
        Assert.Contains("***", text);
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>超期轮转副本在启动时清理；当前文件保留（14 天窗口）。</summary>
    [Fact]
    public void FileLog_StartupDropsExpiredRotatedPartsOnly()
    {
        var dir = Path.Combine(Path.GetTempPath(), "eos-logtest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "test.log");
        var expiredPart = path + ".1";
        File.WriteAllText(expiredPart, "old");
        File.SetLastWriteTime(expiredPart, DateTime.Now.AddDays(-30));
        File.WriteAllText(path, "current");
        var provider = new JsonFileLoggerProvider(path, 1024 * 1024, 3, LogLevel.Warning, retentionDays: 14);
        provider.Dispose();
        Assert.False(File.Exists(expiredPart));
        Assert.True(File.Exists(path));
        Directory.Delete(dir, recursive: true);
    }

    /// <summary>日志目录不可用时降级为"不写"而不是抛异常——日志坏了不能把服务打挂。</summary>
    [Fact]
    public void FileLog_DegradesWhenDirectoryUnusable()
    {
        // 用一个已存在的**文件**充当目录段：CreateDirectory 必然失败。
        var blocker = Path.Combine(Path.GetTempPath(), "eos-logtest-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "not a directory");
        try
        {
            var provider = new JsonFileLoggerProvider(
                Path.Combine(blocker, "logs", "test.log"), 1024 * 1024, 2, LogLevel.Warning);
            Assert.True(provider.IsDisabled);
            provider.CreateLogger("test").LogError("must not throw");
            provider.Dispose();
        }
        finally
        {
            File.Delete(blocker);
        }
    }
}
