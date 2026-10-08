using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;

namespace VoiceChatbot;

public class AppSettings
{
    public const int DefaultImageWidth = 1080;
    public const int DefaultImageHeight = 1920;
    public const string DefaultTavilyApiKey = "";
    public const string DefaultRyzenAiWhisperCommand = "call \"%USERPROFILE%\\VoiceChatbot\\tools\\ryzen-ai-whisper-transcribe.bat\" {input}";
    /// <summary>
    /// False for blank commands and leftover placeholders such as "{ryzen}" that cmd.exe cannot run.
    /// </summary>
    public static bool IsUsableTranscriberCommand(string? command)
    {
        var text = command?.Trim() ?? "";
        return text.Length > 0 && !System.Text.RegularExpressions.Regex.IsMatch(text, @"^\{[^{}\s]*\}$");
    }

    // Chat backend
    public string ChatProvider { get; set; } = "Ollama";
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string OpenAiCompatibleUrl { get; set; } = "http://localhost:8080/v1";
    public string OpenAiCompatibleApiKey { get; set; } = "";
    public string HermesSshHost { get; set; } = "127.0.0.1";
    public int HermesSshPort { get; set; } = 2222;
    public string HermesSshUser { get; set; } = "";
    public string HermesSshPassword { get; set; } = "";
    public string Model { get; set; } = "llama3";
    public string SystemPrompt { get; set; } = "You are a helpful, friendly AI assistant. Keep responses concise and conversational since they will be spoken aloud. Use plain natural language for normal conversation. If the user explicitly asks for code, markup, an SVG, or a script, provide it in a fenced code block.";
    public double Temperature { get; set; } = 0.7;

    // Voice Input
    public string InputLanguage { get; set; } = "en-US";
    public double SilenceTimeout { get; set; } = 2.4;
    public int NoiseSuppression { get; set; } = 30;
    public bool AutoDetectVoice { get; set; } = true;
    public string WakeWord { get; set; } = "hey assistant";
    public int MicDeviceIndex { get; set; } = -1;
    public string WhisperModelSize { get; set; } = "small";
    public string TranscriptionBackend { get; set; } = "Whisper.net";
    public string ExternalNpuTranscriberCommand { get; set; } = DefaultRyzenAiWhisperCommand;

    // Voice Output
    public string VoiceName { get; set; } = "am_onyx (American Male)";
    public int SpeechRate { get; set; } = 1;
    public int Volume { get; set; } = 80;
    public bool TtsEnabled { get; set; } = true;

    // Kokoro text-to-speech. Leave the remote URL blank to use only the bundled local server.
    // Accepts "192.168.1.50", "192.168.1.50:8880" or "http://host:8880/v1".
    public string KokoroRemoteUrl { get; set; } = "";
    public string KokoroMode { get; set; } = KokoroEndpoint.ModeAuto;

    // Conversation
    public int MaxContextMessages { get; set; } = 20;
    public bool StreamResponses { get; set; } = true;

    // Web Search
    public string TavilyApiKey { get; set; } = DefaultTavilyApiKey;
    public bool WebSearchEnabled { get; set; } = true;
    public int MaxTokens { get; set; } = 2048;

    // Image generation / editing
    public string ComfyUiUrl { get; set; } = "http://localhost:8000";
    public int ImageWidth { get; set; } = DefaultImageWidth;
    public int ImageHeight { get; set; } = DefaultImageHeight;
    public int QwenCreateSteps { get; set; } = 4;
    public int QwenEditSteps { get; set; } = 40;
    public int VideoSeconds { get; set; } = 6;
    public int VideoFps { get; set; } = 24;

    // Window
    public double WindowLeft { get; set; } = -1;
    public double WindowTop { get; set; } = -1;
    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 750;
    public bool WindowMaximized { get; set; } = false;
    public bool SidebarVisible { get; set; } = true;
    public double SidebarWidth { get; set; } = 340;

    // Face presence / local identity. Disabled by default to preserve current behavior.
    public FaceFeatureSettings FaceFeatures { get; set; } = new();

    // Local iPhone/browser remote. Disabled by default and only exposed on the LAN when started.
    public PhoneRemoteSettings PhoneRemote { get; set; } = new();
}

public class PhoneRemoteSettings
{
    public bool Enabled { get; set; } = false;
    public int Port { get; set; } = 5100;
    public string Pin { get; set; } = "";
    public bool PlayAudioOnPhone { get; set; } = true;
}

public static class SettingsManager
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceChatbot", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    // Secrets stay plain in AppSettings and are stored as "dpapi:<base64>" in settings.json.
    private static readonly (string Path, string Label)[] SecretFields =
    {
        (nameof(AppSettings.OpenAiCompatibleApiKey), "OpenAI API key"),
        (nameof(AppSettings.HermesSshPassword), "SSH password"),
        (nameof(AppSettings.TavilyApiKey), "Tavily API key"),
        ($"{nameof(AppSettings.PhoneRemote)}.{nameof(PhoneRemoteSettings.Pin)}", "phone remote PIN"),
    };

    private static readonly ISecretProtector Protector = new DpapiSecretProtector();
    private static readonly object SaveLock = new();
    private static string? _loadWarning;
    private static bool _plainSaveLogged;

    /// <summary>True when secrets are written encrypted (Windows DPAPI is available).</summary>
    public static bool SecretsEncrypted => Protector.IsAvailable;

    /// <summary>
    /// A warning from the last Load for the user (for example secrets that could not be decrypted).
    /// Returns it once, then null.
    /// </summary>
    public static string? TakeLoadWarning() => Interlocked.Exchange(ref _loadWarning, null);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                if (JsonNode.Parse(json) is not JsonObject root)
                {
                    AppLog.Warn("settings.json is empty or not a JSON object. Using defaults.");
                    return new AppSettings();
                }

                var secrets = SettingsSecrets.UnprotectFields(root, SecretFields.Select(f => f.Path), Protector);
                var settings = root.Deserialize<AppSettings>(JsonOpts) ?? new AppSettings();
                if (!AppSettings.IsUsableTranscriberCommand(settings.ExternalNpuTranscriberCommand))
                    settings.ExternalNpuTranscriberCommand = AppSettings.DefaultRyzenAiWhisperCommand;

                ReportSecretLoad(secrets);
                // Encrypt secrets an older version left in plain text now instead of at exit.
                if (secrets.Plain.Count > 0 && Protector.IsAvailable)
                    Save(settings);
                return settings;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not read settings.json. Using defaults.", ex);
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            lock (SaveLock)
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var root = JsonSerializer.SerializeToNode(settings, JsonOpts)!.AsObject();
                var leftPlain = SettingsSecrets.ProtectFields(root, SecretFields.Select(f => f.Path), Protector);
                if (leftPlain.Count > 0 && !_plainSaveLogged)
                {
                    _plainSaveLogged = true;
                    AppLog.Warn($"Could not encrypt {DescribeFields(leftPlain)}; saved as plain text.");
                }

                File.WriteAllText(Path, root.ToJsonString(JsonOpts));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not save settings.json.", ex);
        }
    }

    private static void ReportSecretLoad(SecretLoadResult secrets)
    {
        if (secrets.Plain.Count > 0)
            AppLog.Info($"Found {DescribeFields(secrets.Plain)} stored as plain text; encrypting it.");

        if (secrets.Failed.Count == 0)
            return;

        var warning = $"Could not decrypt the saved {DescribeFields(secrets.Failed)}. " +
                      "settings.json was probably copied from another Windows user or PC. " +
                      $"Please enter {(secrets.Failed.Count == 1 ? "it" : "them")} again in Settings.";
        AppLog.Warn(warning);
        _loadWarning = warning;
    }

    private static string DescribeFields(IEnumerable<string> paths)
    {
        var labels = paths
            .Select(p => SecretFields.FirstOrDefault(f => f.Path == p).Label ?? p)
            .ToList();
        return labels.Count <= 1
            ? string.Join("", labels)
            : string.Join(", ", labels.Take(labels.Count - 1)) + " and " + labels[^1];
    }
}
