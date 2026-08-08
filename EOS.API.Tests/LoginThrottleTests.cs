using EOS.API.Security;
using Xunit;

namespace EOS.API.Tests;

public sealed class LoginThrottleTests
{
    [Fact]
    public void Lock_AfterMaxFailures_WithinWindow()
    {
        var (service, advance) = Create(maxFailures: 3);
        Assert.False(service.RecordFailure("u|ip"));
        Assert.False(service.RecordFailure("u|ip"));
        Assert.True(service.RecordFailure("u|ip"));
        Assert.True(service.TryGetLockoutRemaining("u|ip", out var remaining));
        Assert.True(remaining > TimeSpan.Zero);
    }

    [Fact]
    public void NoLock_BelowThreshold()
    {
        var (service, _) = Create(maxFailures: 5);
        service.RecordFailure("u|ip");
        service.RecordFailure("u|ip");
        Assert.False(service.TryGetLockoutRemaining("u|ip", out _));
    }

    [Fact]
    public void Reset_ClearsFailures()
    {
        var (service, _) = Create(maxFailures: 2);
        service.RecordFailure("u|ip");
        service.Reset("u|ip");
        Assert.False(service.RecordFailure("u|ip"));
        Assert.False(service.TryGetLockoutRemaining("u|ip", out _));
    }

    [Fact]
    public void Lockout_Expires_AfterLockoutPeriod()
    {
        var (service, advance) = Create(maxFailures: 2, lockout: TimeSpan.FromMinutes(5));
        service.RecordFailure("u|ip");
        service.RecordFailure("u|ip");
        Assert.True(service.TryGetLockoutRemaining("u|ip", out _));

        advance(TimeSpan.FromMinutes(6));
        Assert.False(service.TryGetLockoutRemaining("u|ip", out _));
    }

    [Fact]
    public void OldFailures_OutsideWindow_DoNotCount()
    {
        var (service, advance) = Create(maxFailures: 2, window: TimeSpan.FromMinutes(10));
        service.RecordFailure("u|ip");
        advance(TimeSpan.FromMinutes(11));
        Assert.False(service.RecordFailure("u|ip"));
        Assert.False(service.TryGetLockoutRemaining("u|ip", out _));
    }

    private static (LoginThrottleService Service, Action<TimeSpan> Advance) Create(
        int maxFailures = 5,
        TimeSpan? window = null,
        TimeSpan? lockout = null)
    {
        var current = DateTimeOffset.UtcNow;
        var options = new LoginThrottleOptions
        {
            MaxFailures = maxFailures,
            Window = window ?? TimeSpan.FromMinutes(10),
            Lockout = lockout ?? TimeSpan.FromMinutes(15),
        };
        var service = new LoginThrottleService(options, () => current);
        return (service, delta => current = current.Add(delta));
    }
}
