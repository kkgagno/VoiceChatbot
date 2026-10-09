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

public partial class MainWindow
{
    private void ClearChat_Click(object sender, RoutedEventArgs e)
    {
        // Chats are saved in Conversations, so only interrupting a reply needs a confirmation.
        if (!SendBtn.IsEnabled)
        {
            var answer = MessageBox.Show(this,
                "A reply is still in progress. Stop it and start a new chat? The current chat stays in Conversations.",
                "Clear Chat", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        // A true fresh start: cancels the turn still running and drops hidden context (live
        // transcript, web results, a staged Hermes command) that later system prompts would still carry.
        StartFreshConversation();
        _history.Clear();
        _recentWebSearchContexts.Clear();
        _hermesApprovals.Clear();
        _latestLiveTranscript = "";
        _latestLiveTranscriptSummary = "";
        ChatPanel.Children.Clear();
        AddSystemMessage(_transcriptionWindow != null
            ? "Chat cleared. The Transcribe window is still open, so its next update adds transcript context again."
            : "Chat cleared.");
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
            AddSystemMessage($"Export failed: {FriendlyErrors.Describe(ex)}");
        }
    }

    private void Transcribe_Click(object sender, RoutedEventArgs e)
    {
        if (_transcriptionWindow != null)
        {
            _transcriptionWindow.ActivateExisting();
            return;
        }

        // Not owned by the main window: an owned window always stays on top of it, and the transcriber
        // is large and often left running beside the chat. The app closes it on exit (Window_Closing).
        _transcriptionWindow = new TranscriptionWindow(
            _speech,
            _settings.Transcriber,
            () => SettingsManager.Save(_settings, userChange: false),
            SummarizeLiveTranscriptAsync,
            OnLiveTranscriptionContextUpdated,
            SendLiveTranscriptToChat);
        _transcriptionWindow.Closed += (_, _) =>
        {
            _transcriptionWindow = null;
            TranscribeBtn.Content = "Transcribe";
        };
        _transcriptionWindow.Show();
        TranscribeBtn.Content = "Transcribing";
        AddSystemMessage("Live transcription window opened. Main chat will use its transcript and summary as context.");
    }

    /// <summary>
    /// The desktop Live Transcriber's summary callback: <see cref="RequestTranscriptSummaryAsync"/>, plus a chat
    /// note when a manual summary is finished (not for every live-notes update). Runs on the UI thread.
    /// </summary>
    private async Task<string> SummarizeLiveTranscriptAsync(TranscriptSummaryRequest request, string systemPrompt, CancellationToken ct)
    {
        var cleaned = await RequestTranscriptSummaryAsync(request, systemPrompt, ct);
        if (!string.IsNullOrWhiteSpace(cleaned) && request.IsFinal && !request.IsLiveUpdate)
            AddSystemMessage("Live transcript summary updated. Main chat has the latest transcription context.");

        return cleaned;
    }

    /// <summary>
    /// Sends one transcriber request (a summary, a part of a long transcript, combining parts, or live-notes
    /// section notes and summary; see TranscriptSummaryPrompts) to the chat model and returns the reply as plain
    /// text. Used by the desktop Live Transcriber and the web transcriber. Runs on the UI thread (it reads the
    /// model picker). It uses the same context window as normal chat (num_ctx on Ollama), so a long request is
    /// not silently cut to the server's default window.
    /// </summary>
    private async Task<string> RequestTranscriptSummaryAsync(TranscriptSummaryRequest request, string systemPrompt, CancellationToken ct)
    {
        var model = ModelCombo.Text;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Select a model first.");

        var messages = new List<ChatMessage>
        {
            new()
            {
                Role = "user",
                Content = request.UserMessage
            }
        };

        var contextTokens = await GetContextTokensForRequestAsync(model, ct);
        var summary = await _ollama.ChatAsync(
            model,
            messages,
            string.IsNullOrWhiteSpace(systemPrompt) ? TranscriptSummaryPrompts.DefaultSystemPrompt : systemPrompt,
            0.2,
            TokenBudget.ResolveMaxTokens(_settings.MaxTokens, isArtifactRequest: false, contextTokens),
            ct,
            contextTokens);

        return MarkdownText.ToPlainText(CleanDisplayText(summary, hidePlanningNotes: false));
    }

    private void OnLiveTranscriptionContextUpdated(string transcript, string summary)
    {
        _latestLiveTranscript = transcript.Trim();
        _latestLiveTranscriptSummary = summary.Trim();
    }

    /// <summary>
    /// "Send to chat" in the transcriber: makes the transcript (and summary) the chat's transcription
    /// context, brings this window forward and starts a question about it in the message box.
    /// </summary>
    private void SendLiveTranscriptToChat(string transcript, string summary)
    {
        OnLiveTranscriptionContextUpdated(transcript, summary);
        ShowFromTray();

        AddSystemMessage($"Live transcript added to the chat context {DescribeTranscriptContext(transcript, summary, "summary")} Ask about it below.");

        // Keep a draft the user already typed.
        if (string.IsNullOrWhiteSpace(MessageInput.Text))
            MessageInput.Text = "Using the transcript, ";
        MessageInput.Focus();
        MessageInput.CaretIndex = MessageInput.Text.Length;
    }

    /// <summary>
    /// "(1,234 words of transcript and the summary)." plus a note when the chat sees only the end of a long
    /// transcript. <paramref name="summaryName"/> is what the transcriber calls its summary ("summary", "notes").
    /// </summary>
    private static string DescribeTranscriptContext(string transcript, string summary, string summaryName)
    {
        var words = LiveTranscriptText.CountWords(transcript);
        var parts = new List<string>();
        if (words > 0)
            parts.Add(words == 1 ? "1 word of transcript" : $"{words:N0} words of transcript");
        if (!string.IsNullOrWhiteSpace(summary))
            parts.Add($"the {summaryName}");
        if (parts.Count == 0)
            parts.Add("the transcript");
        var note = transcript.Trim().Length > LiveTranscriptContextChars
            ? $" The chat sees the last {LiveTranscriptContextChars:N0} characters of the transcript" +
              (string.IsNullOrWhiteSpace(summary) ? "; summarize it first to cover all of it." : $" plus the {summaryName}.")
            : "";
        return $"({string.Join(" and ", parts)}).{note}";
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
                    Summary = MarkdownText.ToPlainText(CleanDisplayText(summary, hidePlanningNotes: false)),
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
        var count = MemoryManager.LoadAll().Count;
        if (count == 0)
        {
            AddSystemMessage("There are no saved memories to clear.");
            return;
        }

        var question = count == 1
            ? "Delete the 1 saved memory? This cannot be undone."
            : $"Delete all {count} saved memories? This cannot be undone.";
        var answer = MessageBox.Show(this, question, "Clear All Memories", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;

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
                Background = FindResource("InputBrush") as Brush,
                BorderBrush = FindResource("BorderBrush") as Brush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var stack = new StackPanel();

            var header = new TextBlock
            {
                Text = mem.Timestamp,
                FontSize = 9,
                FontWeight = FontWeights.Bold,
                Foreground = FindResource("PrimaryLightBrush") as Brush
            };
            stack.Children.Add(header);

            var detail = new TextBlock
            {
                Text = $"Messages: {mem.MessageCount} | Duration: {mem.DurationMinutes:F0} min | Model: {mem.Model}",
                FontSize = 9,
                Foreground = FindResource("TextMutedBrush") as Brush
            };
            stack.Children.Add(detail);

            var summary = new TextBlock
            {
                Text = mem.Summary.Length > 200 ? mem.Summary[..200] + "..." : mem.Summary,
                FontSize = 11,
                Foreground = FindResource("TextPrimaryBrush") as Brush,
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
                Style = (Style)FindResource("BubbleActionButton"),
                Content = "Edit",
                Tag = mem
            };
            editBtn.Click += EditSingleMemory_Click;
            actions.Children.Add(editBtn);

            var delBtn = new Button
            {
                Style = (Style)FindResource("BubbleDangerButton"),
                Content = "Delete",
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
                Foreground = FindResource("TextMutedBrush") as Brush,
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
            Foreground = FindResource("PrimaryLightBrush") as Brush,
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
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6)
        };
        panel.Children.Add(editor);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var saveBtn = new Button
        {
            Style = (Style)FindResource("BubbleActionButton"),
            Content = "Save"
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
            Style = (Style)FindResource("BubbleActionButton"),
            Content = "Cancel"
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
}
