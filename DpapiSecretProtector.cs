using System;
using System.Security.Cryptography;
using System.Text;

namespace VoiceChatbot;

/// <summary>
/// Windows DPAPI bound to the current Windows user: only this user on this PC can decrypt the values.
/// The entropy is not a secret; it only keeps these blobs from being interchangeable with other
/// DPAPI data of the same user.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VoiceChatbot.Settings.Secrets.v1");

    public bool IsAvailable => OperatingSystem.IsWindows();

    public byte[] Protect(byte[] plain)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI is only available on Windows.");
        return ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
    }
}
