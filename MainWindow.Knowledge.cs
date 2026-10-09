using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VoiceChatbot;

// Knowledge folder ("Knowledge Folder" expander). The app indexes the owner's documents in the
// background (KnowledgeService + Core/KnowledgeIndex) and, when the switch is on, appends to each
// desktop or phone message a list of the documents plus the excerpts that best match it, or the
// whole folder when it fits (Core/KnowledgeContext), like attached documents. A reindex the owner
// starts always ends with a one-line summary in the chat; the Files window lists every file.
public partial class MainWindow
{
    // Startup catch-up indexing waits a little so it does not compete with loading models and speech.
    private static readonly TimeSpan KnowledgeStartupDelay = TimeSpan.FromSeconds(5);

    private readonly KnowledgeService _knowledge = new(new DocumentTextService(), KnowledgeService.DefaultIndexPath);
    private CancellationTokenSource? _knowledgeCts;
    // The run the Stop button cancelled: only that one reports "stopped" in the chat (a new folder or
    // closing the app cancels silently).
    private CancellationTokenSource? _knowledgeStoppedCts;
    private KnowledgeFilesWindow? _knowledgeFilesWindow;
    // 1 while a status refresh is queued on the dispatcher (progress can arrive many times a second).
    private int _knowledgeStatusQueued;
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
            StartKnowledgeReindex(retryFailed: false, KnowledgeStartupDelay, announce: false);
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
            StartKnowledgeReindex(retryFailed: false, TimeSpan.Zero, announce: true);
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
            _knowledgeStoppedCts = _knowledgeCts;
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
        StartKnowledgeReindex(retryFailed: true, TimeSpan.Zero, announce: true);
    }

    private void KnowledgeFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_knowledgeFilesWindow is { IsVisible: true })
        {
            _knowledgeFilesWindow.Refresh();
            _knowledgeFilesWindow.Activate();
            return;
        }

        try
        {
            _knowledgeFilesWindow = new KnowledgeFilesWindow(() => _knowledge.GetFileList(_knowledgeFolder), () => _knowledge.Status)
            {
                Owner = this
            };
            _knowledgeFilesWindow.Closed += (_, _) => _knowledgeFilesWindow = null;
            _knowledgeFilesWindow.Show();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not open the knowledge file list: {ex.Message}");
        }
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
            StartKnowledgeReindex(retryFailed: false, TimeSpan.Zero, announce: true);
        else
            CancelKnowledgeIndexing();
        UpdateKnowledgeControls();
    }

    // ==================== Indexing ====================

    /// <param name="announce">
    /// The owner started it (Reindex, a new folder, the switch): post a one-line summary in the chat when
    /// it finishes, even when nothing changed. Background runs only post when something failed.
    /// </param>
    private async void StartKnowledgeReindex(bool retryFailed, TimeSpan delay, bool announce)
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
        KnowledgeReindexResult? result = null;
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cts.Token);
            result = await _knowledge.ReindexAsync(folder, retryFailed, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Stopped before it started, the folder changed or the app is closing.
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Knowledge indexing failed: {ex}");
        }
        finally
        {
            var stoppedByUser = ReferenceEquals(_knowledgeStoppedCts, cts);
            if (stoppedByUser)
                _knowledgeStoppedCts = null;
            if (ReferenceEquals(_knowledgeCts, cts))
                _knowledgeCts = null;
            cts.Dispose();
            UpdateKnowledgeControls();
            _knowledgeFilesWindow?.Refresh();
            if (result != null)
                PostKnowledgeSummary(result, announce, stoppedByUser);
        }
    }

    private void PostKnowledgeSummary(KnowledgeReindexResult result, bool announce, bool stoppedByUser)
    {
        var post = result.Outcome switch
        {
            KnowledgeReindexOutcome.Completed => announce || result.FailedThisRun > 0,
            KnowledgeReindexOutcome.Stopped => stoppedByUser,
            KnowledgeReindexOutcome.Failed or KnowledgeReindexOutcome.FolderNotFound => true,
            _ => false
        };
        if (!post)
            return;

        var summary = KnowledgeReport.FormatChatSummary(result);
        if (summary.Length > 0)
            AddSystemMessage(summary);
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
        // One queued refresh at a time; it shows the latest status when it runs.
        if (Interlocked.Exchange(ref _knowledgeStatusQueued, 1) == 1)
            return;

        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                Interlocked.Exchange(ref _knowledgeStatusQueued, 0);
                UpdateKnowledgeControls();
            }));
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _knowledgeStatusQueued, 0);
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
        // The tooltip explains the status line (unreadable and skipped files), so it only goes with it.
        var details = "";
        if (_knowledgeFolder.Length == 0)
            text = _settings.KnowledgeEnabled
                ? "Choose a folder with your documents."
                : "Choose a folder with your documents, then turn this on.";
        else if (status.IsIndexing)
            text = status.Message;
        else if (running)
            text = "Checking the folder for new or changed files...";
        else if (!_settings.KnowledgeEnabled)
        {
            text = string.IsNullOrEmpty(status.Message)
                ? "Off. Turn on to let answers use your documents."
                : "Off. " + status.Message;
            details = status.Details;
        }
        else
        {
            text = string.IsNullOrEmpty(status.Message) ? "Not indexed yet. Click Reindex." : status.Message;
            details = status.Details;
        }

        KnowledgeStatusText.Text = text;
        KnowledgeStatusText.ToolTip = string.IsNullOrEmpty(details) ? null : details;
        KnowledgeReindexBtn.Content = running ? "Stop" : "Reindex";
        Ui.SetIcon(KnowledgeReindexBtn, running ? "\uE71A" : "\uE72C");
        KnowledgeReindexBtn.ToolTip = running
            ? "Stop indexing. Files read so far stay searchable."
            : "Read new and changed files now, and retry files that could not be read";
        if (KnowledgeFilesBtn != null)
            KnowledgeFilesBtn.IsEnabled = _knowledgeFolder.Length > 0;
    }

    // ==================== Chat context ====================

    /// <summary>
    /// When the knowledge folder is on, adds to this request (appended to the current user message) a
    /// list of the folder's documents and, for a message about them, the best-matching excerpts or the
    /// whole folder when it fits about a third of the context window (Core/KnowledgeContext); the chat
    /// notes which files were used. Shared by the desktop chat and the phone remote and safe on any
    /// thread. Best effort: if anything goes wrong the request simply goes without it.
    /// </summary>
    private async Task AddKnowledgeContextAsync(List<ChatMessage> messages, string? userText, string model, CancellationToken ct)
    {
        if (!_settings.KnowledgeEnabled || messages.Count == 0 || string.IsNullOrWhiteSpace(userText) || IsLargePaste(userText))
            return;

        try
        {
            var folder = _knowledgeFolder;
            if (!_knowledge.HasFiles(folder))
                return;

            var minExcerpts = _settings.KnowledgeMaxChunks;
            // Reads the chat history, which belongs to the UI thread.
            var (query, followUp) = Dispatcher.CheckAccess()
                ? GetKnowledgeQuery(userText)
                : await Dispatcher.InvokeAsync(() => GetKnowledgeQuery(userText));

            // The same (cached) detection the request itself uses right after this.
            var contextTokens = await GetContextTokensForRequestAsync(model, ct);
            var budget = KnowledgeContext.BudgetTokens(contextTokens, GetMaxTokensForRequest(userText, contextTokens),
                EstimateTextTokens(GetLastUserMessageContent(messages)));

            var context = await Task.Run(() => _knowledge.BuildContext(folder, query, followUp, minExcerpts, budget), ct);
            if (context.Mode == KnowledgeContextMode.None || !AppendToCurrentUserMessage(messages, context.Text))
                return;

            AppLog.Info($"Knowledge folder: {context.Mode}, ~{KnowledgeContext.EstimateTokens(context.Text):N0} of {budget:N0} tokens, " +
                        $"{context.DocumentsUsed} document(s).");
            // Only the file list went along: nothing worth a note in the chat.
            if (context.Note.Length == 0)
                return;

            if (Dispatcher.CheckAccess())
                AddSystemMessage(context.Note);
            else
                await Dispatcher.InvokeAsync(() => AddSystemMessage(context.Note));
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

    /// <summary>
    /// What to search the knowledge folder for (UI thread): "continue" searches for the request being
    /// continued; a short follow-up ("and the parking rules?") also passes the previous question, which
    /// ranks excerpts that match both higher.
    /// </summary>
    private (string? Query, string? FollowUp) GetKnowledgeQuery(string userText)
    {
        if (IsManualContinuationRequest(userText))
            return (GetMemoryQueryText(userText), null);

        return KnowledgeIndex.IsShortFollowUp(userText)
            ? (userText, GetPreviousUserMessage(userText))
            : (userText, null);
    }

    // The user message before the current one (which is normally already the last one in the history).
    private string? GetPreviousUserMessage(string currentText)
    {
        var userMessages = _history.GetAll()
            .Where(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Content ?? "")
            .ToList();

        var i = userMessages.Count - 1;
        if (i >= 0 && string.Equals(userMessages[i].Trim(), currentText.Trim(), StringComparison.Ordinal))
            i--;
        for (; i >= 0; i--)
        {
            if (!string.IsNullOrWhiteSpace(userMessages[i]) && !IsManualContinuationRequest(userMessages[i]))
                return userMessages[i];
        }

        return null;
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
