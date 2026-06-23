using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceChatbot;

public class AppSettings
{
    public const int DefaultImageWidth = 1080;
    public const int DefaultImageHeight = 1920;
    public const string DefaultTavilyApiKey = "";
    public const string DefaultRyzenAiWhisperCommand = "call \"%USERPROFILE%\\VoiceChatbot\\tools\\ryzen-ai-whisper-transcribe.bat\" {input}";
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
    public string LiveTranscriberSystemPrompt { get; set; } = "You are a live transcriber and summarizer. Produce accurate, concise transcripts from spoken audio. When summarizing, preserve decisions, action items, names, dates, numbers, and important context. Do not invent details.";

    // Voice Output
    public string VoiceName { get; set; } = "am_onyx (American Male)";
    public int SpeechRate { get; set; } = 1;
    public int Volume { get; set; } = 80;
    public bool TtsEnabled { get; set; } = true;

    // Conversation
    public int MaxContextMessages { get; set; } = 20;
    public bool StreamResponses { get; set; } = true;
    public bool DarkMode { get; set; } = false;
    public bool SettingsPanelOpen { get; set; } = true;

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
    public double WindowHeight { get; set; } = 900;
    public int DesktopLayoutVersion { get; set; } = 0;

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

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts) ?? new AppSettings();
                if (string.IsNullOrWhiteSpace(settings.ExternalNpuTranscriberCommand))
                    settings.ExternalNpuTranscriberCommand = AppSettings.DefaultRyzenAiWhisperCommand;
                return settings;
            }
        }
        catch { /* ignore errors, use defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, JsonOpts);
            File.WriteAllText(Path, json);
        }
        catch { /* best effort */ }
    }
}
