using System;
using VoiceChatbot;
using Xunit;

public class LoginAttemptLimiterTests
{
    private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private LoginAttemptLimiter Create() => new(clock: () => _now);

    [Fact]
    public void DefaultsAreFiveAttemptsInTenMinutesThenTenMinuteLockout()
    {
        var limiter = new LoginAttemptLimiter();

        Assert.Equal(5, limiter.MaxFailures);
        Assert.Equal(TimeSpan.FromMinutes(10), limiter.Window);
        Assert.Equal(TimeSpan.FromMinutes(10), limiter.LockoutDuration);
    }

    [Fact]
    public void FifthFailureStartsLockoutOnce()
    {
        var limiter = Create();

        for (var i = 1; i <= 4; i++)
        {
            var result = limiter.RecordFailure("10.0.0.5");
            Assert.False(result.LockedOut);
            Assert.Equal(5 - i, result.AttemptsLeft);
            _now += TimeSpan.FromSeconds(30);
        }

        var fifth = limiter.RecordFailure("10.0.0.5");
        Assert.True(fifth.LockedOut);
        Assert.True(fifth.LockoutStarted);
        Assert.Equal(0, fifth.AttemptsLeft);
        Assert.Equal(TimeSpan.FromMinutes(10), fifth.RetryAfter);

        Assert.True(limiter.IsLockedOut("10.0.0.5", out var retry));
        Assert.Equal(TimeSpan.FromMinutes(10), retry);

        // Further failures while locked out are refused but neither restart nor extend the lockout.
        _now += TimeSpan.FromMinutes(4);
        var during = limiter.RecordFailure("10.0.0.5");
        Assert.True(during.LockedOut);
        Assert.False(during.LockoutStarted);
        Assert.Equal(TimeSpan.FromMinutes(6), during.RetryAfter);
    }

    [Fact]
    public void LockoutEndsAfterTenMinutesWithAFreshCounter()
    {
        var limiter = Create();
        for (var i = 0; i < 5; i++)
            limiter.RecordFailure("phone");

        _now += TimeSpan.FromMinutes(10) - TimeSpan.FromSeconds(1);
        Assert.True(limiter.IsLockedOut("phone", out var retry));
        Assert.Equal(TimeSpan.FromSeconds(1), retry);

        _now += TimeSpan.FromSeconds(1);
        Assert.False(limiter.IsLockedOut("phone", out retry));
        Assert.Equal(TimeSpan.Zero, retry);

        var next = limiter.RecordFailure("phone");
        Assert.False(next.LockedOut);
        Assert.Equal(4, next.AttemptsLeft);
    }

    [Fact]
    public void FailuresOlderThanTheWindowDoNotCount()
    {
        var limiter = Create();
        for (var i = 0; i < 4; i++)
        {
            limiter.RecordFailure("phone");
            _now += TimeSpan.FromMinutes(3);
        }

        // Failures at 0, 3, 6 and 9 minutes; at 12 minutes the first is older than 10 minutes,
        // so this is the 4th failure in the window, not the 5th.
        var result = limiter.RecordFailure("phone");
        Assert.False(result.LockedOut);
        Assert.Equal(1, result.AttemptsLeft);

        // Failures at 3, 6, 9 and 12 minutes are still in the window at 12.5 minutes.
        _now += TimeSpan.FromSeconds(30);
        Assert.True(limiter.RecordFailure("phone").LockedOut);
    }

    [Fact]
    public void FailuresExactlyAtTheWindowEdgeHaveExpired()
    {
        var limiter = Create();
        for (var i = 0; i < 4; i++)
            limiter.RecordFailure("phone");

        _now += TimeSpan.FromMinutes(10);
        var result = limiter.RecordFailure("phone");
        Assert.False(result.LockedOut);
        Assert.Equal(4, result.AttemptsLeft);
    }

    [Fact]
    public void SuccessResetsTheCounter()
    {
        var limiter = Create();
        for (var i = 0; i < 4; i++)
            limiter.RecordFailure("phone");

        limiter.RecordSuccess("phone");

        var result = limiter.RecordFailure("phone");
        Assert.False(result.LockedOut);
        Assert.Equal(4, result.AttemptsLeft);
    }

    [Fact]
    public void ClientsAreCountedSeparately()
    {
        var limiter = Create();
        for (var i = 0; i < 5; i++)
            limiter.RecordFailure("10.0.0.5");

        Assert.True(limiter.IsLockedOut("10.0.0.5", out _));
        Assert.False(limiter.IsLockedOut("10.0.0.6", out _));
        Assert.Equal(4, limiter.RecordFailure("10.0.0.6").AttemptsLeft);
    }

    [Fact]
    public void BlankClientsShareOneBucket()
    {
        var limiter = Create();
        limiter.RecordFailure(null);
        limiter.RecordFailure("");

        Assert.Equal(2, limiter.RecordFailure("  ").AttemptsLeft);
    }

    [Fact]
    public void ClearForgetsLockouts()
    {
        var limiter = Create();
        for (var i = 0; i < 5; i++)
            limiter.RecordFailure("phone");

        limiter.Clear();

        Assert.False(limiter.IsLockedOut("phone", out _));
        Assert.Equal(0, limiter.TrackedCount);
    }

    [Fact]
    public void ExpiredClientsArePruned()
    {
        var limiter = Create();
        for (var i = 0; i < LoginAttemptLimiter.PruneThreshold; i++)
            limiter.RecordFailure($"10.1.{i / 256}.{i % 256}");
        Assert.Equal(LoginAttemptLimiter.PruneThreshold, limiter.TrackedCount);

        _now += TimeSpan.FromMinutes(11);
        limiter.RecordFailure("10.9.9.9");

        Assert.Equal(1, limiter.TrackedCount);
    }

    [Fact]
    public void CustomLimitsAreUsed()
    {
        var limiter = new LoginAttemptLimiter(2, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), () => _now);

        Assert.False(limiter.RecordFailure("phone").LockedOut);
        var second = limiter.RecordFailure("phone");
        Assert.True(second.LockedOut);
        Assert.Equal(TimeSpan.FromSeconds(30), second.RetryAfter);
    }
}
