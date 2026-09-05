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
}
