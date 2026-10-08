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
            OnLiveTranscriptionContextUpdated)
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

        var cleaned = MarkdownText.ToPlainText(CleanDisplayText(summary));
        if (!string.IsNullOrWhiteSpace(cleaned))
            AddSystemMessage("Live transcript summary updated. Main chat has the latest transcription context.");

        return cleaned;
    }

    private void OnLiveTranscriptionContextUpdated(string transcript, string summary)
    {
        _latestLiveTranscript = transcript.Trim();
        _latestLiveTranscriptSummary = summary.Trim();
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
                    Summary = MarkdownText.ToPlainText(CleanDisplayText(summary)),
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
