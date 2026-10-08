using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VoiceChatbot;

// Saved conversation history. Every user/assistant message that goes through _history is recorded
// into one JSON file per chat (ConversationStore), and the Conversations flyout (Ctrl+H) reopens,
// renames, searches and deletes them.
public partial class MainWindow
{
    private const int MaxRestoredBubbles = 200;
    private const string HistoryRenameBoxTag = "HistoryRename";

    private readonly ConversationStore _conversationStore = new(ConversationStore.DefaultFolder);
    // All history disk access runs here, one item at a time and in order, off the UI thread.
    private readonly SerialWorkQueue _conversationIo = new();
    // Assistant bubble -> the stored message it shows, so audio/images that arrive later land on it.
    private readonly ConditionalWeakTable<AssistantMessageUi, RecordedMessage> _recordedBubbles = new();
    // Audio/images attached to a bubble before its text reached _history (phone remote, scheduler, media).
    private readonly ConditionalWeakTable<AssistantMessageUi, BubbleMedia> _unrecordedBubbleMedia = new();
    private readonly HashSet<string> _deletedConversationIds = new(StringComparer.Ordinal);
    private StoredConversation? _currentConversation;
    // Bumped whenever the chat switches conversation, so a late reply to the old chat is not
    // recorded into the new one.
    private int _conversationEpoch;
    private AssistantMessageUi? _lastAssistantBubble;
    private int _lastAssistantBubbleEpoch;
    private List<string> _pendingUserImagePaths = new();
    private bool _restoringConversation;
    private bool _historySaveErrorShown;
    private DispatcherTimer? _historySearchTimer;
    private CancellationTokenSource? _historySearchCts;
    private int _historyListVersion;

    private sealed record RecordedMessage(StoredConversation Conversation, StoredMessage Message);

    private sealed class BubbleMedia
    {
        public string? AudioPath { get; set; }
        public List<string> ImagePaths { get; } = new();
    }

    private sealed class HistoryRowState
    {
        public HistoryRowState(ConversationSummary summary) => Summary = summary;
        public ConversationSummary Summary { get; }
        public bool Busy { get; set; }
    }

    // ==================== Settings ====================

    private void ApplyConversationHistorySettings()
    {
        SaveHistoryToggle.IsChecked = _settings.SaveConversationHistory;
        UpdateHistorySettingsHint();
    }

    private void SaveConversationHistorySettings() =>
        _settings.SaveConversationHistory = SaveHistoryToggle.IsChecked == true;

    private void SaveHistoryToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.SaveConversationHistory = SaveHistoryToggle.IsChecked == true;
        UpdateHistorySettingsHint();
    }

    private void UpdateHistorySettingsHint()
    {
        HistorySettingsHint.Text = _settings.SaveConversationHistory
            ? "Every chat is saved on this PC so you can reopen it later (Ctrl+H)."
            : "Off - new messages are not saved. Chats saved earlier stay until you delete them.";
    }

    private void OpenHistoryFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_conversationStore.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_conversationStore.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not open the history folder: {ex.Message}");
        }
    }

    // ==================== Recording ====================

    private void OnHistoryMessageAdded(object? sender, ChatMessageAddedEventArgs e)
    {
        if (Dispatcher.CheckAccess())
            RecordHistoryMessage(e);
        else
            Dispatcher.BeginInvoke(() => RecordHistoryMessage(e));
    }

    private void RecordHistoryMessage(ChatMessageAddedEventArgs e)
    {
        try
        {
            var role = e.Role;
            var content = e.Content;
            var isUser = role.Equals("user", StringComparison.OrdinalIgnoreCase);
            var imagePaths = isUser ? _pendingUserImagePaths : null;
            if (isUser)
                _pendingUserImagePaths = new List<string>();

            if (_restoringConversation)
                return;

            // The reply belongs to a conversation the user has already left (a reply that finished
            // just as the chat was switched): keep it out of this chat's file and model context.
            if (!isUser && _lastAssistantBubble != null && _lastAssistantBubbleEpoch != _conversationEpoch)
            {
                if (e.Message != null)
                    _history.RemoveWhere(m => ReferenceEquals(m, e.Message));
                return;
            }

            if (!_settings.SaveConversationHistory)
                return;

            AssistantMessageUi? bubble = null;
            if (!isUser && _lastAssistantBubble != null && !_recordedBubbles.TryGetValue(_lastAssistantBubble, out _))
                bubble = _lastAssistantBubble;

            var now = DateTime.UtcNow;
            _currentConversation ??= ConversationStore.Create(now, ModelCombo.Text?.Trim() ?? "", _settings.ActivePersona);
            var message = _currentConversation.AddMessage(role, content, now, imagePaths);
            if (!isUser && !string.IsNullOrWhiteSpace(ModelCombo.Text))
                _currentConversation.Model = ModelCombo.Text.Trim();
            if (!isUser && !string.IsNullOrWhiteSpace(_settings.ActivePersona))
                _currentConversation.Persona = _settings.ActivePersona;

            if (bubble != null)
            {
                _recordedBubbles.AddOrUpdate(bubble, new RecordedMessage(_currentConversation, message));
                if (_unrecordedBubbleMedia.TryGetValue(bubble, out var media))
                {
                    message.AudioPath = media.AudioPath ?? message.AudioPath;
                    message.ImagePaths.AddRange(media.ImagePaths);
                    _unrecordedBubbleMedia.Remove(bubble);
                }
            }

            QueueConversationSave(_currentConversation);
            if (IsHistoryPanelOpen)
                ScheduleHistoryListRefresh();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Conversation history recording failed: {ex.Message}");
        }
    }

    /// <summary>Called by AddUserMessage; the next recorded user message picks these up.</summary>
    private void NoteUserMessageImages(IEnumerable<string> imagePaths)
    {
        if (_restoringConversation)
            return;

        _pendingUserImagePaths = imagePaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
    }

    /// <summary>Called by AddAssistantMessage; the next recorded assistant message is tied to this bubble.</summary>
    private void NoteAssistantBubble(AssistantMessageUi bubble)
    {
        if (_restoringConversation)
            return;

        _lastAssistantBubble = bubble;
        _lastAssistantBubbleEpoch = _conversationEpoch;
    }

    private void RecordAssistantAudio(AssistantMessageUi bubble, string audioPath) =>
        RecordBubbleMedia(bubble, audioPath, isAudio: true);

    private void RecordAssistantImage(AssistantMessageUi bubble, string imagePath) =>
        RecordBubbleMedia(bubble, imagePath, isAudio: false);

    private void RecordBubbleMedia(AssistantMessageUi bubble, string path, bool isAudio)
    {
        if (_restoringConversation || string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            if (_recordedBubbles.TryGetValue(bubble, out var recorded))
            {
                var message = recorded.Message;
                if (isAudio ? message.AudioPath == path : message.ImagePaths.Contains(path))
                    return;

                if (isAudio)
                    message.AudioPath = path;
                else
                    message.ImagePaths.Add(path);

                if (_settings.SaveConversationHistory)
                    SaveLateBubbleMedia(recorded);
                return;
            }

            var media = _unrecordedBubbleMedia.GetOrCreateValue(bubble);
            if (isAudio)
                media.AudioPath = path;
            else if (!media.ImagePaths.Contains(path))
                media.ImagePaths.Add(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Conversation history media recording failed: {ex.Message}");
        }
    }

    private void QueueConversationSave(StoredConversation conversation)
    {
        var snapshot = conversation.Clone();
        _ = _conversationIo.Enqueue(() => SaveConversationSnapshot(snapshot));
    }

    /// <summary>
    /// Saves audio/images that reached a bubble after its text was recorded. Speech can finish after
    /// the user moved on, so a chat that is no longer open is patched in its file rather than
    /// re-saved from the old copy in memory, which would undo a rename or drop messages added after
    /// the chat was reopened.
    /// </summary>
    private void SaveLateBubbleMedia(RecordedMessage recorded)
    {
        var current = _currentConversation;
        if (ReferenceEquals(current, recorded.Conversation))
        {
            QueueConversationSave(current);
            return;
        }

        var source = recorded.Message.Clone();
        if (current != null && current.Id == recorded.Conversation.Id)
        {
            // The chat was reopened since: copy the media onto the reopened copy of the message.
            if (CopyMessageMedia(source, current.Messages))
                QueueConversationSave(current);
            return;
        }

        var id = recorded.Conversation.Id;
        _ = _conversationIo.Enqueue(() =>
        {
            try
            {
                lock (_deletedConversationIds)
                {
                    if (_deletedConversationIds.Contains(id))
                        return;
                }

                var saved = _conversationStore.Load(id);
                if (saved != null && CopyMessageMedia(source, saved.Messages))
                    _conversationStore.Save(saved);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Conversation history media save failed: {ex.Message}");
            }
        });
    }

    // Finds the stored copy of a message (same role and time) and gives it the source's audio/images.
    private static bool CopyMessageMedia(StoredMessage source, IEnumerable<StoredMessage> messages)
    {
        var target = messages.LastOrDefault(m => m.Role == source.Role && m.TimestampUtc == source.TimestampUtc);
        if (target == null)
            return false;

        target.AudioPath = source.AudioPath ?? target.AudioPath;
        foreach (var path in source.ImagePaths.Where(p => !target.ImagePaths.Contains(p)))
            target.ImagePaths.Add(path);
        return true;
    }

    // Runs on the history queue.
    private void SaveConversationSnapshot(StoredConversation snapshot)
    {
        try
        {
            lock (_deletedConversationIds)
            {
                if (_deletedConversationIds.Contains(snapshot.Id))
                    return;
            }

            _conversationStore.Save(snapshot);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Conversation history save failed: {ex.Message}");
            Dispatcher.BeginInvoke(() =>
            {
                if (_historySaveErrorShown)
                    return;

                _historySaveErrorShown = true;
                AddSystemMessage($"Could not save chat history: {ex.Message}");
            });
        }
    }

    private async Task FlushConversationHistoryAsync(TimeSpan timeout)
    {
        try
        {
            await Task.WhenAny(_conversationIo.WhenIdleAsync(), Task.Delay(timeout));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Conversation history flush failed: {ex.Message}");
        }
    }

    // ==================== New chat / switching ====================

    /// <summary>
    /// Ends the current recording (it stays in history). The next message starts a new saved
    /// conversation. Also used by Clear Chat.
    /// </summary>
    private void StartFreshConversation()
    {
        // A reply still being generated belongs to the chat being left.
        _chatCts?.Cancel();
        _conversationEpoch++;
        if (_currentConversation is { Messages.Count: > 0 } current && _settings.SaveConversationHistory)
            QueueConversationSave(current);

        _currentConversation = null;
        _pendingUserImagePaths = new List<string>();
        _recentWebSearchContexts.Clear();
    }

    private void StartNewChat()
    {
        StartFreshConversation();
        _history.Clear();
        ChatPanel.Children.Clear();
        ShowWelcomeCard();
        _conversationStartTime = DateTime.Now;
        CloseHistoryPanel();
    }

    private void NewChat_Click(object sender, RoutedEventArgs e) => StartNewChat();

    private void ShowWelcomeCard()
    {
        if (WelcomeCard.Parent == null)
            ChatPanel.Children.Insert(0, WelcomeCard);
    }

    private static string FormatBubbleTime(DateTime? timestamp)
    {
        var local = timestamp ?? DateTime.Now;
        if (local.Date == DateTime.Today)
            return local.ToString("t");

        return local.Year == DateTime.Today.Year
            ? $"{local:MMM d}, {local:t}"
            : $"{local:MMM d, yyyy}, {local:t}";
    }

    private async void OpenConversationFromHistory(string id)
    {
        try
        {
            await OpenConversationAsync(id);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not open conversation: {ex.Message}");
        }
    }

    private async Task OpenConversationAsync(string id)
    {
        if (_currentConversation?.Id == id)
        {
            CloseHistoryPanel();
            return;
        }

        StoredConversation? conversation;
        try
        {
            conversation = await _conversationIo.Enqueue(() => _conversationStore.Load(id));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Conversation load failed: {ex.Message}");
            conversation = null;
        }

        if (conversation == null)
        {
            HistoryFooterText.Text = "That conversation could not be opened. The file may be missing or damaged.";
            return;
        }

        StartFreshConversation();
        _currentConversation = conversation;
        // Context first, so the model context always matches the open chat even if drawing fails.
        var context = conversation.Messages
            .TakeLast(_history.MaxMessages)
            .Select(m => new ChatMessage { Role = m.Role, Content = m.Content, Timestamp = m.TimestampUtc.ToLocalTime() })
            .ToList();
        _history.Seed(context);
        _conversationStartTime = DateTime.Now;
        CloseHistoryPanel();

        ChatPanel.Children.Clear();
        RenderStoredConversation(conversation);

        AddSystemMessage(context.Count < conversation.Messages.Count
            ? $"Reopened \"{conversation.DisplayTitle}\". The last {context.Count} of {conversation.Messages.Count} messages are back in context."
            : $"Reopened \"{conversation.DisplayTitle}\". The conversation is back in context.");
        SwitchToConversationPersona(conversation.Persona);
    }

    private void RenderStoredConversation(StoredConversation conversation)
    {
        _restoringConversation = true;
        try
        {
            var skip = Math.Max(0, conversation.Messages.Count - MaxRestoredBubbles);
            if (skip > 0)
                AddSystemMessage($"Showing the last {MaxRestoredBubbles} of {conversation.Messages.Count} messages.");

            foreach (var message in conversation.Messages.Skip(skip))
            {
                var local = message.TimestampUtc.ToLocalTime();
                if (message.Role == "user")
                {
                    try
                    {
                        AddUserMessage(message.Content, message.ImagePaths.Where(File.Exists).ToList(), local);
                    }
                    catch (Exception ex)
                    {
                        // A saved image that can no longer be decoded: show the text alone.
                        Debug.WriteLine($"Could not restore user message images: {ex.Message}");
                        AddUserMessage(message.Content, null, local);
                    }
                    continue;
                }

                var bubble = AddAssistantMessage("", local);
                SetAssistantMessageText(bubble, message.Content, renderCodeBlocks: ContainsFencedCodeBlock(message.Content));
                _recordedBubbles.AddOrUpdate(bubble, new RecordedMessage(conversation, message));

                try
                {
                    if (!string.IsNullOrWhiteSpace(message.AudioPath) && File.Exists(message.AudioPath))
                        AddAudioButtons(bubble, message.AudioPath);

                    foreach (var imagePath in message.ImagePaths.Where(File.Exists))
                        AddGeneratedImageToAssistantMessage(bubble, imagePath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Could not restore assistant message media: {ex.Message}");
                }
            }

            if (conversation.Messages.Count == 0)
                ShowWelcomeCard();
        }
        finally
        {
            _restoringConversation = false;
        }

        ScrollChat();
    }

    // ==================== Conversations flyout ====================

    private bool IsHistoryPanelOpen => HistoryOverlay.Visibility == Visibility.Visible;

    private static bool IsHistoryRenameBox(object? source) =>
        source is TextBox { Tag: HistoryRenameBoxTag };

    private void HistoryToggle_Click(object sender, RoutedEventArgs e) => ToggleHistoryPanel();

    private void CloseHistory_Click(object sender, RoutedEventArgs e) => CloseHistoryPanel();

    private void HistoryScrim_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CloseHistoryPanel();
        e.Handled = true;
    }

    private void ToggleHistoryPanel()
    {
        if (IsHistoryPanelOpen)
            CloseHistoryPanel();
        else
            OpenHistoryPanel();
    }

    private void OpenHistoryPanel()
    {
        HistoryOverlay.Visibility = Visibility.Visible;
        HistoryBtn.SetResourceReference(BackgroundProperty, "PrimarySoftBrush");
        HistoryBtn.SetResourceReference(ForegroundProperty, "PrimaryLightBrush");

        var duration = TimeSpan.FromMilliseconds(170);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        HistoryFlyoutShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-48, 0, duration) { EasingFunction = ease });
        HistoryOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));

        HistoryFooterText.Text = "Loading...";
        RefreshHistoryList();
        HistorySearchBox.Focus();
    }

    private void CloseHistoryPanel()
    {
        if (!IsHistoryPanelOpen)
            return;

        // Collapsing the focused search box would leave nothing focused; hand focus back to the
        // button that opened the panel so keyboard shortcuts keep working.
        var hadFocus = HistoryOverlay.IsKeyboardFocusWithin;
        HistoryOverlay.Visibility = Visibility.Collapsed;
        if (hadFocus)
            HistoryBtn.Focus();
        HistoryBtn.ClearValue(BackgroundProperty);
        HistoryBtn.ClearValue(ForegroundProperty);
        _historySearchTimer?.Stop();
        _historySearchCts?.Cancel();
        _historyListVersion++;
        HistorySearchBox.Text = "";
        HistoryListPanel.Children.Clear();
    }

    private void HistorySearchBox_TextChanged(object sender, TextChangedEventArgs e) => ScheduleHistoryListRefresh();

    private void HistorySearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return)
            return;

        // Enter opens the top result.
        var first = HistoryListPanel.Children.OfType<Border>().Select(b => b.Tag).OfType<HistoryRowState>().FirstOrDefault();
        if (first != null)
            OpenConversationFromHistory(first.Summary.Id);
        e.Handled = true;
    }

    private void ScheduleHistoryListRefresh()
    {
        if (_historySearchTimer == null)
        {
            _historySearchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _historySearchTimer.Tick += (_, _) =>
            {
                _historySearchTimer.Stop();
                if (IsHistoryPanelOpen)
                    RefreshHistoryList();
            };
        }

        _historySearchTimer.Stop();
        _historySearchTimer.Start();
    }

    private async void RefreshHistoryList()
    {
        try
        {
            var version = ++_historyListVersion;
            _historySearchCts?.Cancel();
            var cts = new CancellationTokenSource();
            _historySearchCts = cts;
            var query = HistorySearchBox.Text.Trim();

            // A blank query lists everything; queued behind pending saves so the list is current.
            var results = await _conversationIo.Enqueue(() => _conversationStore.Search(query, cts.Token));
            if (version != _historyListVersion || !IsHistoryPanelOpen)
                return;

            RenderHistoryList(results, query);
        }
        catch (OperationCanceledException)
        {
            // A newer search replaced this one.
        }
        catch (Exception ex)
        {
            HistoryFooterText.Text = $"Could not read saved conversations: {ex.Message}";
        }
    }

    private void RenderHistoryList(IReadOnlyList<ConversationSummary> results, string query)
    {
        HistoryListPanel.Children.Clear();
        if (results.Count == 0)
        {
            HistoryListPanel.Children.Add(new TextBlock
            {
                Text = query.Length == 0
                    ? (_settings.SaveConversationHistory
                        ? "No saved conversations yet. Your chats will show up here."
                        : "No saved conversations. Saving is turned off in Chat History settings.")
                    : $"No conversations match \"{query}\".",
                FontSize = 12,
                Foreground = FindResource("TextMutedBrush") as Brush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 8, 4, 0)
            });
            HistoryFooterText.Text = "";
            return;
        }

        var now = DateTime.Now;
        ConversationAge? group = null;
        foreach (var summary in results)
        {
            var local = summary.UpdatedUtc.ToLocalTime();
            var age = ConversationGrouping.GetAge(local, now);
            if (age != group)
            {
                group = age;
                HistoryListPanel.Children.Add(new TextBlock
                {
                    Text = ConversationGrouping.Label(age).ToUpperInvariant(),
                    Style = (Style)FindResource("SubHeading"),
                    Margin = new Thickness(6, HistoryListPanel.Children.Count == 0 ? 0 : 12, 0, 6)
                });
            }

            HistoryListPanel.Children.Add(CreateHistoryRow(summary, local, now));
        }

        HistoryFooterText.Text = query.Length == 0
            ? $"{results.Count} saved conversation{(results.Count == 1 ? "" : "s")}"
            : $"{results.Count} match{(results.Count == 1 ? "" : "es")} for \"{query}\"";
    }

    private Border CreateHistoryRow(ConversationSummary summary, DateTime local, DateTime now)
    {
        var state = new HistoryRowState(summary);
        var isCurrent = summary.Id == _currentConversation?.Id;
        var restingBackground = isCurrent ? FindResource("PrimarySoftBrush") as Brush : Brushes.Transparent;

        var row = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7, 4, 7),
            Margin = new Thickness(0, 0, 0, 2),
            Background = restingBackground,
            Cursor = Cursors.Hand,
            Tag = state,
            ToolTip = summary.DisplayTitle
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = summary.DisplayTitle,
            FontSize = 12.5,
            FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = FindResource("TextPrimaryBrush") as Brush,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        text.Children.Add(title);
        text.Children.Add(new TextBlock
        {
            Text = $"{ConversationGrouping.FormatTime(local, now)}  ·  {summary.MessageCount} message{(summary.MessageCount == 1 ? "" : "s")}",
            FontSize = 10.5,
            Foreground = FindResource("TextMutedBrush") as Brush,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrWhiteSpace(summary.Snippet))
        {
            text.Children.Add(new TextBlock
            {
                Text = summary.Snippet,
                FontSize = 11,
                Foreground = FindResource("TextSecondaryBrush") as Brush,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 32,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }
        grid.Children.Add(text);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Visibility = Visibility.Hidden
        };
        var renameBtn = CreateHistoryRowButton("", "Rename");
        renameBtn.Click += (_, _) => BeginHistoryRename(state, text, title);
        var deleteBtn = CreateHistoryRowButton("", "Delete");
        deleteBtn.Click += (_, _) => ConfirmHistoryDelete(row, state);
        actions.Children.Add(renameBtn);
        actions.Children.Add(deleteBtn);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        row.Child = grid;

        row.MouseEnter += (_, _) =>
        {
            if (!isCurrent)
                row.Background = FindResource("SurfaceHoverBrush") as Brush;
            actions.Visibility = Visibility.Visible;
        };
        row.MouseLeave += (_, _) =>
        {
            row.Background = restingBackground;
            if (!state.Busy)
                actions.Visibility = Visibility.Hidden;
        };
        row.MouseLeftButtonUp += (_, e) =>
        {
            if (state.Busy)
                return;

            e.Handled = true;
            OpenConversationFromHistory(summary.Id);
        };

        return row;
    }

    private Button CreateHistoryRowButton(string glyph, string toolTip)
    {
        var button = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 26,
            Height = 26,
            ToolTip = toolTip
        };
        Ui.SetIcon(button, glyph);
        Ui.SetIconSize(button, 12);
        return button;
    }

    private void BeginHistoryRename(HistoryRowState state, Panel text, TextBlock title)
    {
        if (state.Busy)
            return;

        state.Busy = true;
        var box = new TextBox
        {
            Text = state.Summary.Title,
            FontSize = 12.5,
            Tag = HistoryRenameBoxTag,
            Padding = new Thickness(4, 2, 4, 2)
        };
        Ui.SetPlaceholder(box, StoredConversation.DefaultTitle);
        var index = text.Children.IndexOf(title);
        text.Children.RemoveAt(index);
        text.Children.Insert(index, box);

        var finished = false;
        void Finish(bool commit)
        {
            if (finished)
                return;

            finished = true;
            if (commit)
            {
                CommitHistoryRename(state.Summary, box.Text);
                return;
            }

            text.Children.Remove(box);
            text.Children.Insert(index, title);
            state.Busy = false;
        }

        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                Finish(commit: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Finish(commit: false);
                e.Handled = true;
            }
        };
        box.LostKeyboardFocus += (_, _) => Finish(commit: true);
        box.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
    }

    private async void CommitHistoryRename(ConversationSummary summary, string newTitle)
    {
        try
        {
            var title = Regex.Replace(newTitle ?? "", @"\s+", " ").Trim();
            if (title.Length > 0 && title != summary.Title)
            {
                if (_currentConversation?.Id == summary.Id)
                {
                    // The open chat is re-saved after every message, so rename it in memory too.
                    _currentConversation.Title = title;
                    QueueConversationSave(_currentConversation);
                }
                else
                {
                    await _conversationIo.Enqueue(() => _conversationStore.Rename(summary.Id, title));
                }
            }
        }
        catch (Exception ex)
        {
            HistoryFooterText.Text = $"Rename failed: {ex.Message}";
        }

        if (IsHistoryPanelOpen)
            RefreshHistoryList();
    }

    private void ConfirmHistoryDelete(Border row, HistoryRowState state)
    {
        if (state.Busy)
            return;

        state.Busy = true;
        var original = row.Child;

        var confirm = new DockPanel { LastChildFill = true };
        var cancelBtn = new Button
        {
            Content = "Cancel",
            Style = (Style)FindResource("BubbleActionButton"),
            Margin = new Thickness(0)
        };
        var deleteBtn = new Button
        {
            Content = "Delete",
            Style = (Style)FindResource("BubbleDangerButton"),
            Margin = new Thickness(0, 0, 6, 0)
        };
        DockPanel.SetDock(cancelBtn, Dock.Right);
        DockPanel.SetDock(deleteBtn, Dock.Right);
        confirm.Children.Add(cancelBtn);
        confirm.Children.Add(deleteBtn);
        confirm.Children.Add(new TextBlock
        {
            Text = $"Delete \"{state.Summary.DisplayTitle}\"?",
            FontSize = 12,
            Foreground = FindResource("TextPrimaryBrush") as Brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        row.Child = confirm;

        cancelBtn.Click += (_, _) =>
        {
            row.Child = original;
            state.Busy = false;
        };
        deleteBtn.Click += (_, _) => DeleteConversationFromHistory(state.Summary.Id);
    }

    private async void DeleteConversationFromHistory(string id)
    {
        try
        {
            if (_currentConversation?.Id == id)
            {
                // Deleting the open chat also clears it from the screen.
                StartFreshConversation();
                _history.Clear();
                ChatPanel.Children.Clear();
                ShowWelcomeCard();
                _conversationStartTime = DateTime.Now;
            }

            await _conversationIo.Enqueue(() =>
            {
                _conversationStore.Delete(id);
                // Late audio for a bubble of this chat must not bring the file back.
                lock (_deletedConversationIds)
                    _deletedConversationIds.Add(id);
            });
        }
        catch (Exception ex)
        {
            HistoryFooterText.Text = $"Delete failed: {ex.Message}";
        }

        if (IsHistoryPanelOpen)
            RefreshHistoryList();
    }
}
