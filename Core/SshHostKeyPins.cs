using System;
using System.Collections.Generic;

namespace VoiceChatbot;

/// <summary>What <see cref="SshHostKeyPins.Verify"/> decided about a host key.</summary>
public enum SshHostKeyVerdict
{
    /// <summary>Nothing was pinned for this host:port yet; the presented key is now pinned.</summary>
    TrustedOnFirstUse,
    /// <summary>The presented key matches the pinned one.</summary>
    Match,
    /// <summary>The presented key differs from the pinned one; the connection must be refused.</summary>
    Mismatch
}

/// <param name="HostPort">The pin key, for example "192.168.1.20:2222".</param>
/// <param name="Presented">The fingerprint the server sent, as "SHA256:...".</param>
/// <param name="Pinned">The fingerprint pinned before this check ("" on first use).</param>
/// <param name="KeyType">The host key algorithm, for example "ssh-ed25519" (may be "").</param>
public sealed record SshHostKeyCheck(SshHostKeyVerdict Verdict, string HostPort, string Presented, string Pinned, string KeyType);

/// <summary>
/// Trust-on-first-use pins for SSH host keys, keyed by "host:port". Fingerprints are kept the way
/// OpenSSH prints them ("SHA256:" + unpadded base64), so they can be compared with
/// <c>ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub</c> on the server.
/// Thread-safe: SSH.NET checks host keys on its own thread while the UI reads and forgets pins.
/// </summary>
public sealed class SshHostKeyPins
{
    public const string Sha256Prefix = "SHA256:";

    private readonly object _sync = new();
    private readonly Dictionary<string, string> _pins = new(StringComparer.Ordinal);

    /// <summary>"host:port" with the host lower-cased; IPv6 hosts are bracketed like known_hosts does.</summary>
    public static string HostPortKey(string? host, int port)
    {
        var name = (host ?? "").Trim().ToLowerInvariant();
        if (name.Length > 1 && name[0] == '[' && name[^1] == ']')
            name = name[1..^1];
        return name.Contains(':') ? $"[{name}]:{port}" : $"{name}:{port}";
    }

    /// <summary>
    /// "SHA256:" + base64 without padding, whether or not the input had the prefix or "=" padding.
    /// Returns "" for a blank value. Other text is kept as-is so a mistyped pin fails closed.
    /// </summary>
    public static string NormalizeFingerprint(string? fingerprint)
    {
        var text = (fingerprint ?? "").Trim();
        if (text.StartsWith(Sha256Prefix, StringComparison.OrdinalIgnoreCase))
            text = text[Sha256Prefix.Length..].Trim();
        text = text.TrimEnd('=');
        return text.Length == 0 ? "" : Sha256Prefix + text;
    }

    /// <summary>Replaces all pins with the saved ones. Blank entries are skipped.</summary>
    public void Load(IEnumerable<KeyValuePair<string, string>>? pins)
    {
        lock (_sync)
        {
            _pins.Clear();
            if (pins == null)
                return;

            foreach (var (key, value) in pins)
            {
                var hostPort = (key ?? "").Trim().ToLowerInvariant();
                var fingerprint = NormalizeFingerprint(value);
                if (hostPort.Length > 0 && fingerprint.Length > 0)
                    _pins[hostPort] = fingerprint;
            }
        }
    }

    /// <summary>A copy of the pins for saving.</summary>
    public Dictionary<string, string> ToDictionary()
    {
        lock (_sync)
            return new Dictionary<string, string>(_pins, StringComparer.Ordinal);
    }

    /// <summary>The pinned fingerprint for host:port, or "" when none is pinned.</summary>
    public string Get(string? host, int port)
    {
        lock (_sync)
            return _pins.TryGetValue(HostPortKey(host, port), out var fingerprint) ? fingerprint : "";
    }

    /// <summary>Removes the pin for host:port so the next connection trusts whatever key it gets.</summary>
    public bool Forget(string? host, int port)
    {
        lock (_sync)
            return _pins.Remove(HostPortKey(host, port));
    }

    /// <summary>
    /// Checks the key a server presented. With nothing pinned the key is pinned and accepted (trust on
    /// first use); otherwise it must match. Check and pin happen under one lock, so two first connections
    /// at the same time cannot pin different keys.
    /// </summary>
    public SshHostKeyCheck Verify(string? host, int port, string? keyType, string? presentedFingerprint)
    {
        var hostPort = HostPortKey(host, port);
        var presented = NormalizeFingerprint(presentedFingerprint);
        var type = (keyType ?? "").Trim();

        lock (_sync)
        {
            _pins.TryGetValue(hostPort, out var pinned);
            pinned ??= "";

            // A server that sent no usable key is never trusted.
            if (presented.Length == 0)
                return new SshHostKeyCheck(SshHostKeyVerdict.Mismatch, hostPort, presented, pinned, type);

            if (pinned.Length == 0)
            {
                _pins[hostPort] = presented;
                return new SshHostKeyCheck(SshHostKeyVerdict.TrustedOnFirstUse, hostPort, presented, "", type);
            }

            // Base64 is case-sensitive, so compare ordinally.
            var verdict = string.Equals(pinned, presented, StringComparison.Ordinal)
                ? SshHostKeyVerdict.Match
                : SshHostKeyVerdict.Mismatch;
            return new SshHostKeyCheck(verdict, hostPort, presented, pinned, type);
        }
    }

    /// <summary>The error shown when a server's host key no longer matches its pin.</summary>
    public static string DescribeMismatch(SshHostKeyCheck check)
    {
        if (check.Presented.Length == 0)
            return $"The SSH server at {check.HostPort} did not send a usable host key, so the connection was refused.";

        var type = check.KeyType.Length > 0 ? $" ({check.KeyType})" : "";
        return $"The SSH host key for {check.HostPort} has changed, so the connection was refused. " +
               $"Pinned {check.Pinned}, but the server sent {check.Presented}{type}. " +
               "If you reinstalled or reconfigured the SSH server, open Settings > Model Server Control, " +
               "click \"Forget host key\" and try again; the new key is trusted on the next connection. " +
               "If nothing changed on the server, someone may be intercepting the connection.";
    }
}

/// <summary>Thrown when an SSH server presents a host key that differs from the pinned one.</summary>
public sealed class SshHostKeyMismatchException : Exception
{
    public SshHostKeyCheck Check { get; }

    public SshHostKeyMismatchException(SshHostKeyCheck check, Exception? inner = null)
        : base(SshHostKeyPins.DescribeMismatch(check), inner)
    {
        Check = check;
    }
}
