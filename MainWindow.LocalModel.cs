using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VoiceChatbot;

// The "Built-in model" chat provider: the bundled llama.cpp server with an included or downloaded Gemma 4
// model and its picture support (LocalModelServer), the model chooser (ModelSetupWindow) and model
// downloads (ModelDownloads).
public partial class MainWindow
{
    private readonly LocalModelServer _localModel = new();
    private readonly ModelDownloads _modelDownloads = new();
    // Chosen in the model chooser while it was still downloading: switched to when the download finishes.
    private string? _switchToModelWhenDownloaded;
    // The last start failure shown in the chat, so a failure is reported once.
    private string _lastLocalModelFailureShown = "";
    private bool _processorNoticeShown;
    private bool _pictureFailureNoticeShown;
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
    /// Starts the built-in model server with the chosen model, and its picture support when that is on this PC,
    /// when that provider is selected (or restarts it when the model, picture support or context size changed,
    /// or with <paramref name="restart"/>), and stops it otherwise so its video memory is free.
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

        // The picture support file takes video memory next to the model, too.
        var projector = FindLocalProjectorFile(model) ?? "";
        var bytes = FileLength(path) + FileLength(projector);
        var context = LocalModelCatalog.ChooseContext(_settings.ContextWindow, model.MaxContext, VulkanProbe.Gpu, bytes);

        if (!restart && _localModel.ModelPath == path && _localModel.Alias == model.Id && _localModel.ContextTokens == context &&
            _localModel.ProjectorPath == projector && _localModel.State != LocalModelState.Stopped)
        {
            UpdateLocalModelUi();
            return; // Already running, starting, or failed with these settings (Restart tries again).
        }

        if (restart)
            _localModel.Stop();
        _lastLocalModelFailureShown = "";
        AppLog.Info($"Built-in model: starting {model.Name} ({path}) with a {context}-token context, " +
                    (projector.Length > 0 ? $"with picture support ({projector})." : "text only (no picture support on this PC)."));
        _ = _localModel.StartAsync(path, model.Id, context, projector);
        UpdateLocalModelUi();
    }

    private static string? FindLocalModelFile(LocalModelInfo model) =>
        LocalModelCatalog.FindInstalledFile(model, AppPaths.ModelsDirectory, AppContext.BaseDirectory);

    private static string? FindLocalProjectorFile(LocalModelInfo model) =>
        LocalModelCatalog.FindInstalledProjector(model, AppPaths.ModelsDirectory, AppContext.BaseDirectory);

    private static long FileLength(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return 0;
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

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
                if (_localModel.ProjectorPath.Length > 0 && !_localModel.VisionEnabled && !_pictureFailureNoticeShown)
                {
                    _pictureFailureNoticeShown = true;
                    AddSystemMessage("The built-in model started without its picture support, because that file could not be loaded. " +
                                     $"It answers text as usual but can't see pictures right now (details: {LocalModelServer.LogFilePath}).");
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
                var pictures = _localModel.VisionEnabled ? "sees pictures" : "text only";
                SetLocalModelStatus($"{name}: ready {where} ({_localModel.ContextTokens / 1024}K context, {pictures}){PictureSupportHint()}", "SuccessBrush");
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

    /// <summary>Why the running built-in model cannot see pictures and what to do about it, as a sentence to append; "" when it can.</summary>
    private string PictureSupportHint()
    {
        if (_localModel.VisionEnabled)
            return "";
        if (_localModel.ProjectorPath.Length > 0)
            return ". Its picture support could not be loaded.";
        if (LocalModelCatalog.Find(_localModel.Alias) is not { } model)
            return "";
        return FindLocalProjectorFile(model) != null
            ? ". Picture support was just added: click Restart to load it."
            : ". To let it see pictures, add picture support with Choose AI model...";
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
        var name = result.Model.Name;
        // The model file can be complete even when its picture support is not (failed or paused after it).
        var modelOnPc = FindLocalModelFile(result.Model) != null;
        var switched = false;
        if (modelOnPc && _switchToModelWhenDownloaded == result.Model.Id)
        {
            _switchToModelWhenDownloaded = null;
            ApplyModelChoice(new ModelSetupChoice(ChatProviders.BuiltIn, result.Model.Id));
            switched = true;
        }
        else if (result.Success && _chatCts == null && !_phoneOwnsBusyState && !_schedulerRunning && SendBtn?.IsEnabled == true)
        {
            // New picture support for the model in use: restart it with pictures (no change otherwise). During
            // an answer or a scheduled task it waits: the next picture sent, or Restart, loads it.
            EnsureLocalModelRunning();
        }
        else
        {
            // The Built-in model line then says to click Restart when new picture support waits to be loaded.
            UpdateLocalModelUi();
        }

        if (result.Success)
        {
            if (switched)
                AddSystemMessage($"{name} is downloaded and is now the model in use.");
            else if (_modelChooserOpen)
                return;
            else if (result.ProjectorOnly)
                AddSystemMessage($"Picture support for {name} is installed, so it can look at pictures you attach.");
            else
                AddSystemMessage($"{name} is downloaded. Pick it with Choose AI model... under Chat Backend.");
        }
        else if (modelOnPc && !result.ProjectorOnly)
        {
            // The model arrived; only its picture support did not.
            var model = switched ? $"{name} is downloaded and is now the model in use" : $"{name} is downloaded";
            AddSystemMessage(result.Cancelled
                ? $"{model}. Its picture support download is paused: add it with Choose AI model... (it continues where it stopped)."
                : $"{model}, but its picture support could not be downloaded: {result.Error} Add it with Choose AI model...; it continues where it stopped.");
        }
        else if (!result.Cancelled)
        {
            AddSystemMessage(result.ProjectorOnly
                ? $"Picture support for {name} could not be downloaded: {result.Error} Open Choose AI model... to try again; it continues where it stopped."
                : $"{name} could not be downloaded: {result.Error} Open Choose AI model... to try again; it continues where it stopped.");
        }
    }

    /// <summary>
    /// Before a request with pictures to the built-in model: makes sure it runs with its picture support when
    /// that is on this PC, and when it still cannot see pictures, leaves them out of <paramref name="messages"/>
    /// (with a note for the model, so it does not answer as if it saw them). When it can, pictures it cannot
    /// read as they are (WebP, HEIC, AVIF, TIFF) are converted to JPEG, and one Windows cannot open either is
    /// left out. Returns the chat note that says why, or null when nothing was left out. Call on the UI thread.
    /// </summary>
    private async Task<string?> LeaveOutPicturesTheModelCannotSeeAsync(List<ChatMessage> messages, CancellationToken ct)
    {
        if (!IsBuiltInProvider || !messages.Any(m => m.ImagesBase64.Count > 0))
            return null;

        // Picks up picture support added since the model started (a restart when it is new).
        EnsureLocalModelRunning();
        try
        {
            await _localModel.WaitUntilReadyAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // The request itself reports why the model did not start.
        }
        if (_localModel.VisionEnabled)
            return await ConvertPicturesForBuiltInModelAsync(messages, ct);

        var note = _localModel.ProjectorPath.Length == 0
            ? FriendlyErrors.BuiltInModelCannotSeePictures
            : $"The built-in model can't see pictures right now: its picture support could not be loaded (details: {LocalModelServer.LogFilePath}).";

        var total = messages.Sum(m => m.ImagesBase64.Count);
        for (var i = 0; i < messages.Count; i++)
        {
            var count = messages[i].ImagesBase64.Count;
            if (count == 0)
                continue;
            messages[i] = WithPictures(messages[i], new List<string>(),
                $"({(count == 1 ? "A picture was" : $"{count} pictures were")} attached, but you cannot see pictures right now, " +
                $"so {(count == 1 ? "it was" : "they were")} left out.)");
        }
        AppLog.Info("Built-in model: pictures left out of a request (" + (_localModel.ProjectorPath.Length == 0 ? "no picture support on this PC" : "picture support did not load") + ").");
        return note + (total == 1 ? " This message was sent without the picture." : $" This message was sent without its {total} pictures.");
    }

    /// <summary>
    /// llama-server reads JPEG, PNG, GIF and BMP pictures only: converts the others in <paramref name="messages"/>
    /// (WebP, HEIC, AVIF, TIFF) to JPEG, and leaves out one Windows cannot open either (with a note for the
    /// model). Returns the chat note for left-out pictures, or null when none were.
    /// </summary>
    private static async Task<string?> ConvertPicturesForBuiltInModelAsync(List<ChatMessage> messages, CancellationToken ct)
    {
        if (messages.All(m => m.ImagesBase64.All(PictureFormats.BuiltInModelReadsAsIs)))
            return null;

        // Decoding a big photo takes a moment: keep it off the UI thread.
        var converted = await Task.Run(() => messages
            .Select(m => m.ImagesBase64.Select(ToPictureTheBuiltInModelReads).ToList())
            .ToList(), ct);

        var total = 0;
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].ImagesBase64.Count == 0)
                continue;
            var readable = converted[i].OfType<string>().ToList();
            var leftOut = converted[i].Count - readable.Count;
            total += leftOut;
            messages[i] = WithPictures(messages[i], readable, leftOut == 0
                ? null
                : $"({(leftOut == 1 ? "A picture was" : $"{leftOut} pictures were")} attached, but {(leftOut == 1 ? "it" : "they")} could not be opened, " +
                  $"so {(leftOut == 1 ? "it was" : "they were")} left out.)");
        }
        if (total == 0)
            return null;
        const string extensions = "(WebP and HEIC pictures need their image extension from the Microsoft Store)";
        return total == 1
            ? $"A picture could not be sent to the built-in model: Windows can't open it {extensions}. This message was sent without it; save it as PNG or JPEG to send it."
            : $"{total} pictures could not be sent to the built-in model: Windows can't open them {extensions}. This message was sent without them; save them as PNG or JPEG to send them.";
    }

    /// <summary>
    /// A picture (base64) as the built-in model reads it: a JPEG, PNG, GIF or BMP file as it is, anything else
    /// Windows can open (WebP, HEIC, AVIF, TIFF) as a JPEG. Null when Windows cannot open it either.
    /// </summary>
    private static string? ToPictureTheBuiltInModelReads(string base64)
    {
        if (PictureFormats.BuiltInModelReadsAsIs(base64))
            return base64;
        try
        {
            using var input = new MemoryStream(Convert.FromBase64String(base64));
            var frame = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];

            // JPEG has no transparency: transparent parts become white.
            var pbgra = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            var stride = pbgra.PixelWidth * 4;
            var pixels = new byte[checked(stride * pbgra.PixelHeight)];
            pbgra.CopyPixels(pixels, stride, 0);
            PictureFormats.FlattenOntoWhite(pixels);
            var flat = BitmapSource.Create(pbgra.PixelWidth, pbgra.PixelHeight, 96, 96, PixelFormats.Bgr32, null, pixels, stride);

            var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
            encoder.Frames.Add(BitmapFrame.Create(flat));
            using var output = new MemoryStream();
            encoder.Save(output);
            return Convert.ToBase64String(output.ToArray());
        }
        catch (Exception ex)
        {
            AppLog.Warn("Built-in model: an attached picture could not be converted to JPEG, so it is left out", ex);
            return null;
        }
    }

    /// <summary>A copy of <paramref name="m"/> with these pictures, and <paramref name="note"/> for the model after its text.</summary>
    private static ChatMessage WithPictures(ChatMessage m, List<string> pictures, string? note) => new()
    {
        Role = m.Role,
        Content = note == null ? m.Content : $"{m.Content}\n\n{note}",
        ImagesBase64 = pictures,
        Timestamp = m.Timestamp,
        ToolCalls = m.ToolCalls,
        ToolCallId = m.ToolCallId,
        ToolName = m.ToolName
    };

    // ==================== Model chooser ====================

    private void ChooseModel_Click(object sender, RoutedEventArgs e) => _ = ShowModelChooserAsync(firstRun: false);

    private void RestartLocalModel_Click(object sender, RoutedEventArgs e)
    {
        _processorNoticeShown = false;
        _pictureFailureNoticeShown = false;
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
