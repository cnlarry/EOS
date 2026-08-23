using EOS.API.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace EOS.API.Tests;

public sealed class ConfigurationHealthCheckTests
{
    private static IConfiguration Build(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public async Task Healthy_WhenRequiredConfigPresent()
    {
        var check = new ConfigurationHealthCheck(Build(new Dictionary<string, string?>
        {
            ["ConnectionStrings:ErpDatabase"] = "Server=localhost;Database=EOS.ERP",
            ["DataProtection:ApplicationName"] = "EOS.API",
        }));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Unhealthy_WhenConnectionStringMissing()
    {
        var check = new ConfigurationHealthCheck(Build(new Dictionary<string, string?>
        {
            ["DataProtection:ApplicationName"] = "EOS.API",
        }));

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("ConnectionStrings:ErpDatabase", result.Description);
    }
}
