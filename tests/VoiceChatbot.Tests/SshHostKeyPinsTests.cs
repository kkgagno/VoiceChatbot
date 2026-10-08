using System.Collections.Generic;
using System.Linq;
using VoiceChatbot;
using Xunit;

public class SshHostKeyPinsTests
{
    // Real-looking SHA256 fingerprints (43 base64 characters, no padding), as OpenSSH prints them.
    private const string KeyA = "ohD8VZEXGWo6Ez8GSEJQ9WpafgLFsOfLOtGGQCQo6Og";
    private const string KeyB = "nThbg6kXUpJWGl7E1IGOCspRomTxdCARLviKw6E5SY8";

    [Theory]
    [InlineData("127.0.0.1", 2222, "127.0.0.1:2222")]
    [InlineData("  Model-Box.LAN ", 22, "model-box.lan:22")]
    [InlineData("::1", 22, "[::1]:22")]
    [InlineData("[FE80::1]", 2222, "[fe80::1]:2222")]
    [InlineData(null, 22, ":22")]
    public void HostPortKey(string? host, int port, string expected) =>
        Assert.Equal(expected, SshHostKeyPins.HostPortKey(host, port));

    [Theory]
    [InlineData(KeyA, "SHA256:" + KeyA)]
    [InlineData("SHA256:" + KeyA, "SHA256:" + KeyA)]
    [InlineData("sha256:" + KeyA + "=", "SHA256:" + KeyA)]
    [InlineData("  SHA256: " + KeyA + "  ", "SHA256:" + KeyA)]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("SHA256:", "")]
    [InlineData(null, "")]
    public void NormalizeFingerprint(string? input, string expected) =>
        Assert.Equal(expected, SshHostKeyPins.NormalizeFingerprint(input));

    [Fact]
    public void FirstUseIsTrustedAndPinned()
    {
        var pins = new SshHostKeyPins();

        var check = pins.Verify("Host", 2222, "ssh-ed25519", KeyA);

        Assert.Equal(SshHostKeyVerdict.TrustedOnFirstUse, check.Verdict);
        Assert.Equal("host:2222", check.HostPort);
        Assert.Equal("SHA256:" + KeyA, check.Presented);
        Assert.Equal("", check.Pinned);
        Assert.Equal("ssh-ed25519", check.KeyType);
        Assert.Equal("SHA256:" + KeyA, pins.Get("host", 2222));
    }

    [Fact]
    public void SameKeyMatches()
    {
        var pins = new SshHostKeyPins();
        pins.Verify("host", 22, "ssh-ed25519", KeyA);

        var check = pins.Verify("HOST", 22, "ssh-ed25519", "SHA256:" + KeyA);

        Assert.Equal(SshHostKeyVerdict.Match, check.Verdict);
        Assert.Equal("SHA256:" + KeyA, check.Pinned);
    }

    [Fact]
    public void DifferentKeyIsRefusedAndPinIsKept()
    {
        var pins = new SshHostKeyPins();
        pins.Verify("host", 22, "ssh-ed25519", KeyA);

        var check = pins.Verify("host", 22, "ssh-rsa", KeyB);

        Assert.Equal(SshHostKeyVerdict.Mismatch, check.Verdict);
        Assert.Equal("SHA256:" + KeyA, check.Pinned);
        Assert.Equal("SHA256:" + KeyB, check.Presented);
        Assert.Equal("SHA256:" + KeyA, pins.Get("host", 22));
    }

    [Fact]
    public void FingerprintCaseMatters()
    {
        var pins = new SshHostKeyPins();
        pins.Verify("host", 22, "", KeyA);

        Assert.Equal(SshHostKeyVerdict.Mismatch, pins.Verify("host", 22, "", KeyA.ToLowerInvariant()).Verdict);
    }

    [Fact]
    public void PinsArePerHostAndPort()
    {
        var pins = new SshHostKeyPins();
        pins.Verify("host", 22, "", KeyA);

        Assert.Equal(SshHostKeyVerdict.TrustedOnFirstUse, pins.Verify("host", 2222, "", KeyB).Verdict);
        Assert.Equal(SshHostKeyVerdict.TrustedOnFirstUse, pins.Verify("other", 22, "", KeyB).Verdict);
        Assert.Equal(3, pins.ToDictionary().Count);
    }

    [Fact]
    public void ForgetAllowsANewKey()
    {
        var pins = new SshHostKeyPins();
        pins.Verify("host", 22, "", KeyA);

        Assert.True(pins.Forget("Host", 22));
        Assert.False(pins.Forget("host", 22));
        Assert.Equal("", pins.Get("host", 22));
        Assert.Equal(SshHostKeyVerdict.TrustedOnFirstUse, pins.Verify("host", 22, "", KeyB).Verdict);
        Assert.Equal("SHA256:" + KeyB, pins.Get("host", 22));
    }

    [Fact]
    public void EmptyPresentedKeyIsNeverTrusted()
    {
        var pins = new SshHostKeyPins();

        var check = pins.Verify("host", 22, "", "");

        Assert.Equal(SshHostKeyVerdict.Mismatch, check.Verdict);
        Assert.Equal("", pins.Get("host", 22));
        Assert.Contains("did not send a usable host key", SshHostKeyPins.DescribeMismatch(check));
    }

    [Fact]
    public void LoadNormalizesAndSkipsBlankEntries()
    {
        var pins = new SshHostKeyPins();
        pins.Load(new Dictionary<string, string>
        {
            [" Host:22 "] = KeyA + "=",
            ["blank:22"] = "  ",
            [""] = KeyB,
        });

        var saved = pins.ToDictionary();
        Assert.Single(saved);
        Assert.Equal("SHA256:" + KeyA, saved["host:22"]);
        Assert.Equal(SshHostKeyVerdict.Match, pins.Verify("host", 22, "", KeyA).Verdict);
    }

    [Fact]
    public void LoadReplacesPinsAndAcceptsNull()
    {
        var pins = new SshHostKeyPins();
        pins.Verify("host", 22, "", KeyA);

        pins.Load(null);

        Assert.Empty(pins.ToDictionary());
    }

    [Fact]
    public void MistypedSavedPinFailsClosed()
    {
        var pins = new SshHostKeyPins();
        pins.Load(new Dictionary<string, string> { ["host:22"] = "not a fingerprint" });

        Assert.Equal(SshHostKeyVerdict.Mismatch, pins.Verify("host", 22, "", KeyA).Verdict);
    }

    [Fact]
    public void ToDictionaryIsACopy()
    {
        var pins = new SshHostKeyPins();
        pins.Verify("host", 22, "", KeyA);

        pins.ToDictionary().Clear();

        Assert.Equal("SHA256:" + KeyA, pins.Get("host", 22));
    }

    [Fact]
    public void ConcurrentFirstConnectionsPinOnlyOneKey()
    {
        var pins = new SshHostKeyPins();

        var checks = Enumerable.Range(0, 64)
            .AsParallel()
            .Select(i => pins.Verify("host", 22, "", i % 2 == 0 ? KeyA : KeyB))
            .ToList();

        Assert.Single(checks, c => c.Verdict == SshHostKeyVerdict.TrustedOnFirstUse);
        var pinned = pins.Get("host", 22);
        Assert.All(checks.Where(c => c.Verdict != SshHostKeyVerdict.TrustedOnFirstUse),
            c => Assert.Equal(c.Presented == pinned ? SshHostKeyVerdict.Match : SshHostKeyVerdict.Mismatch, c.Verdict));
    }

    [Fact]
    public void MismatchMessageExplainsHowToReset()
    {
        var check = new SshHostKeyCheck(SshHostKeyVerdict.Mismatch, "host:2222", "SHA256:" + KeyB, "SHA256:" + KeyA, "ssh-ed25519");

        var message = new SshHostKeyMismatchException(check).Message;

        Assert.Contains("host key for host:2222 has changed", message);
        Assert.Contains("SHA256:" + KeyA, message);
        Assert.Contains("SHA256:" + KeyB + " (ssh-ed25519)", message);
        Assert.Contains("Forget host key", message);
    }
}
