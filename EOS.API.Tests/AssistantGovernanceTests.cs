using EOS.API.Features.Assistant.Governance;
using Xunit;

namespace EOS.API.Tests;

public sealed class AssistantGovernanceTests
{
    [Fact]
    public void Cost_CalculatesPerMillionPricing()
    {
        var options = new AssistantCostOptions { InputPerMillionYuan = 1, OutputPerMillionYuan = 2 };

        Assert.Equal(1.5, AssistantCost.Calculate(1_000_000, 250_000, options));
        Assert.Equal(0, AssistantCost.Calculate(0, 0, options));
    }

    [Fact]
    public void Breaker_BlocksAfterMaxFailures_AndRecoversAfterCooldown()
    {
        var now = DateTimeOffset.UtcNow;
        var breaker = new FailureBreaker(() => now, maxFailures: 3, cooldownSeconds: 60);

        Assert.False(breaker.IsBlocked("u1"));
        breaker.RecordFailure("u1");
        breaker.RecordFailure("u1");
        Assert.False(breaker.IsBlocked("u1"));
        breaker.RecordFailure("u1");
        Assert.True(breaker.IsBlocked("u1"));

        now = now.AddSeconds(61);
        Assert.False(breaker.IsBlocked("u1"));
    }

    [Fact]
    public void Breaker_SuccessResets_CountsPerUser()
    {
        var now = DateTimeOffset.UtcNow;
        var breaker = new FailureBreaker(() => now, maxFailures: 2, cooldownSeconds: 60);

        breaker.RecordFailure("u1");
        breaker.RecordSuccess("u1");
        breaker.RecordFailure("u1");
        Assert.False(breaker.IsBlocked("u1"));

        breaker.RecordFailure("u2");
        breaker.RecordFailure("u2");
        Assert.True(breaker.IsBlocked("u2"));
        Assert.False(breaker.IsBlocked("u1"));
    }
}
