using EOS.API.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

public sealed class ImRateLimiterTests
{
    [Fact]
    public void TryConsume_AllowsUpToLimitPerMinute()
    {
        var limiter = new ImRateLimiter(BuildConfig(maxPerMinute: 3));

        Assert.True(limiter.TryConsume("u1"));
        Assert.True(limiter.TryConsume("u1"));
        Assert.True(limiter.TryConsume("u1"));
        Assert.False(limiter.TryConsume("u1"));
    }

    [Fact]
    public void TryConsume_TracksUsersIndependently()
    {
        var limiter = new ImRateLimiter(BuildConfig(maxPerMinute: 1));

        Assert.True(limiter.TryConsume("u1"));
        Assert.False(limiter.TryConsume("u1"));
        Assert.True(limiter.TryConsume("u2"));
    }

    [Fact]
    public void Reset_ClearsCounter()
    {
        var limiter = new ImRateLimiter(BuildConfig(maxPerMinute: 1));
        Assert.True(limiter.TryConsume("u1"));
        Assert.False(limiter.TryConsume("u1"));

        limiter.Reset("u1");

        Assert.True(limiter.TryConsume("u1"));
    }

    [Fact]
    public void TryConsume_NormalizesUserId()
    {
        var limiter = new ImRateLimiter(BuildConfig(maxPerMinute: 1));
        Assert.True(limiter.TryConsume(" admin "));
        Assert.False(limiter.TryConsume("admin"));
    }

    private static IConfiguration BuildConfig(int maxPerMinute)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Im:RateLimit:MaxMessagesPerMinute"] = maxPerMinute.ToString(),
            })
            .Build();
}
