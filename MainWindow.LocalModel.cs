using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoiceChatbot;

// The "Built-in model" chat provider: the bundled llama.cpp server with an included or downloaded Gemma 4
// model (LocalModelServer), the model chooser (ModelSetupWindow) and model downloads (ModelDownloads).
public partial class MainWindow
{
    private readonly LocalModelServer _localModel = new();
    private readonly ModelDownloads _modelDownloads = new();
    // Chosen in the model chooser while it was still downloading: switched to when the download finishes.
    private string? _switchToModelWhenDownloaded;
    // The last start failure shown in the chat, so a failure is reported once.
    private string _lastLocalModelFailureShown = "";
    private bool _processorNoticeShown;
    private bool _modelChooserOpen;

    private bool IsBuiltInProvider => ChatProviders.IsBuiltIn(_settings.ChatProvider);

    /// <summary>The chat server's address for messages and the phone remote.</summary>
    private string CurrentChatEndpoint => IsBuiltInProvider
        ? (_localModel.BaseUrl.Length > 0 ? _localModel.BaseUrl : "the built-in model")
        : ChatProviders.IsOpenAiCompatible(_settings.ChatProvider) ? _settings.OpenAiCompatibleUrl : _settings.OllamaUrl;

    private void InitializeLocalModel()
    {
        _localModel.StateChanged += () => Dispatcher.InvokeAsync(OnLocalModelStateChanged);
        _modelDownloads.Changed += () => Dispatcher.InvokeAsync(UpdateModelDownloadUi);
        _modelDownloads.Finished += result => Dispatcher.InvokeAsync(() => OnModelDownloadFinished(result));

        // Chat requests wait for the built-in model to finish loading (and start it again if it stopped).
        _ollama.BeforeChatRequestAsync = async ct =>
        {
            if (!ChatProviders.IsBuiltIn(_settings.ChatProvider))
                return;
            await _localModel.WaitUntilReadyAsync(ct).ConfigureAwait(false);
            var url = _localModel.BaseUrl;
            if (url.Length > 0)
                _ollama.OpenAiBaseUrl = url;
        };
    }

    /// <summary>Points the chat client at the built-in server (an OpenAI-compatible server on 127.0.0.1).</summary>
    private void ConfigureBuiltInChatClient()
    {
        _ollama.Provider = ChatProviders.OpenAiCompatible;
        // Before the first start there is no port yet; requests wait for it (BeforeChatRequestAsync).
        _ollama.OpenAiBaseUrl = _localModel.BaseUrl.Length > 0 ? _localModel.BaseUrl : $"http://{LlamaServerArgs.Host}:1/v1";
        _ollama.OpenAiApiKey = "";
    }

    /// <summary>
    /// Starts the built-in model server with the chosen model when that provider is selected (or restarts
    /// it when the model or context size changed, or with <paramref name="restart"/>), and stops it otherwise
    /// so its video memory is free.
    /// </summary>
    private void EnsureLocalModelRunning(bool restart = false)
    {
        if (!IsBuiltInProvider)
        {
            if (_localModel.State != LocalModelState.Stopped)
                _localModel.Stop();
            UpdateLocalModelUi();
            return;
        }

        var model = LocalModelCatalog.Find(_settings.LocalModelId) ?? LocalModelCatalog.Default;
        var path = FindLocalModelFile(model);
        if (path == null && model.Id != LocalModelCatalog.DefaultId)
        {
            // The chosen model is not on this PC (deleted, or still downloading): use the included one meanwhile.
            var fallback = LocalModelCatalog.Default;
            var fallbackPath = FindLocalModelFile(fallback);
            if (fallbackPath != null)
            {
                AppLog.Info($"Built-in model: {model.Name} is not on this PC; using {fallback.Name} for now.");
                model = fallback;
                path = fallbackPath;
            }
        }

        if (path == null)
        {
            if (_localModel.State != LocalModelState.Stopped)
                _localModel.Stop();
            UpdateLocalModelUi();
            ShowBuiltInConnectionStatus();
            return;
        }

        long bytes = 0;
        try { bytes = new FileInfo(path).Length; } catch (IOException) { }
        var context = LocalModelCatalog.ChooseContext(_settings.ContextWindow, model.MaxContext, VulkanProbe.Gpu, bytes);

        if (!restart && _localModel.ModelPath == path && _localModel.Alias == model.Id && _localModel.ContextTokens == context &&
            _localModel.State != LocalModelState.Stopped)
        {
            UpdateLocalModelUi();
            return; // Already running, starting, or failed with these settings (Restart tries again).
        }

        if (restart)
            _localModel.Stop();
        _lastLocalModelFailureShown = "";
        AppLog.Info($"Built-in model: starting {model.Name} ({path}) with a {context}-token context.");
        _ = _localModel.StartAsync(path, model.Id, context);
        UpdateLocalModelUi();
    }

    private static string? FindLocalModelFile(LocalModelInfo model) =>
        LocalModelCatalog.FindInstalledFile(model, AppPaths.ModelsDirectory, AppContext.BaseDirectory);

    private void OnLocalModelStateChanged()
    {
        UpdateLocalModelUi();
        if (!IsBuiltInProvider)
            return;

        switch (_localModel.State)
        {
            case LocalModelState.Ready:
                ConfigureChatClient();
                ShowBuiltInModelInCombo();
                ScheduleContextWindowStatusRefresh(forceDetect: true);
                if (_localModel.RunningOnProcessor && !_processorNoticeShown)
                {
                    _processorNoticeShown = true;
                    AddSystemMessage($"The built-in model runs on the processor because the graphics card could not hold it, so answers are slower. " +
                                     "A smaller model (Choose AI model...) or a smaller Context window helps.");
                }
                break;
            case LocalModelState.Failed when _localModel.CrashedAfterReady:
                AddSystemMessage($"The built-in model stopped unexpectedly. It starts again with your next message (details: {LocalModelServer.LogFilePath}).");
                break;
            case LocalModelState.Failed:
                var message = _localModel.StatusMessage;
                if (message != _lastLocalModelFailureShown)
                {
                    _lastLocalModelFailureShown = message;
                    AddSystemMessage($"The built-in model could not start: {message}. Try Restart under Chat Backend, a smaller model " +
                                     $"(Choose AI model...), or see {LocalModelServer.LogFilePath}.");
                }
                break;
        }
        ShowBuiltInConnectionStatus();
    }

    private string LocalModelDisplayName => (LocalModelCatalog.Find(_localModel.Alias) ?? LocalModelCatalog.Find(_settings.LocalModelId))?.Name
                                            ?? _settings.LocalModelId;

    private void UpdateLocalModelUi()
    {
        if (LocalModelStatusText == null)
            return;

        var name = LocalModelDisplayName;
        switch (_localModel.State)
        {
            case LocalModelState.Starting:
                SetLocalModelStatus($"{name}: loading...", "WarningBrush");
                break;
            case LocalModelState.Ready:
                var where = _localModel.RunningOnProcessor
                    ? "on the processor"
                    : VulkanProbe.Gpu is { } gpu ? $"on {gpu.Name}" : "ready";
                SetLocalModelStatus($"{name}: ready {where} ({_localModel.ContextTokens / 1024}K context)", "SuccessBrush");
                break;
            case LocalModelState.Failed:
                SetLocalModelStatus(_localModel.CrashedAfterReady
                    ? $"{name}: {_localModel.StatusMessage}"
                    : $"{name}: could not start ({_localModel.StatusMessage})", "ErrorBrush");
                break;
            default:
                if (!IsBuiltInProvider)
                    break;
                if (FindLocalModelFile(LocalModelCatalog.Find(_settings.LocalModelId) ?? LocalModelCatalog.Default) == null &&
                    FindLocalModelFile(LocalModelCatalog.Default) == null)
                    SetLocalModelStatus("No model file on this PC yet. Click Choose AI model... to download one.", "WarningBrush");
                else
                    SetLocalModelStatus($"{name}: not running", "TextSecondaryBrush");
                break;
        }
    }

    private void SetLocalModelStatus(string text, string brushKey)
    {
        LocalModelStatusText.Text = text;
        LocalModelStatusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    /// <summary>The top bar's connection line for the built-in model (it never needs a network check).</summary>
    private void ShowBuiltInConnectionStatus()
    {
        var (text, brush) = _localModel.State switch
        {
            LocalModelState.Ready => ($"Ready ({LocalModelDisplayName})", "SuccessBrush"),
            LocalModelState.Starting => ($"Loading {LocalModelDisplayName}...", "WarningBrush"),
            LocalModelState.Failed => ("Built-in model failed", "ErrorBrush"),
            _ => ("Built-in model not running", "ErrorBrush")
        };
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, brush);
        StatusDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brush);
        UpdateActiveModelText();
    }

    /// <summary>The Model list holds just the built-in model's name.</summary>
    private void ShowBuiltInModelInCombo()
    {
        var alias = _localModel.Alias.Length > 0 ? _localModel.Alias : (LocalModelCatalog.Find(_settings.LocalModelId) ?? LocalModelCatalog.Default).Id;
        ModelCombo.Items.Clear();
        ModelCombo.Items.Add(alias);
        ModelCombo.SelectedItem = alias;
        UpdateActiveModelText();
    }

    /// <summary>
    /// Leaving the built-in model: the Model list shows the saved Ollama/server model again (Refresh Models
    /// loads the rest), not the built-in model's name.
    /// </summary>
    private void ShowSavedModelInCombo()
    {
        var builtInOnly = ModelCombo.Items.Count == 1 && LocalModelCatalog.Find(ModelCombo.Items[0] as string) != null;
        if (!builtInOnly && !(LocalModelCatalog.Find(ModelCombo.Text) != null))
            return;
        ModelCombo.Items.Clear();
        ModelCombo.Text = LocalModelCatalog.Find(_settings.Model) != null ? "" : _settings.Model;
        UpdateActiveModelText();
    }

    /// <summary>Shows the fields of the selected provider only.</summary>
    private void UpdateProviderPanels()
    {
        if (BuiltInModelPanel == null)
            return;
        var provider = GetSelectedProvider();
        var builtIn = ChatProviders.IsBuiltIn(provider);
        var openAi = ChatProviders.IsOpenAiCompatible(provider);
        BuiltInModelPanel.Visibility = builtIn ? Visibility.Visible : Visibility.Collapsed;
        OllamaUrlPanel.Visibility = !builtIn && !openAi ? Visibility.Visible : Visibility.Collapsed;
        OpenAiUrlPanel.Visibility = openAi ? Visibility.Visible : Visibility.Collapsed;
        OpenAiKeyPanel.Visibility = openAi ? Visibility.Visible : Visibility.Collapsed;
        ModelPanel.Visibility = builtIn ? Visibility.Collapsed : Visibility.Visible;
        UpdateModelDownloadUi();
    }

    private void UpdateModelDownloadUi()
    {
        if (ModelDownloadPanel == null)
            return;
        var running = _modelDownloads.Current != null;
        ModelDownloadPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (!running)
            return;
        ModelDownloadBar.Value = _modelDownloads.Progress.Fraction;
        ModelDownloadText.Text = _modelDownloads.Status;
    }

    private void OnModelDownloadFinished(ModelDownloadResult result)
    {
        UpdateModelDownloadUi();
        if (result.Success)
        {
            if (_switchToModelWhenDownloaded == result.Model.Id)
            {
                _switchToModelWhenDownloaded = null;
                ApplyModelChoice(new ModelSetupChoice(ChatProviders.BuiltIn, result.Model.Id));
                AddSystemMessage($"{result.Model.Name} is downloaded and is now the model in use.");
            }
            else if (!_modelChooserOpen)
            {
                AddSystemMessage($"{result.Model.Name} is downloaded. Pick it with Choose AI model... under Chat Backend.");
            }
        }
        else if (!result.Cancelled)
        {
            AddSystemMessage($"{result.Model.Name} could not be downloaded: {result.Error} Open Choose AI model... to try again; it continues where it stopped.");
        }
    }

    // ==================== Model chooser ====================

    private void ChooseModel_Click(object sender, RoutedEventArgs e) => _ = ShowModelChooserAsync(firstRun: false);

    private void RestartLocalModel_Click(object sender, RoutedEventArgs e)
    {
        _processorNoticeShown = false;
        EnsureLocalModelRunning(restart: true);
    }

    private void CancelModelDownload_Click(object sender, RoutedEventArgs e) => _modelDownloads.Cancel();

    /// <summary>Opens the model chooser and applies what was picked. On first start it is shown before anything connects.</summary>
    private async Task ShowModelChooserAsync(bool firstRun)
    {
        // The Vulkan check loads the graphics driver: keep it off the UI thread.
        var gpu = await Task.Run(() => VulkanProbe.Gpu);
        var ram = (long)Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        var window = new ModelSetupWindow(_settings, _modelDownloads, firstRun, gpu, ram) { Owner = this };
        _modelChooserOpen = true;
        try
        {
            window.ShowDialog();
        }
        finally
        {
            _modelChooserOpen = false;
        }

        if (!_settings.ModelSetupDone)
        {
            _settings.ModelSetupDone = true;
            SettingsManager.Save(_settings, userChange: true);
        }

        if (window.Choice is { } choice)
        {
            ApplyModelChoice(choice);
            if (!firstRun && !IsBuiltInProvider)
                await RefreshModelsInternal();
        }
    }

    /// <summary>Switches the Chat Backend to what the model chooser picked and saves it.</summary>
    private void ApplyModelChoice(ModelSetupChoice choice)
    {
        // A newer choice replaces a download that was to be switched to (set again below if still wanted).
        _switchToModelWhenDownloaded = null;
        if (ChatProviders.IsBuiltIn(choice.Provider))
        {
            var model = LocalModelCatalog.Find(choice.LocalModelId) ?? LocalModelCatalog.Default;
            if (FindLocalModelFile(model) != null)
            {
                _settings.LocalModelId = model.Id;
            }
            else
            {
                // Still downloading: keep using what is on this PC until it is done.
                _switchToModelWhenDownloaded = model.Id;
                if (FindLocalModelFile(LocalModelCatalog.Find(_settings.LocalModelId) ?? LocalModelCatalog.Default) == null)
                    _settings.LocalModelId = LocalModelCatalog.DefaultId;
                AddSystemMessage($"{model.Name} is downloading (see Chat Backend). The app switches to it when it is done" +
                                 (FindLocalModelFile(LocalModelCatalog.Default) != null ? $"; until then it uses {LocalModelCatalog.Default.Name}." : "."));
            }
            _settings.ChatProvider = ChatProviders.BuiltIn;
        }
        else if (ChatProviders.IsOpenAiCompatible(choice.Provider))
        {
            // Another server must not get (or wipe) the OpenAI key: keep it aside for the OpenAI card.
            if (OpenAiRequestCompat.IsOpenAiHost(_settings.OpenAiCompatibleUrl) && _settings.OpenAiCompatibleApiKey.Length > 0)
                _settings.OpenAiCloudApiKey = _settings.OpenAiCompatibleApiKey;
            if (OpenAiRequestCompat.IsOpenAiHost(choice.Url))
                _settings.OpenAiCloudApiKey = choice.ApiKey;
            _settings.ChatProvider = ChatProviders.OpenAiCompatible;
            _settings.OpenAiCompatibleUrl = choice.Url;
            _settings.OpenAiCompatibleApiKey = choice.ApiKey;
            if (!string.IsNullOrWhiteSpace(choice.Model))
                _settings.Model = choice.Model;
        }
        else
        {
            _settings.ChatProvider = ChatProviders.Ollama;
            _settings.OllamaUrl = choice.Url;
        }

        // The fields show the new values (their change handlers copy them into the settings too).
        var applying = _applyingSettings;
        _applyingSettings = true;
        try
        {
            OllamaUrlBox.Text = _settings.OllamaUrl;
            OpenAiUrlBox.Text = _settings.OpenAiCompatibleUrl;
            OpenAiApiKeyBox.Password = _settings.OpenAiCompatibleApiKey;
            SelectProviderCombo(_settings.ChatProvider);
            if (!IsBuiltInProvider && !string.IsNullOrWhiteSpace(_settings.Model))
            {
                ModelCombo.Items.Clear();
                ModelCombo.Text = _settings.Model;
            }
        }
        finally
        {
            _applyingSettings = applying;
        }

        SettingsManager.Save(_settings, userChange: true);
        ConfigureChatClient();
        UpdateProviderPanels();
        EnsureLocalModelRunning();
        if (IsBuiltInProvider)
            ShowBuiltInModelInCombo();
        ScheduleContextWindowStatusRefresh(forceDetect: true);
        _ = TestConnection();
        AppLog.Info($"Model chooser: {_settings.ChatProvider}" +
                    (IsBuiltInProvider ? $" ({_settings.LocalModelId})" : $" at {CurrentChatEndpoint}"));
    }
}
