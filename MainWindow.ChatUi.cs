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
    // ==================== UI Helpers ====================

    private Border AddUserMessage(string text, IEnumerable<string>? imagePaths = null)
    {
        HideWelcomeCard();
        var border = new Border
        {
            Background = FindResource("UserBubbleBrush") as Brush,
            CornerRadius = new CornerRadius(18, 18, 6, 18),
            Padding = new Thickness(16, 10, 16, 12),
            Margin = new Thickness(80, 8, 4, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = UserBubbleMaxWidth
        };

        var stack = new StackPanel();
        var header = new TextBlock
        {
            Text = $"You  ·  {DateTime.Now:t}",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
            Margin = new Thickness(0, 0, 0, 4)
        };
        var body = CreateSelectableText(text, Brushes.White);
        body.FontSize = 14;

        stack.Children.Add(header);
        foreach (var imagePath in imagePaths ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                continue;

            var image = new Image
            {
                Source = new BitmapImage(new Uri(imagePath)),
                MaxWidth = 260,
                MaxHeight = 200,
                Stretch = Stretch.Uniform
            };
            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Margin = new Thickness(0, 2, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = image
            });
        }
        stack.Children.Add(body);
        border.Child = stack;
        ChatPanel.Children.Add(border);
        ScrollChat();
        return border;
    }

    private void HideWelcomeCard()
    {
        if (WelcomeCard.Parent is Panel panel)
            panel.Children.Remove(WelcomeCard);
    }

    private void UpdateChatBubbleWidths()
    {
        if (ChatPanel is null)
            return;

        foreach (var child in ChatPanel.Children.OfType<Border>())
        {
            if (child.HorizontalAlignment == HorizontalAlignment.Right)
                child.MaxWidth = UserBubbleMaxWidth;
            else if (child.HorizontalAlignment == HorizontalAlignment.Left)
                child.MaxWidth = AssistantBubbleMaxWidth;

            UpdateCodeBlockWidths(child);
        }
    }

    private void UpdateCodeBlockWidths(DependencyObject parent)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border border && border.Tag as string == "CodeBlock")
                border.MaxWidth = CodeBlockMaxWidth;

            UpdateCodeBlockWidths(child);
        }
    }

    private AssistantMessageUi AddAssistantMessage(string text)
    {
        // Outer border keeps HorizontalAlignment.Left so UpdateChatBubbleWidths can resize it.
        var border = new Border
        {
            Margin = new Thickness(0, 8, 60, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            MaxWidth = AssistantBubbleMaxWidth
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var avatar = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(10),
            Background = FindResource("BrandGradientBrush") as Brush,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 10, 0),
            Child = new TextBlock
            {
                Text = "\uE99A",
                FontFamily = FindResource("IconFont") as FontFamily,
                FontSize = 14,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        row.Children.Add(avatar);

        var card = new Border
        {
            Background = FindResource("CardBgBrush") as Brush,
            BorderBrush = FindResource("BorderBrush") as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6, 18, 18, 18),
            Padding = new Thickness(16, 10, 16, 12)
        };
        Grid.SetColumn(card, 1);
        row.Children.Add(card);

        var stack = new StackPanel();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 4), LastChildFill = false };
        var nameBlock = new TextBlock
        {
            Text = $"Assistant  ·  {DateTime.Now:t}",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = FindResource("AccentBrush") as SolidColorBrush,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(nameBlock, Dock.Left);
        header.Children.Add(nameBlock);
        stack.Children.Add(header);

        var content = new StackPanel();
        var body = CreateSelectableText(text, FindResource("TextPrimaryBrush") as Brush ?? Brushes.White);
        body.FontSize = 14;
        content.Children.Add(body);

        var copyButton = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Width = 24,
            Height = 22,
            ToolTip = "Copy response",
            Opacity = 0.7
        };
        Ui.SetIcon(copyButton, "\uE8C8");
        Ui.SetIconSize(copyButton, 12);
        copyButton.Click += (_, _) => CopyBubbleText(content);
        DockPanel.SetDock(copyButton, Dock.Right);
        header.Children.Add(copyButton);

        stack.Children.Add(content);
        var actions = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0),
            Visibility = Visibility.Collapsed
        };
        stack.Children.Add(actions);
        card.Child = stack;
        border.Child = row;
        ChatPanel.Children.Add(border);
        ScrollChat();
        return new AssistantMessageUi(body, content, actions);
    }

    private void SetAssistantMessageText(AssistantMessageUi assistantMessage, string text, bool renderCodeBlocks = false)
    {
        if (!renderCodeBlocks || !ContainsFencedCodeBlock(text))
        {
            assistantMessage.Body.Text = text;
            return;
        }

        RenderAssistantMessageWithCodeBlocks(assistantMessage, text);
        ScrollChat();
    }

    private void RenderAssistantMessageWithCodeBlocks(AssistantMessageUi assistantMessage, string text)
    {
        assistantMessage.Content.Children.Clear();

        var foreground = FindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
        var position = 0;

        while (position < text.Length)
        {
            var fenceStart = text.IndexOf("```", position, StringComparison.Ordinal);
            if (fenceStart < 0)
            {
                AddAssistantTextPart(assistantMessage.Content, text[position..], foreground);
                break;
            }

            if (fenceStart > position)
                AddAssistantTextPart(assistantMessage.Content, text[position..fenceStart], foreground);

            var codeStart = fenceStart + 3;
            var lineEnd = text.IndexOf('\n', codeStart);
            string language;
            if (lineEnd >= 0)
            {
                language = text[codeStart..lineEnd].Trim();
                codeStart = lineEnd + 1;
            }
            else
            {
                language = text[codeStart..].Trim();
                codeStart = text.Length;
            }

            if (!Regex.IsMatch(language, "^[A-Za-z0-9_+.#-]*$"))
            {
                language = "";
                codeStart = fenceStart + 3;
            }

            var fenceEnd = text.IndexOf("```", codeStart, StringComparison.Ordinal);
            var codeEnd = fenceEnd >= 0 ? fenceEnd : text.Length;
            var code = text[codeStart..codeEnd].Trim('\r', '\n');
            AddAssistantCodeBlock(assistantMessage.Content, language, code);

            if (fenceEnd < 0)
                break;

            position = fenceEnd + 3;
        }
    }

    private static bool ContainsFencedCodeBlock(string text) =>
        !string.IsNullOrWhiteSpace(text) && text.Contains("```", StringComparison.Ordinal);

    private void AddAssistantTextPart(StackPanel content, string text, Brush foreground)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return;

        content.Children.Add(CreateSelectableText(trimmed, foreground));
    }

    private void AddAssistantCodeBlock(StackPanel content, string language, string code)
    {
        var wrapper = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0C, 0x12)),
            BorderBrush = FindResource("BorderBrush") as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(0, 8, 0, 8),
            MaxWidth = CodeBlockMaxWidth,
            Tag = "CodeBlock",
            ClipToBounds = true
        };

        var stack = new StackPanel();
        var header = new DockPanel
        {
            Background = FindResource("SurfaceBrush") as Brush,
            LastChildFill = false
        };
        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(language) ? "code" : language.ToLowerInvariant(),
            FontSize = 11,
            FontFamily = FindResource("CodeFont") as FontFamily,
            Foreground = FindResource("TextSecondaryBrush") as Brush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        DockPanel.SetDock(label, Dock.Left);
        header.Children.Add(label);

        var copyButton = new Button
        {
            Content = "Copy",
            Style = (Style)FindResource("GhostButton"),
            FontSize = 11,
            Padding = new Thickness(10, 5, 10, 5),
            Tag = code
        };
        Ui.SetIcon(copyButton, "\uE8C8");
        Ui.SetIconSize(copyButton, 12);
        copyButton.Click += (_, _) =>
        {
            Clipboard.SetText(code);
            copyButton.Content = "Copied";
            Ui.SetIcon(copyButton, "\uE73E");
        };
        DockPanel.SetDock(copyButton, Dock.Right);
        header.Children.Add(copyButton);
        stack.Children.Add(header);

        var codeBox = CreateSelectableText(code, new SolidColorBrush(Color.FromRgb(0xE3, 0xE6, 0xF0)));
        codeBox.FontFamily = FindResource("CodeFont") as FontFamily;
        codeBox.FontSize = 12.5;
        codeBox.Padding = new Thickness(14, 10, 14, 12);
        codeBox.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        codeBox.TextWrapping = TextWrapping.NoWrap;
        stack.Children.Add(codeBox);

        wrapper.Child = stack;
        content.Children.Add(wrapper);
    }

    private void AddSystemMessage(string text)
    {
        var block = CreateSelectableText(text, FindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray);
        block.FontSize = 11.5;
        block.TextAlignment = TextAlignment.Center;
        var pill = new Border
        {
            Background = FindResource("SurfaceBrush") as Brush,
            BorderBrush = FindResource("BorderBrush") as Brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(40, 6, 40, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = block
        };
        ChatPanel.Children.Add(pill);
        ScrollChat();
    }

    private static TextBox CreateSelectableText(string text, Brush foreground)
    {
        return new TextBox
        {
            Text = text,
            FontSize = 13,
            Foreground = foreground,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            IsReadOnly = true,
            IsTabStop = false,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            Cursor = Cursors.IBeam
        };
    }

    private void ScrollChat()
    {
        Dispatcher.BeginInvoke(() =>
        {
            ChatScroll.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void SetUIState(string state, string label)
    {
        StateLabel.Text = label;
        ActivityLabel.Text = state switch
        {
            "thinking" => "... Waiting for AI...",
            "searching" => "... Searching the web...",
            "processing" => "... Generating response...",
            "idle" => "",
            _ => ""
        };

        // Enable/disable send
        SendBtn.IsEnabled = state == "idle";
        CameraBtn.IsEnabled = state == "idle";
        ImageBtn.IsEnabled = state == "idle";
        DocumentBtn.IsEnabled = state == "idle";
        KeepDocumentActiveToggle.IsEnabled = state == "idle";
        CreateImageBtn.IsEnabled = state == "idle";
        EditImageBtn.IsEnabled = state == "idle";
        AudioBtn.IsEnabled = state == "idle";
        CreateVideoBtn.IsEnabled = state == "idle";
        MessageInput.IsEnabled = state == "idle";
    }

    private void UpdateImageButtonLabel()
    {
        ImageBtn.Content = _pendingImages.Count > 0 ? $"Image ({_pendingImages.Count})" : "Image";
    }

    private void UpdateAudioButtonLabel()
    {
        AudioBtn.Content = File.Exists(_pendingVideoAudioPath) ? "Video Audio (1)" : "Video Audio";
    }

    private void UpdateDocumentButtonLabel()
    {
        if (_pendingDocuments.Count > 0)
            DocumentBtn.Content = $"Document ({_pendingDocuments.Count})";
        else if (_activeDocuments.Count > 0 && KeepDocumentActiveToggle.IsChecked == true)
            DocumentBtn.Content = $"Document Active ({_activeDocuments.Count})";
        else
            DocumentBtn.Content = "Document";
    }

    private void KeepDocumentActiveToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (KeepDocumentActiveToggle.IsChecked == true)
        {
            if (_pendingDocuments.Count > 0)
                AddSystemMessage("Keep doc is on. The next sent message will make the attached document active.");
        }
        else
        {
            if (_activeDocuments.Count > 0)
            {
                _activeDocuments.Clear();
                AddSystemMessage("Active document cleared.");
            }
        }

        UpdateDocumentButtonLabel();
    }

    // Speak the last assistant response
    private void SpeakLastResponse(string text, AssistantMessageUi? assistantMessage = null)
    {
        if (!string.IsNullOrWhiteSpace(text) && TtsToggle.IsChecked == true)
        {
            try
            {
                var speechText = CleanSpeechText(text);
                if (string.IsNullOrWhiteSpace(speechText))
                {
                    SetUIState("idle", "Ready");
                    _speech.ReadyForNextSpeech();
                    return;
                }

                // Speak sentence by sentence so playback starts after the first sentence is rendered.
                // The Replay/Download buttons appear once the whole reply has been spoken and saved.
                var session = _speech.BeginSpeechSession(GetAssistantAudioDirectory());
                AddAudioButtonsWhenSpoken(session, assistantMessage);
                foreach (var sentence in SentenceChunker.Split(speechText))
                    session.Enqueue(sentence);
                session.Complete();
                // SpeechFinished will restart auto-listen
                return;
            }
            catch
            {
                // Speech failed
            }
        }
        // No TTS or TTS disabled - mark ready, restart auto-listen if active
        SetUIState("idle", "Ready");
        _speech.ReadyForNextSpeech();

        if (_autoListening)
        {
            Task.Delay(100).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_autoListening && !_pausedListeningForTextInput)
                    {
                        if (!IsVoiceInputAllowedByFacePolicy())
                            return;

                        if (_speech.CurrentState == VoiceState.Speaking)
                            return;

                        _speech.StartListening();
                    }
                });
            });
        }
    }

    private static string GetAssistantAudioDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceChatbot",
            "assistant-audio");
    }

    private void AddAudioButtons(AssistantMessageUi assistantMessage, string audioPath)
    {
        assistantMessage.Actions.Children.Clear();
        assistantMessage.Actions.Visibility = Visibility.Visible;

        var replayBtn = new Button
        {
            Style = (Style)FindResource("BubbleActionButton"),
            Content = "Replay Audio",
            Tag = audioPath
        };
        replayBtn.Click += ReplayAssistantAudio_Click;
        assistantMessage.Actions.Children.Add(replayBtn);

        var downloadBtn = new Button
        {
            Style = (Style)FindResource("BubbleActionButton"),
            Content = "Download Audio",
            Tag = audioPath
        };
        downloadBtn.Click += DownloadAssistantAudio_Click;
        assistantMessage.Actions.Children.Add(downloadBtn);
    }

    private void ReplayAssistantAudio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string audioPath } || !File.Exists(audioPath))
        {
            AddSystemMessage("Audio file is no longer available.");
            return;
        }

        _speech.PlayAudioFile(audioPath);
    }

    private void DownloadAssistantAudio_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string audioPath } || !File.Exists(audioPath))
        {
            AddSystemMessage("Audio file is no longer available.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save assistant audio",
            Filter = "WAV audio (*.wav)|*.wav",
            FileName = $"assistant_response_{DateTime.Now:yyyyMMdd_HHmmss}.wav",
            AddExtension = true,
            DefaultExt = ".wav"
        };

        if (dialog.ShowDialog(this) != true)
            return;

        File.Copy(audioPath, dialog.FileName, overwrite: true);
        AddSystemMessage($"Audio saved to {dialog.FileName}");
    }
}
