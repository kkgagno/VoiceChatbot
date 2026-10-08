using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using VoiceChatbot;
using Xunit;

public class SettingsSecretsTests
{
    private static readonly string[] Paths = { "ApiKey", "Password", "Remote.Pin" };

    /// <summary>Reversible stand-in for DPAPI: XOR with a key, so ciphertext differs from the plain text.</summary>
    private sealed class FakeProtector : ISecretProtector
    {
        private readonly byte _key;
        public FakeProtector(byte key = 0x5A) => _key = key;
        public bool IsAvailable { get; init; } = true;
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ _key)).Append(_key).ToArray();
        public byte[] Unprotect(byte[] data)
        {
            // Mimic DPAPI refusing data protected by another user (a different key here).
            if (data.Length == 0 || data[^1] != _key)
                throw new CryptographicException("Key not valid for use in specified state.");
            return data[..^1].Select(b => (byte)(b ^ _key)).ToArray();
        }
    }

    private sealed class ThrowingProtector : ISecretProtector
    {
        public bool IsAvailable => true;
        public byte[] Protect(byte[] plain) => throw new CryptographicException("boom");
        public byte[] Unprotect(byte[] data) => throw new CryptographicException("boom");
    }

    private static JsonObject Settings(string apiKey = "sk-123", string password = "hunter22", string pin = "4321") =>
        new()
        {
            ["Model"] = "llama3",
            ["ApiKey"] = apiKey,
            ["Password"] = password,
            ["Port"] = 2222,
            ["Remote"] = new JsonObject { ["Pin"] = pin, ["Enabled"] = true }
        };

    private static string Str(JsonObject root, string path)
    {
        JsonNode? node = root;
        foreach (var part in path.Split('.'))
            node = node![part];
        return node!.GetValue<string>();
    }

    [Fact]
    public void ProtectFields_EncryptsSecretsAndLeavesOtherFieldsAlone()
    {
        var root = Settings();
        var leftPlain = SettingsSecrets.ProtectFields(root, Paths, new FakeProtector());

        Assert.Empty(leftPlain);
        foreach (var path in Paths)
            Assert.StartsWith(SettingsSecrets.Prefix, Str(root, path));
        Assert.DoesNotContain("sk-123", root.ToJsonString());
        Assert.DoesNotContain("hunter22", root.ToJsonString());
        Assert.Equal("llama3", Str(root, "Model"));
        Assert.Equal(2222, root["Port"]!.GetValue<int>());
    }

    [Fact]
    public void RoundTrip_ThroughJsonText_RestoresPlainValues()
    {
        var protector = new FakeProtector();
        var root = Settings(apiKey: "key with ünicode ✓");
        SettingsSecrets.ProtectFields(root, Paths, protector);

        var reloaded = (JsonObject)JsonNode.Parse(root.ToJsonString())!;
        var result = SettingsSecrets.UnprotectFields(reloaded, Paths, protector);

        Assert.Empty(result.Failed);
        Assert.Empty(result.Plain);
        Assert.Equal("key with ünicode ✓", Str(reloaded, "ApiKey"));
        Assert.Equal("hunter22", Str(reloaded, "Password"));
        Assert.Equal("4321", Str(reloaded, "Remote.Pin"));
    }

    [Fact]
    public void Unprotect_LegacyPlainValues_LoadAsIsAndAreReported()
    {
        var root = Settings();
        var result = SettingsSecrets.UnprotectFields(root, Paths, new FakeProtector());

        Assert.Empty(result.Failed);
        Assert.Equal(Paths, result.Plain);
        Assert.Equal("sk-123", Str(root, "ApiKey"));
        Assert.Equal("4321", Str(root, "Remote.Pin"));
    }

    [Fact]
    public void Unprotect_ValueFromAnotherUser_IsClearedAndReported()
    {
        var root = Settings();
        SettingsSecrets.ProtectFields(root, Paths, new FakeProtector(0x11));

        var result = SettingsSecrets.UnprotectFields(root, Paths, new FakeProtector(0x22));

        Assert.Equal(Paths, result.Failed);
        foreach (var path in Paths)
            Assert.Equal("", Str(root, path));
    }

    [Theory]
    [InlineData("dpapi:")]
    [InlineData("dpapi:not base64 !!")]
    public void Unprotect_CorruptValue_IsClearedNotThrown(string stored)
    {
        var root = Settings(password: stored);
        var result = SettingsSecrets.UnprotectFields(root, Paths, new FakeProtector());

        Assert.Equal(new[] { "Password" }, result.Failed);
        Assert.Equal("", Str(root, "Password"));
        Assert.Equal("sk-123", Str(root, "ApiKey"));
    }

    [Fact]
    public void EmptyAndMissingFields_AreSkipped()
    {
        var root = new JsonObject { ["ApiKey"] = "", ["Password"] = 42, ["Remote"] = null };
        var protector = new FakeProtector();

        Assert.Empty(SettingsSecrets.ProtectFields(root, Paths, protector));
        var result = SettingsSecrets.UnprotectFields(root, Paths, protector);

        Assert.Empty(result.Failed);
        Assert.Empty(result.Plain);
        Assert.Equal("", Str(root, "ApiKey"));
        Assert.Equal(42, root["Password"]!.GetValue<int>());
    }

    [Fact]
    public void UnavailableProtector_KeepsPlainTextOnSave_AndCannotReadEncryptedValues()
    {
        var unavailable = new FakeProtector { IsAvailable = false };
        var root = Settings();

        var leftPlain = SettingsSecrets.ProtectFields(root, Paths, unavailable);
        Assert.Equal(Paths, leftPlain);
        Assert.Equal("sk-123", Str(root, "ApiKey"));

        var encrypted = Settings();
        SettingsSecrets.ProtectFields(encrypted, Paths, new FakeProtector());
        var result = SettingsSecrets.UnprotectFields(encrypted, Paths, unavailable);
        Assert.Equal(Paths, result.Failed);
    }

    [Fact]
    public void ThrowingProtector_NeverThrowsToCaller()
    {
        var root = Settings();
        Assert.Equal(Paths, SettingsSecrets.ProtectFields(root, Paths, new ThrowingProtector()));
        Assert.Equal("hunter22", Str(root, "Password"));

        root["Password"] = "dpapi:AAAA";
        var result = SettingsSecrets.UnprotectFields(root, Paths, new ThrowingProtector());
        Assert.Contains("Password", result.Failed);
    }

    private sealed class SampleSettings
    {
        public string ApiKey { get; set; } = "";
        public string Password { get; set; } = "";
        public SampleRemote Remote { get; set; } = new();
    }

    private sealed class SampleRemote
    {
        public string Pin { get; set; } = "";
    }

    [Fact]
    public void WorksWithSerializedObjects()
    {
        var protector = new FakeProtector();
        var original = new SampleSettings { ApiKey = "abc", Password = "pw-1", Remote = new SampleRemote { Pin = "9876" } };

        var root = JsonSerializer.SerializeToNode(original)!.AsObject();
        SettingsSecrets.ProtectFields(root, Paths, protector);
        var json = root.ToJsonString();
        Assert.DoesNotContain("pw-1", json);

        var loaded = (JsonObject)JsonNode.Parse(json)!;
        SettingsSecrets.UnprotectFields(loaded, Paths, protector);
        var copy = loaded.Deserialize<SampleSettings>()!;

        Assert.Equal("abc", copy.ApiKey);
        Assert.Equal("pw-1", copy.Password);
        Assert.Equal("9876", copy.Remote.Pin);
        // The in-memory object is never touched by saving.
        Assert.Equal("pw-1", original.Password);
    }
}
