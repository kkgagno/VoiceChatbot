using System;
using System.Linq;
using VoiceChatbot;
using Xunit;

public class PhoneRemoteAuthTests
{
    private DateTimeOffset _now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    private PhoneRemoteAuthenticator Create(string pin = "482913")
    {
        var auth = new PhoneRemoteAuthenticator(new LoginAttemptLimiter(clock: () => _now));
        auth.SetPin(pin);
        return auth;
    }

    [Fact]
    public void GeneratedPinsAreSixDigits()
    {
        var pins = Enumerable.Range(0, 200).Select(_ => PhoneRemotePin.Generate()).ToList();

        Assert.All(pins, pin =>
        {
            Assert.Equal(6, pin.Length);
            Assert.True(pin.All(char.IsAsciiDigit));
            Assert.True(PhoneRemotePin.IsValid(pin));
            Assert.False(PhoneRemotePin.IsWeak(pin));
        });
        Assert.True(pins.Distinct().Count() > 150);
    }

    [Theory]
    [InlineData("482913", true)]
    [InlineData("  4829 ", true)]
    [InlineData("Tr0ub4dor&3", true)]
    [InlineData("123", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("12 34", false)]
    [InlineData("1234é", false)]
    [InlineData("1234\t5", false)]
    public void IsValid(string? pin, bool expected) => Assert.Equal(expected, PhoneRemotePin.IsValid(pin));

    [Fact]
    public void IsValidRejectsVeryLongPins()
    {
        Assert.True(PhoneRemotePin.IsValid(new string('7', PhoneRemotePin.MaxLength)));
        Assert.False(PhoneRemotePin.IsValid(new string('7', PhoneRemotePin.MaxLength + 1)));
    }

    [Theory]
    [InlineData("1234", true)]
    [InlineData("12345", true)]
    [InlineData("123456", false)]
    [InlineData("123", false)]
    public void IsWeak(string pin, bool expected) => Assert.Equal(expected, PhoneRemotePin.IsWeak(pin));

    [Theory]
    [InlineData("482913", "482913", true)]
    [InlineData(" 482913 ", "482913", true)]
    [InlineData("482914", "482913", false)]
    [InlineData("48291", "482913", false)]
    [InlineData("4829130", "482913", false)]
    [InlineData("", "482913", false)]
    [InlineData(null, "482913", false)]
    [InlineData("abcd", "ABCD", false)]
    [InlineData("", "", false)]
    [InlineData(null, null, false)]
    public void Matches(string? presented, string? expected, bool result) =>
        Assert.Equal(result, PhoneRemotePin.Matches(presented, expected));

    [Fact]
    public void RightPinIsAuthorized()
    {
        var result = Create().Authenticate("10.0.0.5", "482913");

        Assert.True(result.IsAuthorized);
        Assert.Equal(200, result.HttpStatus);
    }

    [Fact]
    public void MissingPinAsksForItWithoutCountingAnAttempt()
    {
        var auth = Create();
        for (var i = 0; i < 10; i++)
        {
            var result = auth.Authenticate("10.0.0.5", i % 2 == 0 ? null : " ");
            Assert.Equal(PhoneRemoteAuthStatus.PinRequired, result.Status);
            Assert.Equal(401, result.HttpStatus);
            Assert.Contains("PIN", result.Message);
        }

        Assert.Equal(4, auth.Authenticate("10.0.0.5", "000000").AttemptsLeft);
    }

    [Fact]
    public void NoConfiguredPinRefusesEverything()
    {
        var auth = new PhoneRemoteAuthenticator();

        Assert.False(auth.Authenticate("10.0.0.5", "").IsAuthorized);
        Assert.False(auth.Authenticate("10.0.0.5", "anything").IsAuthorized);
    }

    [Fact]
    public void WrongPinsCountDownThenLockOutWith429()
    {
        var auth = Create();

        var first = auth.Authenticate("10.0.0.5", "111111");
        Assert.Equal(PhoneRemoteAuthStatus.WrongPin, first.Status);
        Assert.Equal(401, first.HttpStatus);
        Assert.Equal(4, first.AttemptsLeft);
        Assert.Equal("Wrong PIN. 4 attempts left before this device is locked out for 10 minutes.", first.Message);

        auth.Authenticate("10.0.0.5", "222222");
        auth.Authenticate("10.0.0.5", "333333");
        var fourth = auth.Authenticate("10.0.0.5", "444444");
        Assert.Equal("Wrong PIN. 1 attempt left before this device is locked out for 10 minutes.", fourth.Message);

        var fifth = auth.Authenticate("10.0.0.5", "555555");
        Assert.Equal(PhoneRemoteAuthStatus.LockedOut, fifth.Status);
        Assert.True(fifth.LockoutStarted);
        Assert.Equal(429, fifth.HttpStatus);
        Assert.Equal(600, fifth.RetryAfterSeconds);
        Assert.Contains("Try again in 10 minutes", fifth.Message);

        // Even the right PIN is refused during the lockout, and other devices are unaffected.
        _now += TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(30);
        var locked = auth.Authenticate("10.0.0.5", "482913");
        Assert.Equal(PhoneRemoteAuthStatus.LockedOut, locked.Status);
        Assert.False(locked.LockoutStarted);
        Assert.Equal(30, locked.RetryAfterSeconds);
        Assert.Contains("Try again in 1 minute.", locked.Message);
        Assert.True(auth.Authenticate("10.0.0.6", "482913").IsAuthorized);

        _now += TimeSpan.FromSeconds(30);
        Assert.True(auth.Authenticate("10.0.0.5", "482913").IsAuthorized);
    }

    [Fact]
    public void MissingPinDuringLockoutStillReports429()
    {
        var auth = Create();
        for (var i = 0; i < 5; i++)
            auth.Authenticate("10.0.0.5", "000000");

        Assert.Equal(429, auth.Authenticate("10.0.0.5", null).HttpStatus);
    }

    [Fact]
    public void SuccessResetsTheCount()
    {
        var auth = Create();
        for (var i = 0; i < 4; i++)
            auth.Authenticate("10.0.0.5", "000000");

        Assert.True(auth.Authenticate("10.0.0.5", "482913").IsAuthorized);
        Assert.Equal(4, auth.Authenticate("10.0.0.5", "000000").AttemptsLeft);
    }

    [Fact]
    public void ChangingThePinClearsLockouts()
    {
        var auth = Create();
        for (var i = 0; i < 5; i++)
            auth.Authenticate("10.0.0.5", "000000");

        Assert.False(auth.SetPin(" 482913 "));
        Assert.Equal(429, auth.Authenticate("10.0.0.5", "482913").HttpStatus);

        Assert.True(auth.SetPin("777111"));
        Assert.Equal("777111", auth.Pin);
        Assert.False(auth.Authenticate("10.0.0.5", "482913").IsAuthorized);
        Assert.True(auth.Authenticate("10.0.0.5", "777111").IsAuthorized);
    }

    [Theory]
    [InlineData(0, "1 minute")]
    [InlineData(1, "1 minute")]
    [InlineData(60, "1 minute")]
    [InlineData(61, "2 minutes")]
    [InlineData(600, "10 minutes")]
    public void DescribeMinutes(int seconds, string expected) =>
        Assert.Equal(expected, PhoneRemoteAuthenticator.DescribeMinutes(TimeSpan.FromSeconds(seconds)));
}
