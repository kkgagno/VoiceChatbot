using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace VoiceChatbot;

// Knowledge folder ("Knowledge Folder" expander). The app indexes the owner's documents in the
// background (KnowledgeService + Core/KnowledgeIndex) and, when the switch is on, appends the
// excerpts that best match each desktop or phone message to that message, like attached documents.
public partial class MainWindow
{
    // Startup catch-up indexing waits a little so it does not compete with loading models and speech.
    private static readonly TimeSpan KnowledgeStartupDelay = TimeSpan.FromSeconds(5);

    private readonly KnowledgeService _knowledge = new(new DocumentTextService(), KnowledgeService.DefaultIndexPath);
    private CancellationTokenSource? _knowledgeCts;
    // The folder in use. _settings.KnowledgeFolder can briefly hold half-typed text, because
    // SaveSettings copies the text box whenever another setting is saved.
    private string _knowledgeFolder = "";
    private bool _knowledgeStatusHooked;

    // ==================== Settings ====================

    private void ApplyKnowledgeSettings()
    {
        _settings.KnowledgeFolder = (_settings.KnowledgeFolder ?? "").Trim();
        _settings.KnowledgeMaxChunks = KnowledgeIndex.ClampMaxChunks(_settings.KnowledgeMaxChunks);
        _knowledgeFolder = _settings.KnowledgeFolder;
        KnowledgeToggle.IsChecked = _settings.KnowledgeEnabled;
        KnowledgeFolderBox.Text = _knowledgeFolder;
        KnowledgeMaxChunksSlider.Value = _settings.KnowledgeMaxChunks;
        KnowledgeMaxChunksValue.Text = _settings.KnowledgeMaxChunks.ToString();

        if (!_knowledgeStatusHooked)
        {
            _knowledge.StatusChanged += OnKnowledgeStatusChanged;
            _knowledgeStatusHooked = true;
        }

        // Pick up files added or changed while the app was closed.
        if (_settings.KnowledgeEnabled && _knowledgeFolder.Length > 0)
            StartKnowledgeReindex(retryFailed: false, KnowledgeStartupDelay);
        else
            UpdateKnowledgeControls();
    }

    private void SaveKnowledgeSettings()
    {
        _settings.KnowledgeEnabled = KnowledgeToggle.IsChecked == true;
        _settings.KnowledgeFolder = KnowledgeFolderBox.Text.Trim().Trim('"');
        _settings.KnowledgeMaxChunks = KnowledgeIndex.ClampMaxChunks((int)Math.Round(KnowledgeMaxChunksSlider.Value));
    }

    // ==================== UI events ====================

    private void KnowledgeToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.KnowledgeEnabled = KnowledgeToggle.IsChecked == true;
        CommitKnowledgeFolder(startIndexing: false);
        if (_settings.KnowledgeEnabled && _knowledgeFolder.Length > 0 && _knowledgeCts == null)
            StartKnowledgeReindex(retryFailed: false, TimeSpan.Zero);
        else if (!_settings.KnowledgeEnabled)
            CancelKnowledgeIndexing();
        UpdateKnowledgeControls();
    }

    private void KnowledgeFolderBox_LostFocus(object sender, RoutedEventArgs e) => CommitKnowledgeFolder(startIndexing: true);

    private void KnowledgeFolderBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return))
            return;

        CommitKnowledgeFolder(startIndexing: true);
        e.Handled = true;
    }

    private void KnowledgeBrowse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Choose the folder with your documents",
                Multiselect = false
            };
            var current = KnowledgeFolderBox.Text.Trim().Trim('"');
            if (current.Length > 0 && Directory.Exists(current))
                dialog.InitialDirectory = current;

            if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
                return;

            KnowledgeFolderBox.Text = dialog.FolderName;
            CommitKnowledgeFolder(startIndexing: true);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not open the folder picker: {ex.Message}");
        }
    }

    private void KnowledgeReindex_Click(object sender, RoutedEventArgs e)
    {
        // The button reads "Stop" while indexing.
        if (_knowledgeCts != null)
        {
            CancelKnowledgeIndexing();
            UpdateKnowledgeControls();
            return;
        }

        CommitKnowledgeFolder(startIndexing: false);
        if (_knowledgeFolder.Length == 0)
        {
            KnowledgeStatusText.Text = "Choose a folder first (Browse).";
            return;
        }

        // A manual reindex also retries files that could not be read last time (e.g. after installing OCR).
        StartKnowledgeReindex(retryFailed: true, TimeSpan.Zero);
    }

    private void KnowledgeMaxChunksSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var maxChunks = KnowledgeIndex.ClampMaxChunks((int)Math.Round(e.NewValue));
        if (KnowledgeMaxChunksValue != null)
            KnowledgeMaxChunksValue.Text = maxChunks.ToString();
        if (_settings != null)
            _settings.KnowledgeMaxChunks = maxChunks;
    }

    // Takes the text box value as the folder in use; a new folder is indexed when the feature is on.
    private void CommitKnowledgeFolder(bool startIndexing)
    {
        var folder = KnowledgeFolderBox.Text.Trim().Trim('"');
        if (string.Equals(folder, _knowledgeFolder, StringComparison.OrdinalIgnoreCase))
            return;

        _knowledgeFolder = folder;
        _settings.KnowledgeFolder = folder;
        if (startIndexing && _settings.KnowledgeEnabled && folder.Length > 0)
            StartKnowledgeReindex(retryFailed: false, TimeSpan.Zero);
        else
            CancelKnowledgeIndexing();
        UpdateKnowledgeControls();
    }

    // ==================== Indexing ====================

    private async void StartKnowledgeReindex(bool retryFailed, TimeSpan delay)
    {
        CancelKnowledgeIndexing();
        var folder = _knowledgeFolder;
        if (folder.Length == 0)
        {
            UpdateKnowledgeControls();
            return;
        }

        var cts = new CancellationTokenSource();
        _knowledgeCts = cts;
        UpdateKnowledgeControls();
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cts.Token);
            await _knowledge.ReindexAsync(folder, retryFailed, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Stopped, the folder changed or the app is closing.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Knowledge indexing failed: {ex}");
        }
        finally
        {
            if (ReferenceEquals(_knowledgeCts, cts))
                _knowledgeCts = null;
            cts.Dispose();
            UpdateKnowledgeControls();
        }
    }

    private void CancelKnowledgeIndexing()
    {
        var cts = _knowledgeCts;
        _knowledgeCts = null;
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished.
        }
    }

    private void OnKnowledgeStatusChanged(KnowledgeStatus status)
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(UpdateKnowledgeControls));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Knowledge status update failed: {ex.Message}");
        }
    }

    private void UpdateKnowledgeControls()
    {
        if (KnowledgeStatusText == null || KnowledgeReindexBtn == null || _settings == null)
            return;

        var running = _knowledgeCts != null;
        var status = _knowledge.Status;
        string text;
        if (_knowledgeFolder.Length == 0)
            text = _settings.KnowledgeEnabled
                ? "Choose a folder with your documents."
                : "Choose a folder with your documents, then turn this on.";
        else if (status.IsIndexing)
            text = status.Message;
        else if (running)
            text = "Checking the folder for new or changed files...";
        else if (!_settings.KnowledgeEnabled)
            text = string.IsNullOrEmpty(status.Message)
                ? "Off. Turn on to let answers use your documents."
                : "Off. " + status.Message;
        else
            text = string.IsNullOrEmpty(status.Message) ? "Not indexed yet. Click Reindex." : status.Message;

        KnowledgeStatusText.Text = text;
        KnowledgeStatusText.ToolTip = string.IsNullOrEmpty(status.Details) || status.IsIndexing ? null : status.Details;
        KnowledgeReindexBtn.Content = running ? "Stop" : "Reindex";
        Ui.SetIcon(KnowledgeReindexBtn, running ? "\uE71A" : "\uE72C");
        KnowledgeReindexBtn.ToolTip = running
            ? "Stop indexing. Files read so far stay searchable."
            : "Read new and changed files now, and retry files that could not be read";
    }

    // ==================== Chat context ====================

    /// <summary>
    /// When the knowledge folder is on, adds the excerpts that best match the user's message to this
    /// request (appended to the current user message), and notes in the chat which files they came from. Shared by the desktop chat and the phone remote and safe on any thread.
    /// Best effort: if anything goes wrong the request simply goes without excerpts.
    /// </summary>
    private async Task AddKnowledgeContextAsync(List<ChatMessage> messages, string? userText, CancellationToken ct)
    {
        if (!_settings.KnowledgeEnabled || messages.Count == 0 || string.IsNullOrWhiteSpace(userText) || IsLargePaste(userText))
            return;

        try
        {
            var folder = _knowledgeFolder;
            var maxChunks = _settings.KnowledgeMaxChunks;
            // "continue" has no topic of its own, so search for the request being continued
            // (this reads the chat history, which belongs to the UI thread).
            var query = Dispatcher.CheckAccess()
                ? GetMemoryQueryText(userText)
                : await Dispatcher.InvokeAsync(() => GetMemoryQueryText(userText));
            var hits = await Task.Run(() => _knowledge.Search(query, folder, maxChunks), ct);
            if (hits.Count == 0 || !AppendToCurrentUserMessage(messages, KnowledgeIndex.FormatContext(hits, folder)))
                return;

            var note = KnowledgeIndex.FormatNote(hits);
            if (Dispatcher.CheckAccess())
                AddSystemMessage(note);
            else
                await Dispatcher.InvokeAsync(() => AddSystemMessage(note));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Knowledge search failed: {ex.Message}");
        }
    }

    // The excerpts ride along in the user's turn, like attached documents. A system message in the
    // middle of the chat breaks strict chat templates (Gemma under llama.cpp --jinja rejects roles
    // that do not alternate), and document text should not get system-prompt authority.
    private static bool AppendToCurrentUserMessage(List<ChatMessage> messages, string context)
    {
        if (string.IsNullOrWhiteSpace(context))
            return false;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (!messages[i].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                continue;

            messages[i] = new ChatMessage
            {
                Role = messages[i].Role,
                Content = $"{messages[i].Content}\n\n{context}",
                ImagesBase64 = messages[i].ImagesBase64,
                Timestamp = messages[i].Timestamp
            };
            return true;
        }

        return false;
    }
}
