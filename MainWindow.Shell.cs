using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace VoiceChatbot;

// Window chrome, sidebar, keyboard shortcuts and Kokoro voice-output settings.
public partial class MainWindow
{
    private bool _kokoroTestRunning;
    // The background check of the remote Kokoro host (startup, host or mode change, after a failure); null when idle.
    private System.Threading.CancellationTokenSource? _kokoroCheckCts;
    // Waits between background checks while the host does not answer: quick at first (Wi-Fi coming up,
    // the Kokoro server still starting), then once a minute.
    private static readonly int[] KokoroCheckRetrySeconds = { 5, 10, 20, 30 };
    private const int KokoroCheckSteadySeconds = 60;

    // ==================== Kokoro ====================

    private void SelectKokoroModeCombo(string mode)
    {
        var normalized = KokoroEndpoint.NormalizeMode(mode);
        foreach (var item in KokoroModeCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                KokoroModeCombo.SelectedItem = item;
                return;
            }
        }

        KokoroModeCombo.SelectedIndex = 0;
    }

    private string GetSelectedKokoroMode() =>
        KokoroEndpoint.NormalizeMode((KokoroModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString());

    private void KokoroModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_speech == null || _settings == null)
            return;

        _settings.KokoroMode = GetSelectedKokoroMode();
        _speech.KokoroMode = _settings.KokoroMode;
        _speech.ResetRemoteKokoroBackoff();
        StartKokoroAutoCheck(TimeSpan.Zero);
        UpdateKokoroHint();
    }

    /// <summary>
    /// Checks the remote Kokoro host in the background, the way the Test button does, and keeps checking
    /// until it answers. When it answers, the remote back-off is cleared (the next reply uses it right
    /// away), the status turns green and the host's voice list is loaded. Does nothing for Local only or
    /// without a host. <paramref name="restart"/> false leaves a check that is already running alone.
    /// UI thread.
    /// </summary>
    private void StartKokoroAutoCheck(TimeSpan delay, bool restart = true)
    {
        if (_settings == null || _speech == null)
            return;
        if (!restart && _kokoroCheckCts != null)
            return;

        _kokoroCheckCts?.Cancel();
        _kokoroCheckCts = null;

        var mode = KokoroEndpoint.NormalizeMode(_settings.KokoroMode);
        var host = _settings.KokoroRemoteUrl ?? "";
        if (mode == KokoroEndpoint.ModeLocalOnly || KokoroEndpoint.NormalizeBaseUrl(host).Length == 0)
            return;

        var cts = new System.Threading.CancellationTokenSource();
        _kokoroCheckCts = cts;
        _ = RunKokoroAutoCheckAsync(host, mode, delay, cts);
    }

    private async Task RunKokoroAutoCheckAsync(string host, string mode, TimeSpan delay, System.Threading.CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var authority = new Uri(KokoroEndpoint.NormalizeBaseUrl(host)).Authority;
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);

            for (var attempt = 0; !ct.IsCancellationRequested; attempt++)
            {
                if (attempt == 0 && string.IsNullOrEmpty(_speech.LastTtsBackendUsed))
                {
                    TtsStatusText.Text = $"Kokoro: checking {authority}...";
                    TtsStatusText.Foreground = FindResource("TextSecondaryBrush") as Brush;
                }

                var result = await KokoroEndpoint.ProbeAsync(host, ct);
                if (ct.IsCancellationRequested)
                    return;

                if (result.Ok)
                {
                    _speech.ResetRemoteKokoroBackoff();
                    UpdateTtsStatus($"Kokoro: {authority} ready", ok: true);
                    if (!_kokoroTestRunning && result.Voices.Count > 0)
                        ReplaceVoiceList(result.Voices);
                    AppLog.Info($"Kokoro: {authority} answered ({result.Message}){(attempt > 0 ? $" after {attempt + 1} checks" : "")}.");
                    return;
                }

                if (attempt == 0)
                    AppLog.Warn($"Kokoro: {authority} did not answer ({result.Message}); checking again in the background.");
                UpdateTtsStatus(mode == KokoroEndpoint.ModeRemoteOnly
                    ? $"Kokoro: {authority} not answering - speech is silent until it does (retrying)"
                    : $"Kokoro: {authority} not answering - local voice for now (retrying)", ok: false);

                var wait = attempt < KokoroCheckRetrySeconds.Length ? KokoroCheckRetrySeconds[attempt] : KokoroCheckSteadySeconds;
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Replaced by a newer check, or the window is closing.
        }
        catch (Exception ex)
        {
            AppLog.Warn("Kokoro: the background check failed.", ex);
        }
        finally
        {
            if (ReferenceEquals(_kokoroCheckCts, cts))
                _kokoroCheckCts = null;
            cts.Dispose();
        }
    }

    private void UpdateKokoroHint()
    {
        if (KokoroStatusText == null || _settings == null)
            return;

        var baseUrl = KokoroEndpoint.NormalizeBaseUrl(KokoroHostBox.Text);
        var mode = GetSelectedKokoroMode();
        var muted = FindResource("TextMutedBrush") as Brush;

        if (string.IsNullOrWhiteSpace(KokoroHostBox.Text))
        {
            KokoroStatusText.Text = mode == KokoroEndpoint.ModeRemoteOnly
                ? "Remote only is selected but no host is set - speech will be silent."
                : "Blank = the built-in Kokoro on this PC. Default remote port is 8880.";
            KokoroStatusText.Foreground = mode == KokoroEndpoint.ModeRemoteOnly ? FindResource("WarningBrush") as Brush : muted;
        }
        else if (baseUrl.Length == 0)
        {
            KokoroStatusText.Text = "That doesn't look like a valid host or URL.";
            KokoroStatusText.Foreground = FindResource("ErrorBrush") as Brush;
        }
        else
        {
            KokoroStatusText.Text = mode == KokoroEndpoint.ModeLocalOnly
                ? $"Local only - {baseUrl} is ignored."
                : $"Will use {KokoroEndpoint.SpeechUrl(baseUrl)}";
            KokoroStatusText.Foreground = muted;
        }

        if (string.IsNullOrEmpty(_speech?.LastTtsBackendUsed))
        {
            var target = mode == KokoroEndpoint.ModeLocalOnly || baseUrl.Length == 0
                ? "Kokoro: local"
                : $"Kokoro: {new Uri(baseUrl).Authority}{(mode == KokoroEndpoint.ModeAuto ? " (not checked yet)" : "")}";
            TtsStatusText.Text = target;
            TtsStatusText.Foreground = FindResource("TextSecondaryBrush") as Brush;
            TtsStatusDot.Fill = muted;
        }
    }

    private void UpdateTtsStatus(string text, bool ok)
    {
        TtsStatusText.Text = text;
        TtsStatusText.Foreground = FindResource(ok ? "SuccessBrush" : "WarningBrush") as Brush;
        TtsStatusDot.Fill = FindResource(ok ? "SuccessBrush" : "WarningBrush") as Brush;
    }

    private async void KokoroTest_Click(object sender, RoutedEventArgs e)
    {
        if (_kokoroTestRunning)
            return;

        _kokoroTestRunning = true;
        KokoroTestBtn.IsEnabled = false;
        KokoroStatusText.Text = "Testing...";
        KokoroStatusText.Foreground = FindResource("TextSecondaryBrush") as Brush;

        try
        {
            var result = await KokoroEndpoint.ProbeAsync(KokoroHostBox.Text);
            KokoroStatusText.Text = result.Ok ? $"{result.Message} - {result.BaseUrl}" : result.Message;
            KokoroStatusText.Foreground = FindResource(result.Ok ? "SuccessBrush" : "ErrorBrush") as Brush;

            if (!result.Ok)
                return;

            _speech.ResetRemoteKokoroBackoff();
            UpdateTtsStatus($"Kokoro: {new Uri(result.BaseUrl).Authority} reachable", ok: true);

            if (result.Voices.Count > 0)
                ReplaceVoiceList(result.Voices);
        }
        finally
        {
            _kokoroTestRunning = false;
            KokoroTestBtn.IsEnabled = true;
        }
    }

    private void ReplaceVoiceList(System.Collections.Generic.IEnumerable<string> voiceIds)
    {
        var currentId = (VoiceCombo.SelectedItem?.ToString() ?? _settings.VoiceName).Split('(')[0].Trim();
        var described = voiceIds.Select(KokoroEndpoint.DescribeVoice).ToList();

        VoiceCombo.Items.Clear();
        foreach (var voice in described)
            VoiceCombo.Items.Add(voice);

        var match = described.FirstOrDefault(v => v.Split('(')[0].Trim().Equals(currentId, StringComparison.OrdinalIgnoreCase));
        if (match != null)
            VoiceCombo.SelectedItem = match;
        else if (VoiceCombo.Items.Count > 0)
            VoiceCombo.SelectedIndex = 0;
    }

    private async void VoicePreview_Click(object sender, RoutedEventArgs e)
    {
        var voice = VoiceCombo.SelectedItem?.ToString() ?? _speech.VoiceName;
        if (TtsToggle.IsChecked != true)
        {
            AddSystemMessage("Turn on \"Speak responses\" to preview voices.");
            return;
        }

        VoicePreviewBtn.IsEnabled = false;
        try
        {
            var name = voice.Split('(')[0].Trim();
            var spoken = name.Length > 3 ? char.ToUpperInvariant(name[3]) + name[4..] : "your assistant";
            var previewDir = AppPaths.TempPath("voice-preview");
            var path = await _speech.CreateSpeechAudioFileAsync(
                $"Hi, I'm {spoken}. This is how I'll sound when I answer you.", previewDir);
            if (string.IsNullOrWhiteSpace(path))
            {
                AddSystemMessage("Voice preview failed. Check the Kokoro host, or the app log for the built-in Kokoro's error.");
                return;
            }

            _speech.PlayAudioFile(path);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Voice preview failed: {ex.Message}");
        }
        finally
        {
            VoicePreviewBtn.IsEnabled = true;
        }
    }

    // ==================== Header / sidebar ====================

    private void UpdateActiveModelText()
    {
        if (ActiveModelText == null || _settings == null)
            return;

        var model = ModelCombo.Text;
        ActiveModelText.Text = string.IsNullOrWhiteSpace(model)
            ? _settings.ChatProvider
            : $"{model}  ·  {_settings.ChatProvider}";
        Title = string.IsNullOrWhiteSpace(model) ? AppPaths.ProductName : $"{AppPaths.ProductName} - {model}";
    }

    private void SetSidebarVisible(bool visible)
    {
        if (visible)
        {
            SidebarPanel.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
            SidebarColumn.MinWidth = 280;
            SidebarColumn.Width = new GridLength(Math.Clamp(_settings.SidebarWidth, 280, SidebarColumn.MaxWidth));
        }
        else
        {
            if (SidebarPanel.Visibility == Visibility.Visible && SidebarColumn.ActualWidth > 0)
                _settings.SidebarWidth = SidebarColumn.ActualWidth;
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
        }

        _settings.SidebarVisible = visible;
        Dispatcher.BeginInvoke(UpdateChatBubbleWidths, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) =>
        SetSidebarVisible(SidebarPanel.Visibility != Visibility.Visible);

    private void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _settings.SidebarWidth = SidebarColumn.ActualWidth;
        UpdateChatBubbleWidths();
    }

    // ==================== Chat surface ====================

    // How far above the end the user can be and still have streamed text keep the chat at the bottom.
    private const double ChatFollowDistance = 160;

    private void ChatScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        var distanceFromBottom = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset;
        ScrollToBottomBtn.Visibility = distanceFromBottom > ChatFollowDistance ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScrollToBottom_Click(object sender, RoutedEventArgs e) => ChatScroll.ScrollToEnd();

    private void Suggestion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string prompt })
            return;

        MessageInput.Text = prompt;
        MessageInput.Focus();
        MessageInput.CaretIndex = MessageInput.Text.Length;
    }

    private void SaveChat_Click(object sender, RoutedEventArgs e)
    {
        var text = _history.Export();
        if (string.IsNullOrWhiteSpace(text))
        {
            AddSystemMessage("Nothing to save yet.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save conversation",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            FileName = $"voice-chat_{DateTime.Now:yyyyMMdd_HHmm}.md",
            AddExtension = true,
            DefaultExt = ".md"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var header = $"# {AppPaths.ProductName} conversation\n\n_{DateTime.Now:f} · {ModelCombo.Text}_\n\n";
            File.WriteAllText(dialog.FileName, header + text);
            AddSystemMessage($"Conversation saved to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Save failed: {ex.Message}");
        }
    }

    /// <summary>Copies all text shown in an assistant bubble (prose and code blocks).</summary>
    private void CopyBubbleText(Panel content)
    {
        var parts = new System.Collections.Generic.List<string>();
        CollectText(content, parts);
        var text = string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        if (string.IsNullOrWhiteSpace(text))
            return;

        try
        {
            Clipboard.SetText(text);
            ActivityLabel.Text = "Copied to clipboard";
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Copy failed: {ex.Message}");
        }
    }

    private static void CollectText(DependencyObject parent, System.Collections.Generic.List<string> parts)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            // A rendered reply hides its plain text box; copy what is shown.
            if (child is UIElement { Visibility: not Visibility.Visible })
                continue;

            if (child is TextBox box)
                parts.Add(box.Text);
            else if (child is RichTextBox rich)
                parts.Add(new System.Windows.Documents.TextRange(rich.Document.ContentStart, rich.Document.ContentEnd).Text.Trim());
            else
                CollectText(child, parts);
        }
    }

    // ==================== Keyboard ====================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        if (e.Key == Key.F1)
        {
            OpenHelp(ContextHelpTopicId(e.OriginalSource as DependencyObject));
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.B)
        {
            SetSidebarVisible(SidebarPanel.Visibility != Visibility.Visible);
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.L)
        {
            if (AlwaysListenToggle.IsEnabled)
                ToggleListening();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.K)
        {
            MessageInput.Focus();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.H)
        {
            ToggleHistoryPanel();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.N)
        {
            StartNewChat();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && IsHistoryPanelOpen)
        {
            // Esc closes the Conversations panel; inside a rename box it only cancels the rename.
            if (!IsHistoryRenameBox(e.OriginalSource))
            {
                CloseHistoryPanel();
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape && !IsInsideOpenDropDown(e.OriginalSource as DependencyObject))
        {
            var busy = _chatCts != null || _renderingReplySpeech > 0 || _speech.CurrentState != VoiceState.Idle || _autoListening;
            if (busy)
            {
                StopAll_Click(StopBtn, new RoutedEventArgs());
                e.Handled = true;
            }
        }
    }

    private static bool IsInsideOpenDropDown(DependencyObject? source)
    {
        for (var node = source; node != null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ComboBox { IsDropDownOpen: true })
                return true;
        }

        return false;
    }
}
