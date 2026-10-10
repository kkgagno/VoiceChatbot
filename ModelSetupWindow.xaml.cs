using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace VoiceChatbot;

/// <summary>What the model chooser decided. <see cref="LocalModelId"/> is set for the built-in provider.</summary>
public sealed record ModelSetupChoice(string Provider, string LocalModelId = "", string Url = "", string ApiKey = "", string Model = "");

/// <summary>
/// The model chooser, shown on first start and from Chat Backend's Choose AI model button: the Gemma 4
/// models the app can run itself (included or downloadable, with the video memory and graphics card each
/// needs and how it suits this PC), Ollama, an OpenAI-compatible server such as llama.cpp, or OpenAI.
/// </summary>
public partial class ModelSetupWindow : Window
{
    public const string DefaultOpenAiModel = "gpt-5-mini";

    private const string KeyOllama = "ollama";
    private const string KeyServer = "server";
    private const string KeyOpenAi = "openai";

    private static readonly Regex Url = new(@"https?://[^\s)]+", RegexOptions.CultureInvariant);

    private readonly AppSettings _settings;
    private readonly ModelDownloads _downloads;
    private readonly GpuInfo? _gpu;
    private readonly long _ramBytes;
    private readonly List<Option> _options = new();
    private readonly CancellationTokenSource _lookupCts = new();
    private Option? _selected;
    // The local model this window started downloading; the window closes with it chosen when it finishes.
    private string? _downloadingId;

    private sealed class Option
    {
        public required string Key { get; init; }
        public LocalModelInfo? Model { get; init; }
        public required Border Card { get; init; }
        public required RadioButton Radio { get; init; }
        public FrameworkElement? Inputs { get; init; }
        public TextBlock? DownloadLine { get; init; }
        public TextBox? UrlBox { get; init; }
        public PasswordBox? KeyBox { get; init; }
        public TextBox? ModelBox { get; init; }
        public string Title { get; init; } = "";
        /// <summary>The exact download size from Hugging Face, once looked up.</summary>
        public double? ExactDownloadGb { get; set; }
    }

    /// <summary>The choice, or null when the window was closed without one. Set as soon as a download starts.</summary>
    public ModelSetupChoice? Choice { get; private set; }

    public ModelSetupWindow(AppSettings settings, ModelDownloads downloads, bool firstRun, GpuInfo? gpu, long ramBytes)
    {
        InitializeComponent();
        WindowTheme.UseThemedTitleBar(this);
        _settings = settings;
        _downloads = downloads;
        _gpu = gpu;
        _ramBytes = ramBytes;

        CloseBtn.Content = firstRun ? "Skip for now" : "Cancel";
        if (firstRun)
            IntroText.Text = "Welcome! Pick the AI model to talk to. The included Gemma 4 E4B is ready right away; bigger models " +
                             "are smarter but need a stronger graphics card. You can change this any time with Choose AI model... under Chat Backend.";
        PcInfoText.Text = DescribePc(gpu, ramBytes);

        BuildOptions();
        SelectInitialOption();

        _downloads.Changed += OnDownloadsChanged;
        _downloads.Finished += OnDownloadFinished;
        Closed += (_, _) =>
        {
            _downloads.Changed -= OnDownloadsChanged;
            _downloads.Finished -= OnDownloadFinished;
            _lookupCts.Cancel();
        };
        Loaded += (_, _) => _ = LookupDownloadSizesAsync();
        UpdateDownloadUi();
    }

    // ==================== Building the cards ====================

    private static string DescribePc(GpuInfo? gpu, long ramBytes)
    {
        var ram = ramBytes > 0 ? $"{Math.Round(ramBytes / (double)LocalModelCatalog.GiB):0} GB of memory" : "";
        if (gpu is { Discrete: true })
            return $"This PC: {gpu.Name} with {Math.Round(gpu.VramGb):0} GB of video memory{(ram.Length > 0 ? $", and {ram}" : "")}. " +
                   "Models that fit in the video memory answer fastest.";
        if (gpu != null)
            return $"This PC: {gpu.Name} (built-in graphics that share {(ram.Length > 0 ? "the " + ram : "system memory")}). " +
                   "Models on this PC work, but slower than with a graphics card; the included model is the best fit.";
        return $"No graphics card the built-in model can use was found{(ram.Length > 0 ? $" ({ram})" : "")}. " +
               "Models on this PC run on the processor: fine for the included small model, slow for the big ones.";
    }

    private void BuildOptions()
    {
        OptionsPanel.Children.Add(SectionHeader("ON THIS PC", "Private and free; works without internet once downloaded."));
        foreach (var model in LocalModelCatalog.Models)
            AddLocalOption(model);

        OptionsPanel.Children.Add(SectionHeader("ON A SERVER OR IN THE CLOUD", "Uses another computer's graphics card, or OpenAI's."));

        var ollamaUrl = Input("Ollama address", _settings.OllamaUrl, "http://localhost:11434");
        AddExternalOption(KeyOllama, "Ollama", "",
            "Models from Ollama, on this PC or another computer on your network.",
            new[]
            {
                ("", "Video memory on this PC: none when Ollama runs on another computer (otherwise what its model needs)."),
                ("", "You need: Ollama (https://ollama.com) with a model downloaded, for example `ollama pull gemma4`."),
                ("", "Cost: free.")
            },
            ollamaUrl.Panel, url: ollamaUrl.Box);

        var serverUrl = Input("Server address", _settings.OpenAiCompatibleUrl, "http://192.168.1.50:8080/v1");
        var serverKey = Secret("API key (only if the server has one)",
            ChatProviders.IsOpenAiCompatible(_settings.ChatProvider) && !IsOpenAiCloud(_settings.OpenAiCompatibleUrl) ? _settings.OpenAiCompatibleApiKey : "");
        AddExternalOption(KeyServer, "llama.cpp server (or another OpenAI-compatible server)", "",
            "llama.cpp's llama-server, LM Studio, vLLM and similar servers, on this PC or another computer.",
            new[]
            {
                ("", "Video memory on this PC: none when the server is another computer."),
                ("", "You need: the server's address, such as http://192.168.1.50:8080/v1."),
                ("", "Cost: free.")
            },
            Stack(serverUrl.Panel, serverKey.Panel), url: serverUrl.Box, key: serverKey.Box);

        var openAiKey = Secret("OpenAI API key", IsOpenAiCloud(_settings.OpenAiCompatibleUrl) ? _settings.OpenAiCompatibleApiKey : _settings.OpenAiCloudApiKey);
        var openAiModel = Input("Model", IsOpenAiCloud(_settings.OpenAiCompatibleUrl) && !string.IsNullOrWhiteSpace(_settings.Model) &&
                                         LocalModelCatalog.Find(_settings.Model) == null
            ? _settings.Model : DefaultOpenAiModel, DefaultOpenAiModel);
        AddExternalOption(KeyOpenAi, "OpenAI (cloud)", "",
            "OpenAI's GPT models, running on OpenAI's servers. Works on any PC.",
            new[]
            {
                ("", "Video memory: none (0 GB). Any PC works, no graphics card needed."),
                ("", "You need: an OpenAI API key from https://platform.openai.com/api-keys, with billing set up."),
                ("", "Cost: pay per use, billed by OpenAI. A small model such as gpt-5-mini costs a fraction of a cent per answer; bigger models cost more. Prices: https://openai.com/api/pricing"),
                ("", "Privacy: your messages (and documents you share) are sent to OpenAI.")
            },
            Stack(openAiKey.Panel, openAiModel.Panel), key: openAiKey.Box, model: openAiModel.Box);
    }

    private void AddLocalOption(LocalModelInfo model)
    {
        var fit = LocalModelCatalog.Evaluate(model, _gpu, _ramBytes);
        var badges = new List<UIElement>();
        if (model.Included)
            badges.Add(Badge("Included", "PrimaryLightBrush"));
        var fitLabel = LocalModelCatalog.FitLabel(fit);
        if (fitLabel.Length > 0)
            badges.Add(Badge(fitLabel, fit switch
            {
                ModelFit.Fits => "SuccessBrush",
                ModelFit.TooBig => "ErrorBrush",
                _ => "WarningBrush"
            }));

        var downloadLine = new TextBlock();
        var lines = new List<UIElement>
        {
            Line("", downloadLine),
            Line("", $"Video memory: about {LocalModelCatalog.FormatGb(model.VramGb)}."),
            Line("", $"Graphics card: {LocalModelCatalog.FormatGb(model.MinCardGb)} or more, for example {model.ExampleCards}")
        };

        var option = BuildCard(model.Id, model.Name, "", model.Summary, badges, lines, inputs: null, model, downloadLine);
        UpdateDownloadLine(option);
    }

    private void AddExternalOption(string optionKey, string title, string icon, string summary, IEnumerable<(string Icon, string Text)> details,
        FrameworkElement inputs, TextBox? url = null, PasswordBox? key = null, TextBox? model = null)
    {
        var lines = details.Select(d => Line(d.Icon, d.Text)).ToList();
        inputs.Margin = new Thickness(0, 10, 0, 0);
        inputs.Visibility = Visibility.Collapsed;
        BuildCard(optionKey, title, icon, summary, new List<UIElement>(), lines, inputs, null, null, url, key, model);
    }

    private Option BuildCard(string key, string title, string icon, string summary, List<UIElement> badges, List<UIElement> lines,
        FrameworkElement? inputs, LocalModelInfo? model, TextBlock? downloadLine,
        TextBox? url = null, PasswordBox? apiKey = null, TextBox? modelBox = null)
    {
        var radio = new RadioButton { GroupName = "model-choice", VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 10, 0) };

        var header = new WrapPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = icon, Style = (Style)FindResource("IconText"), FontSize = 15, Margin = new Thickness(0, 1, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock { Text = title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        foreach (var badge in badges)
            header.Children.Add(badge);

        var body = new StackPanel();
        body.Children.Add(header);
        var summaryText = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Margin = new Thickness(0, 4, 0, 6) };
        summaryText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
        summaryText.Text = summary;
        body.Children.Add(summaryText);
        foreach (var line in lines)
            body.Children.Add(line);
        if (inputs != null)
            body.Children.Add(inputs);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(radio);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);

        var card = new Border
        {
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 10),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1.5),
            Child = grid,
            Cursor = Cursors.Hand
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

        var option = new Option
        {
            Key = key,
            Title = title,
            Model = model,
            Card = card,
            Radio = radio,
            Inputs = inputs,
            DownloadLine = downloadLine,
            UrlBox = url,
            KeyBox = apiKey,
            ModelBox = modelBox
        };
        radio.Checked += (_, _) => Select(option);
        // A click anywhere on the card picks it, except in its text boxes.
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && FindAncestor<TextBoxBase>(source) == null &&
                FindAncestor<PasswordBox>(source) == null && FindAncestor<Hyperlink>(source) == null)
                radio.IsChecked = true;
        };

        _options.Add(option);
        OptionsPanel.Children.Add(card);
        return option;
    }

    private UIElement SectionHeader(string title, string subtitle)
    {
        var panel = new StackPanel { Margin = new Thickness(2, _options.Count == 0 ? 0 : 10, 0, 8) };
        var heading = new TextBlock { Text = title, FontSize = 11.5, FontWeight = FontWeights.Bold };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryLightBrush");
        var sub = new TextBlock { Text = subtitle, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        sub.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        panel.Children.Add(heading);
        panel.Children.Add(sub);
        return panel;
    }

    private static UIElement Badge(string text, string brushKey)
    {
        var label = new TextBlock { Text = text, FontSize = 11, FontWeight = FontWeights.SemiBold };
        label.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        var border = new Border
        {
            Child = label,
            Padding = new Thickness(7, 1, 7, 2),
            Margin = new Thickness(0, 2, 6, 2),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center
        };
        border.SetResourceReference(Border.BorderBrushProperty, brushKey);
        return border;
    }

    private UIElement Line(string icon, string text)
    {
        var block = new TextBlock();
        SetTextWithLinks(block, text);
        return Line(icon, block);
    }

    private UIElement Line(string icon, TextBlock text)
    {
        text.TextWrapping = TextWrapping.Wrap;
        text.FontSize = 12.5;
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var glyph = new TextBlock { Text = icon, Style = (Style)FindResource("IconText"), FontSize = 12, Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        grid.Children.Add(glyph);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    // Plain text with its http(s) addresses as clickable links; `code` spans in a monospace font.
    private static void SetTextWithLinks(TextBlock block, string text)
    {
        block.Inlines.Clear();
        var position = 0;
        foreach (Match match in Url.Matches(text))
        {
            AddCodeAware(block, text[position..match.Index]);
            var address = match.Value.TrimEnd('.', ',');
            var link = new Hyperlink(new Run(address)) { NavigateUri = new Uri(address) };
            link.RequestNavigate += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) { AppLog.Warn($"Could not open {e.Uri}.", ex); }
                e.Handled = true;
            };
            block.Inlines.Add(link);
            position = match.Index + address.Length;
        }
        AddCodeAware(block, text[position..]);
    }

    private static void AddCodeAware(TextBlock block, string text)
    {
        var parts = text.Split('`');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0)
                continue;
            var run = new Run(parts[i]);
            if (i % 2 == 1)
                run.SetResourceReference(TextElement.FontFamilyProperty, "CodeFont");
            block.Inlines.Add(run);
        }
    }

    private (StackPanel Panel, TextBox Box) Input(string label, string value, string placeholder)
    {
        var box = new TextBox { Text = value ?? "" };
        Ui.SetPlaceholder(box, placeholder);
        return (Labeled(label, box), box);
    }

    private (StackPanel Panel, PasswordBox Box) Secret(string label, string value)
    {
        var box = new PasswordBox { Password = value ?? "" };
        return (Labeled(label, box), box);
    }

    private StackPanel Labeled(string label, Control control)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        panel.Children.Add(new TextBlock { Text = label, Style = (Style)FindResource("SectionLabel") });
        panel.Children.Add(control);
        return panel;
    }

    private static StackPanel Stack(params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children)
            panel.Children.Add(child);
        return panel;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match)
                return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    // ==================== Selection ====================

    private void SelectInitialOption()
    {
        Option? initial;
        if (ChatProviders.IsBuiltIn(_settings.ChatProvider))
            initial = _options.FirstOrDefault(o => o.Model?.Id == (LocalModelCatalog.Find(_settings.LocalModelId) ?? LocalModelCatalog.Default).Id);
        else if (ChatProviders.IsOpenAiCompatible(_settings.ChatProvider))
            initial = _options.FirstOrDefault(o => o.Key == (IsOpenAiCloud(_settings.OpenAiCompatibleUrl) ? KeyOpenAi : KeyServer));
        else
            initial = _options.FirstOrDefault(o => o.Key == KeyOllama);

        (initial ?? _options.First()).Radio.IsChecked = true;
    }

    private void Select(Option option)
    {
        _selected = option;
        foreach (var o in _options)
        {
            var on = ReferenceEquals(o, option);
            o.Card.SetResourceReference(Border.BorderBrushProperty, on ? "PrimaryBrush" : "BorderBrush");
            if (o.Inputs != null)
                o.Inputs.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
        ErrorText.Visibility = Visibility.Collapsed;
        UpdateUseButton();
    }

    private void UpdateUseButton()
    {
        var option = _selected;
        if (option == null)
            return;

        UseBtn.IsEnabled = true;
        if (option.Model is { } model)
        {
            if (IsInstalled(model))
                UseBtn.Content = "Use this model";
            else if (_downloads.Current?.Id == model.Id)
            {
                UseBtn.Content = "Downloading...";
                UseBtn.IsEnabled = false;
            }
            else
                UseBtn.Content = File.Exists(ModelDownloader.PartPath(DownloadTarget(model))) ? "Continue download" : "Download and use";
            return;
        }

        UseBtn.Content = option.Key switch
        {
            KeyOllama => "Use Ollama",
            KeyServer => "Use this server",
            _ => "Use OpenAI"
        };
    }

    private static bool IsOpenAiCloud(string? url) => OpenAiRequestCompat.IsOpenAiHost(url);

    private static string DownloadTarget(LocalModelInfo model) => LocalModelCatalog.DownloadPath(model, AppPaths.ModelsDirectory);

    private static bool IsInstalled(LocalModelInfo model) =>
        LocalModelCatalog.FindInstalledFile(model, AppPaths.ModelsDirectory, AppContext.BaseDirectory) != null;

    // ==================== Download sizes and progress ====================

    private void UpdateDownloadLine(Option option)
    {
        if (option.Model is not { } model || option.DownloadLine is not { } line)
            return;

        string text;
        var installed = LocalModelCatalog.FindInstalledFile(model, AppPaths.ModelsDirectory, AppContext.BaseDirectory);
        if (installed != null)
            text = installed.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase)
                ? "Download: none, it came with the app."
                : "Download: done, it is on this PC.";
        else
        {
            var size = option.ExactDownloadGb ?? model.ApproxDownloadGb;
            text = $"Download: {(option.ExactDownloadGb.HasValue ? "" : "about ")}{LocalModelCatalog.FormatGb(size)} (saved in your models folder).";
            var part = ModelDownloader.PartPath(DownloadTarget(model));
            try
            {
                if (File.Exists(part))
                    text += $" {ModelDownloader.FormatBytes(new FileInfo(part).Length)} downloaded so far.";
            }
            catch (IOException) { }
        }
        line.Text = text;
    }

    private async Task LookupDownloadSizesAsync()
    {
        foreach (var option in _options.Where(o => o.Model != null && !IsInstalled(o.Model)).ToList())
        {
            try
            {
                var file = await _downloads.LookupAsync(option.Model!, _lookupCts.Token);
                if (file is { Size: > 0 })
                {
                    option.ExactDownloadGb = file.Size / (double)LocalModelCatalog.GiB;
                    UpdateDownloadLine(option);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                AppLog.Info($"Model chooser: could not look up {option.Model!.Name} ({ex.Message}).");
            }
        }
    }

    private void OnDownloadsChanged() => Dispatcher.InvokeAsync(UpdateDownloadUi);

    private void UpdateDownloadUi()
    {
        var current = _downloads.Current;
        if (current != null)
        {
            DownloadPanel.Visibility = Visibility.Visible;
            DownloadBar.Value = _downloads.Progress.Fraction;
            DownloadText.Text = _downloads.Status;
            PauseDownloadBtn.Visibility = Visibility.Visible;
            if (_downloadingId != null)
                CloseBtn.Content = "Keep downloading in the background";
        }
        else if (_downloadingId == null)
        {
            DownloadPanel.Visibility = Visibility.Collapsed;
        }
        UpdateUseButton();
    }

    private void OnDownloadFinished(ModelDownloadResult result) => Dispatcher.InvokeAsync(() =>
    {
        foreach (var option in _options)
            UpdateDownloadLine(option);

        if (result.Model.Id != _downloadingId)
        {
            UpdateDownloadUi();
            return;
        }

        if (result.Success)
        {
            Choice = new ModelSetupChoice(ChatProviders.BuiltIn, result.Model.Id);
            Close();
            return;
        }

        _downloadingId = null;
        PauseDownloadBtn.Visibility = Visibility.Collapsed;
        DownloadText.Text = _downloads.Status;
        if (!result.Cancelled)
            ShowError($"{result.Model.Name} could not be downloaded: {result.Error} Click {UseBtn.Content} to try again; it continues where it stopped.");
        // Paused or failed: nothing to switch to.
        if (Choice?.LocalModelId == result.Model.Id)
            Choice = null;
        CloseBtn.Content = "Close";
        UpdateUseButton();
    });

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    // ==================== Buttons ====================

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        var option = _selected;
        if (option == null)
            return;
        ErrorText.Visibility = Visibility.Collapsed;

        if (option.Model is { } model)
        {
            if (IsInstalled(model))
            {
                Choice = new ModelSetupChoice(ChatProviders.BuiltIn, model.Id);
                Close();
                return;
            }
            StartDownload(model);
            return;
        }

        switch (option.Key)
        {
            case KeyOllama:
            {
                var url = ChatProviders.NormalizeServerUrl(option.UrlBox?.Text, defaultPort: 11434, addV1: false);
                if (url.Length == 0)
                {
                    ShowError("Enter Ollama's address, for example http://localhost:11434 or http://192.168.1.50:11434.");
                    return;
                }
                Choice = new ModelSetupChoice(ChatProviders.Ollama, Url: url);
                break;
            }
            case KeyServer:
            {
                var url = ChatProviders.NormalizeServerUrl(option.UrlBox?.Text, defaultPort: 8080, addV1: true);
                if (url.Length == 0)
                {
                    ShowError("Enter the server's address, for example http://192.168.1.50:8080/v1.");
                    return;
                }
                Choice = new ModelSetupChoice(ChatProviders.OpenAiCompatible, Url: url, ApiKey: option.KeyBox?.Password.Trim() ?? "");
                break;
            }
            default:
            {
                var key = option.KeyBox?.Password.Trim() ?? "";
                if (key.Length == 0)
                {
                    ShowError("Enter your OpenAI API key. Create one at https://platform.openai.com/api-keys (billing must be set up).");
                    return;
                }
                var openAiModel = option.ModelBox?.Text.Trim();
                Choice = new ModelSetupChoice(ChatProviders.OpenAiCompatible, Url: ChatProviders.OpenAiCloudUrl, ApiKey: key,
                    Model: string.IsNullOrWhiteSpace(openAiModel) ? DefaultOpenAiModel : openAiModel);
                break;
            }
        }
        Close();
    }

    private async void StartDownload(LocalModelInfo model)
    {
        var fit = LocalModelCatalog.Evaluate(model, _gpu, _ramBytes);
        if (fit == ModelFit.TooBig &&
            MessageBox.Show(this,
                $"{model.Name} needs about {LocalModelCatalog.FormatGb(model.VramGb)} of memory, more than this PC has free for it. " +
                "It may not start, or run very slowly.\n\nDownload it anyway?",
                "Model too big for this PC?", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (_downloads.Current is { } other && other.Id != model.Id)
        {
            ShowError($"{other.Name} is still downloading. Pause it first, or wait for it to finish.");
            return;
        }

        _downloadingId = model.Id;
        Choice = new ModelSetupChoice(ChatProviders.BuiltIn, model.Id);
        var task = _downloads.StartAsync(model, AppPaths.ModelsDirectory);
        UpdateDownloadUi();
        DownloadPanel.Visibility = Visibility.Visible;
        DownloadText.Text = _downloads.Status.Length > 0 ? _downloads.Status : $"Starting the download of {model.Name}...";
        CloseBtn.Content = "Keep downloading in the background";

        var result = await task;
        if (!result.Success && !result.Cancelled && string.IsNullOrEmpty(result.FilePath))
        {
            // Refused before it started (another download running).
            _downloadingId = null;
            Choice = null;
            ShowError(result.Error);
            UpdateDownloadUi();
        }
    }

    private void PauseDownload_Click(object sender, RoutedEventArgs e) => _downloads.Cancel();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenModelsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ModelsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.ModelsDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError($"Could not open {AppPaths.ModelsDirectory}: {ex.Message}");
        }
    }
}
