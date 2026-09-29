using EOS.API.Features.Diagnostics;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 诊断包的配置摘要必须**只含应用自身的配置键名**，绝不含机器环境变量名。
/// 这条是实测踩出来的：早先按"排除几个敏感节"实现，结果把进程的全部环境变量名
/// （`DEEPSEEK_API_KEY` / `MSSQL_ERP_CONN` / `WINDOWS_PASSWORD` …）一起导进了诊断包——
/// 值没泄露，但键名本身不该外发，且与"排查这一版应用"无关。故改为白名单 + 本用例钉住。
/// </summary>
public sealed class DiagnosticsConfigDigestTests
{
    private static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information",
                ["Logging:File:Path"] = "logs/api-json.log",
                ["Logging:File:RetentionDays"] = "14",
                ["Attachment:StorageRoot"] = "",
                ["Attachment:MaxSizeBytes"] = "52428800",
                ["Assistant:BaseUrl"] = "https://example.invalid",
                ["ConnectionStrings:ErpDatabase"] = "Server=db01;Password=should-not-appear",
            })
            // 环境变量提供程序：真实运行时就是这么把机器环境读进来的
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DEEPSEEK_API_KEY"] = "sk-should-not-appear",
                ["MSSQL_ERP_CONN"] = "Server=db01;Password=should-not-appear",
                ["WINDOWS_PASSWORD"] = "should-not-appear",
            })
            .Build();

    /// <summary>白名单里的节才被登记，白名单外（这里故意不传 Logging）一律不出现。</summary>
    [Fact]
    public void 只登记白名单内的节()
    {
        var digest = LogsController.BuildConfigDigest(BuildConfiguration(), ["Attachment"]);
        Assert.Equal(["Attachment:MaxSizeBytes", "Attachment:StorageRoot"], digest.Keys.ToArray());
    }

    /// <summary>与生产同一份白名单：不得出现环境变量名、凭据节（`ConnectionStrings`/`Assistant` 不在其中）。</summary>
    [Fact]
    public void 生产白名单下不得出现环境变量名与凭据节()
    {
        var digest = LogsController.BuildConfigDigest(BuildConfiguration(), LogsController.ObservableSections);
        Assert.NotEmpty(digest);
        Assert.Contains("Logging:File:Path", digest.Keys);
        Assert.Contains("Attachment:MaxSizeBytes", digest.Keys);
        foreach (var key in digest.Keys)
        {
            Assert.DoesNotContain("API_KEY", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MSSQL", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WINDOWS", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ConnectionStrings", key, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Assistant", key, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void 摘要报的是是否已设置_而不是值()
    {
        var digest = LogsController.BuildConfigDigest(BuildConfiguration(), ["Logging", "Attachment"]);
        Assert.True(digest["Logging:File:Path"]);
        // 空串视为"未设置"：Attachment:StorageRoot 配成空表示走默认目录
        Assert.False(digest["Attachment:StorageRoot"]);
        Assert.All(digest.Values, value => Assert.IsType<bool>(value));
    }

    /// <summary>数组型配置折叠成一项并报项数：`EnabledModuleIds` 有 200+ 下标，逐项列出只会把包撑大。</summary>
    [Fact]
    public void 数组型配置折叠为一项并给出项数()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UnifiedFormEditor:EnabledModuleIds:0"] = "1201",
                ["UnifiedFormEditor:EnabledModuleIds:1"] = "1401",
                ["UnifiedFormEditor:EnabledModuleIds:2"] = "1406",
            })
            .Build();
        var digest = LogsController.BuildConfigDigest(configuration, ["UnifiedFormEditor"]);
        var key = Assert.Single(digest.Keys);
        Assert.Equal("UnifiedFormEditor:EnabledModuleIds（共 3 项）", key);
        Assert.True(digest[key]);
    }
}
