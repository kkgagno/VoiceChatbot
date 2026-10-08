using System;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace VoiceChatbot;

/// <summary>Rules for the phone remote PIN, which is required whenever the remote runs.</summary>
public static class PhoneRemotePin
{
    public const int GeneratedLength = 6;
    public const int MinLength = 4;
    public const int MaxLength = 64;

    /// <summary>
    /// The browser localStorage key under which the remote's pages (/ and /transcribe) remember the PIN, so a PIN
    /// entered on one page works on the other. The main page in PhoneRemoteServer.BuildPhonePage spells it out.
    /// </summary>
    public const string BrowserStorageKey = "voicechatbot-remote-pin";

    /// <summary>A random 6-digit PIN from the cryptographic random number generator ("000000"-"999999").</summary>
    public static string Generate() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public static string Normalize(string? pin) => (pin ?? "").Trim();

    /// <summary>
    /// 4 to 64 printable ASCII characters without spaces. The phone sends the PIN in an HTTP header,
    /// which browsers refuse for other characters.
    /// </summary>
    public static bool IsValid(string? pin)
    {
        var text = Normalize(pin);
        return text.Length is >= MinLength and <= MaxLength && text.All(c => c > ' ' && c < '\x7f');
    }

    /// <summary>True for a valid PIN shorter than the generated ones, which is quicker to guess.</summary>
    public static bool IsWeak(string? pin) => IsValid(pin) && Normalize(pin).Length < GeneratedLength;

    /// <summary>
    /// Constant-time comparison. Both sides are hashed first so the time taken does not depend on how
    /// many characters of a guess are right or on its length. A blank expected PIN never matches.
    /// </summary>
    public static bool Matches(string? presented, string? expected)
    {
        var want = Normalize(expected);
        if (want.Length == 0)
            return false;

        var a = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(presented)));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(want));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}

public enum PhoneRemoteAuthStatus
{
    Ok,
    /// <summary>No PIN was sent (not counted as a failed attempt; it reveals nothing).</summary>
    PinRequired,
    WrongPin,
    /// <summary>Too many wrong PINs from this client: HTTP 429 until <see cref="PhoneRemoteAuthResult.RetryAfter"/> passes.</summary>
    LockedOut
}

/// <param name="Message">Text for the phone page.</param>
/// <param name="LockoutStarted">True only for the wrong PIN that started a lockout, so it is reported once.</param>
public sealed record PhoneRemoteAuthResult(
    PhoneRemoteAuthStatus Status,
    string Message,
    int AttemptsLeft = 0,
    TimeSpan RetryAfter = default,
    bool LockoutStarted = false)
{
    public bool IsAuthorized => Status == PhoneRemoteAuthStatus.Ok;

    /// <summary>HTTP status for a refused request: 401, or 429 while locked out.</summary>
    public int HttpStatus => Status switch
    {
        PhoneRemoteAuthStatus.Ok => 200,
        PhoneRemoteAuthStatus.LockedOut => 429,
        _ => 401
    };

    /// <summary>Whole seconds for the Retry-After header (at least 1 while locked out).</summary>
    public int RetryAfterSeconds => Status == PhoneRemoteAuthStatus.LockedOut
        ? Math.Max(1, (int)Math.Ceiling(RetryAfter.TotalSeconds))
        : 0;
}

/// <summary>Raised when a client was locked out after too many wrong PINs.</summary>
public sealed record PhoneRemoteLockout(string Client, int FailedAttempts, TimeSpan Duration);

/// <summary>
/// Checks the PIN the phone sends with every API request, with per-client brute-force protection
/// (<see cref="LoginAttemptLimiter"/>). Thread-safe: Kestrel calls it from many threads while the UI
/// changes the PIN.
/// </summary>
public sealed class PhoneRemoteAuthenticator
{
    private readonly object _sync = new();
    private string _pin = "";

    public PhoneRemoteAuthenticator(LoginAttemptLimiter? limiter = null)
    {
        Limiter = limiter ?? new LoginAttemptLimiter();
    }

    public LoginAttemptLimiter Limiter { get; }

    public string Pin
    {
        get { lock (_sync) return _pin; }
    }

    /// <summary>
    /// Sets the PIN. A different PIN also clears all failure counters and lockouts, since earlier guesses
    /// were against the old PIN. Returns true when the PIN changed.
    /// </summary>
    public bool SetPin(string? pin)
    {
        var text = PhoneRemotePin.Normalize(pin);
        lock (_sync)
        {
            if (string.Equals(_pin, text, StringComparison.Ordinal))
                return false;
            _pin = text;
        }

        Limiter.Clear();
        return true;
    }

    public PhoneRemoteAuthResult Authenticate(string? client, string? presentedPin)
    {
        if (Limiter.IsLockedOut(client, out var retryAfter))
            return LockedOut(retryAfter, started: false);

        var expected = Pin;
        if (expected.Length == 0)
            return new PhoneRemoteAuthResult(PhoneRemoteAuthStatus.PinRequired,
                "The desktop app has no phone remote PIN yet. Restart the phone remote in the desktop app.");

        if (PhoneRemotePin.Normalize(presentedPin).Length == 0)
            return new PhoneRemoteAuthResult(PhoneRemoteAuthStatus.PinRequired,
                "Enter the PIN shown in the desktop app under Settings > Phone Remote.");

        if (PhoneRemotePin.Matches(presentedPin, expected))
        {
            Limiter.RecordSuccess(client);
            return new PhoneRemoteAuthResult(PhoneRemoteAuthStatus.Ok, "");
        }

        var failure = Limiter.RecordFailure(client);
        if (failure.LockedOut)
            return LockedOut(failure.RetryAfter, failure.LockoutStarted);

        var attempts = failure.AttemptsLeft == 1 ? "1 attempt" : $"{failure.AttemptsLeft} attempts";
        return new PhoneRemoteAuthResult(PhoneRemoteAuthStatus.WrongPin,
            $"Wrong PIN. {attempts} left before this device is locked out for {DescribeMinutes(Limiter.LockoutDuration)}.",
            failure.AttemptsLeft);
    }

    /// <summary>"1 minute" or "N minutes", rounded up.</summary>
    public static string DescribeMinutes(TimeSpan time)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(time.TotalMinutes));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }

    private PhoneRemoteAuthResult LockedOut(TimeSpan retryAfter, bool started) =>
        new(PhoneRemoteAuthStatus.LockedOut,
            $"Too many wrong PIN attempts. This device is locked out. Try again in {DescribeMinutes(retryAfter)}.",
            0, retryAfter, started);
}
