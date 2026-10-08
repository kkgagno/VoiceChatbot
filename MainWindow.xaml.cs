using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    private const int CodeContextTokens = 131072;
    private const int CodeContinuationMaxAttempts = 6;
    private const int LargePasteChars = 24000;
    private const int MaxCurrentModelInputChars = 300000;
    private const int EstimatedCharsPerToken = 4;
    private const int ContextSafetyTokens = 4096;
    private static readonly string SyncedVideoDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "VoiceChatbot",
        "videos");
    private OllamaClient _ollama;
    private HermesSshClient _hermesSsh;
    // The one staged "Hermes run ..." command (expires after two minutes), for the desktop and phone paths.
    private readonly HermesApprovalGate _hermesApprovals = new();
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
    private string _desktopRunningModel = "";
    // True while startup pushes saved settings into the UI. Selection-changed handlers call
    // SaveSettings, which would otherwise copy half-initialized controls back over the saved values.
    private bool _applyingSettings;

    private double ChatContentWidth => Math.Max(360, ChatScroll.ActualWidth - 64);
    private double UserBubbleMaxWidth => Math.Clamp(ChatContentWidth * 0.78, 500, 1400);
    private double AssistantBubbleMaxWidth => Math.Clamp(ChatContentWidth * 0.88, 600, 1800);
    private double CodeBlockMaxWidth => Math.Clamp(ChatContentWidth * 0.86, 560, 1760);

    public MainWindow()
    {
        InitializeComponent();
        WindowTheme.UseThemedTitleBar(this);
        _settings = App.TakeStartupSettings() ?? SettingsManager.Load();
        _schedulerStore = SchedulerStore.Load();
        _history = new ConversationHistory();
        _history.MessageAdded += OnHistoryMessageAdded;
        _ollama = new OllamaClient(_settings.OllamaUrl);
        _hermesSsh = new HermesSshClient();
        _piAgent = new PiAgentService(GetDefaultPiWorkingDirectory());
        _youtubeTranscripts = new YouTubeTranscriptService();
        _documentText = new DocumentTextService();
        ConfigureChatClient();
        _tavily = new TavilySearchClient(_settings.TavilyApiKey);
        _tavily.SearchFailed += ShowWebSearchFailure;
        _comfyImages = new ComfyUiImageClient { BaseUrl = _settings.ComfyUiUrl };
        _speech = new SpeechEngine();
        _camera = new CameraService();
        _phoneRemoteServer = new PhoneRemoteServer(
            (stream, ct) => _speech.TranscribeWavAsync(stream, ct),
            HandlePhoneRemoteChatAsync,
            (path, ct) => _documentText.ExtractAsync(path, ct),
            GetPhoneRemoteModelState);
        _faceIdentityManager = new FaceIdentityManager(
            FaceServiceFactory.CreateProfileStore(_settings.FaceFeatures.ModelOptions),
            _settings.FaceFeatures.ModelOptions);

        WireMessageInput();
        Loaded += MainWindow_Loaded;
        SizeChanged += (_, _) => UpdateChatBubbleWidths();
    }

    private void WireMessageInput()
    {
        // PreviewKeyDown, because a TextBox with AcceptsReturn handles Enter itself and swallows
        // KeyDown before our handler sees it. Enter and Shift+Enter send; Ctrl+Enter adds a line.
        MessageInput.PreviewKeyDown += (s, ev) =>
        {
            if (ev.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && TryAttachClipboardImage())
            {
                ev.Handled = true;
                return;
            }

            if (ev.Key != Key.Enter && ev.Key != Key.Return)
                return;

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                var caret = MessageInput.CaretIndex;
                MessageInput.SelectedText = Environment.NewLine;
                MessageInput.CaretIndex = caret + Environment.NewLine.Length;
                MessageInput.SelectionLength = 0;
            }
            else if (SendBtn.IsEnabled)
            {
                SendText_Click(MessageInput, ev);
            }

            ev.Handled = true;
        };
        MessageInput.KeyDown += (s, ev) => PauseListeningForTextInput();
        DataObject.AddPastingHandler(MessageInput, MessageInput_Pasting);
        MessageInput.PreviewMouseDown += (s, ev) => PauseListeningForTextInput();
        MessageInput.GotKeyboardFocus += (s, ev) => PauseListeningForTextInput();
        MessageInput.LostKeyboardFocus += (s, ev) => ResumeListeningAfterTextInput();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _applyingSettings = true;
            ApplySettings();
            SetupSliderBindings();
            UpdateSliderValueLabels();
            SetupTimers();

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
            StartWakeWordDetectorIfEnabled();

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
            else if (!string.IsNullOrEmpty(_settings.Model))
                ModelCombo.Text = _settings.Model; // Backend offline: keep showing (and saving) the saved model.

            _applyingSettings = false;
            _schedulerTimer.Start();
            UpdateActiveModelText();
            await InitializeDesktopServiceControlsAsync();

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
            _applyingSettings = false;
            _schedulerTimer?.Start();
            AddSystemMessage($"Startup error: {ex}");
            System.Diagnostics.Debug.WriteLine($"Startup error: {ex}");
        }
    }

    // ==================== Settings ====================

    private void ApplySettings()
    {
        OllamaUrlBox.Text = _settings.OllamaUrl;
        OpenAiUrlBox.Text = _settings.OpenAiCompatibleUrl;
        OpenAiApiKeyBox.Password = _settings.OpenAiCompatibleApiKey;
        HermesSshHostBox.Text = _settings.HermesSshHost;
        HermesSshPortBox.Text = _settings.HermesSshPort.ToString();
        HermesSshUserBox.Text = _settings.HermesSshUser;
        HermesSshPasswordBox.Password = _settings.HermesSshPassword;
        SelectProviderCombo(_settings.ChatProvider);
        SystemPromptBox.Text = _settings.SystemPrompt;
        RunOneTimeSettingsMigrations();
        TempSlider.Value = _settings.Temperature;
        MaxTokensBox.Text = _settings.MaxTokens.ToString();
        ContextWindowBox.Text = _settings.ContextWindow.ToString();
        ApplyToolSettings();
        ApplyMarkdownSettings();
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
        ApplyStreamingSpeechSettings();
        ApplyBargeInSettings();
        ApplyWhisperGpuSettings();
        ApplyWakeWordSettings();
        KokoroHostBox.Text = _settings.KokoroRemoteUrl;
        SelectKokoroModeCombo(_settings.KokoroMode);
        UpdateKokoroHint();
        ContextSlider.Value = _settings.MaxContextMessages;
        StreamToggle.IsChecked = _settings.StreamResponses;
        ApplyPersonaSettings();
        ApplyKnowledgeSettings();
        ApplyMemoryPromptSettings();
        ApplyConversationHistorySettings();
        WebSearchToggle.IsChecked = _settings.WebSearchEnabled;
        TavilyApiKeyBox.Password = _settings.TavilyApiKey;
        ComfyUrlBox.Text = _settings.ComfyUiUrl;
        ImageWidthBox.Text = _settings.ImageWidth.ToString();
        ImageHeightBox.Text = _settings.ImageHeight.ToString();
        QwenCreateStepsBox.Text = _settings.QwenCreateSteps.ToString();
        QwenEditStepsBox.Text = _settings.QwenEditSteps.ToString();
        VideoSecondsBox.Text = _settings.VideoSeconds.ToString();
        VideoFpsBox.Text = _settings.VideoFps.ToString();
        FaceFeaturesToggle.IsChecked = _settings.FaceFeatures.CameraFeaturesEnabled;
        FaceGatingToggle.IsChecked = _settings.FaceFeatures.FaceGatingEnabled;
        FacePolicyText.Text = _settings.FaceFeatures.FaceGatingEnabled ? "Face gating enabled" : "Face gating disabled";
        PhoneRemoteToggle.IsChecked = _settings.PhoneRemote.Enabled;
        PhoneRemotePortBox.Text = _settings.PhoneRemote.Port.ToString();
        PhoneRemotePinBox.Text = _settings.PhoneRemote.Pin;
        PhoneRemoteAudioToggle.IsChecked = _settings.PhoneRemote.PlayAudioOnPhone;
        ApplyTrayAndHotkeySettings();
        ApplyThemeSetting();
        ApplyPhoneRemoteSecuritySettings();
        ApplySecretsAndLogsUi();
        ApplySshHostKeySettings();
        UpdatePhoneRemoteUi();
        UpdateFacePresenceUi(_settings.FaceFeatures.CameraFeaturesEnabled
            ? FacePresenceState.CameraUnavailable
            : FacePresenceState.CameraUnavailable);

        _history.MaxMessages = _settings.MaxContextMessages;

        // Window position: negative values are valid on monitors left of or above the primary one.
        if (IsSavedWindowPositionVisible(_settings.WindowLeft, _settings.WindowTop, _settings.WindowWidth))
        {
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;
        if (_settings.WindowMaximized)
            WindowState = WindowState.Maximized;

        SetSidebarVisible(_settings.SidebarVisible);
    }

    /// <summary>
    /// Fixes older versions applied on every launch; they now run once per settings file and the
    /// version is saved with the settings, so later user choices (for example silence below 2 s) stick.
    /// </summary>
    private void RunOneTimeSettingsMigrations()
    {
        if (_settings.SettingsVersion >= AppSettings.CurrentSettingsVersion)
            return;

        if (_settings.SettingsVersion < 1)
        {
            if (_settings.SilenceTimeout < 2.0)
                _settings.SilenceTimeout = 2.4;
            if (_settings.QwenEditSteps <= 4)
                _settings.QwenEditSteps = 40;
            if (_settings.ImageWidth == 1328 && _settings.ImageHeight == 1328)
            {
                _settings.ImageWidth = AppSettings.DefaultImageWidth;
                _settings.ImageHeight = AppSettings.DefaultImageHeight;
            }
        }

        _settings.SettingsVersion = AppSettings.CurrentSettingsVersion;
    }

    /// <summary>
    /// True when a saved position (not the -1/-1 "never saved" default) puts the title bar on the
    /// connected monitors, including monitors at negative coordinates.
    /// </summary>
    private static bool IsSavedWindowPositionVisible(double left, double top, double width)
    {
        if (double.IsNaN(left) || double.IsNaN(top) || double.IsInfinity(left) || double.IsInfinity(top))
            return false;
        if (left == -1 && top == -1)
            return false;

        const double visibleTitleBar = 100;
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
        var right = left + Math.Max(width, visibleTitleBar);
        return right - visibleTitleBar >= screenLeft &&
               left + visibleTitleBar <= screenRight &&
               top >= screenTop - 8 &&
               top + 30 <= screenBottom;
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
            FaceProfileCombo.Items.Add(new ComboBoxItem { Content = name });
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

    /// <param name="userChange">False when saving on exit, so an unreadable settings.json the user has not
    /// replaced yet is kept (see SettingsManager.Save).</param>
    private void SaveSettings(bool userChange = true)
    {
        if (_applyingSettings)
            return;

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
        // With the backend offline at launch the list is empty: keep the saved model instead of saving "".
        if (!string.IsNullOrWhiteSpace(ModelCombo.Text) || ModelCombo.Items.Count > 0)
            _settings.Model = ModelCombo.Text;
        _settings.SystemPrompt = SystemPromptBox.Text;
        _settings.Temperature = TempSlider.Value;
        ReadTokenBudgetFields();
        SaveToolSettings();
        SaveMarkdownSettings();
        _settings.InputLanguage = InputLangCombo.Text;
        _settings.SilenceTimeout = SilenceSlider.Value;
        _settings.NoiseSuppression = (int)NoiseSlider.Value;
        _settings.AutoDetectVoice = AutoDetectToggle.IsChecked == true;
        _settings.WakeWord = WakeWordBox.Text;
        _settings.MicDeviceIndex = MicCombo.SelectedItem is AudioDeviceInfo mic ? mic.Index : -1;
        _settings.TranscriptionBackend = GetSelectedTranscriptionBackend();
        var npuCommand = NpuTranscriberCommandBox.Text.Trim();
        _settings.ExternalNpuTranscriberCommand = AppSettings.IsUsableTranscriberCommand(npuCommand)
            ? npuCommand
            : AppSettings.DefaultRyzenAiWhisperCommand;
        _settings.VoiceName = VoiceCombo.Text;
        _settings.SpeechRate = (int)RateSlider.Value;
        _settings.Volume = (int)VolumeSlider.Value;
        _settings.TtsEnabled = TtsToggle.IsChecked == true;
        SaveStreamingSpeechSettings();
        SaveBargeInSettings();
        SaveWhisperGpuSettings();
        SaveWakeWordSettings();
        _settings.KokoroRemoteUrl = KokoroHostBox.Text.Trim();
        _settings.KokoroMode = GetSelectedKokoroMode();
        _settings.MaxContextMessages = (int)ContextSlider.Value;
        _settings.StreamResponses = StreamToggle.IsChecked == true;
        SavePersonaSettings();
        SaveKnowledgeSettings();
        SaveMemoryPromptSettings();
        SaveConversationHistorySettings();
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
        SaveTrayAndHotkeySettings();
        SavePhoneRemoteSecuritySettings();
        SaveSshHostKeySettings();
        // Remember the restored size/position even when closing maximized.
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
        }
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (SidebarPanel.Visibility == Visibility.Visible)
            _settings.SidebarWidth = SidebarColumn.ActualWidth;

        SettingsManager.Save(_settings, userChange);
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

    /// <summary>Reads the Max reply tokens and Context window fields into the settings.</summary>
    private void ReadTokenBudgetFields()
    {
        _settings.MaxTokens = ParseBoundedInt(MaxTokensBox.Text, _settings.MaxTokens, TokenBudget.MinMaxTokens, TokenBudget.ArtifactMaxTokens);
        var contextWindow = ParseBoundedInt(ContextWindowBox.Text, _settings.ContextWindow, 0, 1_048_576);
        _settings.ContextWindow = contextWindow == 0 ? 0 : Math.Max(TokenBudget.MinContextWindow, contextWindow);
        MaxTokensBox.Text = _settings.MaxTokens.ToString();
        ContextWindowBox.Text = _settings.ContextWindow.ToString();
    }

    private void TokenBudgetBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
            return;

        ReadTokenBudgetFields();
        SaveSettings();
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

    /// <summary>Moves voice input to Whisper.net for good when the Ryzen AI command does not exist.</summary>
    private void SwitchToWhisperNet(string reason)
    {
        _speech.TranscriptionBackend = "Whisper.net";
        _settings.TranscriptionBackend = "Whisper.net";
        SelectTranscriptionBackendCombo("Whisper.net");
        SaveSettings();
        AddSystemMessage($"{reason}, so voice input now uses Whisper.net. You can choose AMD Ryzen AI Whisper again under Voice Input once it is installed.");
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
        _speech.RyzenTranscriberUnavailable += reason => Dispatcher.BeginInvoke(() =>
            SwitchToWhisperNet($"AMD Ryzen AI Whisper isn't set up on this PC ({reason.Trim()})"));
        if (_settings.TranscriptionBackend.Contains("Ryzen", StringComparison.OrdinalIgnoreCase) &&
            TranscriberCommand.GetProgramPath(_settings.ExternalNpuTranscriberCommand) is { } program &&
            !File.Exists(Environment.ExpandEnvironmentVariables(program)))
        {
            Dispatcher.BeginInvoke(() =>
                SwitchToWhisperNet($"AMD Ryzen AI Whisper isn't set up on this PC ({Environment.ExpandEnvironmentVariables(program)} was not found)"));
        }
        _speech.VoiceName = _settings.VoiceName;
        _speech.SpeechRate = _settings.SpeechRate;
        _speech.Volume = _settings.Volume;
        _speech.TtsEnabled = _settings.TtsEnabled;
        _speech.KokoroRemoteUrl = _settings.KokoroRemoteUrl;
        _speech.KokoroMode = KokoroEndpoint.NormalizeMode(_settings.KokoroMode);
        _speech.TtsBackendUsed += backend => Dispatcher.BeginInvoke(() => UpdateTtsStatus(backend, ok: true));

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
        if (!string.IsNullOrEmpty(_settings.VoiceName) && !VoiceCombo.Items.Contains(_settings.VoiceName))
            VoiceCombo.Items.Add(_settings.VoiceName);
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

    /// <summary>Shows the applied (saved) slider values; the ValueChanged handlers only run on later changes.</summary>
    private void UpdateSliderValueLabels()
    {
        TempValue.Text = TempSlider.Value.ToString("F1");
        SilenceValue.Text = SilenceSlider.Value.ToString("F1");
        NoiseValue.Text = $"{(int)NoiseSlider.Value}%";
        RateValue.Text = $"{(int)RateSlider.Value:+#;-#;0}";
        VolumeValue.Text = $"{(int)VolumeSlider.Value}%";
        ContextValue.Text = ((int)ContextSlider.Value).ToString();
    }

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

        KokoroHostBox.TextChanged += (s, e) =>
        {
            _settings.KokoroRemoteUrl = KokoroHostBox.Text.Trim();
            _speech.KokoroRemoteUrl = _settings.KokoroRemoteUrl;
            _speech.ResetRemoteKokoroBackoff();
            UpdateKokoroHint();
        };

        ModelCombo.SelectionChanged += (s, e) => Dispatcher.BeginInvoke(UpdateActiveModelText, DispatcherPriority.Background);
        ModelCombo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((s, e) => UpdateActiveModelText()));

        // Wake word change
        WakeWordBox.TextChanged += (s, e) => { _speech.WakeWord = WakeWordBox.Text; };

        TranscriptionBackendCombo.SelectionChanged += (s, e) =>
        {
            _speech.TranscriptionBackend = GetSelectedTranscriptionBackend();
        };
        NpuTranscriberCommandBox.TextChanged += (s, e) =>
        {
            var npuCommand = NpuTranscriberCommandBox.Text.Trim();
            _settings.ExternalNpuTranscriberCommand = AppSettings.IsUsableTranscriberCommand(npuCommand)
                ? npuCommand
                : AppSettings.DefaultRyzenAiWhisperCommand;
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
        InputLangCombo.SelectionChanged += async (s, e) =>
        {
            // Text still holds the previous choice while SelectionChanged runs.
            var language = InputLangCombo.SelectedItem?.ToString() ?? InputLangCombo.Text;
            try
            {
                await _speech.SetInputLanguageAsync(language);
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Could not change the recognition language: {ex.Message}");
            }
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

        // Started by MainWindow_Loaded once settings and the model list are loaded.
        _schedulerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _schedulerTimer.Tick += async (_, _) =>
        {
            try
            {
                await RunDueScheduledTasksAsync();
            }
            catch (Exception ex)
            {
                AppLog.Error("Scheduler tick failed", ex);
            }
        };

        // Wire volume level
        _speech.VolumeLevelChanged += (level) =>
        {
            Dispatcher.Invoke(() => VolumeMeter.Value = level, DispatcherPriority.Background);
        };
    }

    // ==================== Window Events ====================

    private bool _shutdownComplete;
    private bool _shutdownStarted;

    private async void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete)
            return;

        // Stopping the phone server and camera is async. Blocking the UI thread on it deadlocks
        // (their continuations need this thread), which is what hung the app when the phone
        // remote was still running. Cancel this close, shut down asynchronously, then close again.
        e.Cancel = true;
        if (_shutdownStarted)
            return;

        _shutdownStarted = true;
        AppLog.Info("Main window closing. Stopping services.");
        // First, so the hotkey cannot start listening and the tray icon goes away while services stop.
        DisposeTrayAndHotkey();
        IsEnabled = false;
        StateLabel.Text = "Closing...";

        try
        {
            SaveSettings(userChange: false);
            _schedulerTimer?.Stop();
            _schedulerStore.Save();
            _chatCts?.Cancel();
            DisposeWakeWordDetector();
            CancelKnowledgeIndexing();
            await FlushConversationHistoryAsync(TimeSpan.FromSeconds(3));

            var shutdown = Task.WhenAll(
                StopFacePresenceAsync(),
                _phoneRemoteServer.DisposeAsync().AsTask());
            // Never let a stuck service keep the window open.
            if (await Task.WhenAny(shutdown, Task.Delay(TimeSpan.FromSeconds(6))) != shutdown)
                AppLog.Warn("Services did not stop within 6 seconds. Closing anyway.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Shutdown error: {ex.Message}");
            AppLog.Warn("Shutdown error", ex);
        }

        try { _speech.Dispose(); } catch { }
        try { _camera.Dispose(); } catch { }
        try { _ollama.Dispose(); } catch { }
        try { _tavily.Dispose(); } catch { }
        try { DisposeToolServices(); } catch { }
        try { _comfyImages.Dispose(); } catch { }

        _shutdownComplete = true;
        await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
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
        HideToTrayIfMinimized();
    }

    /// <summary>
    /// Removes noise (emojis, hashtags, citation artifacts, stray tags such as &lt;unused49&gt;) from a
    /// reply while keeping its Markdown: replies are rendered as Markdown on screen, and CleanSpeechText
    /// strips the syntax before speaking. Code is left as written.
    /// </summary>
    private static string CleanDisplayText(string text, bool preserveCodeBlocks = false)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Reasoning models: <think>...</think> is neither shown, saved nor spoken.
        text = ReasoningText.StripThinking(text);
        // Roleplay narration such as "(The AI responds warmly.)" is neither shown nor spoken.
        text = StageDirections.Strip(text);
        if (string.IsNullOrWhiteSpace(text)) return "";
        if (LooksLikeOnlyUnusedTokens(text))
            return "The model returned only special placeholder tokens, such as <unused49>. That usually means the llama.cpp server was launched with the wrong Gemma chat template or an incompatible/missing mmproj projector. Restart the Gemma 4 12B server with the correct Gemma template and matching mmproj, then try the image again.";
        if (LooksLikeLeakedReasoningDump(text))
            return "The model returned internal reasoning instead of a normal answer. This usually means the selected llama.cpp model/server template is misconfigured or using an incompatible reasoning/chat format. Try switching models or restarting the server with the correct chat template.";

        if (preserveCodeBlocks)
            return RemoveCitationArtifacts(text).Trim();

        return MarkdownText.CleanForDisplay(RemoveCitationArtifacts(text));
    }

    private static bool LooksLikeOnlyUnusedTokens(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var withoutUnusedTokens = Regex.Replace(text, @"<unused\d+>", "", RegexOptions.IgnoreCase);
        withoutUnusedTokens = Regex.Replace(withoutUnusedTokens, @"\s+", "");
        return withoutUnusedTokens.Length == 0 && Regex.IsMatch(text, @"<unused\d+>", RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeLeakedReasoningDump(string text) => ReasoningText.LooksLikeLeakedReasoning(text);

    /// <summary>
    /// Removes citation/bracket artifacts and URLs before TTS, without changing chat display text.
    /// </summary>
    private static string CleanSpeechText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        text = StageDirections.Strip(ReasoningText.StripThinking(text));

        var firstCodeBlock = Regex.Match(text, "```[\\s\\S]*?```");
        var cleaned = firstCodeBlock.Success
            ? text[..firstCodeBlock.Index]
            : text;

        cleaned = RemoveCitationArtifacts(cleaned);

        // Replies keep their Markdown for display; none of it should be read aloud.
        cleaned = MarkdownText.StripForSpeech(cleaned);

        // Avoid reading raw URLs aloud.
        cleaned = Regex.Replace(cleaned, "https?://\\S+", "");

        // Remove common leftover citation fragments.
        cleaned = Regex.Replace(cleaned, "\\b\\d+†L\\d+(?:-L\\d+)?\\b", "");

        cleaned = NormalizeSpeechNumbers(cleaned);

        cleaned = Regex.Replace(cleaned, "\\s+([,.!?;:])", "$1");
        cleaned = Regex.Replace(cleaned, "\\n{3,}", "\n\n");
        cleaned = Regex.Replace(cleaned, "[ \\t]{2,}", " ");

        return cleaned.Trim();
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

    /// <summary>
    /// Removes citation artifacts such as [1] or 【Title†L1-L2】 from the prose. Code blocks and inline
    /// code are left alone, so code such as items[0] survives.
    /// </summary>
    private static string RemoveCitationArtifacts(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        return MarkdownText.MapProseOutsideCode(text, RemoveCitationArtifactsFromProse).Trim();
    }

    private static string RemoveCitationArtifactsFromProse(string text)
    {
        var cleaned = text;

        // Remove source citation artifacts like 【Title†L1-L2】 and (1†L1-L4).
        cleaned = Regex.Replace(cleaned, "【[^】]*†[^】]*】", "");
        cleaned = Regex.Replace(cleaned, "\\([^)]*†[^)]*\\)", "");

        // Remove bracketed source/citation fragments, but keep normal prose in parentheses.
        cleaned = Regex.Replace(cleaned, "\\[(?:\\d+|source|sources|citation|citations|cancelled|[^\\]]*†[^\\]]*)\\]", "", RegexOptions.IgnoreCase);

        // Remove common leftover citation fragments.
        cleaned = Regex.Replace(cleaned, "\\b\\d+†L\\d+(?:-L\\d+)?\\b", "");
        // Only spaces inside a line: indentation (nested Markdown lists, code) and line breaks stay.
        cleaned = Regex.Replace(cleaned, "(?<=\\S)[ \\t]+([,.!?;:])", "$1");
        cleaned = Regex.Replace(cleaned, "(?<=\\S)[ \\t]{2,}", " ");

        return cleaned;
    }

    private sealed record PendingImageAttachment(string Path, string Base64);
    private sealed record PendingDocumentAttachment(string Path, DocumentTextResult Document);
    private sealed record AssistantMessageUi(TextBox Body, StackPanel Content, Panel Actions);
}
