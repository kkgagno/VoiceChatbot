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
    /// <summary>Version of the one-time fixes MainWindow applied to these settings (0 = none yet).</summary>
    public const int CurrentSettingsVersion = 3;
    public int SettingsVersion { get; set; }

    public const string DefaultTavilyApiKey = "";
    public const string DefaultRyzenAiWhisperCommand = "call \"%USERPROFILE%\\VoiceChatbot\\tools\\ryzen-ai-whisper-transcribe.bat\" {input}";
    /// <summary>
    /// False for blank commands and leftover placeholders such as "{ryzen}" that cmd.exe cannot run.
    /// </summary>
    public static bool IsUsableTranscriberCommand(string? command)
    {
        return TranscriberCommand.IsUsable(command);
    }

    // Chat backend. "Built-in model" runs LocalModelId with the bundled llama.cpp server (LocalModelServer).
    public string ChatProvider { get; set; } = ChatProviders.BuiltIn;
    public string LocalModelId { get; set; } = LocalModelCatalog.DefaultId;
    // False until the model chooser has been answered (or skipped) once; it opens at startup until then.
    public bool ModelSetupDone { get; set; }
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string OpenAiCompatibleUrl { get; set; } = "http://localhost:8080/v1";
    public string OpenAiCompatibleApiKey { get; set; } = "";
    // The OpenAI (cloud) key, kept while another OpenAI-compatible server is in use (model chooser).
    public string OpenAiCloudApiKey { get; set; } = "";
    public string Model { get; set; } = "llama3";
    public string SystemPrompt { get; set; } = "You are a helpful, friendly AI assistant. Keep responses concise and conversational since they will be spoken aloud. Light Markdown such as short lists, bold text or a small table is fine because it is rendered on screen and removed before speaking. Avoid emojis and hashtags. If the user explicitly asks for code, markup, an SVG, or a script, provide it in a fenced code block.";
    public double Temperature { get; set; } = 0.7;
    // Native tool calling (web search, date/time, stock quotes, memories, web pages). Falls back per model when unsupported.
    public bool UseTools { get; set; } = true;
    // Show assistant replies as formatted Markdown (headings, lists, tables, links). Off shows plain text.
    public bool RenderMarkdown { get; set; } = true;
    // "Hide model thinking": ask the server to skip the model's thinking phase (OllamaClient). Planning
    // notes a model still writes are kept out of the chat, the history and speech either way (PlanningNotes).
    public bool DisableModelThinking { get; set; } = true;

    // Voice Input
    public string InputLanguage { get; set; } = "en-US";
    public double SilenceTimeout { get; set; } = 2.4;
    public int NoiseSuppression { get; set; } = 30;
    // False = "Only respond after the wake word": speech is answered only when it contains WakeWord.
    public bool AutoDetectVoice { get; set; } = true;
    public string WakeWord { get; set; } = WakeWordText.DefaultPhrase;
    public int MicDeviceIndex { get; set; } = -1;
    public string WhisperModelSize { get; set; } = "small";
    public string TranscriptionBackend { get; set; } = "Whisper.net";
    public string ExternalNpuTranscriberCommand { get; set; } = DefaultRyzenAiWhisperCommand;

    // Voice Output
    public string VoiceName { get; set; } = "am_onyx (American Male)";
    public int SpeechRate { get; set; } = 1;
    public int Volume { get; set; } = 80;
    public bool TtsEnabled { get; set; } = true;
    // Run Whisper.net on the GPU through Vulkan when one is available. Off (the default) uses the CPU:
    // on some AMD integrated GPUs the Vulkan build repeats or drops words.
    public bool WhisperUseGpu { get; set; } = false;
    // Speak a streamed reply in pieces of a few sentences while it is written. Off (the default) speaks
    // the whole reply in one go once it is finished, which sounds more natural with Kokoro.
    public bool StreamingSpeechEnabled { get; set; } = false;

    // Kokoro text-to-speech. Leave the remote URL blank to use only the bundled local server.
    // Accepts "192.168.1.50", "192.168.1.50:8880" or "http://host:8880/v1".
    public string KokoroRemoteUrl { get; set; } = "";
    public string KokoroMode { get; set; } = KokoroEndpoint.ModeAuto;

    // Conversation
    public int MaxContextMessages { get; set; } = 20;
    public bool StreamResponses { get; set; } = true;
    // Personas: named presets for the system prompt, voice, speech rate and (optionally) model.
    // "Default" is created from the current prompt/voice/rate on first run.
    public List<Persona> Personas { get; set; } = new();
    public string ActivePersona { get; set; } = "";
    // Knowledge folder: the documents in KnowledgeFolder are indexed into %APPDATA%\VoiceChatbotMini\knowledge-index.json;
    // while KnowledgeEnabled is on, up to KnowledgeMaxChunks matching excerpts go with each message.
    public bool KnowledgeEnabled { get; set; } = false;
    public string KnowledgeFolder { get; set; } = "";
    public int KnowledgeMaxChunks { get; set; } = KnowledgeIndex.DefaultMaxChunks;
    // Saved memories in the system prompt: "Relevant" = best matches for the message (up to
    // MemoryMaxItems) plus the newest memory, "All" = every saved memory.
    public string MemoryMode { get; set; } = MemorySelector.ModeRelevant;
    public int MemoryMaxItems { get; set; } = MemorySelector.DefaultMaxItems;
    // Saved chat history: one JSON file per conversation in %APPDATA%\VoiceChatbotMini\conversations.
    public bool SaveConversationHistory { get; set; } = true;

    // Web Search
    public string TavilyApiKey { get; set; } = DefaultTavilyApiKey;
    public bool WebSearchEnabled { get; set; } = true;
    public int MaxTokens { get; set; } = 2048;
    /// <summary>Largest context window to use; sent to Ollama as num_ctx. 0 = the model's full window.</summary>
    public int ContextWindow { get; set; } = TokenBudget.DefaultContextWindow;

    // Window
    public double WindowLeft { get; set; } = -1;
    public double WindowTop { get; set; } = -1;
    public double WindowWidth { get; set; } = 1100;
    public double WindowHeight { get; set; } = 750;
    public bool WindowMaximized { get; set; } = false;
    public bool SidebarVisible { get; set; } = true;
    public double SidebarWidth { get; set; } = 340;
    // One of ThemePalette.Choices ("Dark", "Light", "Use Windows setting"); see ThemeManager.cs.
    public string Theme { get; set; } = ThemePalette.Default;
    // Token counts and the transcription backend in the chat (always written to the log).
    public bool ShowDiagnostics { get; set; } = false;
    // Tray icon and global listen hotkey (MainWindow.Tray.cs). Minimizing hides to the tray only when this is on.
    public bool MinimizeToTray { get; set; } = false;
    // One of GlobalHotkeys.Choices ("Off", "Ctrl+Alt+Space", "Ctrl+Shift+Space", "Ctrl+Alt+L").
    public string GlobalListenHotkey { get; set; } = GlobalHotkeys.Default;

    // Local iPhone/browser remote. Disabled by default and only exposed on the LAN when started.
    public PhoneRemoteSettings PhoneRemote { get; set; } = new();

    // Live Transcriber window: size, pane heights, text size, audio source and summary style.
    public TranscriberSettings Transcriber { get; set; } = new();
}

public class TranscriberSettings
{
    public const string DefaultSystemPrompt = "You are a live transcriber and summarizer. Produce accurate, concise transcripts from spoken audio. When summarizing, preserve decisions, action items, names, dates, numbers, and important context. Do not invent details.";
    public const string SourceMicrophone = "Microphone";
    public const string SourcePcAudio = "PC audio";

    // -1/-1 = not placed yet (centered on screen).
    public double Left { get; set; } = -1;
    public double Top { get; set; } = -1;
    public double Width { get; set; } = 1000;
    public double Height { get; set; } = 760;
    public bool Maximized { get; set; } = false;
    // Star heights of the transcript and summary panes (only their ratio matters).
    public double TranscriptPaneHeight { get; set; } = 2;
    public double SummaryPaneHeight { get; set; } = 1;
    public double SystemPromptHeight { get; set; } = 90;
    public double FontSize { get; set; } = 15;
    public string Source { get; set; } = SourceMicrophone;
    public string SummaryStyle { get; set; } = TranscriptSummaryStyles.Summary;
    public string SystemPrompt { get; set; } = DefaultSystemPrompt;
    // Live notes: while recording, notes on what was said since the last update are added to the notes pane.
    // Settings files from before this option load with it off and the default interval.
    public bool LiveNotes { get; set; } = false;
    // One of LiveNotesPolicy.IntervalChoicesMinutes (5, 10, 15); other values are rounded to the nearest (2 becomes 5).
    public int LiveNotesIntervalMinutes { get; set; } = LiveNotesPolicy.DefaultIntervalMinutes;
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
    private static readonly string Path = AppPaths.SettingsFile;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    // Secrets stay plain in AppSettings and are stored as "dpapi:<base64>" in settings.json.
    private static readonly (string Path, string Label)[] SecretFields =
    {
        (nameof(AppSettings.OpenAiCompatibleApiKey), "OpenAI API key"),
        (nameof(AppSettings.OpenAiCloudApiKey), "OpenAI (cloud) API key"),
        (nameof(AppSettings.TavilyApiKey), "Tavily API key"),
        ($"{nameof(AppSettings.PhoneRemote)}.{nameof(PhoneRemoteSettings.Pin)}", "phone remote PIN"),
    };

    private static readonly ISecretProtector Protector = new DpapiSecretProtector();
    private static readonly object SaveLock = new();
    private static string? _loadWarning;
    private static string? _recoveryNotice;
    private static string? _importNotice;
    private static int _importChecked;
    private static bool _plainSaveLogged;
    // Set when settings.json could not be read: the file is kept as it is until the user changes a setting.
    private static volatile bool _keepUnreadableFile;

    /// <summary>True when secrets are written encrypted (Windows DPAPI is available).</summary>
    public static bool SecretsEncrypted => Protector.IsAvailable;

    /// <summary>
    /// A warning from the last Load for the user (for example secrets that could not be decrypted).
    /// Returns it once, then null.
    /// </summary>
    public static string? TakeLoadWarning() => Interlocked.Exchange(ref _loadWarning, null);

    /// <summary>
    /// The notice for the user after settings.json could not be read and was backed up. Returns it once, then null.
    /// </summary>
    public static string? TakeRecoveryNotice() => Interlocked.Exchange(ref _recoveryNotice, null);

    /// <summary>
    /// The notice for the user after the first run copied the full app's settings. Returns it once, then null.
    /// </summary>
    public static string? TakeImportNotice() => Interlocked.Exchange(ref _importNotice, null);

    /// <summary>
    /// Reads settings.json. Properties Mini does not know, such as the full app's settings for features Mini
    /// does not have, are ignored, so the full app's file loads as it is (see ImportFullAppSettingsOnce).
    /// </summary>
    public static AppSettings Load()
    {
        ImportFullAppSettingsOnce();
        try
        {
            if (File.Exists(Path))
            {
                var json = File.ReadAllText(Path);
                if (JsonNode.Parse(json) is not JsonObject root)
                {
                    AppLog.Warn("settings.json is empty or not a JSON object. Using defaults.");
                    HandleUnreadableFile();
                    return new AppSettings();
                }

                var secrets = SettingsSecrets.UnprotectFields(root, SecretFields.Select(f => f.Path), Protector);
                var settings = root.Deserialize<AppSettings>(JsonOpts) ?? new AppSettings();
                // "Transcriber": null in the file would otherwise leave no transcriber settings at all.
                settings.Transcriber ??= new TranscriberSettings();
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
            HandleUnreadableFile();
        }
        return new AppSettings();
    }

    /// <summary>
    /// First start of Mini on a PC with the full Voice Chatbot app: copies its settings.json (only that file)
    /// so the backend, voice, personas and keys carry over. Checked once per run, before the first read.
    /// </summary>
    private static void ImportFullAppSettingsOnce()
    {
        if (Interlocked.Exchange(ref _importChecked, 1) != 0)
            return;

        var result = AppPaths.ImportFullAppSettingsIfMissing(AppPaths.DataDirectory, AppPaths.FullAppDataDirectory);
        switch (result.Status)
        {
            case SettingsImportStatus.Imported:
                AppLog.Info(result.Describe());
                _importNotice = $"Copied your settings from the full Voice Chatbot app ({result.Source}). " +
                                $"Voice Chatbot Mini keeps its own chats, memories, Whisper models and logs in {AppPaths.DataDirectory}.";
                break;
            case SettingsImportStatus.Failed:
                AppLog.Warn(result.Describe());
                break;
        }
    }

    /// <summary>
    /// Copies an unreadable settings.json to settings.json.bak (or a timestamped .bak when one exists),
    /// prepares a one-time notice and keeps the file from being overwritten until the user changes something.
    /// </summary>
    private static void HandleUnreadableFile()
    {
        _keepUnreadableFile = true;
        string? backup = null;
        try
        {
            if (File.Exists(Path))
            {
                backup = Path + ".bak";
                if (File.Exists(backup))
                    backup = $"{Path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";
                File.Copy(Path, backup, overwrite: false);
                AppLog.Warn($"Backed up the unreadable settings.json to {backup}.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not back up the unreadable settings.json.", ex);
            backup = null;
        }

        _recoveryNotice = backup is null
            ? "Your settings could not be read, so default settings are in use. The settings file is left unchanged until you change a setting."
            : $"Your settings could not be read, so default settings are in use. A copy of the old file was saved to {backup}. " +
              "The settings file is left unchanged until you change a setting.";
    }

    /// <summary>
    /// Writes settings.json. With <paramref name="userChange"/> false (saving on exit), nothing is written
    /// while an unreadable settings.json is being kept; the first save after a user change replaces it.
    /// </summary>
    public static void Save(AppSettings settings, bool userChange = true)
    {
        if (_keepUnreadableFile)
        {
            if (!userChange)
                return;

            _keepUnreadableFile = false;
            AppLog.Info("A setting was changed; replacing the unreadable settings.json (a backup was kept).");
        }

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
