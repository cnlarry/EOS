using EOS.API.Features.Assistant;
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
    public void Estimate_IsConservative_PerCharClass()
    {
        // 中文按 1 token/字（上界），ASCII 按 0.35 token/字（保守上界，留出余量）
        Assert.Equal(1200, ChatService.EstimateTokens(new string('中', 1200)));
        Assert.Equal(350, ChatService.EstimateTokens(new string('a', 1000)));
        Assert.Equal(0, ChatService.EstimateTokens(string.Empty));

        var (prompt, completion) = ChatService.EstimateUsage(1200, new string('中', 300));

        Assert.Equal(1200, prompt);
        Assert.Equal(300, completion);
        // 空回复也要算 1 个 token：0 会让"模型什么都没返回"看起来像没花钱
        Assert.Equal(1, ChatService.EstimateUsage(0, string.Empty).CompletionTokens);
    }

    [Fact]
    public void ToMicroYuan_Ceilings()
    {
        Assert.Equal(50_000, ChatService.ToMicroYuan(0.05));
        Assert.Equal(1, ChatService.ToMicroYuan(0.0000001));
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
