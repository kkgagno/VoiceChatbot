using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Cv2 = OpenCvSharp.Cv2;
using Mat = OpenCvSharp.Mat;

namespace VoiceChatbot;

public partial class MainWindow : Window
{
    private const int CodeResponseMaxTokens = 32768;
    private const int CodeContextTokens = 131072;
    private const int CodeContinuationMaxAttempts = 6;
    private const int LargePasteChars = 24000;
    private const int MaxCurrentModelInputChars = 300000;
    private const int EstimatedCharsPerToken = 4;
    private const int ContextSafetyTokens = 4096;
    private static readonly List<string> Krea2AspectRatios = new()
    {
        "1:1 (Square)",
        "3:2 (Photo)",
        "4:3 (Standard)",
        "16:9 (Widescreen)",
        "21:9 (Ultrawide)",
        "2:3 (Portrait Photo)",
        "3:4 (Portrait Standard)",
        "9:16 (Portrait Widescreen)"
    };
    private static readonly string SyncedVideoDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "VoiceChatbot",
        "videos");
    private OllamaClient _ollama;
    private HermesSshClient _hermesSsh;
    private string _pendingHermesControlCommand = "";
    private string _pendingHermesSshCommand = "";
    private PiAgentService _piAgent;
    private YouTubeTranscriptService _youtubeTranscripts;
    private DocumentTextService _documentText;
    private TavilySearchClient _tavily;
    private ComfyUiImageClient _comfyImages;
    private SpeechEngine _speech;
    private CameraService _camera;
    private PhoneRemoteServer _phoneRemoteServer;
    private FacePresenceMonitor? _facePresenceMonitor;
    private FaceAccessPolicyEvaluator _facePolicy = null!;
    private FaceIdentityManager _faceIdentityManager = null!;
    private FaceDetectionSnapshot? _latestFaceSnapshot;
    private bool _recognitionInProgress;
    private FacePresenceState _facePresenceState = FacePresenceState.CameraUnavailable;
    private FaceIdentity _recognizedFaceIdentity = FaceIdentity.Unknown;
    private FaceIdentity _lastIdentifiedFaceIdentity = FaceIdentity.Unknown;
    private string _recognizedFaceName = "";
    private string _lastIdentifiedFaceName = "";
    private DateTime _lastIdentifiedFaceUtc = DateTime.MinValue;
    private DateTime _lastRecognizedFaceUtc = DateTime.MinValue;
    private DateTime _lastPreviewUpdatedUtc = DateTime.MinValue;
    private DateTime _lastRecognitionStartedUtc = DateTime.MinValue;
    private float _lastRecognizedSimilarity;
    private FaceAccessDecision _faceAccessDecision = new();
    private ConversationHistory _history;
    private AppSettings _settings;
    private Krea2Window? _krea2Window;
    private CancellationTokenSource? _chatCts;
    private DispatcherTimer _volumeTimer = null!;
    private DispatcherTimer _statusTimer = null!;
    private DispatcherTimer _schedulerTimer = null!;
    private SchedulerStore _schedulerStore;
    private SchedulerWindow? _schedulerWindow;
    private bool _schedulerRunning;
    private bool _autoListening;
    private bool _pausedListeningForTextInput;
    private DateTime _listenStartTime;
    private DateTime _conversationStartTime;
    private List<ConversationMemory> _loadedMemories = new();
    private readonly List<PendingImageAttachment> _pendingImages = new();
    private readonly List<PendingDocumentAttachment> _pendingDocuments = new();
    private readonly List<PendingDocumentAttachment> _activeDocuments = new();
    private readonly List<PendingDocumentAttachment> _activePhoneDocuments = new();
    private readonly Dictionary<DependencyObject, ThemeSnapshot> _themeSnapshots = new();
    private readonly SemaphoreSlim _phoneRemoteChatLock = new(1, 1);
    private TranscriptionWindow? _transcriptionWindow;
    private string _latestLiveTranscript = "";
    private string _latestLiveTranscriptSummary = "";
    private string _latestGeneratedImagePath = "";
    private string _latestGeneratedVideoPath = "";
    private string _pendingVideoAudioPath = "";
    private readonly List<RecentWebSearchContext> _recentWebSearchContexts = new();
    private bool _desktopModelRunning;
    private bool _comfyUiRunning;
    private bool _serviceControlBusy;
    private bool _restoringRecentChat;
    private string _desktopRunningModel = "";

    private double ChatContentWidth => Math.Max(360, ChatScroll.ActualWidth - 64);
    private double UserBubbleMaxWidth => Math.Clamp(ChatContentWidth * 0.78, 500, 1400);
    private double AssistantBubbleMaxWidth => Math.Clamp(ChatContentWidth * 0.88, 600, 1800);
    private double CodeBlockMaxWidth => Math.Clamp(ChatContentWidth * 0.86, 560, 1760);

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsManager.Load();
        _schedulerStore = SchedulerStore.Load();
        _history = new ConversationHistory();
        _history.Changed += SaveRecentChatSnapshot;
        _ollama = new OllamaClient(_settings.OllamaUrl);
        _hermesSsh = new HermesSshClient();
        _piAgent = new PiAgentService(GetDefaultPiWorkingDirectory());
        _youtubeTranscripts = new YouTubeTranscriptService();
        _documentText = new DocumentTextService();
        ConfigureChatClient();
        _tavily = new TavilySearchClient(_settings.TavilyApiKey);
        _comfyImages = new ComfyUiImageClient { BaseUrl = _settings.ComfyUiUrl };
        _speech = new SpeechEngine();
        _camera = new CameraService();
        _phoneRemoteServer = new PhoneRemoteServer(
            (stream, ct) => _speech.TranscribeWavAsync(stream, ct),
            (stream, ct) => _speech.ContainsSpeechWavAsync(stream, ct),
            HandlePhoneRemoteChatAsync,
            HandlePhoneRemoteToolAsync,
            HandlePhoneRemoteTextMessageAsync,
            HandlePhoneRemoteCalendarAsync,
            HandlePhoneRemoteGroundedAnswerAsync,
            (path, ct) => _documentText.ExtractAsync(path, ct),
            async (text, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                var speechText = CleanSpeechText(text);
                if (string.IsNullOrWhiteSpace(speechText))
                    return null;
                return await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
            },
            HandlePhoneKrea2OptionsAsync,
            HandlePhoneKrea2CreateAsync,
            GetPhoneRemoteModelState);
        _faceIdentityManager = new FaceIdentityManager(
            FaceServiceFactory.CreateProfileStore(_settings.FaceFeatures.ModelOptions),
            _settings.FaceFeatures.ModelOptions);

        Loaded += MainWindow_Loaded;
        SizeChanged += (_, _) => UpdateChatBubbleWidths();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            ApplySettings();
            ApplyTheme(_settings.DarkMode);
            SetupSliderBindings();
            SetupTimers();
            RestoreRecentChat();

            // Initialize speech - may fail on some systems
            try
            {
                InitializeSpeech();
                _speech.SpeechRecognized += OnSpeechRecognized;
                _speech.SpeechFinished += OnSpeechFinished;
                _speech.StateChanged += OnVoiceStateChanged;
                _speech.ListeningTimedOut += OnListenTimedOut;
                _speech.Log += OnSpeechLog;
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Speech init failed: {ex.Message}. Text-only mode active.");
            }

            // Test connection
            await TestConnection();

            _facePolicy = new FaceAccessPolicyEvaluator(_settings.FaceFeatures.Policy);
            await InitializeFacePresenceAsync();
            await RefreshFaceProfileChoicesAsync();
            await StartPhoneRemoteIfEnabledAsync();

            // Load models
            try
            {
                await RefreshModelsInternal();
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Could not load models: {ex.Message}");
            }

            // Select saved model
            if (!string.IsNullOrEmpty(_settings.Model) && ModelCombo.Items.Contains(_settings.Model))
                ModelCombo.SelectedItem = _settings.Model;
            else if (ModelCombo.Items.Count > 0)
                ModelCombo.SelectedIndex = 0;

            await InitializeDesktopServiceControlsAsync();

            // Keyboard shortcut: Enter to send, Shift+Enter for new line
            MessageInput.PreviewKeyDown += (s, ev) =>
            {
                if (ev.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && TryAttachClipboardImage())
                {
                    ev.Handled = true;
                }
            };
            DataObject.AddPastingHandler(MessageInput, MessageInput_Pasting);
            MessageInput.KeyDown += (s, ev) =>
            {
                PauseListeningForTextInput();
                if (ev.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                {
                    SendText_Click(s, ev);
                    ev.Handled = true;
                }
            };
            MessageInput.PreviewMouseDown += (s, ev) => PauseListeningForTextInput();
            MessageInput.GotKeyboardFocus += (s, ev) => PauseListeningForTextInput();
            MessageInput.LostKeyboardFocus += (s, ev) => ResumeListeningAfterTextInput();

            // Always-listen toggle
            AlwaysListenToggle.Checked += (s, ev) => StartAutoListen();
            AlwaysListenToggle.Unchecked += (s, ev) => StopAutoListen();

            // Load conversation memories
            _loadedMemories = MemoryManager.LoadAll();
            if (_loadedMemories.Count > 0)
                AddSystemMessage($"Loaded {_loadedMemories.Count} conversation memory(ies). AI has context from previous sessions.");

            _conversationStartTime = DateTime.Now;
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Startup error: {ex}");
            System.Diagnostics.Debug.WriteLine($"Startup error: {ex}");
        }
    }

    // ==================== Settings ====================

    private void ApplySettings()
    {
        if (_settings.DesktopLayoutVersion < 3)
        {
            _settings.WindowWidth = 1100;
            _settings.WindowHeight = 900;
            _settings.DesktopLayoutVersion = 3;
        }

        OllamaUrlBox.Text = _settings.OllamaUrl;
        OpenAiUrlBox.Text = _settings.OpenAiCompatibleUrl;
        OpenAiApiKeyBox.Password = _settings.OpenAiCompatibleApiKey;
        HermesSshHostBox.Text = _settings.HermesSshHost;
        HermesSshPortBox.Text = _settings.HermesSshPort.ToString();
        HermesSshUserBox.Text = _settings.HermesSshUser;
        HermesSshPasswordBox.Password = _settings.HermesSshPassword;
        SelectProviderCombo(_settings.ChatProvider);
        SystemPromptBox.Text = _settings.SystemPrompt;
        TempSlider.Value = _settings.Temperature;
        if (_settings.SilenceTimeout < 2.0)
            _settings.SilenceTimeout = 2.4;
        SilenceSlider.Value = _settings.SilenceTimeout;
        NoiseSlider.Value = _settings.NoiseSuppression;
        AutoDetectToggle.IsChecked = _settings.AutoDetectVoice;
        WakeWordBox.Text = _settings.WakeWord;
        WakeWordBox.IsEnabled = !_settings.AutoDetectVoice;
        SelectTranscriptionBackendCombo(_settings.TranscriptionBackend);
        NpuTranscriberCommandBox.Text = _settings.ExternalNpuTranscriberCommand;
        RateSlider.Value = _settings.SpeechRate;
        VolumeSlider.Value = _settings.Volume;
        TtsToggle.IsChecked = _settings.TtsEnabled;
        ContextSlider.Value = _settings.MaxContextMessages;
        StreamToggle.IsChecked = _settings.StreamResponses;
        DarkModeToggle.IsChecked = _settings.DarkMode;
        DarkModeToggle.Content = _settings.DarkMode ? "Light" : "Dark";
        SetSettingsPanelOpen(_settings.SettingsPanelOpen, save: false);
        WebSearchToggle.IsChecked = _settings.WebSearchEnabled;
        WebSearchToggle.Content = _settings.WebSearchEnabled ? "Web Search ON" : "Web Search OFF";
        TavilyApiKeyBox.Password = _settings.TavilyApiKey;
        ComfyUrlBox.Text = _settings.ComfyUiUrl;
        if (_settings.ImageWidth == 1328 && _settings.ImageHeight == 1328)
        {
            _settings.ImageWidth = AppSettings.DefaultImageWidth;
            _settings.ImageHeight = AppSettings.DefaultImageHeight;
        }

        ImageWidthBox.Text = _settings.ImageWidth.ToString();
        ImageHeightBox.Text = _settings.ImageHeight.ToString();
        QwenCreateStepsBox.Text = _settings.QwenCreateSteps.ToString();
        if (_settings.QwenEditSteps <= 4)
            _settings.QwenEditSteps = 40;
        QwenEditStepsBox.Text = _settings.QwenEditSteps.ToString();
        VideoSecondsBox.Text = _settings.VideoSeconds.ToString();
        VideoFpsBox.Text = _settings.VideoFps.ToString();
        FaceFeaturesToggle.IsChecked = _settings.FaceFeatures.CameraFeaturesEnabled;
        FaceFeaturesToggle.Content = _settings.FaceFeatures.CameraFeaturesEnabled ? "Camera ON" : "Camera OFF";
        FaceGatingToggle.IsChecked = _settings.FaceFeatures.FaceGatingEnabled;
        FaceGatingToggle.Content = _settings.FaceFeatures.FaceGatingEnabled ? "Use Identity ON" : "Use Identity OFF";
        FacePolicyText.Text = _settings.FaceFeatures.FaceGatingEnabled ? "Face gating enabled" : "Face gating disabled";
        PhoneRemoteToggle.IsChecked = _settings.PhoneRemote.Enabled;
        PhoneRemoteToggle.Content = _settings.PhoneRemote.Enabled ? "Phone Remote ON" : "Phone Remote OFF";
        PhoneRemotePortBox.Text = _settings.PhoneRemote.Port.ToString();
        PhoneRemotePinBox.Text = _settings.PhoneRemote.Pin;
        PhoneRemoteAudioToggle.IsChecked = _settings.PhoneRemote.PlayAudioOnPhone;
        PhoneRemoteAudioToggle.Content = _settings.PhoneRemote.PlayAudioOnPhone ? "Phone Audio ON" : "Phone Audio OFF";
        UpdatePhoneRemoteUi();
        UpdateFacePresenceUi(_settings.FaceFeatures.CameraFeaturesEnabled
            ? FacePresenceState.CameraUnavailable
            : FacePresenceState.CameraUnavailable);

        _history.MaxMessages = _settings.MaxContextMessages;

        // Window position
        if (_settings.WindowLeft >= SystemParameters.VirtualScreenLeft &&
            _settings.WindowTop >= SystemParameters.VirtualScreenTop &&
            _settings.WindowLeft < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 120 &&
            _settings.WindowTop < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 80)
        {
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
        Width = Math.Max(_settings.WindowWidth, MinWidth);
        Height = Math.Max(_settings.WindowHeight, MinHeight);
    }

    private async Task RefreshFaceProfileChoicesAsync()
    {
        if (_faceIdentityManager == null)
            return;

        var current = GetSelectedFaceProfileName();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase) { "Keith" };
        foreach (var profile in await _faceIdentityManager.LoadProfilesAsync())
        {
            if (!string.IsNullOrWhiteSpace(profile.DisplayName))
                names.Add(profile.DisplayName);
        }

        FaceProfileCombo.Items.Clear();
        foreach (var name in names)
        {
            FaceProfileCombo.Items.Add(new ComboBoxItem
            {
                Content = name,
                Foreground = Brushes.Black
            });
        }

        FaceProfileCombo.Text = string.IsNullOrWhiteSpace(current) ? "Keith" : current;
    }

    private string GetSelectedFaceProfileName()
    {
        var text = FaceProfileCombo.Text;
        if (string.IsNullOrWhiteSpace(text) && FaceProfileCombo.SelectedItem is ComboBoxItem item)
            text = item.Content?.ToString() ?? "";

        return string.IsNullOrWhiteSpace(text) ? "Keith" : text.Trim();
    }

    private void SaveSettings()
    {
        _settings.OllamaUrl = OllamaUrlBox.Text.Trim();
        _settings.OpenAiCompatibleUrl = OpenAiUrlBox.Text.Trim();
        _settings.OpenAiCompatibleApiKey = OpenAiApiKeyBox.Password.Trim();
        _settings.HermesSshHost = HermesSshHostBox.Text.Trim();
        _settings.HermesSshPort = int.TryParse(HermesSshPortBox.Text.Trim(), out var hermesSshPort)
            ? Math.Clamp(hermesSshPort, 1, 65535)
            : 2222;
        _settings.HermesSshUser = HermesSshUserBox.Text.Trim();
        _settings.HermesSshPassword = HermesSshPasswordBox.Password;
        _settings.ChatProvider = GetSelectedProvider();
        _settings.Model = ModelCombo.Text;
        _settings.SystemPrompt = SystemPromptBox.Text;
        _settings.Temperature = TempSlider.Value;
        _settings.InputLanguage = InputLangCombo.Text;
        _settings.SilenceTimeout = SilenceSlider.Value;
        _settings.NoiseSuppression = (int)NoiseSlider.Value;
        _settings.AutoDetectVoice = AutoDetectToggle.IsChecked == true;
        _settings.WakeWord = WakeWordBox.Text;
        _settings.MicDeviceIndex = MicCombo.SelectedItem is AudioDeviceInfo mic ? mic.Index : -1;
        _settings.TranscriptionBackend = GetSelectedTranscriptionBackend();
        var npuCommand = NpuTranscriberCommandBox.Text.Trim();
        _settings.ExternalNpuTranscriberCommand = string.IsNullOrWhiteSpace(npuCommand)
            ? AppSettings.DefaultRyzenAiWhisperCommand
            : npuCommand;
        _settings.VoiceName = VoiceCombo.Text;
        _settings.SpeechRate = (int)RateSlider.Value;
        _settings.Volume = (int)VolumeSlider.Value;
        _settings.TtsEnabled = TtsToggle.IsChecked == true;
        _settings.MaxContextMessages = (int)ContextSlider.Value;
        _settings.StreamResponses = StreamToggle.IsChecked == true;
        _settings.DarkMode = DarkModeToggle.IsChecked == true;
        _settings.SettingsPanelOpen = SettingsPanel.Visibility == Visibility.Visible;
        _settings.WebSearchEnabled = WebSearchToggle.IsChecked == true;
        var tavilyKey = TavilyApiKeyBox.Password.Trim();
        _settings.TavilyApiKey = string.IsNullOrWhiteSpace(tavilyKey)
            ? AppSettings.DefaultTavilyApiKey
            : tavilyKey;
        _settings.ComfyUiUrl = string.IsNullOrWhiteSpace(ComfyUrlBox.Text)
            ? "http://localhost:8000"
            : ComfyUrlBox.Text.Trim();
        _settings.ImageWidth = ParseBoundedInt(ImageWidthBox.Text, AppSettings.DefaultImageWidth, 256, 2048);
        _settings.ImageHeight = ParseBoundedInt(ImageHeightBox.Text, AppSettings.DefaultImageHeight, 256, 2048);
        _settings.QwenCreateSteps = ParseBoundedInt(QwenCreateStepsBox.Text, 4, 1, 80);
        _settings.QwenEditSteps = ParseBoundedInt(QwenEditStepsBox.Text, 40, 1, 80);
        _settings.VideoSeconds = ParseBoundedInt(VideoSecondsBox.Text, 6, 1, 30);
        _settings.VideoFps = ParseBoundedInt(VideoFpsBox.Text, 24, 1, 60);
        _settings.FaceFeatures.CameraFeaturesEnabled = FaceFeaturesToggle.IsChecked == true;
        _settings.FaceFeatures.FaceGatingEnabled = FaceGatingToggle.IsChecked == true;
        _settings.PhoneRemote.Enabled = PhoneRemoteToggle.IsChecked == true;
        _settings.PhoneRemote.Port = int.TryParse(PhoneRemotePortBox.Text.Trim(), out var phonePort)
            ? Math.Clamp(phonePort, 1024, 65535)
            : 5100;
        _settings.PhoneRemote.Pin = PhoneRemotePinBox.Text.Trim();
        _settings.PhoneRemote.PlayAudioOnPhone = PhoneRemoteAudioToggle.IsChecked == true;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;

        SettingsManager.Save(_settings);
        ConfigureChatClient();
        ConfigureImageClient();
    }

    private void ConfigureChatClient()
    {
        _ollama.Provider = _settings.ChatProvider;
        _ollama.BaseUrl = _settings.OllamaUrl;
        _ollama.OpenAiBaseUrl = _settings.OpenAiCompatibleUrl;
        _ollama.OpenAiApiKey = _settings.OpenAiCompatibleApiKey;
        _hermesSsh.Host = _settings.HermesSshHost;
        _hermesSsh.Port = _settings.HermesSshPort;
        _hermesSsh.User = _settings.HermesSshUser;
        _hermesSsh.Password = _settings.HermesSshPassword;
    }

    private void ConfigureImageClient()
    {
        _comfyImages.BaseUrl = string.IsNullOrWhiteSpace(_settings.ComfyUiUrl)
            ? "http://localhost:8000"
            : _settings.ComfyUiUrl;
    }

    private static int ParseBoundedInt(string text, int fallback, int min, int max)
    {
        return int.TryParse(text.Trim(), out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    private static string GetDefaultPiWorkingDirectory()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private string GetSelectedProvider()
    {
        if (ProviderCombo?.SelectedItem is ComboBoxItem item)
            return item.Content?.ToString() ?? "Ollama";
        return ProviderCombo?.Text ?? _settings.ChatProvider;
    }

    private void SelectProviderCombo(string provider)
    {
        foreach (var item in ProviderCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), provider, StringComparison.OrdinalIgnoreCase))
            {
                ProviderCombo.SelectedItem = item;
                return;
            }
        }

        ProviderCombo.SelectedIndex = 0;
    }

    private string GetSelectedTranscriptionBackend()
    {
        if (TranscriptionBackendCombo?.SelectedItem is ComboBoxItem item)
            return item.Content?.ToString() ?? "Whisper.net";
        return TranscriptionBackendCombo?.Text ?? _settings.TranscriptionBackend;
    }

    private void SelectTranscriptionBackendCombo(string backend)
    {
        foreach (var item in TranscriptionBackendCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), backend, StringComparison.OrdinalIgnoreCase))
            {
                TranscriptionBackendCombo.SelectedItem = item;
                return;
            }
        }

        TranscriptionBackendCombo.SelectedIndex = 0;
    }

    // ==================== Speech Init ====================

    private void InitializeSpeech()
    {
        _speech.InputLanguage = _settings.InputLanguage;
        _speech.SilenceTimeout = _settings.SilenceTimeout;
        _speech.NoiseGate = _settings.NoiseSuppression;
        _speech.AutoDetect = _settings.AutoDetectVoice;
        _speech.WakeWord = _settings.WakeWord;
        _speech.MicDeviceIndex = _settings.MicDeviceIndex;
        _speech.WhisperModelPath = GetWhisperModelPath(_settings.WhisperModelSize);
        _speech.TranscriptionBackend = _settings.TranscriptionBackend;
        _speech.ExternalNpuTranscriberCommand = _settings.ExternalNpuTranscriberCommand;
        _speech.VoiceName = _settings.VoiceName;
        _speech.SpeechRate = _settings.SpeechRate;
        _speech.Volume = _settings.Volume;
        _speech.TtsEnabled = _settings.TtsEnabled;

        _speech.Initialize();

        // Show any init errors
        if (!string.IsNullOrEmpty(_speech.InitError))
            AddSystemMessage($"{_speech.InitError}");

        PopulateMicrophoneCombo(_settings.MicDeviceIndex);
        SelectWhisperModelCombo(_settings.WhisperModelSize);

        // Populate voice combo with real TTS voices
        VoiceCombo.Items.Clear();
        foreach (var v in _speech.AvailableVoices)
            VoiceCombo.Items.Add(v);
        if (!string.IsNullOrEmpty(_settings.VoiceName) && VoiceCombo.Items.Contains(_settings.VoiceName))
            VoiceCombo.SelectedItem = _settings.VoiceName;
        else if (VoiceCombo.Items.Count > 0)
            VoiceCombo.SelectedIndex = 0;

        // Populate input language combo
        InputLangCombo.Items.Clear();
        foreach (var lang in _speech.AvailableLanguages)
            InputLangCombo.Items.Add(lang);
        if (InputLangCombo.Items.Contains(_settings.InputLanguage))
            InputLangCombo.SelectedItem = _settings.InputLanguage;
        else if (InputLangCombo.Items.Count > 0)
            InputLangCombo.SelectedIndex = 0;
    }

    private void PopulateMicrophoneCombo(int selectedDeviceIndex)
    {
        MicCombo.Items.Clear();
        foreach (var mic in _speech.AvailableMics)
            MicCombo.Items.Add(mic);

        var selected = _speech.AvailableMics.FirstOrDefault(mic => mic.Index == selectedDeviceIndex);
        if (selected != null)
            MicCombo.SelectedItem = selected;
        else if (MicCombo.Items.Count > 0)
            MicCombo.SelectedIndex = 0;

        ApplySelectedMicrophone();
    }

    private void ApplySelectedMicrophone()
    {
        if (MicCombo.SelectedItem is not AudioDeviceInfo mic)
            return;

        _speech.MicDeviceIndex = mic.Index;
        _settings.MicDeviceIndex = mic.Index;
    }

    private static string GetWhisperModelPath(string size)
    {
        var normalized = size is "tiny" or "base" or "small" or "medium" ? size : "small";
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceChatbot",
            $"ggml-{normalized}.bin");
    }

    private void SelectWhisperModelCombo(string size)
    {
        foreach (var item in WhisperModelCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), size, StringComparison.OrdinalIgnoreCase))
            {
                WhisperModelCombo.SelectedItem = item;
                return;
            }
        }

        WhisperModelCombo.SelectedIndex = 2;
    }

    // ==================== Slider Bindings ====================

    private void SetupSliderBindings()
    {
        TempSlider.ValueChanged += (s, e) => TempValue.Text = TempSlider.Value.ToString("F1");
        SilenceSlider.ValueChanged += (s, e) => { SilenceValue.Text = SilenceSlider.Value.ToString("F1"); _speech.SilenceTimeout = SilenceSlider.Value; };
        NoiseSlider.ValueChanged += (s, e) => { NoiseValue.Text = $"{(int)NoiseSlider.Value}%"; _speech.NoiseGate = (int)NoiseSlider.Value; };
        RateSlider.ValueChanged += (s, e) => { RateValue.Text = $"{(int)RateSlider.Value:+#;-#;0}"; _speech.SpeechRate = (int)RateSlider.Value; };
        VolumeSlider.ValueChanged += (s, e) => { VolumeValue.Text = $"{(int)VolumeSlider.Value}%"; _speech.Volume = (int)VolumeSlider.Value; };
        ContextSlider.ValueChanged += (s, e) => { ContextValue.Text = ((int)ContextSlider.Value).ToString(); _history.MaxMessages = (int)ContextSlider.Value; };

        OllamaUrlBox.TextChanged += (s, e) =>
        {
            _settings.OllamaUrl = OllamaUrlBox.Text.Trim();
            ConfigureChatClient();
        };
        OpenAiUrlBox.TextChanged += (s, e) =>
        {
            _settings.OpenAiCompatibleUrl = OpenAiUrlBox.Text.Trim();
            ConfigureChatClient();
        };
        OpenAiApiKeyBox.PasswordChanged += (s, e) =>
        {
            _settings.OpenAiCompatibleApiKey = OpenAiApiKeyBox.Password.Trim();
            ConfigureChatClient();
        };
        HermesSshHostBox.TextChanged += (s, e) =>
        {
            _settings.HermesSshHost = HermesSshHostBox.Text.Trim();
            ConfigureChatClient();
        };
        HermesSshPortBox.TextChanged += (s, e) =>
        {
            _settings.HermesSshPort = int.TryParse(HermesSshPortBox.Text.Trim(), out var port)
                ? Math.Clamp(port, 1, 65535)
                : 2222;
            ConfigureChatClient();
        };
        HermesSshUserBox.TextChanged += (s, e) =>
        {
            _settings.HermesSshUser = HermesSshUserBox.Text.Trim();
            ConfigureChatClient();
        };
        HermesSshPasswordBox.PasswordChanged += (s, e) =>
        {
            _settings.HermesSshPassword = HermesSshPasswordBox.Password;
            ConfigureChatClient();
        };
        ComfyUrlBox.TextChanged += (s, e) =>
        {
            _settings.ComfyUiUrl = string.IsNullOrWhiteSpace(ComfyUrlBox.Text)
                ? "http://localhost:8000"
                : ComfyUrlBox.Text.Trim();
            ConfigureImageClient();
        };

        // Voice combo change - guard against empty/null during init
        VoiceCombo.SelectionChanged += (s, e) =>
        {
            var selected = VoiceCombo.SelectedItem?.ToString();
            if (!string.IsNullOrEmpty(selected))
                _speech.VoiceName = selected;
        };

        // Wake word change
        WakeWordBox.TextChanged += (s, e) => { _speech.WakeWord = WakeWordBox.Text; };

        TranscriptionBackendCombo.SelectionChanged += (s, e) =>
        {
            _speech.TranscriptionBackend = GetSelectedTranscriptionBackend();
        };
        NpuTranscriberCommandBox.TextChanged += (s, e) =>
        {
            var npuCommand = NpuTranscriberCommandBox.Text.Trim();
            _settings.ExternalNpuTranscriberCommand = string.IsNullOrWhiteSpace(npuCommand)
                ? AppSettings.DefaultRyzenAiWhisperCommand
                : npuCommand;
            _speech.ExternalNpuTranscriberCommand = _settings.ExternalNpuTranscriberCommand;
        };

        // Tavily key change
        TavilyApiKeyBox.PasswordChanged += (s, e) =>
        {
            var tavilyKey = TavilyApiKeyBox.Password.Trim();
            _settings.TavilyApiKey = string.IsNullOrWhiteSpace(tavilyKey)
                ? AppSettings.DefaultTavilyApiKey
                : tavilyKey;
            _tavily.ApiKey = _settings.TavilyApiKey;
        };

        // Input language change
        InputLangCombo.SelectionChanged += (s, e) =>
        {
            _speech.InputLanguage = InputLangCombo.Text;
        };
    }

    // ==================== Timers ====================

    private void SetupTimers()
    {
        _volumeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _volumeTimer.Tick += (s, e) =>
        {
            // Volume meter is driven by SpeechEngine event
        };
        _volumeTimer.Start();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (s, e) =>
        {
            if (_speech.CurrentState == VoiceState.Listening)
            {
                var elapsed = DateTime.Now - _listenStartTime;
                TimerLabel.Text = $"{elapsed:mm\\:ss}";
            }
        };
        _statusTimer.Start();

        _schedulerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _schedulerTimer.Tick += async (_, _) => await RunDueScheduledTasksAsync();
        _schedulerTimer.Start();

        // Wire volume level
        _speech.VolumeLevelChanged += (level) =>
        {
            Dispatcher.Invoke(() => VolumeMeter.Value = level, DispatcherPriority.Background);
        };
    }

    // ==================== Face Presence ====================

    private async Task InitializeFacePresenceAsync()
    {
        await StopFacePresenceAsync();

        _facePolicy = new FaceAccessPolicyEvaluator(_settings.FaceFeatures.Policy);
        _faceIdentityManager = new FaceIdentityManager(
            FaceServiceFactory.CreateProfileStore(_settings.FaceFeatures.ModelOptions),
            _settings.FaceFeatures.ModelOptions);

        if (!_settings.FaceFeatures.CameraFeaturesEnabled)
        {
            _facePresenceState = FacePresenceState.CameraUnavailable;
            _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, FaceIdentity.Unknown);
            UpdateFacePresenceUi(_facePresenceState);
            await UpdateFaceSampleInfoAsync();
            return;
        }

        try
        {
            UpdateFacePresenceUi(FacePresenceState.CameraUnavailable);
            FacePresenceText.Text = "Manual scan mode";
            await UpdateFaceSampleInfoAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Face presence init failed: {ex.Message}");
            _facePresenceState = FacePresenceState.CameraUnavailable;
            _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, FaceIdentity.Unknown);
            UpdateFacePresenceUi(_facePresenceState);
            await UpdateFaceSampleInfoAsync();
        }
    }

    private async Task StopFacePresenceAsync()
    {
        var oldSnapshot = _latestFaceSnapshot;
        _latestFaceSnapshot = null;
        oldSnapshot?.Dispose();

        if (_facePresenceMonitor != null)
        {
            _facePresenceMonitor.StateChanged -= OnFacePresenceChanged;
            _facePresenceMonitor.DetectionUpdated -= OnFaceDetectionUpdated;
            try { await _facePresenceMonitor.StopAsync(); }
            catch (Exception ex) { Debug.WriteLine($"Face presence stop failed: {ex.Message}"); }
            _facePresenceMonitor.Dispose();
            _facePresenceMonitor = null;
        }
    }

    private void OnFacePresenceChanged(object? sender, FacePresenceState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _facePresenceState = state;
            _faceAccessDecision = _facePolicy.Evaluate(state, _recognizedFaceIdentity);
            UpdateFacePresenceUi(state);
            ApplyFaceAccessDecision();
        });
    }

    private void OnFaceDetectionUpdated(object? sender, FaceDetectionSnapshot snapshot)
    {
        var oldSnapshot = _latestFaceSnapshot;
        _latestFaceSnapshot = snapshot;
        oldSnapshot?.Dispose();

        if (snapshot.Result.State == FacePresenceState.FaceDetected)
        {
            var now = DateTime.UtcNow;
            if (now - _lastPreviewUpdatedUtc > TimeSpan.FromMilliseconds(700))
            {
                _lastPreviewUpdatedUtc = now;
                _ = Dispatcher.BeginInvoke(() => UpdateFacePreview(snapshot));
            }

            if (now - _lastRecognitionStartedUtc > TimeSpan.FromMilliseconds(1200))
            {
                _lastRecognitionStartedUtc = now;
                _ = RecognizeLatestFaceAsync(snapshot);
            }
        }
        else if (snapshot.Result.State == FacePresenceState.CameraUnavailable)
            _ = Dispatcher.BeginInvoke(() => MaybeExpireRecognizedIdentity());
    }

    private void UpdateFacePresenceUi(FacePresenceState state)
    {
        if (!_settings.FaceFeatures.CameraFeaturesEnabled)
        {
            FacePresenceText.Text = "Camera features off";
            FacePresenceDot.Fill = new SolidColorBrush(Color.FromRgb(136, 136, 136));
            return;
        }

        (FacePresenceText.Text, FacePresenceDot.Fill) = state switch
        {
            FacePresenceState.FaceDetected => ($"Face detected", FindResource("SuccessBrush") as SolidColorBrush ?? Brushes.Green),
            FacePresenceState.MultipleFacesDetected => ("Multiple faces", FindResource("WarningBrush") as SolidColorBrush ?? Brushes.Goldenrod),
            FacePresenceState.NoFaceDetected => ("No face detected", FindResource("WarningBrush") as SolidColorBrush ?? Brushes.Goldenrod),
            _ => ("Camera unavailable", FindResource("ErrorBrush") as SolidColorBrush ?? Brushes.Red)
        };
    }

    private bool IsVoiceInputAllowedByFacePolicy()
    {
        if (!_settings.FaceFeatures.FaceGatingEnabled)
            return true;

        _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, _recognizedFaceIdentity);
        return !_faceAccessDecision.ShouldMuteMicrophone && !_faceAccessDecision.ShouldBlockWakeWord;
    }

    private async Task RecognizeLatestFaceAsync(FaceDetectionSnapshot snapshot)
    {
        if (_recognitionInProgress || snapshot.Result.Faces.Length != 1)
            return;

        _recognitionInProgress = true;
        try
        {
            var result = await _faceIdentityManager.RecognizeAsync(snapshot.Frame, snapshot.Result.Faces[0]);
            _ = Dispatcher.BeginInvoke(() => UpdateRecognizedIdentity(result.Identity, result.DisplayName, result.Similarity, result.IsRecognized));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Face recognition failed: {ex.Message}");
        }
        finally
        {
            _recognitionInProgress = false;
        }
    }

    private void UpdateRecognizedIdentity(FaceIdentity identity, string displayName, float similarity, bool recognized)
    {
        var now = DateTime.UtcNow;
        displayName = string.IsNullOrWhiteSpace(displayName)
            ? (identity == FaceIdentity.Unknown ? "Unknown" : identity.ToString())
            : displayName.Trim();
        var sameAsCurrent = !string.IsNullOrWhiteSpace(_recognizedFaceName)
            && string.Equals(displayName, _recognizedFaceName, StringComparison.OrdinalIgnoreCase);
        var softMatch = sameAsCurrent && similarity >= 0.55f;
        var recentMatch = !string.IsNullOrWhiteSpace(_recognizedFaceName) && now - _lastRecognizedFaceUtc < TimeSpan.FromSeconds(20);

        if (recognized || softMatch)
        {
            _recognizedFaceIdentity = identity;
            _recognizedFaceName = displayName;
            _lastIdentifiedFaceIdentity = identity;
            _lastIdentifiedFaceName = displayName;
            _lastIdentifiedFaceUtc = now;
            _lastRecognizedFaceUtc = now;
            _lastRecognizedSimilarity = similarity;
        }
        else if (!recentMatch)
        {
            _recognizedFaceIdentity = FaceIdentity.Unknown;
            _recognizedFaceName = "";
            _lastRecognizedSimilarity = similarity;
        }

        FaceIdentityText.Text = !string.IsNullOrWhiteSpace(_recognizedFaceName)
            ? $"Identity: {_recognizedFaceName} | similarity {_lastRecognizedSimilarity:F2} ({DescribeFaceScore(_lastRecognizedSimilarity)})"
            : !string.IsNullOrWhiteSpace(displayName) && displayName != "Unknown"
                ? $"Identity: Unknown | closest {displayName}, similarity {similarity:F2} ({DescribeFaceScore(similarity)})"
                : "Identity: Unknown";

        _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, _recognizedFaceIdentity);
        ApplyFaceAccessDecision();
    }

    private void MaybeExpireRecognizedIdentity()
    {
        if (string.IsNullOrWhiteSpace(_recognizedFaceName))
            return;

        if (DateTime.UtcNow - _lastRecognizedFaceUtc < TimeSpan.FromSeconds(20))
        {
            FaceIdentityText.Text = $"Identity: {_recognizedFaceName} (recent)";
            return;
        }

        _recognizedFaceIdentity = FaceIdentity.Unknown;
        _recognizedFaceName = "";
        _lastRecognizedSimilarity = 0f;
        FaceIdentityText.Text = "Identity: Unknown";
        _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, _recognizedFaceIdentity);
        ApplyFaceAccessDecision();
    }

    private static string DescribeFaceScore(float similarity)
    {
        return similarity switch
        {
            >= 0.55f => "strong",
            >= 0.36f => "usable",
            >= 0.20f => "weak",
            _ => "poor"
        };
    }

    private void UpdateFacePreview(FaceDetectionSnapshot snapshot)
    {
        if (snapshot.Result.Faces.Length == 0)
            return;

        try
        {
            var faceBounds = ExpandFaceBounds(snapshot.Result.Faces[0], snapshot.Frame.Width, snapshot.Frame.Height, 0.25);
            using var face = new Mat(snapshot.Frame, faceBounds);
            Cv2.ImEncode(".jpg", face, out var bytes);

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            FacePreviewImage.Source = image;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Face preview failed: {ex.Message}");
        }
    }

    private void UpdateFacePreviewFrame(Mat frame)
    {
        try
        {
            Cv2.ImEncode(".jpg", frame, out var bytes);

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            FacePreviewImage.Source = image;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Face full-frame preview failed: {ex.Message}");
        }
    }

    private static OpenCvSharp.Rect ExpandFaceBounds(OpenCvSharp.Rect rect, int width, int height, double expandRatio)
    {
        var expandX = (int)Math.Round(rect.Width * expandRatio);
        var expandY = (int)Math.Round(rect.Height * expandRatio);
        var x = Math.Clamp(rect.X - expandX, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y - expandY, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(rect.X + rect.Width + expandX, x + 1, width);
        var bottom = Math.Clamp(rect.Y + rect.Height + expandY, y + 1, height);
        return new OpenCvSharp.Rect(x, y, right - x, bottom - y);
    }

    private async Task UpdateFaceSampleInfoAsync()
    {
        try
        {
            var selected = GetSelectedFaceProfileName();
            var sampleCount = await _faceIdentityManager.CountSamplesAsync(selected);
            FaceDebugText.Text = $"Samples for {selected}: {sampleCount} | Model: SFace ONNX | Threshold: {_settings.FaceFeatures.ModelOptions.RecognitionThreshold:F2}";
        }
        catch (Exception ex)
        {
            FaceDebugText.Text = $"Sample info unavailable: {ex.Message}";
        }
    }

    private async Task<int> CountAllFaceSamplesAsync()
    {
        return (await _faceIdentityManager.LoadProfilesAsync()).Sum(profile => profile.Embeddings.Count);
    }

    private void ApplyFaceAccessDecision()
    {
        if (!_settings.FaceFeatures.FaceGatingEnabled)
            return;

        if (_faceAccessDecision.ShouldMuteMicrophone && _speech.CurrentState == VoiceState.Listening)
        {
            _speech.StopListening();
            _autoListening = false;
            AlwaysListenToggle.IsChecked = false;
            AddSystemMessage("Voice input paused by local face policy.");
            SetUIState("idle", "Face policy paused mic");
        }
    }

    // ==================== Connection ====================

    private async Task TestConnection()
    {
        var connected = await _ollama.PingAsync();
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = connected ? $"Connected ({_settings.ChatProvider})" : $"Disconnected ({_settings.ChatProvider})";
            StatusText.Foreground = connected ? FindResource("SuccessBrush") as SolidColorBrush : FindResource("ErrorBrush") as SolidColorBrush;
            StatusDot.Fill = connected ? FindResource("SuccessBrush") as SolidColorBrush : FindResource("ErrorBrush") as SolidColorBrush;
        });
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        await RefreshModelsInternal();
    }

    private async void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ollama == null)
            return;

        _settings.ChatProvider = GetSelectedProvider();
        ConfigureChatClient();
        SaveSettings();
        await TestConnection();
    }

    private async Task RefreshModelsInternal()
    {
        RefreshModelsBtn.IsEnabled = false;
        RefreshModelsBtn.Content = "... Loading...";
        try
        {
            var previousModel = ModelCombo.Text;
            var models = await _ollama.ListModelsAsync();
            ModelCombo.Items.Clear();
            foreach (var m in models)
                ModelCombo.Items.Add(m);
            if (models.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(previousModel) && models.Contains(previousModel))
                    ModelCombo.SelectedItem = previousModel;
                else if (!string.IsNullOrWhiteSpace(_settings.Model) && models.Contains(_settings.Model))
                    ModelCombo.SelectedItem = _settings.Model;
                else
                    ModelCombo.SelectedIndex = 0;

                _settings.Model = ModelCombo.Text;
            }

            var endpoint = _settings.ChatProvider.Equals("OpenAI-compatible", StringComparison.OrdinalIgnoreCase)
                ? _settings.OpenAiCompatibleUrl
                : _settings.OllamaUrl;
            AddSystemMessage($"Loaded {models.Count} model(s) from {_settings.ChatProvider}: {endpoint}");

            await TestConnection();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Failed to load models: {ex.Message}");
        }
        finally
        {
            RefreshModelsBtn.IsEnabled = true;
            RefreshModelsBtn.Content = "Refresh Models";
        }
    }

    private sealed record ModelBatchOption(string DisplayName, string BatchPath, int? EndpointPort)
    {
        public override string ToString() => DisplayName;
    }

    private async Task InitializeDesktopServiceControlsAsync()
    {
        try
        {
            await RefreshAvailableModelBatchesAsync();
            _desktopModelRunning = await _ollama.PingAsync();
            _desktopRunningModel = _desktopModelRunning ? ModelCombo.Text : "";
            _comfyUiRunning = await IsComfyUiReachableAsync();
            ModelControlStatusText.Text = _desktopModelRunning
                ? $"Running model: {(_desktopRunningModel.Length > 0 ? _desktopRunningModel : "detected")}"
                : "No model detected.";
        }
        catch (Exception ex)
        {
            ModelControlStatusText.Text = $"Service check failed: {ex.Message}";
        }
        finally
        {
            UpdateDesktopServiceControls();
        }
    }

    private async Task RefreshAvailableModelBatchesAsync()
    {
        const string command = "find /mnt/c/llama.cpp -maxdepth 1 -type f -iname 'start-*.bat' -printf '%f\\n' | sort -f";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await _hermesSsh.RunAsync(command, TimeSpan.FromSeconds(15), timeout.Token);
        if (result.ExitStatus != 0 || result.TimedOut)
            throw new InvalidOperationException("Could not list model batch files over SSH.");

        var previous = (LaunchModelCombo.SelectedItem as ModelBatchOption)?.BatchPath;
        var options = result.Stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Trim())
            .Where(name => name.StartsWith("start-", StringComparison.OrdinalIgnoreCase))
            .Where(name => !name.Contains("comfyui", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                var path = $"/mnt/c/llama.cpp/{name}";
                return new ModelBatchOption(
                    Path.GetFileNameWithoutExtension(name).Replace("start-", "", StringComparison.OrdinalIgnoreCase),
                    path,
                    GetEndpointPortForBatchFile(name));
            })
            .ToList();

        LaunchModelCombo.ItemsSource = options;
        LaunchModelCombo.SelectedItem = options.FirstOrDefault(option =>
            string.Equals(option.BatchPath, previous, StringComparison.OrdinalIgnoreCase));
        if (LaunchModelCombo.SelectedItem == null && options.Count > 0)
            LaunchModelCombo.SelectedIndex = 0;
    }

    private static int? GetEndpointPortForBatchFile(string batchFile)
    {
        var name = batchFile.ToLowerInvariant();
        if (name.Contains("gpt-oss-120b")) return 8084;
        if (name.Contains("mistral-medium")) return 8082;
        if (name.Contains("qwen3.6-27b")) return 8081;
        if (name.Contains("lfm2.5-8b")) return 8083;
        if (name.Contains("gemma4-12b")) return 8083;
        if (name.Contains("gemma4-4b")) return 8081;
        if (name.Contains("gemma4")) return 8080;
        return null;
    }

    private static bool CanRunComfyUiAlongsideModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || !model.Contains("gemma", StringComparison.OrdinalIgnoreCase))
            return false;

        return Regex.IsMatch(model, @"(?:^|[^0-9])(?:4b|12b)(?:[^0-9]|$)", RegexOptions.IgnoreCase);
    }

    private async Task<bool> IsComfyUiReachableAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync($"{_settings.ComfyUiUrl.TrimEnd('/')}/system_stats", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateDesktopServiceControls()
    {
        var canLaunchModel = !_serviceControlBusy && !_desktopModelRunning;
        StopCurrentModelBtn.IsEnabled = !_serviceControlBusy && _desktopModelRunning;
        LaunchModelCombo.IsEnabled = canLaunchModel;
        StartSelectedModelBtn.IsEnabled = canLaunchModel && LaunchModelCombo.SelectedItem is ModelBatchOption;

        var comfyAllowed = !_desktopModelRunning || CanRunComfyUiAlongsideModel(_desktopRunningModel);
        ComfyUiStartStopBtn.IsEnabled = !_serviceControlBusy && comfyAllowed;
        ComfyUiStartStopBtn.Content = _comfyUiRunning ? "Stop ComfyUI" : "Start ComfyUI";
        ComfyUiStartStopBtn.ToolTip = comfyAllowed
            ? null
            : "ComfyUI is available only when no model, Gemma 4B, or Gemma 4 12B is running.";
    }

    private async void StopCurrentModel_Click(object sender, RoutedEventArgs e)
    {
        var plan = new LlamaModelControlPlan(
            "Stopping current llama.cpp model processes",
            BuildStopLlamaCommand(),
            null);
        await RunDesktopServiceControlAsync(plan, modelWillBeRunning: false, runningModel: "");
    }

    private async void StartSelectedModel_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchModelCombo.SelectedItem is not ModelBatchOption option)
            return;

        var label = Path.GetFileNameWithoutExtension(option.BatchPath).Replace("start-", "", StringComparison.OrdinalIgnoreCase);
        var plan = new LlamaModelControlPlan(
            $"Starting llama.cpp model using {Path.GetFileName(option.BatchPath)}",
            BuildStartLlamaBatchCommand(option.BatchPath, label),
            option.EndpointPort);
        await RunDesktopServiceControlAsync(plan, modelWillBeRunning: true, runningModel: option.BatchPath);
    }

    private async void ComfyUiStartStop_Click(object sender, RoutedEventArgs e)
    {
        var prompt = _comfyUiRunning ? "stop comfyui" : "start comfyui";
        if (!TryBuildLlamaModelControlPlan(prompt, out var plan))
            return;

        if (await RunDesktopServiceControlAsync(plan, modelWillBeRunning: null, runningModel: null))
        {
            _comfyUiRunning = !_comfyUiRunning;
            ModelControlStatusText.Text = _comfyUiRunning ? "ComfyUI start command sent." : "ComfyUI stop command sent.";
            UpdateDesktopServiceControls();
        }
    }

    private async Task<bool> RunDesktopServiceControlAsync(
        LlamaModelControlPlan plan,
        bool? modelWillBeRunning,
        string? runningModel)
    {
        _serviceControlBusy = true;
        ModelControlStatusText.Text = plan.Description;
        UpdateDesktopServiceControls();

        try
        {
            using var operationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(105));
            var display = await RunLlamaModelControlPlanAsync(plan, operationTimeout.Token);
            AddSystemMessage(display);

            if (plan.EndpointPort is int port)
            {
                var endpoint = SetLlamaEndpointPort(port);
                var readiness = await WaitForLlamaEndpointReadyAsync(endpoint, TimeSpan.FromSeconds(90), operationTimeout.Token);
                AddSystemMessage(readiness.Message);
                if (!readiness.Ready)
                    throw new InvalidOperationException(readiness.Message);
                await RefreshModelsInternal();
            }

            if (modelWillBeRunning.HasValue)
            {
                _desktopModelRunning = modelWillBeRunning.Value;
                _desktopRunningModel = runningModel ?? "";
                ModelControlStatusText.Text = _desktopModelRunning
                    ? $"Running model: {Path.GetFileNameWithoutExtension(_desktopRunningModel)}"
                    : "No model running. Select a batch file to launch.";
            }

            return true;
        }
        catch (Exception ex)
        {
            ModelControlStatusText.Text = $"Control failed: {ex.Message}";
            AddSystemMessage($"Model/Comfy control failed: {ex.Message}");
            return false;
        }
        finally
        {
            _serviceControlBusy = false;
            UpdateDesktopServiceControls();
        }
    }

    private async Task RunOnUiAsync(Func<Task> action)
    {
        if (Dispatcher.CheckAccess())
        {
            await action();
            return;
        }

        var operation = Dispatcher.InvokeAsync(action);
        await await operation;
    }

    private PhoneRemoteModelState GetPhoneRemoteModelState()
    {
        PhoneRemoteModelState ReadState()
        {
            var provider = _settings.ChatProvider;
            var model = !string.IsNullOrWhiteSpace(ModelCombo?.Text) ? ModelCombo.Text : _settings.Model;
            var endpoint = _settings.ChatProvider.Equals("OpenAI-compatible", StringComparison.OrdinalIgnoreCase)
                ? _settings.OpenAiCompatibleUrl
                : _settings.OllamaUrl;
            return new PhoneRemoteModelState(provider, model, endpoint);
        }

        return Dispatcher.CheckAccess()
            ? ReadState()
            : Dispatcher.Invoke(ReadState);
    }

    // ==================== Chat ====================

    /// <summary>
    /// Builds the effective system prompt with conversation memories injected.
    /// </summary>
    private string GetEffectiveSystemPrompt(string? currentUserText = null)
    {
        var basePrompt = SystemPromptBox.Text;
        basePrompt += "\n\n" + GetEasternDateTimeSystemContext();
        basePrompt +=
            "\n\nCapability truthfulness: You do not have direct access to the user's live iPhone " +
            "calendar, Health data, contacts, text messages, reminders, location, or other private " +
            "device data in ordinary chat. Never claim that you checked, found, created, changed, " +
            "deleted, sent, or confirmed anything in those services unless the current request " +
            "explicitly includes live tool results proving it. A user's statement is not proof that " +
            "an item exists. If a likely voice-transcription error makes an action ambiguous, briefly " +
            "state what you think they meant and ask for confirmation instead of fabricating a result.";
        if (IsCodeOrScriptRequest(currentUserText))
        {
            basePrompt += "\n\n" + GetCodeArtifactSystemInstruction(currentUserText);
        }

        var identityContext = GetLastIdentifiedUserSystemContext();
        if (!string.IsNullOrWhiteSpace(identityContext))
            basePrompt += "\n\n" + identityContext;

        if (_settings.FaceFeatures.FaceGatingEnabled)
            basePrompt += "\n\n" + GetFaceIdentitySystemContext();

        var memoryBlock = MemoryManager.FormatForSystemPrompt(_loadedMemories);
        if (!string.IsNullOrWhiteSpace(memoryBlock))
            basePrompt += "\n\n" + memoryBlock;

        var transcriptionContext = GetLiveTranscriptionSystemContext();
        if (!string.IsNullOrWhiteSpace(transcriptionContext))
            basePrompt += "\n\n" + transcriptionContext;

        var recentWebContext = GetRecentWebSearchSystemContext();
        if (!string.IsNullOrWhiteSpace(recentWebContext))
            basePrompt += "\n\n" + recentWebContext;

        return basePrompt;
    }

    private static string GetEasternDateTimeSystemContext()
    {
        TimeZoneInfo eastern;
        try
        {
            eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            eastern = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        }

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, eastern);
        var zoneName = eastern.IsDaylightSavingTime(now) ? "EDT" : "EST";
        var offset = now.ToString("zzz", CultureInfo.InvariantCulture);
        return
            "[Current date and time]\n" +
            $"It is {now:dddd, MMMM d, yyyy 'at' h:mm:ss tt} {zoneName} (UTC{offset}).\n" +
            "Use this as the authoritative current date, weekday, year, and time of day. " +
            "Interpret relative references such as today, tomorrow, yesterday, tonight, " +
            "this morning, and this weekend from this timestamp.";
    }

    private static bool IsCodeOrScriptRequest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = Regex.Replace(text.ToLowerInvariant(), "\\s+", " ").Trim();
        if (Regex.IsMatch(normalized, "\\b(svg|vector)\\b"))
            return true;

        var asksForArtifact = Regex.IsMatch(normalized, "\\b(write|create|make|build|generate|give|show|provide|need|fix|convert|update|draw|design|render|illustrate)\\b");
        var mentionsCode = Regex.IsMatch(normalized, "\\b(code|script|program|function|class|method|snippet|markup|svg|vector|xml|html|css|powershell|python|bash|batch|cmd|javascript|typescript|sql|json|yaml|c#|csharp|dotnet|regex)\\b");

        return mentionsCode && (asksForArtifact || normalized.Contains("code block") || normalized.Contains("```"));
    }

    private static bool IsManualContinuationRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = Regex.Replace(text.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}\s']", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized is "continue" or "continue please" or "please continue" or
               "go on" or "keep going" or "carry on" or "more" or
               "continue from there" or "continue where you left off" or
               "finish it" or "finish that" or "finish the answer" or
               "keep writing";
    }

    private static bool IsGemma412BModel(string? model)
    {
        var normalized = (model ?? "").ToLowerInvariant();
        return normalized.Contains("gemma", StringComparison.Ordinal) &&
               (normalized.Contains("12b", StringComparison.Ordinal) ||
                Regex.IsMatch(normalized, @"\b12\s*b\b"));
    }

    private int GetMaxTokensForRequest(string? text, string? model = null)
    {
        return IsGemma412BModel(model) ? 512 : CodeResponseMaxTokens;
    }

    private async Task<int> GetContextTokensForRequestAsync(string model, CancellationToken ct)
    {
        return await _ollama.GetModelContextTokensAsync(model, ct) ?? CodeContextTokens;
    }

    private List<ChatMessage> BuildMessagesForModel(string currentUserText, IEnumerable<string> currentImagesBase64)
    {
        var currentContent = LimitCurrentModelInput(currentUserText);
        if (IsLargePaste(currentUserText))
        {
            return new List<ChatMessage>
            {
                new()
                {
                    Role = "system",
                    Content = "The next user message is a large pasted document. Treat the entire message as available context, not only the first section. If the user asks what you can see, scan for and report all obvious section/chapter headings present in the pasted text."
                },
                new()
                {
                    Role = "user",
                    Content = currentContent,
                    ImagesBase64 = currentImagesBase64.Where(i => !string.IsNullOrWhiteSpace(i)).ToList()
                }
            };
        }

        var messages = _history.GetAll()
            .Select(m => new ChatMessage
            {
                Role = m.Role,
                Content = m.Content,
                ImagesBase64 = new List<string>(),
                Timestamp = m.Timestamp
            })
            .ToList();
        if (messages.Count > 0 && messages[^1].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            messages[^1] = new ChatMessage
            {
                Role = "user",
                Content = currentContent,
                ImagesBase64 = currentImagesBase64.Where(i => !string.IsNullOrWhiteSpace(i)).ToList()
            };
        }

        return messages;
    }

    private static bool IsLargePaste(string text) => text.Length > LargePasteChars;

    private static string LimitCurrentModelInput(string text)
    {
        if (text.Length <= MaxCurrentModelInputChars)
            return text;

        var headLength = MaxCurrentModelInputChars / 2;
        var tailLength = MaxCurrentModelInputChars - headLength;
        return text[..headLength] +
               $"\n\n[Input trimmed before model request: original length {text.Length:N0} characters. Paste less text or use an attachment/chunking workflow for exact full-document work.]\n\n" +
               text[^tailLength..];
    }

    private int TrimMessagesToContextBudget(List<ChatMessage> messages, string systemPrompt, int contextTokens, int maxOutputTokens)
    {
        if (messages.Count <= 1 || contextTokens <= 0)
            return 0;

        var promptBudget = Math.Max(4096, contextTokens - maxOutputTokens - ContextSafetyTokens);
        var dropped = 0;
        while (messages.Count > 1 && EstimatePromptTokens(messages, systemPrompt) > promptBudget)
        {
            messages.RemoveAt(0);
            dropped++;
        }

        return dropped;
    }

    private void AddTokenEstimateDiagnostic(string userText, List<ChatMessage> messages, string systemPrompt, int contextTokens, int maxOutputTokens, int droppedMessages)
    {
        var sentUserText = GetLastUserMessageContent(messages);
        var typedTokens = EstimateTextTokens(userText);
        var currentTurnTokens = EstimateTextTokens(sentUserText);
        var promptTokens = EstimatePromptTokens(messages, systemPrompt);
        var contextPercent = contextTokens > 0 ? promptTokens / (double)contextTokens : 0;
        var addedContextTokens = Math.Max(0, currentTurnTokens - typedTokens);
        var carriedTokens = Math.Max(0, promptTokens - currentTurnTokens);

        var message = new StringBuilder();
        message.AppendLine("Tokens:");
        if (contextTokens > 0)
        {
            message.AppendLine($"Window: {contextTokens:N0}");
            message.AppendLine($"Context used: ~{promptTokens:N0} / {contextTokens:N0} ({FormatPercent(contextPercent)})");
        }
        else
        {
            message.AppendLine($"Context used: ~{promptTokens:N0}");
        }

        message.AppendLine($"This turn: ~{typedTokens:N0} typed");
        if (addedContextTokens > 0)
            message.AppendLine($"Added context: ~{addedContextTokens:N0}");
        if (carriedTokens > 0)
            message.AppendLine($"Carried chat/system: ~{carriedTokens:N0}");
        if (maxOutputTokens > 0)
            message.AppendLine($"Reply limit: {maxOutputTokens:N0}");
        if (droppedMessages > 0)
            message.Append($"Old messages dropped: {droppedMessages}");
        else
            message.Length--;

        AddSystemMessage(message.ToString());
    }

    private static string GetLastUserMessageContent(List<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                return messages[i].Content;
        }

        return "";
    }

    private static List<string> ExtractLikelySectionMarkers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var markers = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
                     text,
                     @"(?m)^\s*((?:(?:chapter|section)\s+)?(?:[IVXLCDM]+|\d+)[\.\)]?(?:\s+[-:.]?\s*[A-Z][^\r\n]{0,80})?)\s*$",
                     RegexOptions.IgnoreCase))
        {
            var marker = Regex.Replace(match.Groups[1].Value.Trim(), "\\s+", " ");
            if (marker.Length == 0 || marker.Length > 100 || !seen.Add(marker))
                continue;

            markers.Add(marker);
            if (markers.Count >= 20)
                break;
        }

        return markers;
    }

    private static string FormatPercent(double value) => $"{value:P1}";

    private static int EstimatePromptTokens(IEnumerable<ChatMessage> messages, string systemPrompt)
    {
        var total = EstimateTextTokens(systemPrompt);
        foreach (var message in messages)
            total += EstimateTextTokens(message.Content) + 6;

        return total;
    }

    private static int EstimateTextTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return Math.Max(1, (int)Math.Ceiling(text.Length / (double)EstimatedCharsPerToken));
    }

    private static string GetCodeArtifactSystemInstruction(string? currentUserText)
    {
        var isSvgRequest = !string.IsNullOrWhiteSpace(currentUserText) &&
            currentUserText.Contains("svg", StringComparison.OrdinalIgnoreCase);

        var instruction = "For the current desktop chat response, the user is asking for code, markup, an SVG, or a script. In this response only, ignore any instruction that forbids markdown or code blocks. Put complete code, markup, SVG, or scripts in fenced markdown code blocks with an appropriate language tag, such as ```svg, ```html, ```csharp, ```python, ```powershell, ```bash, or ```json. Do not discuss message length limits, do not offer a smaller version, and do not offer a generator script unless the user explicitly asks for one. Produce the complete requested artifact as directly as possible. Keep any setup notes short and outside the code block.";

        if (isSvgRequest)
        {
            instruction += " For SVG requests, create one complete standalone SVG in a single ```svg code block. Do not replace requested detail with a summary. Close the <svg> element.";
        }

        return instruction;
    }

    private async Task<string> CompleteCodeArtifactIfNeededAsync(
        string response,
        string userText,
        List<ChatMessage> messagesForModel,
        string systemPrompt,
        string model,
        double temperature,
        int maxTokens,
        int contextTokens,
        CancellationToken ct)
    {
        var shouldHandleArtifact =
            IsCodeOrScriptRequest(userText) ||
            ContainsFencedCodeBlock(response) ||
            LooksLikeSvgRequestOrOutput(userText, response);

        if (!shouldHandleArtifact)
            return response;

        maxTokens = Math.Max(maxTokens, CodeResponseMaxTokens);
        if (contextTokens <= 0)
            contextTokens = CodeContextTokens;

        var completed = response;
        var needsContinuation = IsIncompleteCodeArtifact(completed, userText) || WasLastResponseTokenLimited();
        for (var attempt = 1; attempt <= CodeContinuationMaxAttempts && needsContinuation; attempt++)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SetUIState("processing", "Continuing code/SVG...");
                AddSystemMessage($"Code/SVG looked incomplete or hit a token limit, continuing automatically ({attempt}/{CodeContinuationMaxAttempts}).");
            });

            var continuationMessages = messagesForModel.Select(m => new ChatMessage
            {
                Role = m.Role,
                Content = m.Content,
                ImagesBase64 = m.ImagesBase64.ToList(),
                Timestamp = m.Timestamp
            }).ToList();

            continuationMessages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = completed
            });
            continuationMessages.Add(new ChatMessage
            {
                Role = "user",
                Content = "Continue exactly from the previous character. Do not restart, do not summarize, do not explain, and do not wrap in a new code fence unless the original code fence was already closed. Finish the artifact completely."
            });

            var continuation = await _ollama.ChatAsync(
                model,
                continuationMessages,
                systemPrompt + "\n\nContinuation repair mode: output only the missing continuation text needed to complete the artifact. Do not repeat earlier content.",
                temperature,
                maxTokens,
                ct,
                contextTokens);
            var continuationHitLimit = WasLastResponseTokenLimited();
            AddBackendFinishDiagnostic($"Continuation {attempt}", continuation.Length, maxTokens, contextTokens);

            if (string.IsNullOrWhiteSpace(continuation))
                break;

            completed += NormalizeArtifactContinuation(completed, continuation);
            needsContinuation = IsIncompleteCodeArtifact(completed, userText) || continuationHitLimit;
        }

        return completed;
    }

    private bool WasLastResponseTokenLimited()
    {
        return IsTokenLimitFinishReason(_ollama.LastFinishReason) ||
               IsTokenLimitFinishReason(_ollama.LastStopReason);
    }

    private static bool IsTokenLimitFinishReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return false;

        var normalized = reason.Trim().ToLowerInvariant();
        return normalized.Contains("length", StringComparison.Ordinal) ||
               normalized.Contains("limit", StringComparison.Ordinal) ||
               normalized.Contains("max_tokens", StringComparison.Ordinal) ||
               normalized.Contains("num_predict", StringComparison.Ordinal);
    }

    private void AddBackendFinishDiagnostic(string label, int? responseChars = null, int? requestedMaxTokens = null, int? requestedContextTokens = null)
    {
        var message = new StringBuilder();
        message.AppendLine(label.StartsWith("Continuation", StringComparison.OrdinalIgnoreCase)
            ? $"Tokens ({label}):"
            : "Tokens:");
        var hasUsage = _ollama.LastPromptTokens is int || _ollama.LastCompletionTokens is int;
        var contextTokens = requestedContextTokens is int ctx && ctx > 0 ? ctx : 0;
        var promptTokens = _ollama.LastPromptTokens;
        var completionTokens = _ollama.LastCompletionTokens;

        if (contextTokens > 0)
            message.AppendLine($"Window: {contextTokens:N0}");
        if (promptTokens is int prompt)
        {
            var contextLine = contextTokens > 0
                ? $" / {contextTokens:N0} ({FormatPercent(prompt / (double)contextTokens)})"
                : "";
            message.AppendLine($"Context used: {prompt:N0}{contextLine}");
        }
        if (promptTokens is int p && completionTokens is int c)
            message.AppendLine($"Last exchange: {p:N0} in + {c:N0} out = {(p + c):N0}");
        else if (completionTokens is int completion)
            message.AppendLine($"Last reply: {completion:N0}");

        if (!string.IsNullOrWhiteSpace(_ollama.LastFinishReason))
            message.AppendLine($"Finish reason: {_ollama.LastFinishReason}");
        if (!string.IsNullOrWhiteSpace(_ollama.LastStopReason))
            message.AppendLine($"Stop reason: {_ollama.LastStopReason}");

        var text = message.ToString().Trim();
        AddSystemMessage(!hasUsage
            ? "Tokens: backend did not return usage metadata."
            : text);
    }

    private static bool IsIncompleteCodeArtifact(string text, string userText)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var isSvgRequest = LooksLikeSvgRequestOrOutput(userText, text);
        if (isSvgRequest &&
            text.Contains("<svg", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("</svg>", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return CountCodeFences(text) % 2 != 0;
    }

    private static bool LooksLikeSvgRequestOrOutput(string userText, string text) =>
        userText.Contains("svg", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("<svg", StringComparison.OrdinalIgnoreCase);

    private static int CountCodeFences(string text) =>
        Regex.Matches(text, "```").Count;

    private static string NormalizeArtifactContinuation(string previous, string continuation)
    {
        var next = continuation.TrimStart();

        if (CountCodeFences(previous) % 2 != 0)
            next = Regex.Replace(next, "^```[A-Za-z0-9_+.#-]*\\s*", "", RegexOptions.Singleline);

        if (previous.Contains("<svg", StringComparison.OrdinalIgnoreCase) &&
            !previous.Contains("</svg>", StringComparison.OrdinalIgnoreCase))
        {
            var duplicateSvgStart = Regex.Match(next, "<svg\\b[^>]*>", RegexOptions.IgnoreCase);
            if (duplicateSvgStart.Success)
                next = next[(duplicateSvgStart.Index + duplicateSvgStart.Length)..].TrimStart();
        }

        return next;
    }

    private string GetLiveTranscriptionSystemContext()
    {
        var hasTranscript = !string.IsNullOrWhiteSpace(_latestLiveTranscript);
        var hasSummary = !string.IsNullOrWhiteSpace(_latestLiveTranscriptSummary);
        if (!hasTranscript && !hasSummary)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("Live transcription context from the separate transcriber window is available. Treat it as user-visible session context and use it when relevant.");

        if (hasSummary)
        {
            sb.AppendLine();
            sb.AppendLine("Latest transcription summary:");
            sb.AppendLine(_latestLiveTranscriptSummary.Trim());
        }

        if (hasTranscript)
        {
            var transcript = _latestLiveTranscript.Trim();
            if (transcript.Length > 8000)
                transcript = transcript[^8000..];

            sb.AppendLine();
            sb.AppendLine("Latest live transcript:");
            sb.AppendLine(transcript);
        }

        return sb.ToString().Trim();
    }

    private sealed record RecentWebSearchContext(DateTime RetrievedAt, string Query, string Context);

    private void RememberWebSearchContext(string query, string context)
    {
        if (string.IsNullOrWhiteSpace(context))
            return;

        var trimmedContext = context.Trim();
        if (trimmedContext.Length > 12000)
            trimmedContext = trimmedContext[..12000] + "\n[Older source text trimmed for context budget.]";

        _recentWebSearchContexts.Insert(0, new RecentWebSearchContext(DateTime.Now, query.Trim(), trimmedContext));
        if (_recentWebSearchContexts.Count > 3)
            _recentWebSearchContexts.RemoveRange(3, _recentWebSearchContexts.Count - 3);
    }

    private string GetRecentWebSearchSystemContext()
    {
        if (_recentWebSearchContexts.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("Recent live web search context from this app session is available for follow-up questions.");
        sb.AppendLine("Treat these retrieved source snippets as current evidence from live web search, not as the model's own prior memory. If these sources conflict with your training data, prefer the retrieved web context and explain that it is newer. Do not call it fake or a mistake solely because it is newer than your built-in knowledge.");

        foreach (var item in _recentWebSearchContexts)
        {
            sb.AppendLine();
            sb.AppendLine($"Retrieved: {item.RetrievedAt:yyyy-MM-dd HH:mm} local time");
            sb.AppendLine($"Query: {item.Query}");
            sb.AppendLine(item.Context);
        }

        return sb.ToString().Trim();
    }

    private string GetFaceIdentitySystemContext()
    {
        return _recognizedFaceIdentity switch
        {
            FaceIdentity.Keith => "Local face identity says Keith is present. Use full assistant mode.",
            FaceIdentity.Child1 or FaceIdentity.Child2 => "Local face identity says a child profile is present. Use kid-safe mode: keep content age-appropriate, avoid adult topics, avoid dangerous instructions, and ask for an adult for sensitive actions.",
            _ when _facePresenceState == FacePresenceState.MultipleFacesDetected => "Local face presence sees multiple people. Use guest/private mode: avoid exposing personal memory or private details unless Keith is recognized.",
            _ => "Local face identity is unknown or no face is present. Use guest/private mode: avoid exposing personal memory or private details unless Keith is recognized."
        };
    }

    private string GetLastIdentifiedUserSystemContext()
    {
        if (string.IsNullOrWhiteSpace(_lastIdentifiedFaceName))
            return "";

        var identifiedWhen = _lastIdentifiedFaceUtc == DateTime.MinValue
            ? "earlier in this app session"
            : $"at {_lastIdentifiedFaceUtc.ToLocalTime():g}";

        return _lastIdentifiedFaceIdentity switch
        {
            FaceIdentity.Keith =>
                $"Local face identity context: the last identified user in this app session is Keith, identified {identifiedWhen}. When replying directly to the user, you may address him as Keith and should treat the conversation as being with Keith unless the user says otherwise.",
            FaceIdentity.Child1 or FaceIdentity.Child2 =>
                $"Local face identity context: the last identified user in this app session is {_lastIdentifiedFaceName}, identified {identifiedWhen}. Keep replies age-appropriate and avoid adult or dangerous content unless Keith is identified again.",
            _ =>
                $"Local face identity context: the last identified user in this app session is {_lastIdentifiedFaceName}, identified {identifiedWhen}. When replying directly to the user, you may address them by that name unless the user says otherwise."
        };
    }

    private async void SendMessage(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return;
        var modelUserText = IsManualContinuationRequest(userText)
            ? "Continue the previous assistant response from where it left off. Do not restart, do not summarize, and do not ask what to continue. If the previous response was code, SVG, markup, a list, or a long answer, continue that same content directly."
            : userText;

        if (TryCreateHermesPrompt(userText, out var hermesPrompt))
        {
            await SendHermesAgentMessageAsync(userText, hermesPrompt);
            return;
        }

        if (PiAgentService.TryCreateReadOnlyPrompt(userText, out var piPrompt, out var piBlockedReason))
        {
            await SendPiAgentMessageAsync(userText, piPrompt, piBlockedReason);
            return;
        }

        if (TryGetVideoPrompt(userText, out var videoPrompt, out var videoSeconds))
        {
            await RunLtxVideoAsync(userText, videoPrompt, videoSeconds);
            return;
        }

        if (TryGetImageEditPrompt(userText, out var editPrompt))
        {
            await RunQwenImageEditAsync(userText, editPrompt);
            return;
        }

        if (TryGetImageCreatePrompt(userText, out var createPrompt))
        {
            await RunQwenImageCreateAsync(userText, createPrompt);
            return;
        }

        if (string.IsNullOrWhiteSpace(ModelCombo.Text))
        {
            AddSystemMessage("Please select a model first!");
            return;
        }

        _chatCts = new CancellationTokenSource();
        var model = ModelCombo.Text;
        AssistantMessageUi? assistantMessage = null;

        SetUIState("thinking", "Thinking...");

        try
        {
            var imagePaths = new List<string>();
            var imagesBase64 = new List<string>();

            if (ShouldCaptureCameraForPrompt(userText))
            {
                SetUIState("processing", "Capturing camera...");
                AddSystemMessage("Taking one camera photo for this message.");
                var photo = await _camera.CapturePhotoAsync(_chatCts.Token);
                imagePaths.Add(photo.Path);
                imagesBase64.Add(photo.Base64);
            }

            if (_pendingImages.Count > 0)
            {
                imagePaths.AddRange(_pendingImages.Select(i => i.Path));
                imagesBase64.AddRange(_pendingImages.Select(i => i.Base64));
                _pendingImages.Clear();
                UpdateImageButtonLabel();
            }

            var keepDocumentsActive = KeepDocumentActiveToggle.IsChecked == true;
            if (keepDocumentsActive && _pendingDocuments.Count > 0)
            {
                _activeDocuments.Clear();
                _activeDocuments.AddRange(_pendingDocuments);
                AddSystemMessage($"{_activeDocuments.Count} document(s) set active. Uncheck Keep doc to stop including them.");
            }

            var documentsForResponse = keepDocumentsActive && _activeDocuments.Count > 0
                ? _activeDocuments
                : _pendingDocuments;
            var documentContext = DocumentTextService.BuildContext(documentsForResponse.Select(d => d.Document), modelUserText);
            var documentCount = documentsForResponse.Count;
            if (_pendingDocuments.Count > 0)
            {
                _pendingDocuments.Clear();
                UpdateDocumentButtonLabel();
            }

            // Add user message to UI and history after optional capture so the thumbnail can be shown.
            AddUserMessage(userText, imagePaths);
            _history.Add("user", userText, imagesBase64);
            if (IsLargePaste(modelUserText))
                AddSystemMessage("Large paste mode: previous chat history will not be sent with this request.");

            // Create assistant bubble for streaming - get direct TextBlock reference
            assistantMessage = AddAssistantMessage("");

            _history.RemoveWhere(m =>
                m.Role.Equals("system", StringComparison.OrdinalIgnoreCase) &&
                (m.Content.StartsWith("YouTube video transcript context for ", StringComparison.Ordinal) ||
                 m.Content.StartsWith("YouTube video local audio transcription context for ", StringComparison.Ordinal)));

            var transientContexts = new List<ChatMessage>();
            var messagesForModel = BuildMessagesForModel(modelUserText, imagesBase64);
            if (!string.IsNullOrWhiteSpace(documentContext))
            {
                AddSystemMessage(keepDocumentsActive
                    ? $"{documentCount} active document(s) included in this response."
                    : $"{documentCount} attached document(s) added to this response.");
            }

            if (DocumentTextService.TryAnswerExactSentenceQuestion(modelUserText, documentsForResponse.Select(d => d.Document), out var exactDocumentAnswer))
            {
                assistantMessage.Body.Text = exactDocumentAnswer;
                _history.Add("assistant", exactDocumentAnswer);
                SpeakLastResponse(exactDocumentAnswer, assistantMessage);
                return;
            }

            if (YouTubeTranscriptService.TryExtractYouTubeUrl(modelUserText, out var youtubeUrl))
            {
                SetUIState("processing", "Fetching YouTube transcript...");
                AddSystemMessage("Fetching YouTube transcript.");
                var transcriptResult = await _youtubeTranscripts.FetchTranscriptAsync(youtubeUrl, _chatCts.Token);
                if (!string.IsNullOrWhiteSpace(transcriptResult.Transcript))
                {
                    var titleLine = string.IsNullOrWhiteSpace(transcriptResult.Title)
                        ? ""
                        : $"Title: {transcriptResult.Title}\n";
                    var transcriptContext =
                        $"YouTube video transcript context for {youtubeUrl}\n{titleLine}Transcript:\n{transcriptResult.Transcript}";

                    transientContexts.Add(new ChatMessage
                    {
                        Role = "system",
                        Content = transcriptContext
                    });
                    messagesForModel = BuildMessagesForModel(modelUserText, imagesBase64);
                    InsertTransientContexts(messagesForModel, transientContexts);
                    ApplyYouTubeContextToCurrentUserMessage(messagesForModel, modelUserText, youtubeUrl, transcriptResult.Title, transcriptResult.Transcript);

                    AddSystemMessage("YouTube transcript added to this response.");
                }
                else
                {
                    AddSystemMessage("YouTube captions unavailable. Downloading audio for local transcription.");
                    transcriptResult = await _youtubeTranscripts.FetchAudioTranscriptAsync(
                        youtubeUrl,
                        (stream, ct) => _speech.TranscribeWavAsync(stream, ct),
                        _chatCts.Token);

                    if (!string.IsNullOrWhiteSpace(transcriptResult.Transcript))
                    {
                        var titleLine = string.IsNullOrWhiteSpace(transcriptResult.Title)
                            ? ""
                            : $"Title: {transcriptResult.Title}\n";
                        var transcriptContext =
                            $"YouTube video local audio transcription context for {youtubeUrl}\n{titleLine}Transcript:\n{transcriptResult.Transcript}";

                        transientContexts.Add(new ChatMessage
                        {
                            Role = "system",
                            Content = transcriptContext
                        });
                        messagesForModel = BuildMessagesForModel(modelUserText, imagesBase64);
                        InsertTransientContexts(messagesForModel, transientContexts);
                        ApplyYouTubeContextToCurrentUserMessage(messagesForModel, modelUserText, youtubeUrl, transcriptResult.Title, transcriptResult.Transcript);

                        AddSystemMessage("YouTube audio transcription added to this response.");
                    }
                    else
                    {
                        var error = $"YouTube transcript unavailable: {transcriptResult.Error}";
                        AddSystemMessage(error);
                        assistantMessage.Body.Text = error;
                        SpeakLastResponse(error, assistantMessage);
                        return;
                    }
                }
            }

            var shouldSearchWeb = WebSearchToggle.IsChecked == true && ShouldTriggerWebSearch(modelUserText);
            if (shouldSearchWeb)
            {
                if (string.IsNullOrWhiteSpace(_tavily.ApiKey))
                {
                    AddSystemMessage("Web search is on, but no Tavily API key is set.");
                }
                else
                {
                    SetUIState("searching", "Searching web...");
                    var webSearchQuery = RemoveWebSearchTriggerPhrases(modelUserText);
                    var searchContext = await _tavily.SearchAndBuildContextAsync(webSearchQuery, maxResults: 5, ct: _chatCts.Token);
                    if (!string.IsNullOrWhiteSpace(searchContext))
                    {
                        RememberWebSearchContext(webSearchQuery, searchContext);
                        AddSystemMessage("Web search results added to this response.");
                        messagesForModel = BuildMessagesForModel(modelUserText, imagesBase64);
                        InsertTransientContexts(messagesForModel, transientContexts);
                        messagesForModel.Insert(Math.Max(0, messagesForModel.Count - 1), new ChatMessage
                        {
                            Role = "system",
                            Content = "You have current web search context for this answer. Use it as the authoritative source for current facts, releases, versions, prices, dates, schedules, and news. If your training data conflicts with the web context, say the web context is newer and answer from it. Do not dismiss retrieved sources as fake unless the source itself says so."
                        });
                        if (IsCodeOrScriptRequest(modelUserText))
                        {
                            messagesForModel.Insert(Math.Max(0, messagesForModel.Count - 1), new ChatMessage
                            {
                                Role = "system",
                                Content = GetCodeArtifactSystemInstruction(modelUserText)
                            });
                        }
                        messagesForModel[^1] = new ChatMessage
                        {
                            Role = "user",
                            Content = $"{RemoveWebSearchTriggerPhrases(modelUserText)}\n\nCurrent web search context:\n{searchContext}\n\nAnswer the user's question using the current web search context above. If the question asks for the latest or current information, prioritize dated official sources over your prior knowledge.",
                            ImagesBase64 = imagesBase64
                        };
                    }
                    else
                    {
                        AddSystemMessage("Web search did not return usable results. Answering from model knowledge.");
                    }
                }
            }

            ApplyDocumentContextToCurrentUserMessage(messagesForModel, documentContext);

            var systemPrompt = GetEffectiveSystemPrompt(modelUserText);
            var maxTokens = GetMaxTokensForRequest(modelUserText, model);
            var contextTokens = await GetContextTokensForRequestAsync(model, _chatCts.Token);
            var droppedContextMessages = TrimMessagesToContextBudget(messagesForModel, systemPrompt, contextTokens, maxTokens);
            AddTokenEstimateDiagnostic(userText, messagesForModel, systemPrompt, contextTokens, maxTokens, droppedContextMessages);

            if (StreamToggle.IsChecked == true)
            {
                // Stream
                SetUIState("thinking", "Thinking...");
                var fullText = new StringBuilder();
                await _ollama.ChatStreamAsync(
                    model,
                    messagesForModel,
                    systemPrompt,
                    TempSlider.Value,
                    maxTokens,
                    contextTokens,
                    onToken: token =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            fullText.Append(token);
                            var streamingText = fullText.ToString();
                            var shouldPreserveCode = IsCodeOrScriptRequest(modelUserText) || ContainsFencedCodeBlock(streamingText);
                            assistantMessage.Body.Text = CleanDisplayText(streamingText, preserveCodeBlocks: shouldPreserveCode);
                            ScrollChat();
                        }, DispatcherPriority.Background);
                    },
                    onComplete: async full =>
                    {
                        try
                        {
                            await Dispatcher.InvokeAsync(() => AddBackendFinishDiagnostic("Backend usage", full.Length, maxTokens, contextTokens));

                            var completed = await CompleteCodeArtifactIfNeededAsync(
                                full,
                                modelUserText,
                                messagesForModel,
                                systemPrompt,
                                model,
                                TempSlider.Value,
                                maxTokens,
                                contextTokens,
                                _chatCts.Token);

                            Dispatcher.Invoke(() =>
                            {
                                var isCodeResponse = IsCodeOrScriptRequest(modelUserText) || ContainsFencedCodeBlock(completed);
                                var cleaned = CleanDisplayText(completed, preserveCodeBlocks: isCodeResponse);
                                if (!string.IsNullOrWhiteSpace(cleaned))
                                {
                                    SetAssistantMessageText(assistantMessage, cleaned, isCodeResponse);
                                    _history.Add("assistant", cleaned);
                                    SpeakLastResponse(cleaned, assistantMessage);
                                }
                                else
                                {
                                    SetUIState("idle", "Ready");
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() =>
                            {
                                assistantMessage.Body.Text = $"Error: {ex.Message}";
                                AddSystemMessage($"Code/SVG continuation error: {ex.Message}");
                                SetUIState("idle", "Ready");
                            });
                        }
                    },
                    onError: ex =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            assistantMessage.Body.Text = $"Error: {ex.Message}";
                            AddSystemMessage($"API Error: {ex.Message}");
                            SetUIState("idle", "Ready");
                        });
                    },
                    ct: _chatCts.Token
                );
            }
            else
            {
                // Non-streaming
                SetUIState("processing", "Generating...");
                var response = await _ollama.ChatAsync(
                    model,
                    messagesForModel,
                    systemPrompt,
                    TempSlider.Value,
                    maxTokens,
                    _chatCts.Token,
                    contextTokens
                );
                AddBackendFinishDiagnostic("Backend usage", response.Length, maxTokens, contextTokens);

                response = await CompleteCodeArtifactIfNeededAsync(
                    response,
                    modelUserText,
                    messagesForModel,
                    systemPrompt,
                    model,
                    TempSlider.Value,
                    maxTokens,
                    contextTokens,
                    _chatCts.Token);

                var isCodeResponse = IsCodeOrScriptRequest(modelUserText) || ContainsFencedCodeBlock(response);
                var cleaned = CleanDisplayText(response, preserveCodeBlocks: isCodeResponse);
                SetAssistantMessageText(assistantMessage, cleaned, isCodeResponse);
                _history.Add("assistant", cleaned);
                SpeakLastResponse(cleaned, assistantMessage);
            }
        }
        catch (OperationCanceledException)
        {
            if (assistantMessage is not null)
                assistantMessage.Body.Text += " [cancelled]";
            SetUIState("idle", "Ready");
        }
        catch (Exception ex)
        {
            if (assistantMessage is not null)
                assistantMessage.Body.Text = $"Error: {ex.Message}";
            AddSystemMessage($"Chat error: {ex.Message}");
            SetUIState("idle", "Ready");
        }
    }

    // ==================== Scheduler ====================

    private void Scheduler_Click(object sender, RoutedEventArgs e)
    {
        if (_schedulerWindow is { IsVisible: true })
        {
            _schedulerWindow.Activate();
            return;
        }

        _schedulerWindow = new SchedulerWindow(
            _schedulerStore,
            SaveScheduler,
            RunScheduledTaskNowAsync,
            AddScheduledRunToMainChatAsync,
            path => _speech.PlayAudioFile(path))
        {
            Owner = this
        };
        _schedulerWindow.Closed += (_, _) => _schedulerWindow = null;
        _schedulerWindow.Show();
    }

    private void SaveScheduler()
    {
        _schedulerStore.Save();
        _schedulerWindow?.RefreshTasks();
    }

    private async Task RunDueScheduledTasksAsync()
    {
        if (_schedulerRunning)
            return;

        if (SendBtn?.IsEnabled != true)
            return;

        var now = DateTime.Now;
        var due = _schedulerStore.Tasks
            .Where(t => t.IsEnabled && t.NextRunAt <= now && !string.IsNullOrWhiteSpace(t.Prompt))
            .OrderBy(t => t.NextRunAt)
            .ToList();

        if (due.Count == 0)
            return;

        _schedulerRunning = true;
        try
        {
            foreach (var task in due)
                await ExecuteAndStoreScheduledTaskAsync(task, CancellationToken.None);
        }
        finally
        {
            _schedulerRunning = false;
        }
    }

    private async Task RunScheduledTaskNowAsync(ScheduledPromptTask task)
    {
        if (_schedulerRunning)
            throw new InvalidOperationException("A scheduled task is already running.");
        if (SendBtn?.IsEnabled != true)
            throw new InvalidOperationException("The app is busy. Try again after the current response finishes.");

        _schedulerRunning = true;
        try
        {
            await ExecuteAndStoreScheduledTaskAsync(task, CancellationToken.None, advanceSchedule: false);
        }
        finally
        {
            _schedulerRunning = false;
        }
    }

    private async Task ExecuteAndStoreScheduledTaskAsync(
        ScheduledPromptTask task,
        CancellationToken ct,
        bool advanceSchedule = true)
    {
        var run = new ScheduledPromptRun
        {
            StartedAt = DateTime.Now,
            Prompt = task.Prompt
        };

        task.LastStatus = "Running";
        _schedulerStore.Save();
        _schedulerWindow?.RefreshTasks();

        try
        {
            var result = await ExecuteScheduledPromptAsync(task.Prompt, ct);
            run.ResponseText = result.Text;
            run.AudioPath = result.AudioPath;
            task.LastStatus = "Completed";
            if (task.ShowInMainChat)
                await AddScheduledRunToMainChatAsync(task, run);
        }
        catch (Exception ex)
        {
            run.Error = ex.Message;
            task.LastStatus = $"Error: {ex.Message}";
            if (task.ShowInMainChat)
                await AddScheduledRunToMainChatAsync(task, run);
        }
        finally
        {
            run.CompletedAt = DateTime.Now;
            _schedulerStore.AddRun(task, run);
            if (advanceSchedule)
                SchedulerStore.AdvanceAfterRun(task, DateTime.Now);
            _schedulerStore.Save();
            _schedulerWindow?.RefreshTasks();
        }
    }

    private async Task<ScheduledPromptResult> ExecuteScheduledPromptAsync(string prompt, CancellationToken ct)
    {
        string model = "";
        string systemPrompt = "";
        double temperature = 0.7;
        int maxTokens = 2048;
        bool makeAudio = false;
        bool webSearchEnabled = false;
        string tavilyApiKey = "";

        await Dispatcher.InvokeAsync(() =>
        {
            model = ModelCombo.Text;
            systemPrompt = GetEffectiveSystemPrompt(prompt);
            temperature = TempSlider.Value;
            maxTokens = GetMaxTokensForRequest(prompt, model);
            makeAudio = TtsToggle.IsChecked == true;
            webSearchEnabled = WebSearchToggle.IsChecked == true;
            tavilyApiKey = TavilyApiKeyBox.Password.Trim();
        });

        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Select a model before running scheduled prompts.");

        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "user",
                Content = prompt
            }
        };

        var modelPrompt = prompt;
        if (webSearchEnabled && ShouldTriggerWebSearch(prompt) && !string.IsNullOrWhiteSpace(tavilyApiKey))
        {
            _tavily.ApiKey = tavilyApiKey;
            var webSearchQuery = RemoveWebSearchTriggerPhrases(prompt);
            var searchContext = await _tavily.SearchAndBuildContextAsync(webSearchQuery, maxResults: 5, ct: ct);
            if (!string.IsNullOrWhiteSpace(searchContext))
            {
                modelPrompt =
                    $"{webSearchQuery}\n\nCurrent web search context:\n{searchContext}\n\nAnswer the user's scheduled prompt using the current web search context above. If the prompt asks for latest or current information, prioritize dated current sources.";
                messages.Insert(0, new ChatMessage
                {
                    Role = "system",
                    Content = "You have current web search context for this scheduled answer. Use it as the authoritative source for current facts, releases, versions, prices, dates, schedules, and news."
                });
                messages[^1] = new ChatMessage { Role = "user", Content = modelPrompt };
            }
        }

        var contextTokens = await GetContextTokensForRequestAsync(model, ct);
        TrimMessagesToContextBudget(messages, systemPrompt, contextTokens, maxTokens);
        var response = await _ollama.ChatAsync(model, messages, systemPrompt, temperature, maxTokens, ct, contextTokens);
        response = await CompleteCodeArtifactIfNeededAsync(
            response,
            prompt,
            messages,
            systemPrompt,
            model,
            temperature,
            maxTokens,
            contextTokens,
            ct);

        var isCodeResponse = IsCodeOrScriptRequest(prompt) || ContainsFencedCodeBlock(response);
        var cleaned = CleanDisplayText(response, preserveCodeBlocks: isCodeResponse);
        var audioPath = "";
        if (makeAudio)
        {
            var speechText = CleanSpeechText(cleaned);
            if (!string.IsNullOrWhiteSpace(speechText))
                audioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetSchedulerAudioDirectory()) ?? "";
        }

        return new ScheduledPromptResult(cleaned, audioPath);
    }

    private async Task AddScheduledRunToMainChatAsync(ScheduledPromptTask task, ScheduledPromptRun run)
    {
        await Dispatcher.InvokeAsync(() =>
        {
            var promptLabel = $"[Scheduled] {task.Name}\n\n{run.Prompt}";
            AddUserMessage(promptLabel);
            _history.Add("user", promptLabel);

            var response = string.IsNullOrWhiteSpace(run.Error)
                ? run.ResponseText
                : $"Scheduled task error: {run.Error}";
            var assistantMessage = AddAssistantMessage(response);
            if (!string.IsNullOrWhiteSpace(run.AudioPath) && File.Exists(run.AudioPath))
                AddAudioButtons(assistantMessage, run.AudioPath);

            _history.Add("assistant", response);
        });
    }

    private static string GetSchedulerAudioDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceChatbot",
            "scheduled-audio");
    }

    private static void InsertTransientContexts(List<ChatMessage> messages, IEnumerable<ChatMessage> contexts)
    {
        foreach (var context in contexts.Where(c => !string.IsNullOrWhiteSpace(c.Content)))
            messages.Insert(Math.Max(0, messages.Count - 1), context);
    }

    private static void ApplyDocumentContextToCurrentUserMessage(List<ChatMessage> messages, string documentContext)
    {
        if (string.IsNullOrWhiteSpace(documentContext))
            return;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (!messages[i].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                continue;

            if (messages[i].Content.Contains("Attached document context for this response.", StringComparison.Ordinal))
                return;

            messages[i] = new ChatMessage
            {
                Role = messages[i].Role,
                Content = $"{messages[i].Content}\n\n{documentContext}\n\nUse the attached document context above when answering this question.",
                ImagesBase64 = messages[i].ImagesBase64
            };
            return;
        }
    }

    private static void ApplyYouTubeContextToCurrentUserMessage(
        List<ChatMessage> messages,
        string userText,
        string youtubeUrl,
        string title,
        string transcript)
    {
        if (string.IsNullOrWhiteSpace(transcript))
            return;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (!messages[i].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                continue;

            var request = userText.Trim();
            if (string.Equals(request, youtubeUrl, StringComparison.OrdinalIgnoreCase))
                request = "Summarize this video and list its main points.";

            var titleLine = string.IsNullOrWhiteSpace(title) ? "" : $"Title: {title}\n";
            messages[i] = new ChatMessage
            {
                Role = messages[i].Role,
                Content =
                    $"{request}\n\n" +
                    $"YouTube transcript for {youtubeUrl}\n" +
                    titleLine +
                    $"Transcript:\n{transcript}\n\n" +
                    "Use the transcript above as the video content. Do not say that you cannot watch or access the video.",
                ImagesBase64 = messages[i].ImagesBase64,
                Timestamp = messages[i].Timestamp
            };
            return;
        }
    }

    private async Task RunQwenImageCreateAsync(string userText, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        _chatCts = new CancellationTokenSource();
        SaveImageSettingsFromUi();
        AddUserMessage(userText);
        _history.Add("user", userText);
        var assistantMessage = AddAssistantMessage("Creating image with Qwen Image on ComfyUI...");
        SetUIState("processing", "Creating image...");

        try
        {
            var result = await _comfyImages.CreateQwenImageAsync(
                prompt,
                _settings.ImageWidth,
                _settings.ImageHeight,
                _settings.QwenCreateSteps,
                _chatCts.Token);

            _latestGeneratedImagePath = result.LocalPath;
            assistantMessage.Body.Text = BuildGeneratedImageMessage(result);
            AddGeneratedImageToAssistantMessage(assistantMessage, result.LocalPath);
            _history.Add("assistant", BuildGeneratedImageHistoryText(result));
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Body.Text = "Image creation cancelled.";
        }
        catch (Exception ex)
        {
            assistantMessage.Body.Text = $"Image creation failed: {ex.Message}";
            AddSystemMessage($"ComfyUI image error: {ex.Message}");
        }
        finally
        {
            SetUIState("idle", "Ready");
        }
    }

    private async Task RunQwenImageEditAsync(string userText, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        var sourcePaths = GetImageEditSourcePaths();
        if (sourcePaths.Count == 0)
        {
            AddSystemMessage("Attach an image first, or create an image before using Edit.");
            return;
        }

        _chatCts = new CancellationTokenSource();
        SaveImageSettingsFromUi();
        AddUserMessage(userText, sourcePaths);
        var sourceNames = string.Join(", ", sourcePaths.Select(Path.GetFileName));
        _history.Add("user", $"{userText}\n\nImage edit source(s): {sourceNames}");
        var isTwoImageEdit = sourcePaths.Count >= 2;
        var assistantMessage = AddAssistantMessage(isTwoImageEdit
            ? "Mixing images with Qwen Image Edit two-image workflow on ComfyUI..."
            : "Editing image with Qwen Image Edit on ComfyUI...");
        SetUIState("processing", "Editing image...");

        try
        {
            var result = isTwoImageEdit
                ? await _comfyImages.EditQwenImagesAsync(
                    sourcePaths,
                    prompt,
                    _settings.QwenEditSteps,
                    _chatCts.Token)
                : await _comfyImages.EditQwenImageAsync(
                    sourcePaths[0],
                    prompt,
                    _settings.QwenEditSteps,
                    _chatCts.Token);

            _latestGeneratedImagePath = result.LocalPath;
            _pendingImages.Clear();
            UpdateImageButtonLabel();
            assistantMessage.Body.Text = BuildGeneratedImageMessage(result);
            AddGeneratedImageToAssistantMessage(assistantMessage, result.LocalPath);
            _history.Add("assistant", BuildGeneratedImageHistoryText(result));
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Body.Text = "Image edit cancelled.";
        }
        catch (Exception ex)
        {
            assistantMessage.Body.Text = $"Image edit failed: {ex.Message}";
            AddSystemMessage($"ComfyUI edit error: {ex.Message}");
        }
        finally
        {
            SetUIState("idle", "Ready");
        }
    }

    private async Task RunLtxVideoAsync(string userText, string prompt, int? requestedSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        var sourcePath = GetVideoSourceImagePath();
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            AddSystemMessage("Attach an image first, or create an image before using Video.");
            return;
        }

        _chatCts = new CancellationTokenSource();
        SaveImageSettingsFromUi();
        var seconds = requestedSeconds.HasValue ? Math.Clamp(requestedSeconds.Value, 1, 30) : _settings.VideoSeconds;
        var audioPath = File.Exists(_pendingVideoAudioPath) ? _pendingVideoAudioPath : null;
        AddUserMessage(userText, new[] { sourcePath });
        _history.Add("user", $"{userText}\n\nVideo source image: {Path.GetFileName(sourcePath)}" +
            (string.IsNullOrWhiteSpace(audioPath) ? "" : $"\nVideo speech audio: {Path.GetFileName(audioPath)}"));
        var assistantMessage = AddAssistantMessage(string.IsNullOrWhiteSpace(audioPath)
            ? "Creating video with video_ltx2_3_i2v on ComfyUI..."
            : "Creating video with video_ltx2_3_ia2v on ComfyUI...");
        SetUIState("processing", "Creating video...");

        try
        {
            var result = await _comfyImages.CreateLtxVideoAsync(
                sourcePath,
                audioPath,
                prompt,
                seconds,
                _settings.VideoFps,
                _chatCts.Token);
            var syncedVideoPath = await CopyVideoToSyncedDirectoryAsync(result.LocalPath, CancellationToken.None);

            _latestGeneratedVideoPath = result.LocalPath;
            _pendingImages.Clear();
            _pendingVideoAudioPath = "";
            UpdateImageButtonLabel();
            UpdateAudioButtonLabel();
            assistantMessage.Body.Text = BuildGeneratedVideoMessage(result, syncedVideoPath);
            AddGeneratedVideoToAssistantMessage(assistantMessage, result.LocalPath);
            _history.Add("assistant", BuildGeneratedVideoHistoryText(result, syncedVideoPath));
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Body.Text = "Video creation cancelled.";
        }
        catch (Exception ex)
        {
            assistantMessage.Body.Text = $"Video creation failed: {ex.Message}";
            AddSystemMessage($"ComfyUI video error: {ex.Message}");
        }
        finally
        {
            SetUIState("idle", "Ready");
        }
    }

    private void SaveImageSettingsFromUi()
    {
        _settings.ComfyUiUrl = string.IsNullOrWhiteSpace(ComfyUrlBox.Text)
            ? "http://localhost:8000"
            : ComfyUrlBox.Text.Trim();
        _settings.ImageWidth = ParseBoundedInt(ImageWidthBox.Text, AppSettings.DefaultImageWidth, 256, 2048);
        _settings.ImageHeight = ParseBoundedInt(ImageHeightBox.Text, AppSettings.DefaultImageHeight, 256, 2048);
        _settings.QwenCreateSteps = ParseBoundedInt(QwenCreateStepsBox.Text, 4, 1, 80);
        _settings.QwenEditSteps = ParseBoundedInt(QwenEditStepsBox.Text, 40, 1, 80);
        _settings.VideoSeconds = ParseBoundedInt(VideoSecondsBox.Text, 6, 1, 30);
        _settings.VideoFps = ParseBoundedInt(VideoFpsBox.Text, 24, 1, 60);
        ConfigureImageClient();
        SettingsManager.Save(_settings);
    }

    private string GetImageEditSourcePath()
    {
        var attached = _pendingImages.FirstOrDefault(i => File.Exists(i.Path));
        if (attached is not null)
            return attached.Path;

        return File.Exists(_latestGeneratedImagePath) ? _latestGeneratedImagePath : "";
    }

    private List<string> GetImageEditSourcePaths()
    {
        var attached = _pendingImages
            .Where(i => File.Exists(i.Path))
            .Select(i => i.Path)
            .Take(2)
            .ToList();

        if (attached.Count > 0)
            return attached;

        return File.Exists(_latestGeneratedImagePath)
            ? new List<string> { _latestGeneratedImagePath }
            : new List<string>();
    }

    private string GetVideoSourceImagePath()
    {
        var attached = _pendingImages.FirstOrDefault(i => File.Exists(i.Path));
        if (attached is not null)
            return attached.Path;

        return File.Exists(_latestGeneratedImagePath) ? _latestGeneratedImagePath : "";
    }

    private static string BuildGeneratedImageMessage(GeneratedImageResult result)
    {
        return $"{result.WorkflowName} finished.\nPrompt: {result.Prompt}\nSaved: {result.LocalPath}";
    }

    private static string BuildGeneratedImageHistoryText(GeneratedImageResult result)
    {
        return $"{result.WorkflowName} generated an image.\nPrompt: {result.Prompt}\nLocal file: {result.LocalPath}\nRemote file: {result.RemoteFileName}";
    }

    private static string BuildGeneratedVideoMessage(GeneratedVideoResult result, string syncedVideoPath = "")
    {
        var syncedLine = string.IsNullOrWhiteSpace(syncedVideoPath)
            ? ""
            : $"\nGoogle Drive copy: {syncedVideoPath}";

        return $"{result.WorkflowName} finished.\nPrompt: {result.Prompt}\nLength: {result.Seconds} seconds at {result.Fps} FPS\nSaved: {result.LocalPath}{syncedLine}";
    }

    private static string BuildGeneratedVideoHistoryText(GeneratedVideoResult result, string syncedVideoPath = "")
    {
        var syncedLine = string.IsNullOrWhiteSpace(syncedVideoPath)
            ? ""
            : $"\nGoogle Drive copy: {syncedVideoPath}";

        return $"{result.WorkflowName} generated a video.\nPrompt: {result.Prompt}\nLength: {result.Seconds} seconds at {result.Fps} FPS\nLocal file: {result.LocalPath}{syncedLine}\nRemote file: {result.RemoteFileName}";
    }

    private static Task<string> CopyVideoToSyncedDirectoryAsync(string videoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return Task.FromResult("");

        return Task.Run(() =>
        {
            Directory.CreateDirectory(SyncedVideoDirectory);

            var extension = Path.GetExtension(videoPath);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".mp4";

            var originalName = Path.GetFileNameWithoutExtension(videoPath);
            var safeName = Regex.Replace(originalName, @"[^\w\-. ]+", "_").Trim(' ', '.', '_');
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "voicechatbot-video";

            var prefix = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var targetPath = Path.Combine(SyncedVideoDirectory, $"{prefix}_{safeName}{extension}");
            var suffix = 1;
            while (File.Exists(targetPath))
            {
                targetPath = Path.Combine(SyncedVideoDirectory, $"{prefix}_{safeName}_{suffix}{extension}");
                suffix++;
            }

            File.Copy(videoPath, targetPath, overwrite: false);
            return targetPath;
        }, ct);
    }

    private void AddGeneratedImageToAssistantMessage(AssistantMessageUi assistantMessage, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return;

        if (assistantMessage.Body.Parent is not StackPanel stack)
            return;

        var image = new Image
        {
            Source = new BitmapImage(new Uri(imagePath)),
            MaxWidth = 360,
            MaxHeight = 360,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 8, 0, 0)
        };
        stack.Children.Insert(Math.Min(2, stack.Children.Count), image);
        AddImageButtons(assistantMessage, imagePath);
        ScrollChat();
    }

    private void AddImageButtons(AssistantMessageUi assistantMessage, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return;

        assistantMessage.Actions.Visibility = Visibility.Visible;

        var saveBtn = new Button
        {
            Content = "Save Image",
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(0, 184, 148)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Tag = imagePath
        };
        saveBtn.Click += SaveGeneratedImage_Click;
        assistantMessage.Actions.Children.Add(saveBtn);
    }

    private void SaveGeneratedImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string imagePath } || !File.Exists(imagePath))
        {
            AddSystemMessage("Image file is no longer available.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save image",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|All files (*.*)|*.*",
            FileName = Path.GetFileName(imagePath),
            AddExtension = true,
            DefaultExt = Path.GetExtension(imagePath)
        };

        if (dialog.ShowDialog(this) != true)
            return;

        File.Copy(imagePath, dialog.FileName, overwrite: true);
        AddSystemMessage($"Image saved to {dialog.FileName}");
    }

    private void AddGeneratedVideoToAssistantMessage(AssistantMessageUi assistantMessage, string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return;

        if (assistantMessage.Body.Parent is not StackPanel stack)
            return;

        var player = new MediaElement
        {
            Source = new Uri(videoPath),
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Stop,
            MaxWidth = 420,
            Height = 240,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 8, 0, 0)
        };
        stack.Children.Insert(Math.Min(2, stack.Children.Count), player);
        AddVideoButtons(assistantMessage, videoPath, player);
        ScrollChat();
    }

    private void AddVideoButtons(AssistantMessageUi assistantMessage, string videoPath, MediaElement player)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return;

        assistantMessage.Actions.Visibility = Visibility.Visible;

        var playBtn = new Button
        {
            Content = "Play Video",
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(108, 92, 231)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        playBtn.Click += (_, _) =>
        {
            player.Position = TimeSpan.Zero;
            player.Play();
        };
        assistantMessage.Actions.Children.Add(playBtn);

        var openBtn = new Button
        {
            Content = "Open Video",
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(9, 132, 227)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Tag = videoPath
        };
        openBtn.Click += OpenGeneratedVideo_Click;
        assistantMessage.Actions.Children.Add(openBtn);

        var saveBtn = new Button
        {
            Content = "Save Video",
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Color.FromRgb(0, 184, 148)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Tag = videoPath
        };
        saveBtn.Click += SaveGeneratedVideo_Click;
        assistantMessage.Actions.Children.Add(saveBtn);
    }

    private void OpenGeneratedVideo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string videoPath } || !File.Exists(videoPath))
        {
            AddSystemMessage("Video file is no longer available.");
            return;
        }

        Process.Start(new ProcessStartInfo(videoPath) { UseShellExecute = true });
    }

    private void SaveGeneratedVideo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string videoPath } || !File.Exists(videoPath))
        {
            AddSystemMessage("Video file is no longer available.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save video",
            Filter = "MP4 video (*.mp4)|*.mp4|WEBM video (*.webm)|*.webm|All files (*.*)|*.*",
            FileName = Path.GetFileName(videoPath),
            AddExtension = true,
            DefaultExt = Path.GetExtension(videoPath)
        };

        if (dialog.ShowDialog(this) != true)
            return;

        File.Copy(videoPath, dialog.FileName, overwrite: true);
        AddSystemMessage($"Video saved to {dialog.FileName}");
    }

    private static bool TryGetImageCreatePrompt(string text, out string prompt)
    {
        prompt = "";
        var trimmed = text.Trim();
        var patterns = new[]
        {
            @"^(?:please\s+)?(?:create|generate|make|draw)\s+(?:an?\s+)?image\s+(?:of|showing|with)?\s*(.+)$",
            @"^(?:please\s+)?(?:create|generate|make|draw)\s+(?:a\s+)?picture\s+(?:of|showing|with)?\s*(.+)$"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase);
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                prompt = match.Groups[1].Value.Trim();
                return true;
            }
        }

        return false;
    }

    private static bool TryGetVideoPrompt(string text, out string prompt, out int? seconds)
    {
        prompt = "";
        seconds = null;
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || !Regex.IsMatch(trimmed, @"\b(video|movie|clip)\b", RegexOptions.IgnoreCase))
            return false;

        var patterns = new[]
        {
            @"^(?:please\s+)?(?:create|generate|make)\s+(?:an?\s+)?(?:\d+\s*(?:second|seconds|sec|s)\s+)?(?:video|movie|clip)\s*(?:of|showing|with)?\s*(.+)$",
            @"^(?:please\s+)?(?:turn|make)\s+(?:this\s+)?(?:image|picture|photo)\s+into\s+(?:an?\s+)?(?:\d+\s*(?:second|seconds|sec|s)\s+)?(?:video|movie|clip)\s*(?:where|that|of|showing|with)?\s*(.*)$"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;

            seconds = TryParseVideoSeconds(trimmed);
            prompt = match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : "";
            if (string.IsNullOrWhiteSpace(prompt))
                prompt = RemoveVideoCommandWords(trimmed);
            return true;
        }

        if (Regex.IsMatch(trimmed, @"\b(?:\d+\s*)?(?:second|seconds|sec|s)\s+video\b", RegexOptions.IgnoreCase))
        {
            seconds = TryParseVideoSeconds(trimmed);
            prompt = RemoveVideoCommandWords(trimmed);
            return true;
        }

        return false;
    }

    private static int? TryParseVideoSeconds(string text)
    {
        var match = Regex.Match(text, @"\b(?<n>\d{1,2})\s*(?:second|seconds|sec|s)\b", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups["n"].Value, out var seconds))
            return Math.Clamp(seconds, 1, 30);

        return null;
    }

    private static string RemoveVideoCommandWords(string text)
    {
        var cleaned = Regex.Replace(text, @"^(?:please\s+)?(?:create|generate|make|turn)\s+", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\b(?:an?\s+)?\d*\s*(?:second|seconds|sec|s)?\s*(?:video|movie|clip)\b", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
        return string.IsNullOrWhiteSpace(cleaned) ? text.Trim() : cleaned;
    }

    private static bool TryGetImageEditPrompt(string text, out string prompt)
    {
        prompt = "";
        var trimmed = text.Trim();
        var patterns = new[]
        {
            @"^(?:please\s+)?edit\s+(?:this\s+)?(?:image|picture|photo)?\s*(?:and|to)?\s*(.+)$",
            @"^(?:please\s+)?(?:change|modify)\s+(?:this\s+)?(?:image|picture|photo)\s+(?:to|and)?\s*(.+)$"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase);
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                prompt = match.Groups[1].Value.Trim();
                return true;
            }
        }

        return false;
    }

    private static bool TryCreateHermesPrompt(string text, out string prompt)
    {
        prompt = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = Regex.Match(text.Trim(), @"^(?:hey\s+)?hermes(?:\s*[,:\-;]\s*|\s+)(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            return false;

        prompt = match.Groups[1].Value.Trim();
        return true;
    }

    private static bool IsHermesModelControlCommand(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var normalized = prompt.ToLowerInvariant();
        var hasControlVerb = Regex.IsMatch(normalized, @"\b(start|stop|switch|restart|load|unload|kill|launch|run|change)\b");
        var hasModelTarget = Regex.IsMatch(normalized, @"\b(llama\.?cpp|model|gpu|gpt-oss|120b|70b|30b|13b|12b|7b|comfyui|comfy)\b");
        return hasControlVerb && hasModelTarget;
    }

    private sealed record LlamaModelControlPlan(string Description, string Command, int? EndpointPort, bool RefreshLlamaModels = true);

    private static bool TryBuildLlamaModelControlPlan(string prompt, out LlamaModelControlPlan plan)
    {
        plan = new LlamaModelControlPlan("", "", null);
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var normalized = Regex.Replace(prompt.ToLowerInvariant(), @"[^a-z0-9.]+", " ").Trim();
        if (normalized.Contains("comfyui", StringComparison.Ordinal) || normalized.Contains("comfy", StringComparison.Ordinal))
        {
            var wantsComfyStop = Regex.IsMatch(normalized, @"\b(stop|kill|shutdown|shut down|terminate|unload)\b");
            var comfyBatch = wantsComfyStop
                ? "/mnt/c/llama.cpp/Stop-ComfyUI-LAN.bat"
                : "/mnt/c/llama.cpp/Start-ComfyUI-LAN.bat";
            var action = wantsComfyStop ? "Stopping" : "Starting";
            var comfyLabel = Path.GetFileNameWithoutExtension(comfyBatch).Replace("-", "_", StringComparison.OrdinalIgnoreCase);
            plan = new LlamaModelControlPlan(
                $"{action} ComfyUI using {Path.GetFileName(comfyBatch)}",
                wantsComfyStop
                    ? BuildRunWindowsBatchCommand(comfyBatch, comfyLabel, detach: false)
                    : BuildRunWindowsBatchCommand(comfyBatch, comfyLabel, detach: true),
                null,
                RefreshLlamaModels: false);
            return true;
        }

        var wantsStop = Regex.IsMatch(normalized, @"\b(stop|kill|shutdown|shut down|terminate|unload)\b")
                        && Regex.IsMatch(normalized, @"\b(current|running|llama|llama.cpp|model|server)\b");
        if (wantsStop)
        {
            plan = new LlamaModelControlPlan(
                "Stopping current llama.cpp model processes",
                BuildStopLlamaCommand(),
                null);
            return true;
        }

        var wantsStart = Regex.IsMatch(normalized, @"\b(start|switch|load|launch|run|change)\b");
        if (!wantsStart)
            return false;

        var target = ResolveLlamaBatchFile(normalized);
        if (target is null)
            return false;

        var (batch, port) = target.Value;
        var label = Path.GetFileNameWithoutExtension(batch).Replace("start-", "", StringComparison.OrdinalIgnoreCase);
        plan = new LlamaModelControlPlan(
            $"Starting llama.cpp model using {Path.GetFileName(batch)}",
            BuildStartLlamaBatchCommand(batch, label),
            port);
        return true;
    }

    private static (string BatchPath, int Port)? ResolveLlamaBatchFile(string normalizedPrompt)
    {
        if (Regex.IsMatch(normalizedPrompt, @"\bgpt\s*oss\b") || normalizedPrompt.Contains("gpt oss") || normalizedPrompt.Contains("gpt-oss") || normalizedPrompt.Contains("120b"))
            return ("/mnt/c/llama.cpp/start-gpt-oss-120b.bat", 8084);
        if (normalizedPrompt.Contains("mistral"))
            return normalizedPrompt.Contains("text")
                ? ("/mnt/c/llama.cpp/start-mistral-medium-3.5-textonly.bat", 8082)
                : ("/mnt/c/llama.cpp/start-mistral-medium-3.5.bat", 8082);
        if (normalizedPrompt.Contains("qwen"))
            return ("/mnt/c/llama.cpp/start-qwen3.6-27b-q8.bat", 8081);
        if (normalizedPrompt.Contains("lfm"))
            return ("/mnt/c/llama.cpp/start-lfm2.5-8b.bat", 8083);
        if (normalizedPrompt.Contains("gemma") && normalizedPrompt.Contains("12b"))
            return ("/mnt/c/llama.cpp/start-gemma4-12b.bat", 8083);
        if (normalizedPrompt.Contains("gemma") && (normalizedPrompt.Contains("4b") || normalizedPrompt.Contains("e4b")))
            return ("/mnt/c/llama.cpp/start-gemma4-4b.bat", 8081);
        if (normalizedPrompt.Contains("gemma") && (normalizedPrompt.Contains("spec") || normalizedPrompt.Contains("draft")))
            return ("/mnt/c/llama.cpp/start-gemma4-speculative.bat", 8080);
        if (normalizedPrompt.Contains("gemma") && (normalizedPrompt.Contains("gpu") || normalizedPrompt.Contains("31b gpu")))
            return ("/mnt/c/llama.cpp/start-gemma4-gpu.bat", 8080);
        if (normalizedPrompt.Contains("gemma") && (normalizedPrompt.Contains("26b") || normalizedPrompt.Contains("a4b")))
            return normalizedPrompt.Contains("spec")
                ? ("/mnt/c/llama.cpp/start-gemma4-26b-a4b-speculative.bat", 8080)
                : ("/mnt/c/llama.cpp/start-gemma4-26b-a4b.bat", 8080);
        if (normalizedPrompt.Contains("gemma"))
            return ("/mnt/c/llama.cpp/start-gemma4.bat", 8080);

        return null;
    }

    private static string BuildStopLlamaCommand()
    {
        return "bash -lc " + BashQuote(string.Join("\n", new[]
        {
            "set +e",
            "echo 'Stopping llama.cpp model processes...'",
            "for image in llama-server.exe llama-cli.exe server.exe main.exe; do",
            "  echo \"taskkill $image\"",
            "  timeout 12s /mnt/c/Windows/System32/taskkill.exe /F /T /IM \"$image\" 2>&1 || true",
            "done",
            "echo 'Stop command sent.'",
            "exit 0"
        }));
    }

    private static string BuildStartLlamaBatchCommand(string batchPath, string label)
    {
        return BuildRunWindowsBatchCommand(batchPath, label, detach: true);
    }

    private static string BuildRunWindowsBatchCommand(string batchPath, string label, bool detach)
    {
        var windowsBatchPath = batchPath
            .Replace("/mnt/c/", "C:\\", StringComparison.OrdinalIgnoreCase)
            .Replace("/", "\\");
        var logName = Regex.Replace(label.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(logName))
            logName = "batch";

        var logPath = $"/tmp/voicechatbot_{logName}.log";
        var runLine = detach
            ? $"nohup /mnt/c/Windows/System32/cmd.exe /c start \"\" \"{windowsBatchPath}\" > {logPath} 2>&1 < /dev/null &"
            : $"timeout 75s /mnt/c/Windows/System32/cmd.exe /c \"{windowsBatchPath}\" > {logPath} 2>&1 || true";

        return "bash -lc " + BashQuote(string.Join("\n", new[]
        {
            "set -e",
            $"batch={BashQuote(batchPath)}",
            "if [ ! -f \"$batch\" ]; then echo \"Batch file not found: $batch\"; exit 2; fi",
            detach ? "echo 'Starting Windows batch file with:'" : "echo 'Running Windows batch file with:'",
            "echo \"$batch\"",
            "cd \"$(dirname \"$batch\")\"",
            runLine,
            (detach ? "echo 'Start command sent. Log: " : "echo 'Batch command finished or timed out. Log: ") + logPath + "'",
            "if [ -f " + BashQuote(logPath) + " ]; then tail -n 40 " + BashQuote(logPath) + " || true; fi",
            "exit 0"
        }));
    }

    private static string BashQuote(string value)
    {
        return "'" + (value ?? "").Replace("'", "'\"'\"'") + "'";
    }

    private async Task<string> RunLlamaModelControlPlanAsync(LlamaModelControlPlan plan, CancellationToken ct)
    {
        using var sshTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sshTimeout.CancelAfter(TimeSpan.FromSeconds(75));
        var result = await _hermesSsh.RunAsync(plan.Command, TimeSpan.FromSeconds(60), sshTimeout.Token);

        var output = new StringBuilder();
        output.AppendLine(plan.Description);
        output.AppendLine();
        if (result.TimedOut)
            output.AppendLine("The SSH command timed out before confirming completion.");
        else if (result.ExitStatus == 0)
            output.AppendLine("Command completed.");
        else
            output.AppendLine($"Command returned exit code {result.ExitStatus}.");

        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            output.AppendLine();
            output.AppendLine(result.Stdout.Trim());
        }

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            output.AppendLine();
            output.AppendLine("Error output:");
            output.AppendLine(result.Stderr.Trim());
        }

        return output.ToString().Trim();
    }

    private string BuildLlamaEndpointUrl(int port)
    {
        var host = _settings.HermesSshHost;
        if (string.IsNullOrWhiteSpace(host))
            host = "127.0.0.1";

        try
        {
            if (Uri.TryCreate(_settings.OpenAiCompatibleUrl, UriKind.Absolute, out var uri)
                && !string.IsNullOrWhiteSpace(uri.Host))
            {
                host = uri.Host;
            }
        }
        catch
        {
            // Fall back to SSH host.
        }

        return $"http://{host}:{port}/v1";
    }

    private string SetLlamaEndpointPort(int port)
    {
        var url = BuildLlamaEndpointUrl(port);
        _settings.OpenAiCompatibleUrl = url;
        OpenAiUrlBox.Text = url;
        ConfigureChatClient();
        SettingsManager.Save(_settings);
        AddSystemMessage($"llama.cpp endpoint set to {url}");
        return url;
    }

    private async Task<(bool Ready, string Message)> WaitForLlamaEndpointReadyAsync(string endpoint, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var lastError = "";

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/models");
                if (!string.IsNullOrWhiteSpace(_settings.OpenAiCompatibleApiKey))
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_settings.OpenAiCompatibleApiKey}");

                using var response = await http.SendAsync(request, requestTimeout.Token);
                var body = await response.Content.ReadAsStringAsync(requestTimeout.Token);
                if (response.IsSuccessStatusCode)
                    return (true, $"Endpoint is responding: {endpoint}");

                lastError = $"{(int)response.StatusCode}: {body.Trim()}";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        return (false, $"Endpoint set to {endpoint}, but it is not responding yet. Last check: {lastError}");
    }

    private static bool TryGetDirectHermesSshCommand(string prompt, out string command)
    {
        command = "";
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var match = Regex.Match(prompt.Trim(), @"^(?:run|execute|shell|terminal|cli)\s+(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            return false;

        command = match.Groups[1].Value.Trim();
        return true;
    }

    private static string BuildHermesCliPrompt(string prompt)
    {
        if (!IsHermesModelControlCommand(prompt))
            return prompt;

        return string.Join(Environment.NewLine, new[]
        {
            "You are being called from Keith's VoiceChatbot over SSH on the Windows/WSL host.",
            "Handle this as a practical Hermes CLI model-control request.",
            "Do not leave the CLI waiting on a long-running llama.cpp server or batch file in the foreground.",
            "If you start a model or batch file, launch it detached/backgrounded so this command returns text promptly.",
            "You are allowed to run the commands needed to inspect and perform this model-control task.",
            "First check what is currently running if that matters, then respond with what you did.",
            "",
            "User request:",
            prompt
        });
    }

    private static string BuildHermesTimeoutMessage(string prompt)
    {
        if (IsHermesModelControlCommand(prompt))
        {
            return "Hermes did not return from that model-control request fast enough, so I stopped waiting in the app. Nothing was confirmed from Hermes. Use a direct SSH command with 'Hermes run ...' if you want me to stage an exact command for approval.";
        }

        return "Hermes timed out before returning final text. I stopped waiting in the app.";
    }

    private static bool IsHermesApproval(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var normalized = Regex.Replace(prompt.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized is "approve" or "approved" or "yes" or "yep" or "ok" or "okay" or "do it" or "go ahead" or "run it" or "execute" or "confirmed" or "confirm";
    }

    private static bool IsHermesCancel(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var normalized = Regex.Replace(prompt.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized is "cancel" or "no" or "stop" or "never mind" or "nevermind" or "abort";
    }

    private async Task<string> BuildHermesSshApprovalPromptAsync(string request, string command, CancellationToken ct)
    {
        var endpoint = string.IsNullOrWhiteSpace(_settings.OpenAiCompatibleUrl)
            ? "http://localhost:8080/v1"
            : _settings.OpenAiCompatibleUrl.TrimEnd('/');

        var status = await TryInspectLlamaCppEndpointAsync(endpoint, _settings.OpenAiCompatibleApiKey, ct);
        var sb = new StringBuilder();
        sb.AppendLine("Hermes SSH command staged. I have not run anything yet.");
        sb.AppendLine();
        sb.AppendLine("Request:");
        sb.AppendLine(request);
        sb.AppendLine();
        sb.AppendLine("Command to run over SSH:");
        sb.AppendLine(command);
        sb.AppendLine();
        sb.AppendLine("Current llama.cpp endpoint check:");
        sb.AppendLine(status);
        sb.AppendLine();
        sb.AppendLine("Say 'Hermes approve' to let Hermes execute this once, or 'Hermes cancel' to clear it.");
        return sb.ToString().Trim();
    }

    private static async Task<string> TryInspectLlamaCppEndpointAsync(string endpoint, string apiKey, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/models");
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

            using var response = await http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                return $"{endpoint}/models returned {(int)response.StatusCode}: {body.Trim()}";

            using var doc = JsonDocument.Parse(body);
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id))
                        models.Add(id.GetString() ?? "");
                }
            }

            if (models.Count == 0 && doc.RootElement.TryGetProperty("models", out var modelArr) && modelArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in modelArr.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var name))
                        models.Add(name.GetString() ?? "");
                    else if (item.TryGetProperty("model", out var model))
                        models.Add(model.GetString() ?? "");
                }
            }

            return models.Count > 0
                ? $"Reachable at {endpoint}. Reported model(s): {string.Join(", ", models.Where(m => !string.IsNullOrWhiteSpace(m)))}"
                : $"Reachable at {endpoint}, but no model names were reported.";
        }
        catch (Exception ex)
        {
            return $"Could not reach {endpoint}/models: {ex.Message}";
        }
    }

    private async Task SendHermesAgentMessageAsync(string userText, string hermesPrompt)
    {
        _chatCts = new CancellationTokenSource();
        AssistantMessageUi? assistantMessage = null;

        AddUserMessage(userText, new List<string>());
        _history.Add("user", userText);
        assistantMessage = AddAssistantMessage("");

        SetUIState("processing", "Asking Hermes...");
        AddSystemMessage("Running Hermes CLI over SSH.");

        try
        {
            if (IsHermesCancel(hermesPrompt) && !string.IsNullOrWhiteSpace(_pendingHermesControlCommand))
            {
                var cancelled = $"Cancelled pending Hermes action: {_pendingHermesControlCommand}";
                _pendingHermesControlCommand = "";
                _pendingHermesSshCommand = "";
                assistantMessage.Body.Text = cancelled;
                _history.Add("assistant", cancelled);
                SpeakLastResponse(cancelled, assistantMessage);
                return;
            }

            if (IsHermesApproval(hermesPrompt) && !string.IsNullOrWhiteSpace(_pendingHermesSshCommand))
            {
                var command = _pendingHermesSshCommand;
                _pendingHermesControlCommand = "";
                _pendingHermesSshCommand = "";
                AddSystemMessage("Hermes approval received. Running pending SSH command.");
                using var sshTimeout = CancellationTokenSource.CreateLinkedTokenSource(_chatCts.Token);
                sshTimeout.CancelAfter(TimeSpan.FromSeconds(120));
                var result = await _hermesSsh.RunAsync(command, TimeSpan.FromSeconds(90), sshTimeout.Token);
                var display = result.ToDisplayText();
                assistantMessage.Body.Text = display;
                _history.Add("assistant", display);
                SpeakLastResponse(display, assistantMessage);
                await RefreshLlamaCppModelAfterHermesAsync();
                return;
            }
            else if (TryBuildLlamaModelControlPlan(hermesPrompt, out var modelPlan))
            {
                AddSystemMessage($"Running direct llama.cpp model control over SSH: {modelPlan.Description}");
                var display = await RunLlamaModelControlPlanAsync(modelPlan, _chatCts.Token);
                if (modelPlan.EndpointPort is int endpointPort)
                {
                    var endpointUrl = SetLlamaEndpointPort(endpointPort);
                    display += $"{Environment.NewLine}{Environment.NewLine}App endpoint set to {endpointUrl}";
                    var readiness = await WaitForLlamaEndpointReadyAsync(endpointUrl, TimeSpan.FromSeconds(90), _chatCts.Token);
                    display += $"{Environment.NewLine}{readiness.Message}";
                    if (readiness.Ready && _settings.ChatProvider.Equals("OpenAI-compatible", StringComparison.OrdinalIgnoreCase))
                    {
                        AddSystemMessage("Refreshing model list from the new llama.cpp endpoint.");
                        await RefreshModelsInternal();
                    }
                }
                assistantMessage.Body.Text = display;
                _history.Add("assistant", display);
                SpeakLastResponse(display, assistantMessage);
                return;
            }
            else if (TryGetDirectHermesSshCommand(hermesPrompt, out var directCommand))
            {
                _pendingHermesControlCommand = hermesPrompt;
                _pendingHermesSshCommand = directCommand;
                var staged = await BuildHermesSshApprovalPromptAsync(hermesPrompt, directCommand, _chatCts.Token);
                assistantMessage.Body.Text = staged;
                _history.Add("assistant", staged);
                SpeakLastResponse(staged, assistantMessage);
                return;
            }

            var isModelControl = IsHermesModelControlCommand(hermesPrompt);
            var hermesPromptForCli = BuildHermesCliPrompt(hermesPrompt);
            var hermesTimeout = isModelControl ? TimeSpan.FromSeconds(90) : TimeSpan.FromMinutes(3);
            var hermesMaxTurns = isModelControl ? 12 : 12;
            using var hermesCliTimeout = CancellationTokenSource.CreateLinkedTokenSource(_chatCts.Token);
            hermesCliTimeout.CancelAfter(hermesTimeout + TimeSpan.FromSeconds(10));
            var cliResult = await _hermesSsh.RunHermesCliAsync(hermesPromptForCli, hermesTimeout, hermesMaxTurns, hermesCliTimeout.Token);
            var cleaned = CleanDisplayText(cliResult.Stdout);
            if (cliResult.TimedOut)
                cleaned = BuildHermesTimeoutMessage(hermesPrompt);
            if (string.IsNullOrWhiteSpace(cleaned))
                cleaned = cliResult.ToDisplayText();
            assistantMessage.Body.Text = cleaned;
            _history.Add("assistant", $"Hermes result:\n{cleaned}");
            SpeakLastResponse(cleaned, assistantMessage);
            if (isModelControl || IsHermesApproval(hermesPrompt))
                await RefreshLlamaCppModelAfterHermesAsync();
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Body.Text = BuildHermesTimeoutMessage(hermesPrompt);
            SetUIState("idle", "Ready");
        }
        catch (Exception ex)
        {
            assistantMessage.Body.Text = $"Hermes error: {ex.Message}";
            AddSystemMessage($"Hermes error: {ex.Message}");
            SetUIState("idle", "Ready");
        }
    }

    private async Task RefreshLlamaCppModelAfterHermesAsync()
    {
        if (!_settings.ChatProvider.Equals("OpenAI-compatible", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            await Task.Delay(2500);
            AddSystemMessage("Refreshing llama.cpp model list after Hermes command.");
            await RefreshModelsInternal();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not auto-refresh llama.cpp model after Hermes command: {ex.Message}");
        }
    }

    private async Task SendPiAgentMessageAsync(string userText, string piPrompt, string blockedReason)
    {
        _chatCts = new CancellationTokenSource();
        AssistantMessageUi? assistantMessage = null;

        AddUserMessage(userText, new List<string>());
        _history.Add("user", userText);
        assistantMessage = AddAssistantMessage("");

        if (!string.IsNullOrWhiteSpace(blockedReason))
        {
            assistantMessage.Body.Text = blockedReason;
            SpeakLastResponse(blockedReason, assistantMessage);
            return;
        }

        SetUIState("processing", "Asking Pi...");
        AddSystemMessage("Sending read-only request to Pi.");

        try
        {
            var response = await _piAgent.AskAsync(piPrompt, _chatCts.Token);
            var cleaned = CleanDisplayText(response);
            assistantMessage.Body.Text = cleaned;
            _history.Add("assistant", $"Pi result:\n{cleaned}");
            SpeakLastResponse(cleaned, assistantMessage);
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Body.Text += " [cancelled]";
            SetUIState("idle", "Ready");
        }
        catch (Exception ex)
        {
            assistantMessage.Body.Text = $"Pi error: {ex.Message}";
            AddSystemMessage($"Pi error: {ex.Message}");
            SetUIState("idle", "Ready");
        }
    }

    // ==================== Voice Events ====================

    private void OnSpeechRecognized(string text)
    {
        Dispatcher.Invoke(() =>
        {
            AddSystemMessage($"You said: \"{text}\"");
            SendMessage(text);
        });
    }

    private void OnSpeechFinished()
    {
        Dispatcher.Invoke(() =>
        {
            SetUIState("idle", "Ready");
            _speech.ReadyForNextSpeech();

            if (_autoListening)
            {
                // Restart recording for next utterance
                Task.Delay(100).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_autoListening && !_pausedListeningForTextInput && IsVoiceInputAllowedByFacePolicy())
                        {
                            if (_speech.CurrentState == VoiceState.Speaking)
                                return;

                            _speech.StartListening();
                        }
                    });
                });
            }
        });
    }

    private void OnListenTimedOut()
    {
        Dispatcher.Invoke(() =>
        {
            if (_autoListening)
            {
                // No speech heard, restart listening
                Task.Delay(300).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_autoListening && !_pausedListeningForTextInput && IsVoiceInputAllowedByFacePolicy())
                        {
                            if (_speech.CurrentState == VoiceState.Speaking)
                                return;

                            _speech.StartListening();
                        }
                    });
                });
            }
            else
            {
                SetUIState("idle", "Ready");
            }
        });
    }

    private void OnSpeechLog(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            AddSystemMessage($"{message}");
        });
    }

    private void OnVoiceStateChanged(VoiceState state)
    {
        Dispatcher.Invoke(() =>
        {
            switch (state)
            {
                case VoiceState.Idle:
                    StateIndicator.Fill = FindResource("SuccessBrush") as SolidColorBrush;
                    if (!_autoListening) StateLabel.Text = "Ready";
                    break;
                case VoiceState.Listening:
                    StateIndicator.Fill = FindResource("ListeningBrush") as SolidColorBrush;
                    StateLabel.Text = "Listening...";
                    ActivityLabel.Text = "Listening for speech...";
                    _listenStartTime = DateTime.Now;
                    break;
                case VoiceState.Processing:
                    StateIndicator.Fill = FindResource("WarningBrush") as SolidColorBrush;
                    StateLabel.Text = "Processing...";
                    ActivityLabel.Text = "Sending to Ollama...";
                    break;
                case VoiceState.Speaking:
                    StateIndicator.Fill = FindResource("SpeakingBrush") as SolidColorBrush;
                    StateLabel.Text = "Speaking...";
                    ActivityLabel.Text = "";
                    break;
            }
        });
    }

    // ==================== Button Handlers ====================

    private void MicToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_speech.CurrentState == VoiceState.Listening)
        {
            _speech.StopListening();
            _autoListening = false;
            AlwaysListenToggle.IsChecked = false;
            SetUIState("idle", "Ready");
        }
        else
        {
            if (!IsVoiceInputAllowedByFacePolicy())
            {
                AddSystemMessage("Voice input is blocked by local face policy.");
                return;
            }

            _speech.StopSpeaking();
            _speech.StartListening();
        }
    }

    private void ListenToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsVoiceInputAllowedByFacePolicy())
        {
            AddSystemMessage("Voice input is blocked by local face policy.");
            return;
        }

        _speech.StopSpeaking();
        _speech.ReadyForNextSpeech();
        if (!_speech.StartListening())
            SetUIState("idle", "Voice input unavailable");
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        _chatCts?.Cancel();
        _speech.StopAll();
        _autoListening = false;
        AlwaysListenToggle.IsChecked = false;
        SetUIState("idle", "Ready");
        ActivityLabel.Text = "";
    }

    private async void CameraAsk_Click(object sender, RoutedEventArgs e)
    {
        SetUIState("processing", "Capturing camera...");

        try
        {
            var photo = await _camera.CapturePhotoAsync();
            _pendingImages.Add(new PendingImageAttachment(photo.Path, photo.Base64));
            AddSystemMessage("Camera photo ready for your next message.");
            UpdateImageButtonLabel();
            MessageInput.Focus();
            MessageInput.CaretIndex = MessageInput.Text.Length;
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Camera capture failed: {ex.Message}");
        }
        finally
        {
            SetUIState("idle", "Ready");
        }
    }

    private void AttachImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach image",
            Filter = "Image files (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|All files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        foreach (var fileName in dialog.FileNames)
        {
            try
            {
                var bytes = File.ReadAllBytes(fileName);
                _pendingImages.Add(new PendingImageAttachment(fileName, Convert.ToBase64String(bytes)));
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Could not attach {System.IO.Path.GetFileName(fileName)}: {ex.Message}");
            }
        }

        if (_pendingImages.Count > 0)
        {
            AddSystemMessage($"{_pendingImages.Count} image(s) ready for your next message.");
            UpdateImageButtonLabel();
            MessageInput.Focus();
            MessageInput.CaretIndex = MessageInput.Text.Length;
        }
    }

    private void MessageInput_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!ClipboardHasImage())
            return;

        if (TryAttachClipboardImage())
            e.CancelCommand();
    }

    private bool TryAttachClipboardImage()
    {
        try
        {
            var image = GetClipboardImage();
            if (image is null)
                return false;

            return AttachClipboardImage(image);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Clipboard image attach failed: {ex.Message}");
            return true;
        }
    }

    private static bool ClipboardHasImage()
    {
        try
        {
            return Clipboard.ContainsImage()
                || Clipboard.ContainsData(DataFormats.Bitmap)
                || Clipboard.ContainsData("PNG")
                || Clipboard.ContainsData("DeviceIndependentBitmap");
        }
        catch
        {
            return false;
        }
    }

    private static BitmapSource? GetClipboardImage()
    {
        if (Clipboard.ContainsImage())
            return Clipboard.GetImage();

        if (Clipboard.ContainsData(DataFormats.Bitmap) && Clipboard.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            return bitmap;

        if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is Stream pngStream)
        {
            var decoder = BitmapDecoder.Create(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.FirstOrDefault();
        }

        return null;
    }

    private bool AttachClipboardImage(BitmapSource image)
    {
        try
        {
            if (image is null)
                return false;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VoiceChatbot",
                "clipboard-images");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(path))
                encoder.Save(stream);

            var bytes = File.ReadAllBytes(path);
            _pendingImages.Add(new PendingImageAttachment(path, Convert.ToBase64String(bytes)));
            AddSystemMessage("Clipboard image ready for your next message.");
            UpdateImageButtonLabel();
            MessageInput.Focus();
            MessageInput.CaretIndex = MessageInput.Text.Length;
            return true;
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Clipboard image attach failed: {ex.Message}");
            return true;
        }
    }

    private async void CreateImage_Click(object sender, RoutedEventArgs e)
    {
        var prompt = MessageInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            AddSystemMessage("Type an image prompt first.");
            return;
        }

        MessageInput.Clear();
        ResumeListeningAfterTextInput();
        await RunQwenImageCreateAsync($"Create image: {prompt}", prompt);
    }

    private void Krea2_Click(object sender, RoutedEventArgs e)
    {
        SaveImageSettingsFromUi();
        if (_krea2Window != null)
        {
            if (_krea2Window.WindowState == WindowState.Minimized)
                _krea2Window.WindowState = WindowState.Normal;
            _krea2Window.Activate();
            return;
        }

        _krea2Window = new Krea2Window(_comfyImages, Krea2AspectRatios)
        {
            Owner = this
        };
        _krea2Window.Closed += (_, _) => _krea2Window = null;
        _krea2Window.Show();
    }

    private async void EditImage_Click(object sender, RoutedEventArgs e)
    {
        var prompt = MessageInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            AddSystemMessage("Type an edit instruction first.");
            return;
        }

        MessageInput.Clear();
        ResumeListeningAfterTextInput();
        await RunQwenImageEditAsync($"Edit image: {prompt}", prompt);
    }

    private void AttachVideoAudio_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach video speech audio",
            Filter = "Audio files (*.wav;*.mp3;*.m4a;*.flac;*.ogg)|*.wav;*.mp3;*.m4a;*.flac;*.ogg|All files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        _pendingVideoAudioPath = dialog.FileName;
        AddSystemMessage($"Video audio ready: {Path.GetFileName(_pendingVideoAudioPath)}");
        UpdateAudioButtonLabel();
        MessageInput.Focus();
        MessageInput.CaretIndex = MessageInput.Text.Length;
    }

    private async void CreateVideo_Click(object sender, RoutedEventArgs e)
    {
        var prompt = MessageInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            AddSystemMessage("Type a video prompt first.");
            return;
        }

        MessageInput.Clear();
        ResumeListeningAfterTextInput();
        await RunLtxVideoAsync($"Create video: {prompt}", prompt, TryParseVideoSeconds(prompt));
    }

    private async void AttachDocument_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach document",
            Filter = "Documents (*.pdf;*.docx;*.txt;*.md;*.csv;*.json;*.xml;*.log)|*.pdf;*.docx;*.txt;*.md;*.csv;*.json;*.xml;*.log|All files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        SetUIState("processing", "Reading document...");
        try
        {
            var added = 0;
            foreach (var fileName in dialog.FileNames)
            {
                var result = await _documentText.ExtractAsync(fileName);
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    _pendingDocuments.Add(new PendingDocumentAttachment(fileName, result));
                    added++;
                }
                else
                {
                    AddSystemMessage($"Could not read {System.IO.Path.GetFileName(fileName)}: {result.Error}");
                }
            }

            if (added > 0)
            {
                AddSystemMessage($"{added} document(s) ready for your next message.");
                UpdateDocumentButtonLabel();
                MessageInput.Focus();
                MessageInput.CaretIndex = MessageInput.Text.Length;
            }
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Document attach failed: {ex.Message}");
        }
        finally
        {
            SetUIState("idle", "Ready");
        }
    }

    private void StartAutoListen()
    {
        if (!IsVoiceInputAllowedByFacePolicy())
        {
            _autoListening = false;
            AlwaysListenToggle.IsChecked = false;
            AddSystemMessage("Auto-listen is blocked by local face policy.");
            return;
        }

        _autoListening = true;
        _pausedListeningForTextInput = false;
        SetUIState("idle", "Auto-listen active");
        _speech.StartListening();
    }

    private void StopAutoListen()
    {
        _autoListening = false;
        _pausedListeningForTextInput = false;
        _speech.StopListening();
        SetUIState("idle", "Ready");
    }

    private void PauseListeningForTextInput()
    {
        if (!_autoListening || _pausedListeningForTextInput)
            return;

        _pausedListeningForTextInput = true;
        _speech.StopListening();
        SetUIState("idle", "Typing - mic paused");
    }

    private void ResumeListeningAfterTextInput()
    {
        if (!_autoListening || !_pausedListeningForTextInput)
            return;

        _pausedListeningForTextInput = false;
        if (IsVoiceInputAllowedByFacePolicy())
        {
            SetUIState("idle", "Auto-listen active");
            _speech.StartListening();
        }
    }

    private void AutoDetectToggle_Click(object sender, RoutedEventArgs e)
    {
        var isAuto = AutoDetectToggle.IsChecked == true;
        AutoDetectToggle.Content = isAuto ? "ON" : "OFF";
        WakeWordBox.IsEnabled = !isAuto;
        _speech.AutoDetect = isAuto;
    }

    private void TtsToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = TtsToggle.IsChecked == true;
        TtsToggle.Content = enabled ? "Speech ON" : "Speech OFF";
        _speech.TtsEnabled = enabled;
    }

    private void StreamToggle_Click(object sender, RoutedEventArgs e)
    {
        // No extra logic needed, checked at send time
    }

    private static bool ShouldTriggerWebSearch(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim();
        return normalized.Contains("search online") ||
               normalized.Contains("check the internet") ||
               normalized.Contains("search the internet") ||
               normalized.Contains("check online") ||
               normalized.Contains("look online") ||
               normalized.Contains("web search");
    }

    private static string RemoveWebSearchTriggerPhrases(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var cleaned = Regex.Replace(
            text,
            @"\b(search online|check the internet|search the internet|check online|look online|web search)\b[:,;\s-]*",
            "",
            RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? text : cleaned;
    }

    private void WebSearchToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = WebSearchToggle.IsChecked == true;
        WebSearchToggle.Content = enabled ? "Web Search ON" : "Web Search OFF";
        _settings.WebSearchEnabled = enabled;
    }

    private async void PhoneRemoteToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = PhoneRemoteToggle.IsChecked == true;
        PhoneRemoteToggle.Content = enabled ? "Phone Remote ON" : "Phone Remote OFF";
        SaveSettings();

        if (enabled)
            await StartPhoneRemoteIfEnabledAsync();
        else
            await StopPhoneRemoteAsync();
    }

    private async void PhoneRemoteStartStop_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (_phoneRemoteServer.IsRunning)
        {
            PhoneRemoteToggle.IsChecked = false;
            _settings.PhoneRemote.Enabled = false;
            SaveSettings();
            await StopPhoneRemoteAsync();
            return;
        }

        PhoneRemoteToggle.IsChecked = true;
        _settings.PhoneRemote.Enabled = true;
        SaveSettings();
        await StartPhoneRemoteIfEnabledAsync();
    }

    private void PhoneRemoteAudioToggle_Click(object sender, RoutedEventArgs e)
    {
        PhoneRemoteAudioToggle.Content = PhoneRemoteAudioToggle.IsChecked == true ? "Phone Audio ON" : "Phone Audio OFF";
        SaveSettings();
    }

    private void PhoneRemoteCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = _phoneRemoteServer.IsRunning ? _phoneRemoteServer.Url : $"https://{PhoneRemoteServerPreviewIp()}:{_settings.PhoneRemote.Port}/";
        Clipboard.SetText(url);
        AddSystemMessage($"Phone remote URL copied: {url}");
    }

    private void PhoneRemoteCert_Click(object sender, RoutedEventArgs e)
    {
        var certPath = _phoneRemoteServer.CertificateExportPath;
        if (string.IsNullOrWhiteSpace(certPath))
        {
            try
            {
                var ip = PhoneRemoteServerPreviewIp();
                var cert = PhoneRemoteCertificateManager.EnsureCertificate(ip);
                certPath = cert.CerPath;
                cert.Certificate.Dispose();
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Could not create local certificate: {ex.Message}");
                return;
            }
        }

        var folder = Path.GetDirectoryName(certPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            AddSystemMessage("Certificate folder was not found.");
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        AddSystemMessage($"Install this certificate on the iPhone and fully trust it: {certPath}");
    }

    private async Task StartPhoneRemoteIfEnabledAsync()
    {
        if (_settings.PhoneRemote.Enabled != true)
        {
            UpdatePhoneRemoteUi();
            return;
        }

        try
        {
            PhoneRemoteStatusText.Text = "Starting...";
            PhoneRemoteStartStopBtn.IsEnabled = false;
            PhoneRemoteToggle.IsEnabled = false;
            var settings = new PhoneRemoteSettings
            {
                Enabled = _settings.PhoneRemote.Enabled,
                Port = _settings.PhoneRemote.Port,
                Pin = _settings.PhoneRemote.Pin,
                PlayAudioOnPhone = _settings.PhoneRemote.PlayAudioOnPhone
            };

            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await Task.Run(() => _phoneRemoteServer.StartAsync(settings, startupTimeout.Token))
                    .WaitAsync(TimeSpan.FromSeconds(25));
            }
            catch (TimeoutException)
            {
                try { await _phoneRemoteServer.StopAsync(); } catch { }
                throw new TimeoutException("Phone remote startup timed out. Check whether the HTTPS port is already in use or Windows is blocking the listener.");
            }
            catch (OperationCanceledException)
            {
                try { await _phoneRemoteServer.StopAsync(); } catch { }
                throw new TimeoutException("Phone remote startup timed out. Check whether the HTTPS port is already in use or Windows is blocking the listener.");
            }
            UpdatePhoneRemoteUi();
            AddSystemMessage($"Phone remote started: {_phoneRemoteServer.Url}");
            if (!string.IsNullOrWhiteSpace(_phoneRemoteServer.CertificateExportPath))
                AddSystemMessage($"iPhone certificate: {_phoneRemoteServer.CertificateExportPath}");
        }
        catch (Exception ex)
        {
            PhoneRemoteToggle.IsChecked = false;
            _settings.PhoneRemote.Enabled = false;
            SaveSettings();
            UpdatePhoneRemoteUi();
            AddSystemMessage($"Phone remote failed to start: {ex.Message}");
        }
        finally
        {
            PhoneRemoteStartStopBtn.IsEnabled = true;
            PhoneRemoteToggle.IsEnabled = true;
        }
    }

    private async Task StopPhoneRemoteAsync()
    {
        try
        {
            PhoneRemoteStatusText.Text = "Stopping...";
            PhoneRemoteStartStopBtn.IsEnabled = false;
            PhoneRemoteToggle.IsEnabled = false;
            await Task.Run(() => _phoneRemoteServer.StopAsync());
            UpdatePhoneRemoteUi();
            AddSystemMessage("Phone remote stopped.");
        }
        finally
        {
            PhoneRemoteStartStopBtn.IsEnabled = true;
            PhoneRemoteToggle.IsEnabled = true;
        }
    }

    private void UpdatePhoneRemoteUi()
    {
        if (PhoneRemoteStatusText == null)
            return;

        if (_phoneRemoteServer.IsRunning)
        {
            PhoneRemoteStatusText.Text = $"Running: {_phoneRemoteServer.Url}";
            PhoneRemoteStartStopBtn.Content = "Stop Phone Remote";
            PhoneRemoteCopyUrlBtn.IsEnabled = true;
        }
        else
        {
            PhoneRemoteStatusText.Text = "Stopped";
            PhoneRemoteStartStopBtn.Content = "Start Phone Remote";
            PhoneRemoteCopyUrlBtn.IsEnabled = true;
        }
    }

    private static string PhoneRemoteServerPreviewIp()
    {
        try
        {
            foreach (var address in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !address.ToString().StartsWith("127.", StringComparison.Ordinal))
                {
                    return address.ToString();
                }
            }
        }
        catch
        {
            // Fall back below.
        }

        return "127.0.0.1";
    }

    private async void FaceFeaturesToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = FaceFeaturesToggle.IsChecked == true;
        FaceFeaturesToggle.Content = enabled ? "Camera ON" : "Camera OFF";
        _settings.FaceFeatures.CameraFeaturesEnabled = enabled;
        SaveSettings();
        await InitializeFacePresenceAsync();
    }

    private void FaceGatingToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = FaceGatingToggle.IsChecked == true;
        FaceGatingToggle.Content = enabled ? "Use Identity ON" : "Use Identity OFF";
        _settings.FaceFeatures.FaceGatingEnabled = enabled;

        if (enabled)
        {
            _settings.FaceFeatures.Policy.UnknownFaceAction = FaceAccessAction.MuteMic;
            _settings.FaceFeatures.Policy.NoFaceAction = FaceAccessAction.MuteMic;
            _settings.FaceFeatures.Policy.MultipleFacesAction = FaceAccessAction.GuestPrivateMode;
            FacePolicyText.Text = "Identity controls mic and assistant mode";
        }
        else
        {
            FacePolicyText.Text = "Face gating disabled";
        }

        SaveSettings();
        _facePolicy = new FaceAccessPolicyEvaluator(_settings.FaceFeatures.Policy);
        _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, _recognizedFaceIdentity);
        ApplyFaceAccessDecision();
    }

    private void MicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplySelectedMicrophone();
        SaveSettings();
        if (MicCombo.SelectedItem is AudioDeviceInfo mic)
            AddSystemMessage($"Microphone set to: {mic.Name}");
    }

    private void RefreshMics_Click(object sender, RoutedEventArgs e)
    {
        var previous = _settings.MicDeviceIndex;
        _speech.RefreshMicrophones();
        PopulateMicrophoneCombo(previous);
        SaveSettings();
        AddSystemMessage("Microphone list refreshed. If your earbuds are connected for calls/input, choose them from the Microphone dropdown.");
    }

    private async void EnrollFace_Click(object sender, RoutedEventArgs e)
    {
        if (_latestFaceSnapshot == null || _latestFaceSnapshot.Result.State != FacePresenceState.FaceDetected)
        {
            AddSystemMessage("No detected face is ready to save. Click Scan Face Once first, then save when the preview shows your face.");
            return;
        }

        if (_latestFaceSnapshot.Result.Faces.Length != 1)
        {
            AddSystemMessage("Enrollment needs exactly one face in view.");
            return;
        }

        var selected = GetSelectedFaceProfileName();
        var identity = FaceProfileNames.ToPolicyIdentity(selected);

        EnrollFaceBtn.IsEnabled = false;
        try
        {
            var sampleCount = await _faceIdentityManager.EnrollSampleAsync(
                selected,
                _latestFaceSnapshot.Frame,
                _latestFaceSnapshot.Result.Faces[0]);

            _recognizedFaceIdentity = identity;
            _recognizedFaceName = selected;
            _lastIdentifiedFaceIdentity = identity;
            _lastIdentifiedFaceName = selected;
            _lastIdentifiedFaceUtc = DateTime.UtcNow;
            _lastRecognizedFaceUtc = DateTime.UtcNow;
            FaceIdentityText.Text = $"Saved this face as {selected}. Samples: {sampleCount}";
            AddSystemMessage($"Saved local face sample {sampleCount} for {selected}. Type a new name in the profile box to add another person.");
            await RefreshFaceProfileChoicesAsync();
            await UpdateFaceSampleInfoAsync();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Face enrollment failed: {ex.Message}");
        }
        finally
        {
            EnrollFaceBtn.IsEnabled = true;
        }
    }

    private async void FaceSnapshot_Click(object sender, RoutedEventArgs e)
    {
        FaceSnapshotBtn.IsEnabled = false;
        FaceSnapshotBtn.Content = "Scanning...";
        FaceIdentityText.Text = "Identity: scanning camera...";
        try
        {
            using var capture = await Task.Run(() =>
                OpenCvStillCameraService.CaptureFrame(_settings.FaceFeatures.CameraIndex));
            using var detector = new HaarCascadeFaceDetectionService(_settings.FaceFeatures.ModelOptions);
            var result = detector.DetectFaces(capture.Frame);
            var usedCenteredFallback = false;
            if (!capture.LooksBlank)
                result = StabilizeFaceScanResult(capture.Frame, result, out usedCenteredFallback);

            if (result.State == FacePresenceState.NoFaceDetected && !capture.LooksBlank)
            {
                result = new FaceDetectionResult
                {
                    State = FacePresenceState.FaceDetected,
                    Faces = [CreateCenteredEnrollmentFace(capture.Frame)]
                };
                usedCenteredFallback = true;
            }

            var snapshot = new FaceDetectionSnapshot(capture.Frame.Clone(), result, DateTime.UtcNow);

            var oldSnapshot = _latestFaceSnapshot;
            _latestFaceSnapshot = snapshot;
            oldSnapshot?.Dispose();

            _facePresenceState = result.State;
            UpdateFacePresenceUi(result.State);
            UpdateFacePreviewFrame(snapshot.Frame);
            FaceDebugText.Text =
                $"Last scan: {capture.FramesRead} frames | brightness {capture.MeanBrightness:F1} | contrast {capture.BrightnessStdDev:F1} | faces {result.Faces.Length}"
                + (usedCenteredFallback ? " | centered crop" : "");

            if (capture.LooksBlank)
            {
                FaceIdentityText.Text = "Identity: Unknown | camera frame is black/blank";
                AddSystemMessage("Face scan saw a blank camera frame. Check the privacy cover/lighting, then scan again.");
                return;
            }

            if (result.State == FacePresenceState.FaceDetected)
            {
                var selected = GetSelectedFaceProfileName();
                var totalSamples = await CountAllFaceSamplesAsync();
                if (totalSamples > 0)
                {
                    FaceIdentityText.Text = usedCenteredFallback
                        ? "Detector missed; recognizing with centered crop..."
                        : "Face detected; recognizing...";
                    await RecognizeLatestFaceAsync(snapshot);
                }
                else
                {
                    FaceIdentityText.Text = usedCenteredFallback
                        ? $"Detector missed you. Preview is using a centered crop. If that crop is your face, click Save Face as {selected}."
                        : $"Face detected. Click Save Face to add this sample as {selected}.";
                }
            }
            else
            {
                FaceIdentityText.Text = result.State == FacePresenceState.MultipleFacesDetected
                    ? "Identity: Unknown | multiple faces"
                    : "Identity: Unknown | no valid face";
            }
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Face scan failed: {ex.Message}");
        }
        finally
        {
            FaceSnapshotBtn.IsEnabled = true;
            FaceSnapshotBtn.Content = "Scan Face Once";
        }
    }

    private static OpenCvSharp.Rect CreateCenteredEnrollmentFace(Mat frame)
    {
        var side = (int)Math.Round(Math.Min(frame.Width, frame.Height) * 0.58);
        side = Math.Clamp(side, 80, Math.Min(frame.Width, frame.Height));
        var x = Math.Clamp((frame.Width - side) / 2, 0, Math.Max(0, frame.Width - side));
        var y = Math.Clamp((int)Math.Round(frame.Height * 0.16), 0, Math.Max(0, frame.Height - side));
        return new OpenCvSharp.Rect(x, y, side, side);
    }

    private static FaceDetectionResult StabilizeFaceScanResult(Mat frame, FaceDetectionResult result, out bool usedCenteredFallback)
    {
        usedCenteredFallback = false;
        if (result.State == FacePresenceState.NoFaceDetected || result.Faces.Length == 0)
        {
            usedCenteredFallback = true;
            return new FaceDetectionResult
            {
                State = FacePresenceState.FaceDetected,
                Faces = [CreateCenteredEnrollmentFace(frame)]
            };
        }

        var expectedCenter = new OpenCvSharp.Point2f(frame.Width / 2f, frame.Height * 0.43f);
        var face = result.Faces
            .OrderBy(rect =>
            {
                var centerX = rect.X + (rect.Width / 2f);
                var centerY = rect.Y + (rect.Height / 2f);
                var normalizedX = (centerX - expectedCenter.X) / Math.Max(1, frame.Width);
                var normalizedY = (centerY - expectedCenter.Y) / Math.Max(1, frame.Height);
                return (normalizedX * normalizedX) + (normalizedY * normalizedY);
            })
            .First();

        if (!IsReasonableFaceScanBox(face, frame.Width, frame.Height))
        {
            usedCenteredFallback = true;
            face = CreateCenteredEnrollmentFace(frame);
        }

        return new FaceDetectionResult
        {
            State = FacePresenceState.FaceDetected,
            Faces = [face]
        };
    }

    private static bool IsReasonableFaceScanBox(OpenCvSharp.Rect face, int frameWidth, int frameHeight)
    {
        var centerX = face.X + (face.Width / 2.0);
        var centerY = face.Y + (face.Height / 2.0);
        var expectedY = frameHeight * 0.43;
        var dx = Math.Abs(centerX - (frameWidth / 2.0)) / Math.Max(1, frameWidth);
        var dy = Math.Abs(centerY - expectedY) / Math.Max(1, frameHeight);

        if (dx > 0.28 || dy > 0.30)
            return false;

        if (centerY < frameHeight * 0.18 || centerY > frameHeight * 0.78)
            return false;

        return true;
    }

    private async void ClearFaces_Click(object sender, RoutedEventArgs e)
    {
        ClearFacesBtn.IsEnabled = false;
        try
        {
            await _faceIdentityManager.ClearAllProfilesAsync();
            _recognizedFaceIdentity = FaceIdentity.Unknown;
            _lastIdentifiedFaceIdentity = FaceIdentity.Unknown;
            _recognizedFaceName = "";
            _lastIdentifiedFaceName = "";
            _lastIdentifiedFaceUtc = DateTime.MinValue;
            _lastRecognizedSimilarity = 0f;
            FaceIdentityText.Text = "Identity: Unknown";
            AddSystemMessage("Cleared all local face samples.");
            await RefreshFaceProfileChoicesAsync();
            await UpdateFaceSampleInfoAsync();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not clear face samples: {ex.Message}");
        }
        finally
        {
            ClearFacesBtn.IsEnabled = true;
        }
    }

    private async void FaceProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_faceIdentityManager == null)
            return;

        await UpdateFaceSampleInfoAsync();
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        var size = (WhisperModelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "base";
        _settings.WhisperModelSize = size;
        _speech.WhisperModelPath = GetWhisperModelPath(size);
        SaveSettings();
        DownloadModelBtn.IsEnabled = false;
        DownloadModelBtn.Content = $"... Downloading {size}...";

        try
        {
            await _speech.DownloadModelAsync(size, new Progress<float>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    DownloadModelBtn.Content = $"... {p * 100:F0}%";
                });
            }));

            Dispatcher.Invoke(() =>
            {
                AddSystemMessage($"Whisper {size} model downloaded! You can now use voice recognition.");
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                AddSystemMessage($"Download failed: {ex.Message}");
            });
        }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                DownloadModelBtn.IsEnabled = true;
                DownloadModelBtn.Content = "Download Model";
            });
        }
    }

    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        _recentWebSearchContexts.Clear();
        ChatPanel.Children.Clear();
        AddSystemMessage("Chat cleared.");
        _conversationStartTime = DateTime.Now;
    }

    private void ExportChat_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = _history.Export();
            Clipboard.SetText(text);
            AddSystemMessage("Chat copied to clipboard!");
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Export failed: {ex.Message}");
        }
    }

    private void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        if (_transcriptionWindow != null)
        {
            _transcriptionWindow.ActivateExisting();
            return;
        }

        _transcriptionWindow = new TranscriptionWindow(
            _speech,
            SummarizeLiveTranscriptAsync,
            OnLiveTranscriptionContextUpdated,
            _settings.LiveTranscriberSystemPrompt,
            SaveLiveTranscriberSystemPrompt)
        {
            Owner = this
        };
        _transcriptionWindow.Closed += (_, _) =>
        {
            _transcriptionWindow = null;
            TranscribeBtn.Content = "Transcribe";
        };
        _transcriptionWindow.Show();
        TranscribeBtn.Content = "Transcribing";
        AddSystemMessage("Live transcription window opened. Main chat will use its transcript and summary as context.");
    }

    private async Task<string> SummarizeLiveTranscriptAsync(string transcript, string systemPrompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(ModelCombo.Text))
            throw new InvalidOperationException("Select a model first.");

        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "user",
                Content = "Summarize this live transcript. Include important facts, decisions, action items, questions, names, dates, and numbers. Keep it concise but useful for the main assistant to reference later.\n\nTRANSCRIPT:\n" + transcript
            }
        };

        var summary = await _ollama.ChatAsync(
            ModelCombo.Text,
            messages,
            string.IsNullOrWhiteSpace(systemPrompt)
                ? "You are a live transcript summarizer. Preserve important context and do not invent details."
                : systemPrompt,
            0.2,
            _settings.MaxTokens,
            ct);

        var cleaned = CleanDisplayText(summary);
        if (!string.IsNullOrWhiteSpace(cleaned))
            AddSystemMessage("Live transcript summary updated. Main chat has the latest transcription context.");

        return cleaned;
    }

    private void OnLiveTranscriptionContextUpdated(string transcript, string summary)
    {
        _latestLiveTranscript = transcript.Trim();
        _latestLiveTranscriptSummary = summary.Trim();
    }

    private void RestoreRecentChat()
    {
        var messages = RecentChatStore.Load(_settings.MaxContextMessages);
        if (messages.Count == 0)
            return;

        _restoringRecentChat = true;
        try
        {
            _history.ReplaceAll(messages);
            ChatPanel.Children.Clear();
            foreach (var message in _history.GetAll())
            {
                if (message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                    AddUserMessage(message.Content);
                else if (message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
                    AddAssistantMessage(message.Content);
            }
        }
        finally
        {
            _restoringRecentChat = false;
        }

        ScrollChat();
    }

    private void SaveRecentChatSnapshot()
    {
        if (_restoringRecentChat)
            return;

        var messages = _history.GetAll();
        if (messages.Count == 0)
            RecentChatStore.Clear();
        else
            RecentChatStore.Save(messages, _history.MaxMessages);
    }

    private void SaveLiveTranscriberSystemPrompt(string prompt)
    {
        _settings.LiveTranscriberSystemPrompt = prompt.Trim();
        SettingsManager.Save(_settings);
    }

    private async void SummarizeConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_history.GetAll().Count == 0)
        {
            AddSystemMessage("No conversation to summarize.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ModelCombo.Text))
        {
            AddSystemMessage("Select a model first!");
            return;
        }

        SummarizeBtn.IsEnabled = false;
        SummarizeBtn.Content = "... Summarizing...";
        AddSystemMessage("Summarizing conversation and saving memory...");

        try
        {
            var exportText = _history.Export();
            var lengthHint = exportText.Length > 3000
                ? "This was a long conversation - write a very detailed summary, at least 3-4 paragraphs covering all major topics, decisions, and details."
                : "Write a detailed summary of this conversation, at least one full paragraph with specific details and topics discussed.";

            var summarizePrompt = $"Summarize the following conversation in detail. {lengthHint} Focus on what was discussed, any decisions made, problems solved, and key information exchanged. Write it as a narrative a person would read to recall what happened.\n\nCONVERSATION:\n{exportText}";

            var memMessages = new List<ChatMessage>
            {
                new ChatMessage { Role = "user", Content = summarizePrompt }
            };

            var summary = await _ollama.ChatAsync(
                ModelCombo.Text,
                memMessages,
                "You are a conversation summarizer. Write detailed, informative summaries that capture all important details. Do not use markdown, emojis, or special formatting. Write in plain natural language.",
                0.3,
                _settings.MaxTokens,
                CancellationToken.None
            );

            if (!string.IsNullOrWhiteSpace(summary))
            {
                var memory = new ConversationMemory
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Summary = CleanDisplayText(summary),
                    Model = ModelCombo.Text,
                    MessageCount = _history.GetAll().Count,
                    DurationMinutes = (DateTime.Now - _conversationStartTime).TotalMinutes
                };

                MemoryManager.Save(memory);
                _loadedMemories = MemoryManager.LoadAll();
                AddSystemMessage($"Memory saved! ({_loadedMemories.Count} memories stored)");
            }
            else
            {
                AddSystemMessage("Summarization returned empty result. Memory not saved.");
            }
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Summarization failed: {ex.Message}");
        }
        finally
        {
            SummarizeBtn.IsEnabled = true;
            SummarizeBtn.Content = "Save Memory";
        }
    }

    private void ViewMemory_Click(object sender, RoutedEventArgs e)
    {
        RefreshMemoryPanel();
        MemoryPanel.Visibility = MemoryPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void CloseMemoryPanel_Click(object sender, RoutedEventArgs e)
    {
        MemoryPanel.Visibility = Visibility.Collapsed;
    }

    private void ClearMemory_Click(object sender, RoutedEventArgs e)
    {
        MemoryManager.ClearAll();
        _loadedMemories.Clear();
        RefreshMemoryPanel();
        AddSystemMessage("All conversation memories cleared.");
    }

    private void AddManualMemory_Click(object sender, RoutedEventArgs e)
    {
        ShowMemoryEditor(null);
    }

    private void RefreshMemoryPanel()
    {
        MemoryListPanel.Children.Clear();
        var memories = MemoryManager.LoadAll();
        // Show newest first in the viewer
        foreach (var mem in Enumerable.Reverse(memories))
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(245, 245, 250)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var stack = new StackPanel();

            var header = new TextBlock
            {
                Text = mem.Timestamp,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(100, 92, 231)) // #6C5CE7
            };
            stack.Children.Add(header);

            var detail = new TextBlock
            {
                Text = $"Messages: {mem.MessageCount} | Duration: {mem.DurationMinutes:F0} min | Model: {mem.Model}",
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136))
            };
            stack.Children.Add(detail);

            var summary = new TextBlock
            {
                Text = mem.Summary.Length > 200 ? mem.Summary[..200] + "..." : mem.Summary,
                FontSize = 10,
                Foreground = Brushes.Black,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            };
            stack.Children.Add(summary);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var editBtn = new Button
            {
                Content = "Edit",
                FontSize = 9,
                Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(0, 0, 4, 0),
                Background = new SolidColorBrush(Color.FromRgb(108, 92, 231)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Tag = mem
            };
            editBtn.Click += EditSingleMemory_Click;
            actions.Children.Add(editBtn);

            var delBtn = new Button
            {
                Content = "Delete",
                FontSize = 9,
                Padding = new Thickness(4, 1, 4, 1),
                Background = new SolidColorBrush(Color.FromRgb(225, 112, 85)), // #E17055
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                Tag = mem.Timestamp
            };
            delBtn.Click += DeleteSingleMemory_Click;
            actions.Children.Add(delBtn);
            stack.Children.Add(actions);

            border.Child = stack;
            MemoryListPanel.Children.Add(border);
        }

        if (memories.Count == 0)
        {
            MemoryListPanel.Children.Add(new TextBlock
            {
                Text = "No memories saved yet. Click 'Save Memory' during or after a conversation.",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromRgb(136, 136, 136)),
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private void EditSingleMemory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ConversationMemory mem })
            ShowMemoryEditor(mem);
    }

    private void ShowMemoryEditor(ConversationMemory? mem)
    {
        var isNew = mem == null;
        MemoryListPanel.Children.Clear();

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = isNew ? "Adding manual memory" : $"Editing memory from {mem!.Timestamp}",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 92, 231)),
            Margin = new Thickness(0, 0, 0, 4)
        });

        var editor = new TextBox
        {
            Text = mem?.Summary ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MinHeight = 120,
            MaxHeight = 260,
            FontSize = 11,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(204, 204, 204)),
            Margin = new Thickness(0, 0, 0, 6)
        };
        panel.Children.Add(editor);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var saveBtn = new Button
        {
            Content = "Save",
            FontSize = 10,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 4, 0),
            Background = new SolidColorBrush(Color.FromRgb(0, 184, 148)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        saveBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(editor.Text))
            {
                AddSystemMessage("Memory summary cannot be empty.");
                return;
            }

            if (isNew)
            {
                MemoryManager.Save(new ConversationMemory
                {
                    Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                    Summary = editor.Text.Trim(),
                    Model = "Manual",
                    MessageCount = 0,
                    DurationMinutes = 0
                });
                _loadedMemories = MemoryManager.LoadAll();
                RefreshMemoryPanel();
                AddSystemMessage("Manual memory added.");
                return;
            }

            if (mem != null && MemoryManager.UpdateSummary(mem.Timestamp, editor.Text))
            {
                _loadedMemories = MemoryManager.LoadAll();
                RefreshMemoryPanel();
                AddSystemMessage($"Memory from {mem.Timestamp} updated.");
            }
            else
            {
                AddSystemMessage(mem == null
                    ? "Could not add manual memory."
                    : $"Could not update memory from {mem.Timestamp}.");
            }
        };
        actions.Children.Add(saveBtn);

        var cancelBtn = new Button
        {
            Content = "Cancel",
            FontSize = 10,
            Padding = new Thickness(8, 2, 8, 2),
            Background = new SolidColorBrush(Color.FromRgb(99, 110, 114)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0)
        };
        cancelBtn.Click += (_, _) => RefreshMemoryPanel();
        actions.Children.Add(cancelBtn);
        panel.Children.Add(actions);

        MemoryListPanel.Children.Add(panel);
    }

    private void DeleteSingleMemory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string timestamp)
        {
            MemoryManager.Delete(timestamp);
            _loadedMemories = MemoryManager.LoadAll();
            RefreshMemoryPanel();
            AddSystemMessage($"Memory from {timestamp} deleted.");
        }
    }

    private void SendText_Click(object sender, RoutedEventArgs e)
    {
        var text = MessageInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        MessageInput.Clear();
        ResumeListeningAfterTextInput();
        SendMessage(text);
    }

    private async Task<PhoneRemoteAssistantResult> HandlePhoneQwenImageCreateAsync(string userText, string prompt, CancellationToken ct)
    {
        AssistantMessageUi? assistantMessage = null;
        await Dispatcher.InvokeAsync(() =>
        {
            SaveImageSettingsFromUi();
            AddUserMessage($"[iPhone] {userText}");
            _history.Add("user", userText);
            assistantMessage = AddAssistantMessage("Creating image with Qwen Image on ComfyUI...");
            SetUIState("processing", "Phone image...");
        });

        try
        {
            var result = await _comfyImages.CreateQwenImageAsync(
                prompt,
                _settings.ImageWidth,
                _settings.ImageHeight,
                _settings.QwenCreateSteps,
                ct);

            await Dispatcher.InvokeAsync(() =>
            {
                _latestGeneratedImagePath = result.LocalPath;
                if (assistantMessage is not null)
                {
                    assistantMessage.Body.Text = BuildGeneratedImageMessage(result);
                    AddGeneratedImageToAssistantMessage(assistantMessage, result.LocalPath);
                }
                _history.Add("assistant", BuildGeneratedImageHistoryText(result));
                SetUIState("idle", "Ready");
            });

            return new PhoneRemoteAssistantResult(BuildGeneratedImageMessage(result), null, result.LocalPath);
        }
        catch (Exception ex)
        {
            var error = $"Phone image creation failed: {ex.Message}";
            await Dispatcher.InvokeAsync(() =>
            {
                if (assistantMessage is not null)
                    assistantMessage.Body.Text = error;
                AddSystemMessage(error);
                SetUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult(error, null);
        }
    }

    private async Task<PhoneRemoteKrea2Options> HandlePhoneKrea2OptionsAsync(CancellationToken ct)
    {
        SaveImageSettingsFromUi();
        var loras = await _comfyImages.ListKrea2LorasAsync(ct);
        return new PhoneRemoteKrea2Options(loras.ToList(), Krea2AspectRatios.ToList());
    }

    private async Task<PhoneRemoteAssistantResult> HandlePhoneKrea2CreateAsync(PhoneRemoteKrea2Request request, CancellationToken ct)
    {
        AssistantMessageUi? assistantMessage = null;
        var prompt = request.Prompt.Trim();
        await Dispatcher.InvokeAsync(() =>
        {
            SaveImageSettingsFromUi();
            AddUserMessage($"[iPhone Krea2] {prompt}");
            _history.Add("user", $"Krea2 image request: {prompt}");
            assistantMessage = AddAssistantMessage("Creating image with Krea2 Turbo on ComfyUI...");
            SetUIState("processing", "Phone Krea2...");
        });

        try
        {
            var result = await _comfyImages.CreateKrea2ImageAsync(
                prompt,
                request.EnableLora,
                request.LoraName.Trim(),
                request.AspectRatio.Trim(),
                ct);

            await Dispatcher.InvokeAsync(() =>
            {
                _latestGeneratedImagePath = result.LocalPath;
                if (assistantMessage is not null)
                {
                    assistantMessage.Body.Text = BuildGeneratedImageMessage(result);
                    AddGeneratedImageToAssistantMessage(assistantMessage, result.LocalPath);
                }
                _history.Add("assistant", BuildGeneratedImageHistoryText(result));
                SetUIState("idle", "Ready");
            });

            return new PhoneRemoteAssistantResult(BuildGeneratedImageMessage(result), null, result.LocalPath);
        }
        catch (Exception ex)
        {
            var error = $"Krea2 image creation failed: {ex.Message}";
            await Dispatcher.InvokeAsync(() =>
            {
                if (assistantMessage is not null)
                    assistantMessage.Body.Text = error;
                AddSystemMessage(error);
                SetUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult(error, null);
        }
    }

    private async Task<PhoneRemoteAssistantResult> HandlePhoneQwenImageEditAsync(PhoneRemoteUserInput input, string userText, string prompt, CancellationToken ct)
    {
        var sourcePaths = input.ImagePaths
            .Where(File.Exists)
            .Take(2)
            .ToList();
        if (sourcePaths.Count == 0 && File.Exists(_latestGeneratedImagePath))
            sourcePaths.Add(_latestGeneratedImagePath);

        if (sourcePaths.Count == 0)
            return new PhoneRemoteAssistantResult("Attach an image first, or create an image before using Edit.", null);

        var isTwoImageEdit = sourcePaths.Count >= 2;
        AssistantMessageUi? assistantMessage = null;
        await Dispatcher.InvokeAsync(() =>
        {
            SaveImageSettingsFromUi();
            AddUserMessage($"[iPhone] {userText}", sourcePaths);
            var sourceNames = string.Join(", ", sourcePaths.Select(Path.GetFileName));
            _history.Add("user", $"{userText}\n\nImage edit source(s): {sourceNames}");
            assistantMessage = AddAssistantMessage(isTwoImageEdit
                ? "Mixing images with Qwen Image Edit two-image workflow on ComfyUI..."
                : "Editing image with Qwen Image Edit on ComfyUI...");
            SetUIState("processing", "Phone edit...");
        });

        try
        {
            var result = isTwoImageEdit
                ? await _comfyImages.EditQwenImagesAsync(
                    sourcePaths,
                    prompt,
                    _settings.QwenEditSteps,
                    ct)
                : await _comfyImages.EditQwenImageAsync(
                    sourcePaths[0],
                    prompt,
                    _settings.QwenEditSteps,
                    ct);

            await Dispatcher.InvokeAsync(() =>
            {
                _latestGeneratedImagePath = result.LocalPath;
                if (assistantMessage is not null)
                {
                    assistantMessage.Body.Text = BuildGeneratedImageMessage(result);
                    AddGeneratedImageToAssistantMessage(assistantMessage, result.LocalPath);
                }
                _history.Add("assistant", BuildGeneratedImageHistoryText(result));
                SetUIState("idle", "Ready");
            });

            return new PhoneRemoteAssistantResult(BuildGeneratedImageMessage(result), null, result.LocalPath);
        }
        catch (Exception ex)
        {
            var error = $"Phone image edit failed: {ex.Message}";
            await Dispatcher.InvokeAsync(() =>
            {
                if (assistantMessage is not null)
                    assistantMessage.Body.Text = error;
                AddSystemMessage(error);
                SetUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult(error, null);
        }
    }

    private async Task<PhoneRemoteAssistantResult> HandlePhoneLtxVideoAsync(PhoneRemoteUserInput input, string userText, string prompt, int? requestedSeconds, CancellationToken ct)
    {
        var sourcePath = input.ImagePaths.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(sourcePath) && File.Exists(_latestGeneratedImagePath))
            sourcePath = _latestGeneratedImagePath;

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new PhoneRemoteAssistantResult("Attach an image first, or create an image before using Make Video.", null);

        var audioPath = input.AudioPaths.FirstOrDefault(File.Exists);
        AssistantMessageUi? assistantMessage = null;
        await Dispatcher.InvokeAsync(() =>
        {
            SaveImageSettingsFromUi();
            AddUserMessage($"[iPhone] {userText}", new[] { sourcePath });
            _history.Add("user", $"{userText}\n\nVideo source image: {Path.GetFileName(sourcePath)}" +
                (string.IsNullOrWhiteSpace(audioPath) ? "" : $"\nVideo speech audio: {Path.GetFileName(audioPath)}"));
            assistantMessage = AddAssistantMessage(string.IsNullOrWhiteSpace(audioPath)
                ? "Creating video with video_ltx2_3_i2v on ComfyUI..."
                : "Creating video with video_ltx2_3_ia2v on ComfyUI...");
            SetUIState("processing", "Phone video...");
        });

        try
        {
            var result = await _comfyImages.CreateLtxVideoAsync(
                sourcePath,
                audioPath,
                prompt,
                requestedSeconds ?? _settings.VideoSeconds,
                _settings.VideoFps,
                CancellationToken.None);
            var syncedVideoPath = await CopyVideoToSyncedDirectoryAsync(result.LocalPath, CancellationToken.None);

            await Dispatcher.InvokeAsync(() =>
            {
                _latestGeneratedVideoPath = result.LocalPath;
                if (assistantMessage is not null)
                {
                    assistantMessage.Body.Text = BuildGeneratedVideoMessage(result, syncedVideoPath);
                    AddGeneratedVideoToAssistantMessage(assistantMessage, result.LocalPath);
                }
                _history.Add("assistant", BuildGeneratedVideoHistoryText(result, syncedVideoPath));
                SetUIState("idle", "Ready");
            });

            return new PhoneRemoteAssistantResult(BuildGeneratedVideoMessage(result, syncedVideoPath), null, null, result.LocalPath);
        }
        catch (Exception ex)
        {
            var error = $"Phone video creation failed: {ex.Message}";
            await Dispatcher.InvokeAsync(() =>
            {
                if (assistantMessage is not null)
                    assistantMessage.Body.Text = error;
                AddSystemMessage(error);
                SetUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult(error, null);
        }
    }

    private static string GetPhoneCommandText(string text)
    {
        var marker = text.IndexOf("\n\nAttached", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? text[..marker].Trim() : text.Trim();
    }

    private async Task<PhoneRemoteAssistantResult> HandlePhoneHermesAsync(string userText, string hermesPrompt, CancellationToken ct)
    {
        AssistantMessageUi? assistantMessage = null;
        bool makePhoneAudio = false;
        await Dispatcher.InvokeAsync(() =>
        {
            AddUserMessage($"[iPhone] {userText}", new List<string>());
            _history.Add("user", userText);
            assistantMessage = AddAssistantMessage("");
            makePhoneAudio = _settings.PhoneRemote.PlayAudioOnPhone && TtsToggle.IsChecked == true;
            SetUIState("processing", "Phone Hermes...");
            AddSystemMessage("Phone remote running Hermes CLI over SSH.");
        });

        try
        {
            if (IsHermesCancel(hermesPrompt) && !string.IsNullOrWhiteSpace(_pendingHermesControlCommand))
            {
                var cancelled = $"Cancelled pending Hermes action: {_pendingHermesControlCommand}";
                _pendingHermesControlCommand = "";
                _pendingHermesSshCommand = "";
                await Dispatcher.InvokeAsync(() =>
                {
                    if (assistantMessage != null)
                        assistantMessage.Body.Text = cancelled;
                    _history.Add("assistant", cancelled);
                    SetUIState("idle", "Ready");
                });
                return new PhoneRemoteAssistantResult(cancelled, null);
            }

            if (IsHermesApproval(hermesPrompt) && !string.IsNullOrWhiteSpace(_pendingHermesSshCommand))
            {
                var command = _pendingHermesSshCommand;
                _pendingHermesControlCommand = "";
                _pendingHermesSshCommand = "";
                await Dispatcher.InvokeAsync(() => AddSystemMessage("Phone Hermes approval received. Running pending SSH command."));
                using var sshTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sshTimeout.CancelAfter(TimeSpan.FromSeconds(120));
                var sshResult = await _hermesSsh.RunAsync(command, TimeSpan.FromSeconds(90), sshTimeout.Token);
                var sshDisplay = sshResult.ToDisplayText();
                string? sshAudioPath = null;
                if (makePhoneAudio && !string.IsNullOrWhiteSpace(sshDisplay))
                {
                    var speechText = CleanSpeechText(sshDisplay);
                    sshAudioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (assistantMessage != null)
                    {
                        assistantMessage.Body.Text = sshDisplay;
                        if (!string.IsNullOrWhiteSpace(sshAudioPath))
                            AddAudioButtons(assistantMessage, sshAudioPath);
                    }

                    _history.Add("assistant", sshDisplay);
                    SetUIState("idle", "Ready");
                });
                await RunOnUiAsync(RefreshLlamaCppModelAfterHermesAsync);
                return new PhoneRemoteAssistantResult(sshDisplay, sshAudioPath);
            }
            else if (TryBuildLlamaModelControlPlan(hermesPrompt, out var modelPlan))
            {
                await Dispatcher.InvokeAsync(() => AddSystemMessage($"Phone remote running direct llama.cpp model control over SSH: {modelPlan.Description}"));
                var display = await RunLlamaModelControlPlanAsync(modelPlan, ct);
                if (modelPlan.EndpointPort is int endpointPort)
                {
                    string endpointUrl = "";
                    await Dispatcher.InvokeAsync(() =>
                    {
                        endpointUrl = SetLlamaEndpointPort(endpointPort);
                    });
                    display += $"{Environment.NewLine}{Environment.NewLine}App endpoint set to {endpointUrl}";
                    var readiness = await WaitForLlamaEndpointReadyAsync(endpointUrl, TimeSpan.FromSeconds(90), ct);
                    display += $"{Environment.NewLine}{readiness.Message}";
                    if (readiness.Ready && _settings.ChatProvider.Equals("OpenAI-compatible", StringComparison.OrdinalIgnoreCase))
                    {
                        await RunOnUiAsync(async () =>
                        {
                            AddSystemMessage("Refreshing model list from the new llama.cpp endpoint.");
                            await RefreshModelsInternal();
                        });
                    }
                }
                string? modelAudioPath = null;
                if (makePhoneAudio && !string.IsNullOrWhiteSpace(display))
                {
                    var speechText = CleanSpeechText(display);
                    modelAudioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (assistantMessage != null)
                    {
                        assistantMessage.Body.Text = display;
                        if (!string.IsNullOrWhiteSpace(modelAudioPath))
                            AddAudioButtons(assistantMessage, modelAudioPath);
                    }

                    _history.Add("assistant", display);
                    SetUIState("idle", "Ready");
                });
                return new PhoneRemoteAssistantResult(display, modelAudioPath);
            }
            else if (TryGetDirectHermesSshCommand(hermesPrompt, out var directCommand))
            {
                _pendingHermesControlCommand = hermesPrompt;
                _pendingHermesSshCommand = directCommand;
                var staged = await BuildHermesSshApprovalPromptAsync(hermesPrompt, directCommand, ct);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (assistantMessage != null)
                        assistantMessage.Body.Text = staged;
                    _history.Add("assistant", staged);
                    SetUIState("idle", "Ready");
                });
                return new PhoneRemoteAssistantResult(staged, null);
            }

            await Dispatcher.InvokeAsync(() => AddSystemMessage("Phone remote running Hermes CLI over SSH."));
            var isModelControl = IsHermesModelControlCommand(hermesPrompt);
            var hermesPromptForCli = BuildHermesCliPrompt(hermesPrompt);
            var hermesTimeout = isModelControl ? TimeSpan.FromSeconds(90) : TimeSpan.FromMinutes(3);
            var hermesMaxTurns = isModelControl ? 12 : 12;
            using var hermesCliTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hermesCliTimeout.CancelAfter(hermesTimeout + TimeSpan.FromSeconds(10));
            var cliResult = await _hermesSsh.RunHermesCliAsync(hermesPromptForCli, hermesTimeout, hermesMaxTurns, hermesCliTimeout.Token);
            var cleaned = CleanDisplayText(cliResult.Stdout);
            if (cliResult.TimedOut)
                cleaned = BuildHermesTimeoutMessage(hermesPrompt);
            if (string.IsNullOrWhiteSpace(cleaned))
                cleaned = cliResult.ToDisplayText();
            string? audioPath = null;
            if (makePhoneAudio && !string.IsNullOrWhiteSpace(cleaned))
            {
                var speechText = CleanSpeechText(cleaned);
                audioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (assistantMessage != null)
                {
                    assistantMessage.Body.Text = cleaned;
                    if (!string.IsNullOrWhiteSpace(audioPath))
                        AddAudioButtons(assistantMessage, audioPath);
                }

                _history.Add("assistant", $"Hermes result:\n{cleaned}");
                SetUIState("idle", "Ready");
            });

            if (isModelControl || IsHermesApproval(hermesPrompt))
                await RunOnUiAsync(RefreshLlamaCppModelAfterHermesAsync);
            return new PhoneRemoteAssistantResult(cleaned, audioPath);
        }
        catch (OperationCanceledException)
        {
            var error = BuildHermesTimeoutMessage(hermesPrompt);
            await Dispatcher.InvokeAsync(() =>
            {
                if (assistantMessage != null)
                    assistantMessage.Body.Text = error;
                AddSystemMessage(error);
                SetUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult(error, null);
        }
        catch (Exception ex)
        {
            var error = $"Hermes error: {ex.Message}";
            await Dispatcher.InvokeAsync(() =>
            {
                if (assistantMessage != null)
                    assistantMessage.Body.Text = error;
                AddSystemMessage(error);
                SetUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult(error, null);
        }
    }

    private async Task<PhoneRemoteAssistantResult> HandlePhoneRemoteChatAsync(PhoneRemoteUserInput input, CancellationToken ct)
    {
        await _phoneRemoteChatLock.WaitAsync(ct);
        try
        {
            var userText = input.Text.Trim();
            if (string.IsNullOrWhiteSpace(userText) && input.ImagesBase64.Count > 0)
                userText = "Please analyze the attached image.";
            if (string.IsNullOrWhiteSpace(userText) && input.AudioPaths.Count > 0 && input.Documents.Count > 0)
                userText = "Please transcribe and summarize the attached meeting audio. Include key points, decisions, action items, questions, names, dates, and numbers. I may ask follow-up questions about it.";
            if (string.IsNullOrWhiteSpace(userText) && input.Documents.Count > 0)
                userText = "Please answer using the attached document.";

            if (TryCreateHermesPrompt(userText, out var hermesPrompt))
                return await HandlePhoneHermesAsync(userText, hermesPrompt, ct);

            if (PiAgentService.TryCreateReadOnlyPrompt(userText, out var piPrompt, out var piBlockedReason))
            {
                AssistantMessageUi? piAssistantMessage = null;
                bool makePiPhoneAudio = false;
                await Dispatcher.InvokeAsync(() =>
                {
                    AddUserMessage($"[iPhone] {userText}", new List<string>());
                    _history.Add("user", userText);
                    piAssistantMessage = AddAssistantMessage("");
                    makePiPhoneAudio = _settings.PhoneRemote.PlayAudioOnPhone && TtsToggle.IsChecked == true;
                    SetUIState("thinking", "Phone Pi...");
                });

                var piAnswer = piBlockedReason;
                if (string.IsNullOrWhiteSpace(piAnswer))
                {
                    try
                    {
                        piAnswer = CleanDisplayText(await _piAgent.AskAsync(piPrompt, ct));
                    }
                    catch (Exception ex)
                    {
                        piAnswer = $"Pi failed: {ex.Message}";
                    }
                }

                string? piAudioPath = null;
                if (makePiPhoneAudio && !string.IsNullOrWhiteSpace(piAnswer))
                {
                    var speechText = CleanSpeechText(piAnswer);
                    piAudioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (piAssistantMessage != null)
                    {
                        piAssistantMessage.Body.Text = piAnswer;
                        if (!string.IsNullOrWhiteSpace(piAudioPath))
                            AddAudioButtons(piAssistantMessage, piAudioPath);
                    }
                    _history.Add("assistant", $"Pi result:\n{piAnswer}");
                    SetUIState("idle", "Ready");
                });

                return new PhoneRemoteAssistantResult(piAnswer, piAudioPath);
            }

            var phoneCommandText = GetPhoneCommandText(userText);
            if (TryGetVideoPrompt(phoneCommandText, out var phoneVideoPrompt, out var phoneVideoSeconds))
                return await HandlePhoneLtxVideoAsync(input, userText, phoneVideoPrompt, phoneVideoSeconds, ct);

            if (TryGetImageEditPrompt(phoneCommandText, out var phoneEditPrompt))
                return await HandlePhoneQwenImageEditAsync(input, userText, phoneEditPrompt, ct);

            if (TryGetImageCreatePrompt(phoneCommandText, out var phoneCreatePrompt))
                return await HandlePhoneQwenImageCreateAsync(userText, phoneCreatePrompt, ct);

            var modelUserText = IsManualContinuationRequest(userText)
                ? "Continue the previous assistant response from where it left off. Do not restart, do not summarize, and do not ask what to continue. If the previous response was code, SVG, markup, a list, or a long answer, continue that same content directly."
                : userText;
            var phoneImagesBase64 = input.ImagesBase64.ToList();
            var phoneImagePaths = new List<string>();
            string model = "";
            string systemPrompt = "";
            double temperature = 0.7;
            int maxTokens = 2048;
            bool makePhoneAudio = false;
            bool webSearchEnabled = false;
            string tavilyApiKey = "";
            List<ChatMessage> messagesForModel = new();
            var transientContexts = new List<ChatMessage>();
            var attachedPhoneDocuments = input.Documents
                .Select(d => new PendingDocumentAttachment(d.FileName, d.Document))
                .ToList();
            var keepPhoneDocumentsActive = input.KeepDocumentsActive;
            var phoneActiveDocumentSet = false;
            var phoneActiveDocumentCleared = false;
            if (!keepPhoneDocumentsActive && _activePhoneDocuments.Count > 0)
            {
                _activePhoneDocuments.Clear();
                phoneActiveDocumentCleared = true;
            }

            if (keepPhoneDocumentsActive && attachedPhoneDocuments.Count > 0)
            {
                _activePhoneDocuments.Clear();
                _activePhoneDocuments.AddRange(attachedPhoneDocuments);
                phoneActiveDocumentSet = true;
            }

            var phoneDocumentsForResponse = keepPhoneDocumentsActive && _activePhoneDocuments.Count > 0
                ? _activePhoneDocuments
                : attachedPhoneDocuments;
            var phoneDocumentContext = DocumentTextService.BuildContext(phoneDocumentsForResponse.Select(d => d.Document), modelUserText);
            var phoneDocumentCount = phoneDocumentsForResponse.Count;

            if (ShouldCaptureCameraForPrompt(modelUserText))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SetUIState("processing", "Phone camera...");
                    AddSystemMessage("Phone remote requested one camera photo.");
                });

                var photo = await _camera.CapturePhotoAsync(ct);
                phoneImagePaths.Add(photo.Path);
                phoneImagesBase64.Add(photo.Base64);
            }

            await Dispatcher.InvokeAsync(() =>
            {
                model = ModelCombo.Text;
                systemPrompt = GetEffectiveSystemPrompt();
                temperature = TempSlider.Value;
                maxTokens = GetMaxTokensForRequest(modelUserText, model);
                makePhoneAudio = _settings.PhoneRemote.PlayAudioOnPhone && TtsToggle.IsChecked == true;
                webSearchEnabled = WebSearchToggle.IsChecked == true;
                tavilyApiKey = TavilyApiKeyBox.Password.Trim();

                AddUserMessage($"[iPhone] {userText}", phoneImagePaths);
                _history.Add("user", userText, phoneImagesBase64);
                if (IsLargePaste(modelUserText))
                    AddSystemMessage("Large paste mode: previous chat history will not be sent with this request.");
                _history.RemoveWhere(m =>
                    m.Role.Equals("system", StringComparison.OrdinalIgnoreCase) &&
                    (m.Content.StartsWith("YouTube video transcript context for ", StringComparison.Ordinal) ||
                     m.Content.StartsWith("YouTube video local audio transcription context for ", StringComparison.Ordinal)));
                messagesForModel = BuildMessagesForModel(modelUserText, phoneImagesBase64);
                if (phoneActiveDocumentCleared)
                    AddSystemMessage("Phone active document cleared.");
                if (phoneActiveDocumentSet)
                    AddSystemMessage($"{_activePhoneDocuments.Count} phone document(s) set active. Uncheck Keep doc on the phone to stop including them.");
                if (!string.IsNullOrWhiteSpace(phoneDocumentContext))
                {
                    AddSystemMessage(keepPhoneDocumentsActive
                        ? $"{phoneDocumentCount} phone active document(s) included in this response."
                        : $"{phoneDocumentCount} phone attached document(s) added to this response.");
                }
                SetUIState("thinking", "Phone remote...");
            });

            if (string.IsNullOrWhiteSpace(model))
                return new PhoneRemoteAssistantResult("Select an Ollama model in the desktop app first.", null);

            if (DocumentTextService.TryAnswerExactSentenceQuestion(modelUserText, phoneDocumentsForResponse.Select(d => d.Document), out var exactPhoneDocumentAnswer))
            {
                string? exactAudioPath = null;
                if (makePhoneAudio)
                {
                    var speechText = CleanSpeechText(exactPhoneDocumentAnswer);
                    exactAudioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    var assistantMessage = AddAssistantMessage(exactPhoneDocumentAnswer);
                    if (!string.IsNullOrWhiteSpace(exactAudioPath))
                        AddAudioButtons(assistantMessage, exactAudioPath);

                    _history.Add("assistant", exactPhoneDocumentAnswer);
                    SetUIState("idle", "Ready");
                });

                return new PhoneRemoteAssistantResult(
                    exactPhoneDocumentAnswer,
                    exactAudioPath,
                    ActiveDocumentCount: keepPhoneDocumentsActive ? _activePhoneDocuments.Count : 0);
            }

            if (YouTubeTranscriptService.TryExtractYouTubeUrl(modelUserText, out var youtubeUrl))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SetUIState("processing", "Phone YouTube...");
                    AddSystemMessage("Phone remote fetching YouTube transcript.");
                });

                var transcriptResult = await _youtubeTranscripts.FetchTranscriptAsync(youtubeUrl, ct);
                if (string.IsNullOrWhiteSpace(transcriptResult.Transcript))
                {
                    await Dispatcher.InvokeAsync(() => AddSystemMessage("Phone remote YouTube captions unavailable. Downloading audio for local transcription."));
                    transcriptResult = await _youtubeTranscripts.FetchAudioTranscriptAsync(
                        youtubeUrl,
                        (stream, token) => _speech.TranscribeWavAsync(stream, token),
                        ct);
                }

                if (!string.IsNullOrWhiteSpace(transcriptResult.Transcript))
                {
                    var titleLine = string.IsNullOrWhiteSpace(transcriptResult.Title)
                        ? ""
                        : $"Title: {transcriptResult.Title}\n";
                    transientContexts.Add(new ChatMessage
                    {
                        Role = "system",
                        Content = $"YouTube video transcript context for {youtubeUrl}\n{titleLine}Transcript:\n{transcriptResult.Transcript}"
                    });
                    messagesForModel = BuildMessagesForModel(modelUserText, phoneImagesBase64);
                    InsertTransientContexts(messagesForModel, transientContexts);
                    ApplyYouTubeContextToCurrentUserMessage(messagesForModel, modelUserText, youtubeUrl, transcriptResult.Title, transcriptResult.Transcript);
                    await Dispatcher.InvokeAsync(() => AddSystemMessage("Phone remote YouTube transcript added to this response."));
                }
                else
                {
                    var error = $"YouTube transcript unavailable: {transcriptResult.Error}";
                    await Dispatcher.InvokeAsync(() =>
                    {
                        AddSystemMessage(error);
                        var assistantMessage = AddAssistantMessage(error);
                        _history.Add("assistant", error);
                        SetUIState("idle", "Ready");
                    });
                    return new PhoneRemoteAssistantResult(error, null);
                }
            }

            var shouldSearchWeb = webSearchEnabled && ShouldTriggerWebSearch(modelUserText);
            if (shouldSearchWeb)
            {
                if (string.IsNullOrWhiteSpace(tavilyApiKey))
                {
                    await Dispatcher.InvokeAsync(() => AddSystemMessage("Phone remote web search is on, but no Tavily API key is set."));
                }
                else
                {
                    try
                    {
                        await Dispatcher.InvokeAsync(() => SetUIState("searching", "Phone search..."));
                        _tavily.ApiKey = tavilyApiKey;
                        var webSearchQuery = RemoveWebSearchTriggerPhrases(modelUserText);
                        var searchContext = await _tavily.SearchAndBuildContextAsync(webSearchQuery, maxResults: 5, ct: ct);
                        if (!string.IsNullOrWhiteSpace(searchContext))
                        {
                            await Dispatcher.InvokeAsync(() => RememberWebSearchContext(webSearchQuery, searchContext));
                            await Dispatcher.InvokeAsync(() => AddSystemMessage("Phone remote web search results added."));
                            messagesForModel = BuildMessagesForModel(modelUserText, phoneImagesBase64);
                            InsertTransientContexts(messagesForModel, transientContexts);
                            messagesForModel.Insert(Math.Max(0, messagesForModel.Count - 1), new ChatMessage
                            {
                                Role = "system",
                                Content = "You have current web search context for this answer. Use it as the authoritative source for current facts, releases, versions, prices, dates, schedules, and news."
                            });
                            messagesForModel[^1] = new ChatMessage
                            {
                                Role = "user",
                                Content = $"{RemoveWebSearchTriggerPhrases(modelUserText)}\n\nCurrent web search context:\n{searchContext}\n\nAnswer the user's question using the current web search context above.",
                                ImagesBase64 = phoneImagesBase64
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        await Dispatcher.InvokeAsync(() => AddSystemMessage($"Phone remote web search failed: {ex.Message}"));
                    }
                }
            }

            ApplyDocumentContextToCurrentUserMessage(messagesForModel, phoneDocumentContext);

            var phoneContextTokens = await GetContextTokensForRequestAsync(model, ct);
            TrimMessagesToContextBudget(messagesForModel, systemPrompt, phoneContextTokens, maxTokens);
            var response = await _ollama.ChatAsync(model, messagesForModel, systemPrompt, temperature, maxTokens, ct, phoneContextTokens);
            var displayText = CleanDisplayText(response);
            string? audioPath = null;

            if (makePhoneAudio)
            {
                var speechText = CleanSpeechText(displayText);
                audioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
            }

            await Dispatcher.InvokeAsync(() =>
            {
                var assistantMessage = AddAssistantMessage(displayText);
                if (!string.IsNullOrWhiteSpace(audioPath))
                    AddAudioButtons(assistantMessage, audioPath);

                _history.Add("assistant", displayText);
                SetUIState("idle", "Ready");
            });

            return new PhoneRemoteAssistantResult(
                displayText,
                audioPath,
                ActiveDocumentCount: keepPhoneDocumentsActive ? _activePhoneDocuments.Count : 0);
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                AddSystemMessage($"Phone remote chat error: {ex.Message}");
                SetUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult($"Phone remote error: {ex.Message}", null);
        }
        finally
        {
            _phoneRemoteChatLock.Release();
        }
    }

    private void DarkModeToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.DarkMode = DarkModeToggle.IsChecked == true;
        DarkModeToggle.Content = _settings.DarkMode ? "Light" : "Dark";
        ApplyTheme(_settings.DarkMode);
        SaveSettings();
    }

    private void SettingsMenuButton_Click(object sender, RoutedEventArgs e)
    {
        SetSettingsPanelOpen(SettingsPanel.Visibility != Visibility.Visible, save: true);
    }

    private void SetSettingsPanelOpen(bool isOpen, bool save)
    {
        SettingsPanel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;
        SettingsMenuButton.ToolTip = isOpen ? "Hide settings" : "Show settings";
        _settings.SettingsPanelOpen = isOpen;
        if (save)
            SaveSettings();
    }

    private void ApplyTheme(bool darkMode)
    {
        UpdateLayout();
        ApplyThemeToVisualTree(this, darkMode);
    }

    private void ApplyThemeToVisualTree(DependencyObject root, bool darkMode)
    {
        if (root is Border themedBubble &&
            themedBubble.Tag as string is "UserBubble" or "AssistantBubble")
        {
            ApplyFixedChatBubbleTheme(themedBubble);
            return;
        }

        ApplyThemeToElement(root, darkMode);
        // Do not recolor the private template parts inside a Windows control.
        // The control-level palette already supplies a matched foreground,
        // background, and border. Walking into its template was applying a
        // second inversion and causing white-on-white ComboBoxes and toggles.
        if (root is Control && root is not Window)
            return;

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
            ApplyThemeToVisualTree(VisualTreeHelper.GetChild(root, index), darkMode);
    }

    private static void ApplyFixedChatBubbleTheme(Border bubble)
    {
        var isUser = bubble.Tag as string == "UserBubble";
        bubble.Background = new SolidColorBrush(
            isUser ? Color.FromRgb(255, 249, 222) : Color.FromRgb(238, 248, 232));
        bubble.BorderBrush = new SolidColorBrush(
            isUser ? Color.FromRgb(235, 181, 38) : Color.FromRgb(159, 199, 147));

        SetDescendantTextColor(bubble, Brushes.Black);
        if (bubble.Child is Panel panel)
        {
            foreach (var button in FindVisualChildren<Button>(panel))
            {
                button.Foreground = Brushes.Black;
                button.Background = Brushes.White;
                button.BorderBrush = new SolidColorBrush(Color.FromRgb(205, 209, 212));
            }
        }
    }

    private static void SetDescendantTextColor(DependencyObject root, Brush color)
    {
        switch (root)
        {
            case TextBlock textBlock:
                textBlock.Foreground = color;
                break;
            case TextBox textBox:
                textBox.Foreground = color;
                textBox.CaretBrush = color;
                break;
        }
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
            SetDescendantTextColor(VisualTreeHelper.GetChild(root, index), color);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;
            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private void ApplyThemeToElement(DependencyObject element, bool darkMode)
    {
        if (!_themeSnapshots.TryGetValue(element, out var snapshot))
        {
            snapshot = ThemeSnapshot.Capture(element);
            _themeSnapshots[element] = snapshot;
        }
        snapshot.Apply(element, darkMode);
    }

    private static Brush ThemeBrush(Brush? original, bool darkMode)
    {
        if (!darkMode || original is not SolidColorBrush solid)
            return original ?? Brushes.Transparent;

        var color = solid.Color;
        if (color.A == 0)
            return original;

        var spread = Math.Max(color.R, Math.Max(color.G, color.B)) -
                     Math.Min(color.R, Math.Min(color.G, color.B));
        if (spread > 16)
            return original;

        var value = (color.R + color.G + color.B) / 3;
        byte inverted = value switch
        {
            >= 248 => 10,
            >= 235 => 20,
            >= 210 => 34,
            >= 150 => 92,
            >= 90 => 175,
            >= 35 => 224,
            _ => 248
        };
        return new SolidColorBrush(Color.FromArgb(color.A, inverted, inverted, inverted));
    }

    private sealed class ThemeSnapshot
    {
        public Brush? Background { get; init; }
        public Brush? Foreground { get; init; }
        public Brush? BorderBrush { get; init; }

        public static ThemeSnapshot Capture(DependencyObject element) =>
            element switch
            {
                Control control => new ThemeSnapshot
                {
                    Background = control.Background,
                    Foreground = control.Foreground,
                    BorderBrush = control.BorderBrush
                },
                Border border => new ThemeSnapshot
                {
                    Background = border.Background,
                    BorderBrush = border.BorderBrush
                },
                Panel panel => new ThemeSnapshot { Background = panel.Background },
                TextBlock text => new ThemeSnapshot { Foreground = text.Foreground },
                _ => new ThemeSnapshot()
            };

        public void Apply(DependencyObject element, bool darkMode)
        {
            switch (element)
            {
                case ComboBox comboBox:
                    comboBox.SetCurrentValue(Control.BackgroundProperty,
                        darkMode ? new SolidColorBrush(Color.FromRgb(18, 18, 18)) : Background);
                    comboBox.SetCurrentValue(Control.ForegroundProperty,
                        darkMode ? Brushes.White : Foreground);
                    comboBox.SetCurrentValue(Control.BorderBrushProperty,
                        darkMode ? new SolidColorBrush(Color.FromRgb(92, 92, 92)) : BorderBrush);
                    break;
                case ComboBoxItem comboItem:
                    comboItem.SetCurrentValue(Control.BackgroundProperty,
                        darkMode ? new SolidColorBrush(Color.FromRgb(18, 18, 18)) : Background);
                    comboItem.SetCurrentValue(Control.ForegroundProperty,
                        darkMode ? Brushes.White : Foreground);
                    break;
                case PasswordBox passwordBox:
                    passwordBox.SetCurrentValue(Control.BackgroundProperty,
                        darkMode ? new SolidColorBrush(Color.FromRgb(10, 10, 10)) : Background);
                    passwordBox.SetCurrentValue(Control.ForegroundProperty,
                        darkMode ? Brushes.White : Foreground);
                    passwordBox.SetCurrentValue(Control.BorderBrushProperty,
                        darkMode ? new SolidColorBrush(Color.FromRgb(88, 88, 88)) : BorderBrush);
                    passwordBox.CaretBrush = darkMode ? Brushes.White : Foreground;
                    break;
                case TextBox textBox:
                    textBox.SetCurrentValue(Control.BackgroundProperty,
                        darkMode && Background != Brushes.Transparent
                            ? new SolidColorBrush(Color.FromRgb(10, 10, 10))
                            : Background);
                    textBox.SetCurrentValue(Control.ForegroundProperty,
                        darkMode ? Brushes.White : Foreground);
                    textBox.SetCurrentValue(Control.BorderBrushProperty,
                        darkMode ? new SolidColorBrush(Color.FromRgb(88, 88, 88)) : BorderBrush);
                    textBox.CaretBrush = darkMode ? Brushes.White : Foreground;
                    break;
                case ToggleButton toggleButton:
                    var isChecked = toggleButton.IsChecked == true;
                    toggleButton.SetCurrentValue(Control.BackgroundProperty,
                        darkMode
                            ? isChecked ? Brushes.White : new SolidColorBrush(Color.FromRgb(10, 10, 10))
                            : Background);
                    toggleButton.SetCurrentValue(Control.ForegroundProperty,
                        darkMode && isChecked ? Brushes.Black :
                        darkMode ? Brushes.White : Foreground);
                    toggleButton.SetCurrentValue(Control.BorderBrushProperty,
                        darkMode ? new SolidColorBrush(Color.FromRgb(52, 52, 52)) : BorderBrush);
                    break;
                case Button button:
                    var originalColor = (Background as SolidColorBrush)?.Color;
                    var originalIsNearBlack = originalColor is Color buttonColor &&
                        buttonColor.R < 45 && buttonColor.G < 45 && buttonColor.B < 45;
                    button.SetCurrentValue(Control.BackgroundProperty,
                        darkMode && originalIsNearBlack
                            ? Brushes.White
                            : darkMode && originalColor is Color colored &&
                              Math.Max(colored.R, Math.Max(colored.G, colored.B)) -
                              Math.Min(colored.R, Math.Min(colored.G, colored.B)) > 20
                                ? Background
                                : darkMode
                                    ? new SolidColorBrush(Color.FromRgb(10, 10, 10))
                                    : Background);
                    button.SetCurrentValue(Control.ForegroundProperty,
                        darkMode && originalIsNearBlack ? Brushes.Black :
                        darkMode ? Brushes.White : Foreground);
                    button.SetCurrentValue(Control.BorderBrushProperty,
                        darkMode ? new SolidColorBrush(Color.FromRgb(52, 52, 52)) : BorderBrush);
                    break;
                case Control control:
                    control.SetCurrentValue(Control.BackgroundProperty, ThemeBrush(Background, darkMode));
                    control.SetCurrentValue(Control.ForegroundProperty, ThemeBrush(Foreground, darkMode));
                    control.SetCurrentValue(Control.BorderBrushProperty, ThemeBrush(BorderBrush, darkMode));
                    break;
                case Border border:
                    border.SetCurrentValue(Border.BackgroundProperty, ThemeBrush(Background, darkMode));
                    border.SetCurrentValue(Border.BorderBrushProperty, ThemeBrush(BorderBrush, darkMode));
                    break;
                case Panel panel:
                    panel.SetCurrentValue(Panel.BackgroundProperty, ThemeBrush(Background, darkMode));
                    break;
                case TextBlock text:
                    text.SetCurrentValue(TextBlock.ForegroundProperty, ThemeBrush(Foreground, darkMode));
                    break;
            }
        }
    }

    private async Task<string> HandlePhoneRemoteToolAsync(string prompt, CancellationToken ct)
    {
        await _phoneRemoteChatLock.WaitAsync(ct);
        try
        {
            string model = "";
            const string systemPrompt =
                "You are a precise internal tool for a local iPhone assistant. " +
                "Follow the requested output format exactly. Return no commentary unless requested.";
            double temperature = 0.1;
            const int maxTokens = 512;
            await Dispatcher.InvokeAsync(() =>
            {
                model = ModelCombo.Text;
            });

            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("Select an Ollama model in the desktop app first.");

            var messages = new List<ChatMessage>
            {
                new() { Role = "user", Content = prompt }
            };
            var contextTokens = await GetContextTokensForRequestAsync(model, ct);
            return CleanDisplayText(
                await _ollama.ChatAsync(
                    model,
                    messages,
                    systemPrompt,
                    temperature,
                    maxTokens,
                    ct,
                    contextTokens));
        }
        finally
        {
            _phoneRemoteChatLock.Release();
        }
    }

    private async Task<StructuredTextMessageResult> HandlePhoneRemoteTextMessageAsync(
        string request,
        CancellationToken ct)
    {
        await _phoneRemoteChatLock.WaitAsync(ct);
        try
        {
            var model = await Dispatcher.InvokeAsync(() => ModelCombo.Text);
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("Select a model in the desktop app first.");

            Exception? lastError = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    return await _ollama.PrepareTextMessageAsync(model, request, ct);
                }
                catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested)
                {
                    lastError = ex;
                    await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), ct);
                }
            }
            throw lastError ?? new InvalidOperationException(
                "The structured text-message request failed after three attempts.");
        }
        finally
        {
            _phoneRemoteChatLock.Release();
        }
    }

    private async Task<StructuredCalendarEventResult> HandlePhoneRemoteCalendarAsync(
        PhoneRemoteCalendarRequest request,
        CancellationToken ct)
    {
        return await RunStructuredPhoneRequestAsync(
            "calendar",
            (model, token) => _ollama.PrepareCalendarEventAsync(
                model,
                request.Text.Trim(),
                request.CurrentDateTime.Trim(),
                request.TimeZone.Trim(),
                token),
            ct);
    }

    private async Task<StructuredGroundedAnswerResult> HandlePhoneRemoteGroundedAnswerAsync(
        string prompt,
        CancellationToken ct)
    {
        return await RunStructuredPhoneRequestAsync(
            "grounded answer",
            (model, token) => _ollama.AnswerGroundedQuestionAsync(model, prompt, token),
            ct);
    }

    private async Task<T> RunStructuredPhoneRequestAsync<T>(
        string feature,
        Func<string, CancellationToken, Task<T>> operation,
        CancellationToken ct)
    {
        await _phoneRemoteChatLock.WaitAsync(ct);
        try
        {
            var model = await Dispatcher.InvokeAsync(() => ModelCombo.Text);
            if (string.IsNullOrWhiteSpace(model))
                throw new InvalidOperationException("Select a model in the desktop app first.");

            Exception? lastError = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    return await operation(model, ct);
                }
                catch (Exception ex) when (attempt < 3 && !ct.IsCancellationRequested)
                {
                    lastError = ex;
                    await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), ct);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    break;
                }
            }

            var failure = lastError ?? new InvalidOperationException(
                $"The structured {feature} request failed after three attempts.");
            LogPhoneFeatureFailure(feature, failure);
            throw failure;
        }
        finally
        {
            _phoneRemoteChatLock.Release();
        }
    }

    private static void LogPhoneFeatureFailure(string feature, Exception error)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VoiceChatbot");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "phone-feature-errors.log"),
                $"[{DateTimeOffset.Now:O}] {feature}: {error}\n");
        }
        catch
        {
            // Diagnostic logging must never replace the original feature error.
        }
    }

    // ==================== UI Helpers ====================

    private Border AddUserMessage(string text, IEnumerable<string>? imagePaths = null)
    {
        var border = new Border
        {
            Background = FindResource("UserBubbleBrush") as SolidColorBrush,
            BorderBrush = FindResource("UserBubbleBorderBrush") as SolidColorBrush,
            BorderThickness = new Thickness(1),
            Tag = "UserBubble",
            CornerRadius = new CornerRadius(12, 12, 4, 12),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(60, 4, 16, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = UserBubbleMaxWidth
        };

        var stack = new StackPanel();
        var header = new TextBlock
        {
            Text = "You",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(145, 105, 0)),
            Margin = new Thickness(0, 0, 0, 4)
        };
        var body = CreateSelectableText(text, Brushes.Black);

        stack.Children.Add(header);
        foreach (var imagePath in imagePaths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                continue;

            var image = new Image
            {
                Source = new BitmapImage(new Uri(imagePath)),
                MaxWidth = 220,
                MaxHeight = 160,
                Stretch = Stretch.Uniform,
                Margin = new Thickness(0, 0, 0, 8)
            };
            stack.Children.Add(image);
        }
        stack.Children.Add(body);
        border.Child = stack;
        ChatPanel.Children.Add(border);
        ApplyThemeToVisualTree(border, _settings.DarkMode);
        ScrollChat();
        return border;
    }

    private void UpdateChatBubbleWidths()
    {
        if (ChatPanel is null)
            return;

        foreach (var child in ChatPanel.Children.OfType<Border>())
        {
            if (child.HorizontalAlignment == HorizontalAlignment.Right)
                child.MaxWidth = UserBubbleMaxWidth;
            else if (child.HorizontalAlignment == HorizontalAlignment.Left)
                child.MaxWidth = AssistantBubbleMaxWidth;

            UpdateCodeBlockWidths(child);
        }
    }

    private void UpdateCodeBlockWidths(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border border && border.Tag as string == "CodeBlock")
                border.MaxWidth = CodeBlockMaxWidth;

            UpdateCodeBlockWidths(child);
        }
    }

    private AssistantMessageUi AddAssistantMessage(string text)
    {
        var border = new Border
        {
            Background = FindResource("CardBgBrush") as SolidColorBrush,
            BorderBrush = new SolidColorBrush(Color.FromRgb(183, 215, 174)),
            BorderThickness = new Thickness(1),
            Tag = "AssistantBubble",
            CornerRadius = new CornerRadius(12, 12, 12, 4),
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(16, 4, 60, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = AssistantBubbleMaxWidth
        };

        var stack = new StackPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        var nameBlock = new TextBlock
        {
            Text = "Assistant",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = FindResource("AccentBrush") as SolidColorBrush
        };
        header.Children.Add(nameBlock);
        stack.Children.Add(header);

        var content = new StackPanel();
        var body = CreateSelectableText(text, FindResource("TextPrimaryBrush") as Brush ?? Brushes.Black);
        content.Children.Add(body);

        stack.Children.Add(content);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(actions);
        border.Child = stack;
        ChatPanel.Children.Add(border);
        ApplyThemeToVisualTree(border, _settings.DarkMode);
        ScrollChat();
        return new AssistantMessageUi(body, content, actions);
    }

    private void SetAssistantMessageText(AssistantMessageUi assistantMessage, string text, bool renderCodeBlocks = false)
    {
        if (!renderCodeBlocks || !ContainsFencedCodeBlock(text))
        {
            assistantMessage.Body.Text = text;
            return;
        }

        RenderAssistantMessageWithCodeBlocks(assistantMessage, text);
        ScrollChat();
    }

    private void RenderAssistantMessageWithCodeBlocks(AssistantMessageUi assistantMessage, string text)
    {
        assistantMessage.Content.Children.Clear();

        var foreground = FindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
        var position = 0;

        while (position < text.Length)
        {
            var fenceStart = FindFenceLineStart(text, position);
            if (fenceStart < 0)
            {
                AddAssistantTextPart(assistantMessage.Content, text[position..], foreground);
                break;
            }

            if (fenceStart > position)
                AddAssistantTextPart(assistantMessage.Content, text[position..fenceStart], foreground);

            var codeStart = fenceStart + 3;
            var lineEnd = text.IndexOf('\n', codeStart);
            string language;
            if (lineEnd >= 0)
            {
                language = text[codeStart..lineEnd].Trim();
                codeStart = lineEnd + 1;
            }
            else
            {
                language = text[codeStart..].Trim();
                codeStart = text.Length;
            }

            if (!Regex.IsMatch(language, "^[A-Za-z0-9_+.#-]*$"))
            {
                language = "";
                codeStart = fenceStart + 3;
            }

            var fenceEnd = FindFenceLineStart(text, codeStart);
            var codeEnd = fenceEnd >= 0 ? fenceEnd : text.Length;
            var code = text[codeStart..codeEnd].Trim('\r', '\n');
            AddAssistantCodeBlock(assistantMessage.Content, language, code);

            if (fenceEnd < 0)
                break;

            var closingLineEnd = text.IndexOf('\n', fenceEnd + 3);
            position = closingLineEnd >= 0 ? closingLineEnd + 1 : fenceEnd + 3;
        }
    }

    private static bool ContainsFencedCodeBlock(string text) =>
        FindFenceLineStart(text, 0) >= 0;

    private static int FindFenceLineStart(string text, int startIndex)
    {
        if (string.IsNullOrWhiteSpace(text) || startIndex >= text.Length)
            return -1;

        var searchFrom = Math.Max(0, startIndex);
        while (searchFrom < text.Length)
        {
            var fence = text.IndexOf("```", searchFrom, StringComparison.Ordinal);
            if (fence < 0)
                return -1;

            var lineStart = fence;
            while (lineStart > 0 && text[lineStart - 1] != '\n' && text[lineStart - 1] != '\r')
                lineStart--;

            var prefix = text[lineStart..fence];
            if (string.IsNullOrWhiteSpace(prefix))
                return fence;

            searchFrom = fence + 3;
        }

        return -1;
    }

    private void AddAssistantTextPart(StackPanel content, string text, Brush foreground)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        content.Children.Add(CreateSelectableText(trimmed, foreground));
    }

    private void AddAssistantCodeBlock(StackPanel content, string language, string code)
    {
        var wrapper = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(245, 247, 250)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(205, 213, 224)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(0, 8, 0, 8),
            Padding = new Thickness(8),
            MaxWidth = CodeBlockMaxWidth,
            Tag = "CodeBlock"
        };

        var stack = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "Code" : language,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(45, 55, 72)),
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(label, Dock.Left);
        header.Children.Add(label);

        var copyButton = new Button
        {
            Content = "Copy",
            FontSize = 10,
            Padding = new Thickness(8, 2, 8, 2),
            Background = FindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue,
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Tag = code,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        copyButton.Click += (_, _) =>
        {
            Clipboard.SetText(code);
            AddSystemMessage("Code copied to clipboard.");
        };
        DockPanel.SetDock(copyButton, Dock.Right);
        header.Children.Add(copyButton);
        stack.Children.Add(header);

        var codeBox = CreateSelectableText(code, new SolidColorBrush(Color.FromRgb(26, 32, 44)));
        codeBox.FontFamily = new FontFamily("Consolas");
        codeBox.FontSize = 12;
        codeBox.Background = Brushes.White;
        codeBox.BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240));
        codeBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        codeBox.TextWrapping = TextWrapping.NoWrap;
        stack.Children.Add(codeBox);

        wrapper.Child = stack;
        content.Children.Add(wrapper);
        ApplyThemeToVisualTree(wrapper, _settings.DarkMode);
    }

    private void AddSystemMessage(string text)
    {
        var block = CreateSelectableText(text, FindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray);
        block.FontSize = 11;
        block.TextAlignment = TextAlignment.Center;
        block.Margin = new Thickness(0, 8, 0, 4);
        ChatPanel.Children.Add(block);
        ApplyThemeToVisualTree(block, _settings.DarkMode);
        ScrollChat();
    }

    private static TextBox CreateSelectableText(string text, Brush foreground)
    {
        return new TextBox
        {
            Text = text,
            FontSize = 13,
            Foreground = foreground,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            IsReadOnly = true,
            IsTabStop = false,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Cursor = Cursors.IBeam
        };
    }

    private void ScrollChat()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ChatScroll.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void SetUIState(string state, string label)
    {
        StateLabel.Text = label;
        ActivityLabel.Text = state switch
        {
            "thinking" => "... Waiting for AI...",
            "searching" => "... Searching the web...",
            "processing" => "... Generating response...",
            "idle" => "",
            _ => ""
        };

        // Enable/disable send
        SendBtn.IsEnabled = state == "idle";
        CameraBtn.IsEnabled = state == "idle";
        ImageBtn.IsEnabled = state == "idle";
        DocumentBtn.IsEnabled = state == "idle";
        KeepDocumentActiveToggle.IsEnabled = state == "idle";
        CreateImageBtn.IsEnabled = state == "idle";
        EditImageBtn.IsEnabled = state == "idle";
        Krea2Btn.IsEnabled = state == "idle";
        AudioBtn.IsEnabled = state == "idle";
        CreateVideoBtn.IsEnabled = state == "idle";
        MessageInput.IsEnabled = state == "idle";
    }

    private void UpdateImageButtonLabel()
    {
        ImageBtn.Content = _pendingImages.Count > 0 ? $"Image ({_pendingImages.Count})" : "Image";
    }

    private void UpdateAudioButtonLabel()
    {
        AudioBtn.Content = File.Exists(_pendingVideoAudioPath) ? "Video Audio (1)" : "Video Audio";
    }

    private void UpdateDocumentButtonLabel()
    {
        if (_pendingDocuments.Count > 0)
            DocumentBtn.Content = $"Document ({_pendingDocuments.Count})";
        else if (_activeDocuments.Count > 0 && KeepDocumentActiveToggle.IsChecked == true)
            DocumentBtn.Content = $"Document Active ({_activeDocuments.Count})";
        else
            DocumentBtn.Content = "Document";
    }

    private void KeepDocumentActiveToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (KeepDocumentActiveToggle.IsChecked == true)
        {
            if (_pendingDocuments.Count > 0)
                AddSystemMessage("Keep doc is on. The next sent message will make the attached document active.");
        }
        else
        {
            if (_activeDocuments.Count > 0)
            {
                _activeDocuments.Clear();
                AddSystemMessage("Active document cleared.");
            }
        }

        UpdateDocumentButtonLabel();
    }

    // Speak the last assistant response
    private async void SpeakLastResponse(string text, AssistantMessageUi? assistantMessage = null)
    {
        if (!string.IsNullOrWhiteSpace(text) && TtsToggle.IsChecked == true)
        {
            try
            {
                var speechText = CleanSpeechText(text);
                if (string.IsNullOrWhiteSpace(speechText))
                {
                    SetUIState("idle", "Ready");
                    _speech.ReadyForNextSpeech();
                    return;
                }

                var audioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
                if (!string.IsNullOrWhiteSpace(audioPath))
                {
                    if (assistantMessage != null)
                        AddAudioButtons(assistantMessage, audioPath);

                    _speech.PlayAudioFile(audioPath);
                    // SpeechFinished will restart auto-listen
                    return;
                }

                _speech.Speak(speechText);
                // SpeechFinished will restart auto-listen
                return;
            }
            catch
            {
                // Speech failed
            }
        }
        // No TTS or TTS disabled - mark ready, restart auto-listen if active
        SetUIState("idle", "Ready");
        _speech.ReadyForNextSpeech();

        if (_autoListening)
        {
            Task.Delay(100).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_autoListening && !_pausedListeningForTextInput)
                    {
                        if (!IsVoiceInputAllowedByFacePolicy())
                            return;

                        if (_speech.CurrentState == VoiceState.Speaking)
                            return;

                        _speech.StartListening();
                    }
                });
            });
        }
    }

    private static string GetAssistantAudioDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceChatbot",
            "assistant-audio");
    }

    private void AddAudioButtons(AssistantMessageUi assistantMessage, string audioPath)
    {
        assistantMessage.Actions.Children.Clear();
        assistantMessage.Actions.Visibility = Visibility.Visible;

        var replayBtn = new Button
        {
            Content = "Replay Audio",
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Background = new SolidColorBrush(Color.FromRgb(108, 92, 231)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Tag = audioPath
        };
        replayBtn.Click += ReplayAssistantAudio_Click;
        assistantMessage.Actions.Children.Add(replayBtn);

        var downloadBtn = new Button
        {
            Content = "Download Audio",
            FontSize = 10,
            Padding = new Thickness(6, 2, 6, 2),
            Background = new SolidColorBrush(Color.FromRgb(0, 184, 148)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Tag = audioPath
        };
        downloadBtn.Click += DownloadAssistantAudio_Click;
        assistantMessage.Actions.Children.Add(downloadBtn);
    }

    private void ReplayAssistantAudio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string audioPath } || !File.Exists(audioPath))
        {
            AddSystemMessage("Audio file is no longer available.");
            return;
        }

        _speech.PlayAudioFile(audioPath);
    }

    private void DownloadAssistantAudio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string audioPath } || !File.Exists(audioPath))
        {
            AddSystemMessage("Audio file is no longer available.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save assistant audio",
            Filter = "WAV audio (*.wav)|*.wav",
            FileName = $"assistant_response_{DateTime.Now:yyyyMMdd_HHmmss}.wav",
            AddExtension = true,
            DefaultExt = ".wav"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        File.Copy(audioPath, dialog.FileName, overwrite: true);
        AddSystemMessage($"Audio saved to {dialog.FileName}");
    }

    // ==================== Window Events ====================

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveSettings();
        _schedulerTimer?.Stop();
        _schedulerStore.Save();
        StopFacePresenceAsync().GetAwaiter().GetResult();
        _phoneRemoteServer.StopAsync().GetAwaiter().GetResult();
        _speech.Dispose();
        _camera.Dispose();
        _phoneRemoteServer.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _ollama.Dispose();
        _tavily.Dispose();
        _comfyImages.Dispose();
    }

    private static bool ShouldCaptureCameraForPrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9\\s]", " ");
        normalized = Regex.Replace(normalized, "\\s+", " ").Trim();
        return normalized == "what do you see";
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        // Could add minimize to tray here
    }

    /// <summary>
    /// Strips noisy formatting from text while preserving command/code content.
    /// </summary>
    private static string CleanDisplayText(string text, bool preserveCodeBlocks = false)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (LooksLikeOnlyUnusedTokens(text))
            return "The model returned only special placeholder tokens, such as <unused49>. That usually means the llama.cpp server was launched with the wrong Gemma chat template or an incompatible/missing mmproj projector. Restart the Gemma 4 12B server with the correct Gemma template and matching mmproj, then try the image again.";
        if (LooksLikeLeakedReasoningDump(text))
            return "The model returned internal reasoning instead of a normal answer. This usually means the selected llama.cpp model/server template is misconfigured or using an incompatible reasoning/chat format. Try switching models or restarting the server with the correct chat template.";

        if (preserveCodeBlocks)
            return RemoveCitationArtifacts(text).Trim();

        // 1. Strip emojis using char ranges
        var sb = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            var cat = char.GetUnicodeCategory(c);
            if (cat == System.Globalization.UnicodeCategory.Surrogate ||
                cat == System.Globalization.UnicodeCategory.OtherSymbol)
                continue;
            int ci = (int)c;
            if (ci >= 0x2600 && ci <= 0x27BF) continue;   // Misc symbols
            if (ci >= 0xFE00 && ci <= 0xFEFF) continue;   // Variation selectors
            sb.Append(c);
        }
        string cleaned = sb.ToString();

        // 2. Remove generated source citation artifacts.
        cleaned = RemoveCitationArtifacts(cleaned);

        // 3. Strip code fences but keep the commands/content inside them.
        cleaned = Regex.Replace(cleaned, "```[a-zA-Z0-9_-]*\\s*([\\s\\S]*?)```", "$1");

        // 4. Strip inline code
        cleaned = Regex.Replace(cleaned, "`[^`]+`", m => m.Value.Trim('`'));

        // 5. Strip bold **text** or __text__
        cleaned = Regex.Replace(cleaned, "\\*\\*(.+?)\\*\\*", "$1");
        cleaned = Regex.Replace(cleaned, "__(.+?)__", "$1");

        // 6. Strip italic *text* or _text_
        cleaned = Regex.Replace(cleaned, "\\*(.+?)\\*", "$1");
        cleaned = Regex.Replace(cleaned, "(?<!\\w)_(.+?)_(?!\\w)", "$1");

        // 7. Strip headings # ## ### etc
        cleaned = Regex.Replace(cleaned, "^#{1,6}\\s+", "", RegexOptions.Multiline);

        // 8. Strip hashtags
        cleaned = Regex.Replace(cleaned, "#\\w+", "");

        // 9. Strip horizontal rules
        cleaned = Regex.Replace(cleaned, "^[-*]{3,}\\s*$", "", RegexOptions.Multiline);

        // 10. Strip bullet points
        cleaned = Regex.Replace(cleaned, "^[\\-\\*]\\s+", "", RegexOptions.Multiline);

        // 11. Strip numbered lists
        cleaned = Regex.Replace(cleaned, "^\\d+\\.\\s+", "", RegexOptions.Multiline);

        // 12. Strip links [text](url)
        cleaned = Regex.Replace(cleaned, "\\[([^\\]]+)\\]\\([^)]+\\)", "$1");

        // 13. Strip HTML tags
        cleaned = Regex.Replace(cleaned, "<[^>]+>", "");

        // 14. Clean up extra whitespace
        cleaned = Regex.Replace(cleaned, "\\n{3,}", "\n\n");
        cleaned = Regex.Replace(cleaned, "  +", " ");

        return cleaned.Trim();
    }

    private static bool LooksLikeOnlyUnusedTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var withoutUnusedTokens = Regex.Replace(text, @"<unused\d+>", "", RegexOptions.IgnoreCase);
        withoutUnusedTokens = Regex.Replace(withoutUnusedTokens, @"\s+", "");
        return withoutUnusedTokens.Length == 0 && Regex.IsMatch(text, @"<unused\d+>", RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeLeakedReasoningDump(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var sample = text.TrimStart();
        if (Regex.IsMatch(sample, @"\A(?:User asks|Context|Self-Correction|Correction|The user is asking|Previous response)\b", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(sample, @"\b(Self-Correction|Correction|Context|previous response|system prompt|prompt history|tool|video/conversation)\b", RegexOptions.IgnoreCase))
            return true;

        return Regex.IsMatch(sample, @"<\|?channel\|?>\s*thought|thought\s*<\|?channel\|?>|thoughtthought", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Removes citation/bracket artifacts and URLs before TTS, without changing chat display text.
    /// </summary>
    private static string CleanSpeechText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var firstCodeBlock = Regex.Match(text, "```[\\s\\S]*?```");
        var cleaned = firstCodeBlock.Success
            ? text[..firstCodeBlock.Index]
            : text;

        cleaned = RemoveCitationArtifacts(cleaned);

        // Avoid reading raw URLs aloud.
        cleaned = Regex.Replace(cleaned, "https?://\\S+", "");

        // Remove common leftover citation fragments.
        cleaned = Regex.Replace(cleaned, "\\b\\d+†L\\d+(?:-L\\d+)?\\b", "");

        cleaned = CleanSpeechDiagramMarkup(cleaned);
        cleaned = NormalizeSpeechNumbers(cleaned);

        cleaned = Regex.Replace(cleaned, "\\s+([,.!?;:])", "$1");
        cleaned = Regex.Replace(cleaned, "\\n{3,}", "\n\n");
        cleaned = Regex.Replace(cleaned, "[ \\t]{2,}", " ");

        return cleaned.Trim();
    }

    private static string CleanSpeechDiagramMarkup(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var cleaned = text
            .Replace("→", ". ")
            .Replace("←", ". ")
            .Replace("↓", ". ")
            .Replace("↑", ". ")
            .Replace("⇒", ". ")
            .Replace("⇐", ". ")
            .Replace("↔", ". ")
            .Replace("↕", ". ");

        // LaTeX arrows and math wrappers are useful visually, but terrible aloud:
        // "$\downarrow$", "\rightarrow", "\leftarrow", etc.
        cleaned = Regex.Replace(
            cleaned,
            @"\$?\s*\\(?:downarrow|uparrow|leftarrow|rightarrow|to|Rightarrow|Leftarrow|leftrightarrow)\s*\$?",
            ". ",
            RegexOptions.IgnoreCase);

        // Strip common math/display wrappers left behind by diagrams.
        cleaned = Regex.Replace(cleaned, @"\$\s*", "");
        cleaned = Regex.Replace(cleaned, @"\s*\$", "");

        // Mermaid/ASCII/tree connector noise.
        cleaned = Regex.Replace(cleaned, @"(?m)^\s*(?:[-=]{2,}|[|│┃]+|[+`'└├┌┐┘┤┬┴─━]+)\s*$", "");
        cleaned = Regex.Replace(cleaned, @"(?m)^\s*(?:[|│┃]\s*)+", "");
        cleaned = Regex.Replace(cleaned, @"\s*(?:-{1,2}>|<-{1,2}|=>|<=)\s*", ". ");
        cleaned = Regex.Replace(cleaned, @"\s+[|│┃]\s+", ". ");

        // Don't speak literal markdown emphasis/backticks around labels.
        cleaned = Regex.Replace(cleaned, @"[`*_]{1,3}", "");

        // Collapse repeated sentence breaks created by stripped arrows.
        cleaned = Regex.Replace(cleaned, @"(?:\s*\.\s*){2,}", ". ");
        cleaned = Regex.Replace(cleaned, @"(?m)^[ \t]*\.[ \t]*$", "");

        return cleaned;
    }

    private static string NormalizeSpeechNumbers(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var cleaned = text
            .Replace("≈", " about ")
            .Replace("≥", " at least ")
            .Replace("≤", " at most ")
            .Replace("–", "-")
            .Replace("—", "-")
            .Replace("‑", "-");

        // Markdown table pipes sound awful in TTS. Turn table separators into pauses.
        cleaned = Regex.Replace(cleaned, "^\\s*\\|?\\s*:?-{2,}:?\\s*(?:\\|\\s*:?-{2,}:?\\s*)+\\|?\\s*$", "", RegexOptions.Multiline);
        cleaned = Regex.Replace(cleaned, "\\s*\\|\\s*", ", ");

        // Common retirement/finance shorthand: avoid TTS saying "four hundred and one thousand".
        cleaned = Regex.Replace(
            cleaned,
            @"\b401[\s\u00A0\u202F]*\(?[Kk]\)?\b",
            "four oh one k");

        cleaned = Regex.Replace(
            cleaned,
            @"\b([0-9]{3})\(([A-Za-z])\)",
            match => $"{DigitsToWords(match.Groups[1].Value)} {match.Groups[2].Value.ToLowerInvariant()}");

        cleaned = Regex.Replace(
            cleaned,
            @"\b([0-9]{1,2}):([0-9]{2})[\s\u00A0\u202F]*(a\.?m\.?|p\.?m\.?)\b",
            match => ClockTimeToWords(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value),
            RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(
            cleaned,
            @"(?:US[\s\u00A0\u202F]*)?\$[\s\u00A0\u202F]*([0-9][0-9,]*(?:\.[0-9]+)?)[\s\u00A0\u202F]*([KkMmBb])?[\s\u00A0\u202F]*-[\s\u00A0\u202F]*(?:US[\s\u00A0\u202F]*)?\$?[\s\u00A0\u202F]*([0-9][0-9,]*(?:\.[0-9]+)?)[\s\u00A0\u202F]*([KkMmBb])\b",
            match =>
            {
                var firstSuffix = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value;
                var first = ScaledNumberToWords(match.Groups[1].Value, firstSuffix);
                var second = ScaledNumberToWords(match.Groups[3].Value, match.Groups[4].Value);
                return $"{first} dollars to {second} dollars";
            },
            RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(
            cleaned,
            @"(?:US[\s\u00A0\u202F]*)?\$[\s\u00A0\u202F]*([0-9][0-9,]*(?:\.[0-9]+)?)[\s\u00A0\u202F]*([KkMmBb])\b",
            match => $"{ScaledNumberToWords(match.Groups[1].Value, match.Groups[2].Value)} dollars",
            RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(
            cleaned,
            @"(?:US[\s\u00A0\u202F]*)?\$[\s\u00A0\u202F]*([0-9][0-9,]*)(?:[\s\u00A0\u202F]*-[\s\u00A0\u202F]*(?:US[\s\u00A0\u202F]*)?\$?[\s\u00A0\u202F]*([0-9][0-9,]*))?",
            match =>
            {
                var first = NumberToWords(ParseSpeechNumber(match.Groups[1].Value));
                if (!match.Groups[2].Success)
                    return $"{first} dollars";

                var second = NumberToWords(ParseSpeechNumber(match.Groups[2].Value));
                return $"{first} to {second} dollars";
            },
            RegexOptions.IgnoreCase);

        // Plain text money amounts, e.g. "6,300 dollars" or "2,000 to 3,000 dollars".
        cleaned = Regex.Replace(
            cleaned,
            @"\b([0-9][0-9,]*)(?:[\s\u00A0\u202F]*(?:-|to)[\s\u00A0\u202F]*([0-9][0-9,]*))?[\s\u00A0\u202F]*(dollars?|bucks?)\b",
            match =>
            {
                var first = NumberToWords(ParseSpeechNumber(match.Groups[1].Value));
                var unit = match.Groups[3].Value.ToLowerInvariant().StartsWith("buck") ? "bucks" : "dollars";
                if (!match.Groups[2].Success)
                    return $"{first} {unit}";

                var second = NumberToWords(ParseSpeechNumber(match.Groups[2].Value));
                return $"{first} to {second} {unit}";
            },
            RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(
            cleaned,
            @"\b([0-9][0-9,]*(?:\.[0-9]+)?)[\s\u00A0\u202F]*%\b",
            match => $"{DecimalNumberToWords(match.Groups[1].Value)} percent");

        cleaned = Regex.Replace(
            cleaned,
            @"\b([0-9][0-9,]*)[\s\u00A0\u202F]*-[\s\u00A0\u202F]*([0-9][0-9,]*)[\s\u00A0\u202F]*(meters?|metres?|m|feet|foot|ft|°?F|°?C)\b",
            match => $"{NumberToWords(ParseSpeechNumber(match.Groups[1].Value))} to {NumberToWords(ParseSpeechNumber(match.Groups[2].Value))} {UnitToWords(match.Groups[3].Value)}");

        cleaned = Regex.Replace(
            cleaned,
            @"\b([0-9][0-9,]*)[\s\u00A0\u202F]+to[\s\u00A0\u202F]+([0-9][0-9,]*)[\s\u00A0\u202F]*(meters?|metres?|m|feet|foot|ft|°?F|°?C)\b",
            match => $"{NumberToWords(ParseSpeechNumber(match.Groups[1].Value))} to {NumberToWords(ParseSpeechNumber(match.Groups[2].Value))} {UnitToWords(match.Groups[3].Value)}",
            RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(
            cleaned,
            @"\b([0-9][0-9,]*)[\s\u00A0\u202F]*(meters?|metres?|m|feet|foot|ft|°?F|°?C)\b",
            match => $"{NumberToWords(ParseSpeechNumber(match.Groups[1].Value))} {UnitToWords(match.Groups[2].Value)}",
            RegexOptions.IgnoreCase);

        cleaned = Regex.Replace(
            cleaned,
            @"\b([0-9][0-9,]*)\s*-\s*([0-9][0-9,]*)\b",
            match => $"{NumberToWords(ParseSpeechNumber(match.Groups[1].Value))} to {NumberToWords(ParseSpeechNumber(match.Groups[2].Value))}");

        cleaned = Regex.Replace(
            cleaned,
            @"\b([0-9][0-9,]*)\s*/\s*(mo|month)\b",
            match => $"{NumberToWords(ParseSpeechNumber(match.Groups[1].Value))} per month",
            RegexOptions.IgnoreCase);

        return cleaned;
    }

    private static string ClockTimeToWords(string hourText, string minuteText, string meridiemText)
    {
        var hour = int.TryParse(hourText, out var parsedHour) ? parsedHour : 0;
        var minute = int.TryParse(minuteText, out var parsedMinute) ? parsedMinute : 0;
        var meridiem = meridiemText.StartsWith("a", StringComparison.OrdinalIgnoreCase) ? "am" : "pm";

        var hourWords = NumberToWords(hour);
        if (minute == 0)
            return $"{hourWords} {meridiem}";

        var minuteWords = minute < 10
            ? $"oh {NumberToWords(minute)}"
            : NumberToWords(minute);
        return $"{hourWords} {minuteWords} {meridiem}";
    }

    private static string DigitsToWords(string digits)
    {
        return string.Join(" ", digits
            .Where(char.IsDigit)
            .Select(ch => ch == '0' ? "oh" : NumberToWords(ch - '0')));
    }

    private static int ParseSpeechNumber(string value)
    {
        var digits = Regex.Replace(value, "[^0-9]", "");
        return int.TryParse(digits, out var number) ? number : 0;
    }

    private static string ScaledNumberToWords(string value, string suffix)
    {
        var scale = suffix.ToUpperInvariant() switch
        {
            "K" => "thousand",
            "M" => "million",
            "B" => "billion",
            _ => ""
        };

        return string.IsNullOrWhiteSpace(scale)
            ? DecimalNumberToWords(value)
            : $"{DecimalNumberToWords(value)} {scale}";
    }

    private static string DecimalNumberToWords(string value)
    {
        value = value.Replace(",", "").Trim();
        var parts = value.Split('.', 2);
        var whole = int.TryParse(parts[0], out var wholeNumber) ? NumberToWords(wholeNumber) : "zero";
        if (parts.Length == 1 || string.IsNullOrWhiteSpace(parts[1]))
            return whole;

        var decimals = string.Join(" ", parts[1].Select(ch => char.IsDigit(ch) ? NumberToWords(ch - '0') : ""));
        decimals = Regex.Replace(decimals, "\\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(decimals) ? whole : $"{whole} point {decimals}";
    }

    private static string UnitToWords(string unit)
    {
        return unit.Replace("°", "").ToLowerInvariant() switch
        {
            "m" => "meters",
            "meter" => "meters",
            "meters" => "meters",
            "metre" => "meters",
            "metres" => "meters",
            "foot" => "feet",
            "feet" => "feet",
            "ft" => "feet",
            "f" => "degrees Fahrenheit",
            "c" => "degrees Celsius",
            _ => unit
        };
    }

    private static string NumberToWords(int number)
    {
        if (number == 0) return "zero";
        if (number < 0) return "minus " + NumberToWords(Math.Abs(number));

        string[] ones =
        [
            "", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
            "seventeen", "eighteen", "nineteen"
        ];
        string[] tens =
        [
            "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"
        ];

        if (number < 20) return ones[number];
        if (number < 100)
        {
            var remainder = number % 10;
            return remainder == 0 ? tens[number / 10] : $"{tens[number / 10]} {ones[remainder]}";
        }
        if (number < 1000)
        {
            var remainder = number % 100;
            return remainder == 0
                ? $"{ones[number / 100]} hundred"
                : $"{ones[number / 100]} hundred and {NumberToWords(remainder)}";
        }
        if (number < 1_000_000)
        {
            var remainder = number % 1000;
            return remainder == 0
                ? $"{NumberToWords(number / 1000)} thousand"
                : remainder < 100
                    ? $"{NumberToWords(number / 1000)} thousand and {NumberToWords(remainder)}"
                    : $"{NumberToWords(number / 1000)} thousand {NumberToWords(remainder)}";
        }

        var millionRemainder = number % 1_000_000;
        return millionRemainder == 0
            ? $"{NumberToWords(number / 1_000_000)} million"
            : $"{NumberToWords(number / 1_000_000)} million {NumberToWords(millionRemainder)}";
    }

    private static string RemoveCitationArtifacts(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var cleaned = text;

        // Remove source citation artifacts like 【Title†L1-L2】 and (1†L1-L4).
        cleaned = Regex.Replace(cleaned, "【[^】]*†[^】]*】", "");
        cleaned = Regex.Replace(cleaned, "\\([^)]*†[^)]*\\)", "");

        // Remove bracketed source/citation fragments, but keep normal prose in parentheses.
        cleaned = Regex.Replace(cleaned, "\\[(?:\\d+|source|sources|citation|citations|cancelled|[^\\]]*†[^\\]]*)\\]", "", RegexOptions.IgnoreCase);

        // Remove common leftover citation fragments.
        cleaned = Regex.Replace(cleaned, "\\b\\d+†L\\d+(?:-L\\d+)?\\b", "");
        cleaned = Regex.Replace(cleaned, "\\s+([,.!?;:])", "$1");
        cleaned = Regex.Replace(cleaned, "[ \\t]{2,}", " ");

        return cleaned.Trim();
    }

    private sealed record PendingImageAttachment(string Path, string Base64);
    private sealed record PendingDocumentAttachment(string Path, DocumentTextResult Document);
    private sealed record AssistantMessageUi(TextBox Body, StackPanel Content, StackPanel Actions);
}
