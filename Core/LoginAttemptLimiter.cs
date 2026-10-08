using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceChatbot;

/// <param name="LockedOut">True when the client is (now) locked out.</param>
/// <param name="AttemptsLeft">Wrong attempts the client has left before a lockout (0 when locked out).</param>
/// <param name="RetryAfter">How long the lockout still lasts (zero when not locked out).</param>
/// <param name="LockoutStarted">True only for the failure that started the lockout, so it is reported once.</param>
public readonly record struct LoginAttemptResult(bool LockedOut, int AttemptsLeft, TimeSpan RetryAfter, bool LockoutStarted);

/// <summary>
/// Brute-force protection keyed by client (the phone remote uses the remote IP address): after
/// <see cref="MaxFailures"/> failed attempts within <see cref="Window"/> the client is locked out for
/// <see cref="LockoutDuration"/>. A success resets the client's counter. Thread-safe; the clock is
/// injectable for tests.
/// </summary>
public sealed class LoginAttemptLimiter
{
    public const int DefaultMaxFailures = 5;
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultLockout = TimeSpan.FromMinutes(10);

    // Expired entries are pruned once this many clients are tracked, so the table cannot grow without bound.
    internal const int PruneThreshold = 1024;

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _clock;

    public LoginAttemptLimiter(
        int maxFailures = DefaultMaxFailures,
        TimeSpan? window = null,
        TimeSpan? lockout = null,
        Func<DateTimeOffset>? clock = null)
    {
        MaxFailures = Math.Max(1, maxFailures);
        Window = window is { } w && w > TimeSpan.Zero ? w : DefaultWindow;
        LockoutDuration = lockout is { } l && l > TimeSpan.Zero ? l : DefaultLockout;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public int MaxFailures { get; }
    public TimeSpan Window { get; }
    public TimeSpan LockoutDuration { get; }

    /// <summary>Clients tracked right now (failures in the window or an active lockout).</summary>
    public int TrackedCount
    {
        get
        {
            lock (_sync)
            {
                Prune(_clock());
                return _entries.Count;
            }
        }
    }

    /// <summary>True while the client is locked out; <paramref name="retryAfter"/> is the time left.</summary>
    public bool IsLockedOut(string? client, out TimeSpan retryAfter)
    {
        lock (_sync)
        {
            var now = _clock();
            retryAfter = TimeSpan.Zero;
            if (!_entries.TryGetValue(Key(client), out var entry) || entry.LockedUntil <= now)
                return false;

            retryAfter = entry.LockedUntil - now;
            return true;
        }
    }

    /// <summary>
    /// Counts a failed attempt. The failure that reaches <see cref="MaxFailures"/> within the window starts
    /// the lockout. Failures while already locked out do not extend it.
    /// </summary>
    public LoginAttemptResult RecordFailure(string? client)
    {
        lock (_sync)
        {
            var now = _clock();
            if (_entries.Count >= PruneThreshold)
                Prune(now);

            var key = Key(client);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            if (entry.LockedUntil > now)
                return new LoginAttemptResult(true, 0, entry.LockedUntil - now, false);

            DropOldFailures(entry, now);
            entry.Failures.Enqueue(now);
            if (entry.Failures.Count < MaxFailures)
                return new LoginAttemptResult(false, MaxFailures - entry.Failures.Count, TimeSpan.Zero, false);

            // The counter starts fresh once the lockout ends.
            entry.Failures.Clear();
            entry.LockedUntil = now + LockoutDuration;
            return new LoginAttemptResult(true, 0, LockoutDuration, true);
        }
    }

    /// <summary>A successful attempt resets the client's failure counter.</summary>
    public void RecordSuccess(string? client)
    {
        lock (_sync)
            _entries.Remove(Key(client));
    }

    /// <summary>Forgets every failure and lockout (for example after the PIN was changed).</summary>
    public void Clear()
    {
        lock (_sync)
            _entries.Clear();
    }

    private static string Key(string? client)
    {
        var key = (client ?? "").Trim();
        return key.Length == 0 ? "unknown" : key;
    }

    private void DropOldFailures(Entry entry, DateTimeOffset now)
    {
        var cutoff = now - Window;
        while (entry.Failures.Count > 0 && entry.Failures.Peek() <= cutoff)
            entry.Failures.Dequeue();
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries.ToList())
        {
            DropOldFailures(entry, now);
            if (entry.Failures.Count == 0 && entry.LockedUntil <= now)
                _entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        public Queue<DateTimeOffset> Failures { get; } = new();
        public DateTimeOffset LockedUntil { get; set; } = DateTimeOffset.MinValue;
    }
}
