using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace VoiceChatbot;

/// <summary>
/// Encrypts and decrypts secret bytes. The app uses Windows DPAPI (DpapiSecretProtector);
/// tests use a fake.
/// </summary>
public interface ISecretProtector
{
    /// <summary>False where there is no protector (DPAPI off Windows). Secrets then stay plain text.</summary>
    bool IsAvailable { get; }
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>What happened to the secret fields while loading settings.json.</summary>
public sealed class SecretLoadResult
{
    /// <summary>Encrypted fields that could not be decrypted (another Windows user or PC). They were cleared.</summary>
    public List<string> Failed { get; } = new();

    /// <summary>Fields still in plain text from an older version. They get encrypted on the next save.</summary>
    public List<string> Plain { get; } = new();
}

/// <summary>
/// Stores secret settings as "dpapi:&lt;base64&gt;" in the settings JSON while the in-memory settings
/// stay plain. Fields are addressed by property path: "TavilyApiKey" or "PhoneRemote.Pin".
/// </summary>
public static class SettingsSecrets
{
    public const string Prefix = "dpapi:";

    public static bool IsProtected(string? stored) =>
        stored != null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Returns "dpapi:&lt;base64&gt;" for a non-empty value, the value itself when empty, or null when it
    /// could not be protected.
    /// </summary>
    public static string? TryProtect(string? plain, ISecretProtector protector)
    {
        if (string.IsNullOrEmpty(plain))
            return plain ?? "";
        if (!protector.IsAvailable)
            return null;

        try
        {
            return Prefix + Convert.ToBase64String(protector.Protect(Encoding.UTF8.GetBytes(plain)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypts a stored value. Values without the prefix are legacy plain text and are returned as they are.
    /// </summary>
    public static bool TryUnprotect(string? stored, ISecretProtector protector, out string plain)
    {
        plain = "";
        if (!IsProtected(stored))
        {
            plain = stored ?? "";
            return true;
        }
        if (!protector.IsAvailable)
            return false;

        try
        {
            var bytes = Convert.FromBase64String(stored!.Substring(Prefix.Length));
            plain = Encoding.UTF8.GetString(protector.Unprotect(bytes));
            return true;
        }
        catch
        {
            plain = "";
            return false;
        }
    }

    /// <summary>
    /// Encrypts the string fields at <paramref name="paths"/> in place, ready to be written to disk.
    /// Returns the paths that had to stay plain text because protection was unavailable or failed.
    /// </summary>
    public static List<string> ProtectFields(JsonObject root, IEnumerable<string> paths, ISecretProtector protector)
    {
        var leftPlain = new List<string>();
        foreach (var path in paths)
        {
            if (!TryGetStringField(root, path, out var parent, out var key, out var value) || value.Length == 0)
                continue;

            var stored = TryProtect(value, protector);
            if (stored == null)
                leftPlain.Add(path);
            else
                parent[key] = stored;
        }
        return leftPlain;
    }

    /// <summary>
    /// Decrypts the fields at <paramref name="paths"/> in place, before the JSON is deserialized.
    /// Fields that cannot be decrypted are set to "" so the app keeps running without them.
    /// </summary>
    public static SecretLoadResult UnprotectFields(JsonObject root, IEnumerable<string> paths, ISecretProtector protector)
    {
        var result = new SecretLoadResult();
        foreach (var path in paths)
        {
            if (!TryGetStringField(root, path, out var parent, out var key, out var value) || value.Length == 0)
                continue;

            if (!IsProtected(value))
            {
                result.Plain.Add(path);
                continue;
            }

            if (TryUnprotect(value, protector, out var plain))
            {
                parent[key] = plain;
            }
            else
            {
                parent[key] = "";
                result.Failed.Add(path);
            }
        }
        return result;
    }

    private static bool TryGetStringField(JsonObject root, string path, out JsonObject parent, out string key, out string value)
    {
        parent = root;
        key = path;
        value = "";

        var parts = path.Split('.');
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (parent[parts[i]] is not JsonObject child)
                return false;
            parent = child;
        }

        key = parts[^1];
        if (parent[key] is not JsonValue node || !node.TryGetValue<string>(out var text))
            return false;

        value = text ?? "";
        return true;
    }
}
