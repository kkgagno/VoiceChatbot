using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VoiceChatbot;

// Markdown replies: finished assistant replies are shown formatted (prose as a FlowDocument, fenced
// code with the code block UI). Streaming stays a plain text box; speech strips the syntax.
public partial class MainWindow
{
    // ==================== Markdown replies ====================

    // Marks the panel that holds a rendered reply inside an assistant bubble, and a plain text box
    // that already watches for text written after rendering.
    private const string RenderedReplyTag = "RenderedReply";
    private const string WatchedReplyBodyTag = "WatchedReplyBody";
    private bool _settingRenderedReplyText;

    // Defaults shipped before replies were rendered. A prompt still set to one of these was never
    // customized, so it moves to the new default, which allows light Markdown.
    private static readonly string[] LegacyDefaultSystemPrompts =
    {
        "You are a helpful, friendly AI assistant. Keep responses concise and conversational since they will be spoken aloud. Use plain natural language for normal conversation. If the user explicitly asks for code, markup, an SVG, or a script, provide it in a fenced code block.",
        "You are a helpful, friendly AI assistant. Keep responses concise and conversational since they will be spoken aloud. IMPORTANT: Never use emojis, hashtags, asterisks, markdown formatting, or special symbols in normal conversation. If the user explicitly asks for code, markup, an SVG, or a script, provide it in a fenced code block."
    };

    private void ApplyMarkdownSettings()
    {
        RenderMarkdownToggle.IsChecked = _settings.RenderMarkdown;

        var prompt = (_settings.SystemPrompt ?? "").Trim();
        if (LegacyDefaultSystemPrompts.Contains(prompt, StringComparer.Ordinal))
        {
            _settings.SystemPrompt = new AppSettings().SystemPrompt;
            SystemPromptBox.Text = _settings.SystemPrompt;
        }
    }

    private void SaveMarkdownSettings() => _settings.RenderMarkdown = RenderMarkdownToggle.IsChecked == true;

    private void RenderMarkdownToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.RenderMarkdown = RenderMarkdownToggle.IsChecked == true;
    }

    /// <summary>
    /// Text for the live streaming bubble. Streaming stays a cheap plain text box, so Markdown syntax
    /// is hidden there as it always was; the formatted reply replaces it when the answer completes.
    /// </summary>
    private static string GetStreamingDisplayText(string text, bool preserveCodeBlocks)
    {
        // Planning notes ("The user said hi. Wait, ..."): the answer taken out of them replaces this
        // when the reply is complete.
        if (PlanningNotes.LooksLikeStart(ReasoningText.StripThinking(text)))
            return "Thinking...";

        var cleaned = CleanDisplayText(text, preserveCodeBlocks);
        return preserveCodeBlocks ? cleaned : MarkdownText.ToPlainText(cleaned);
    }

    /// <summary>Adds a finished reply (phone remote, scheduled prompt) shown like a chat reply.</summary>
    private AssistantMessageUi AddFinishedAssistantMessage(string text)
    {
        var assistantMessage = AddAssistantMessage("");
        SetAssistantMessageText(assistantMessage, text, renderCodeBlocks: true);
        return assistantMessage;
    }

    /// <summary>
    /// Shows a finished reply. With "Format replies" on, prose is rendered as Markdown and fenced code
    /// gets the code block UI with Copy; a reply with nothing to format stays a plain text box. With it
    /// off, the Markdown syntax is removed and fenced code is drawn as code blocks when
    /// <paramref name="renderCodeBlocks"/> is set, as before.
    /// </summary>
    private void SetAssistantMessageText(AssistantMessageUi assistantMessage, string text, bool renderCodeBlocks = false)
    {
        text ??= "";
        try
        {
            if (_settings.RenderMarkdown)
                ShowMarkdownReply(assistantMessage, text);
            else if (renderCodeBlocks && ContainsFencedCodeBlock(text))
                ShowCodeBlockReply(assistantMessage, text);
            else
                ShowPlainReply(assistantMessage, MarkdownText.ToPlainText(text));
        }
        catch (Exception ex)
        {
            // Never lose a reply to a rendering problem: fall back to the text as it came.
            Debug.WriteLine($"Reply rendering failed: {ex}");
            ShowPlainReply(assistantMessage, text);
        }

        ScrollChat(onlyIfFollowing: true);
    }

    private void ShowMarkdownReply(AssistantMessageUi assistantMessage, string text)
    {
        var segments = MarkdownText.SplitFencedCode(text);
        if (segments.Count == 0)
        {
            ShowPlainReply(assistantMessage, text.Trim());
            return;
        }

        var renderer = new MarkdownRenderer(this);
        var panel = new StackPanel { Tag = RenderedReplyTag };
        foreach (var segment in segments)
        {
            if (segment.IsCode)
            {
                AddAssistantCodeBlock(panel, segment.Language, segment.Text);
                continue;
            }

            var rich = renderer.Render(segment.Text, out var plainText);
            if (rich is null && segments.Count == 1)
            {
                // Nothing to format: keep the snug plain text bubble.
                ShowPlainReply(assistantMessage, plainText);
                return;
            }

            panel.Children.Add(rich is not null ? rich : CreateReplyTextBox(plainText));
        }

        ShowRenderedReply(assistantMessage, text, panel);
    }

    /// <summary>Formatting off: plain prose with fenced code drawn as code blocks.</summary>
    private void ShowCodeBlockReply(AssistantMessageUi assistantMessage, string text)
    {
        var panel = new StackPanel { Tag = RenderedReplyTag };
        foreach (var segment in MarkdownText.SplitFencedCode(text))
        {
            if (segment.IsCode)
                AddAssistantCodeBlock(panel, segment.Language, segment.Text);
            else
                panel.Children.Add(CreateReplyTextBox(MarkdownText.ToPlainText(segment.Text)));
        }

        ShowRenderedReply(assistantMessage, text, panel);
    }

    private TextBox CreateReplyTextBox(string text)
    {
        var box = CreateSelectableText(text, FindResource("TextPrimaryBrush") as Brush ?? Brushes.White);
        box.FontSize = 14;
        return box;
    }

    private void ShowPlainReply(AssistantMessageUi assistantMessage, string text)
    {
        RemoveRenderedReply(assistantMessage);
        assistantMessage.Body.Text = text;
        assistantMessage.Body.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Puts the rendered panel right after the (now hidden) plain text box. The box keeps the reply
    /// text, and anything written to it later (an error, "[cancelled]") brings it back in place of the
    /// rendered view. Images and videos added to the bubble stay where they are.
    /// </summary>
    private void ShowRenderedReply(AssistantMessageUi assistantMessage, string text, StackPanel panel)
    {
        RemoveRenderedReply(assistantMessage);

        var body = assistantMessage.Body;
        if (body.Tag as string != WatchedReplyBodyTag)
        {
            body.Tag = WatchedReplyBodyTag;
            body.TextChanged += (_, _) =>
            {
                if (_settingRenderedReplyText)
                    return;

                RemoveRenderedReply(assistantMessage);
                body.Visibility = Visibility.Visible;
            };
        }

        _settingRenderedReplyText = true;
        try
        {
            body.Text = text;
        }
        finally
        {
            _settingRenderedReplyText = false;
        }

        var children = assistantMessage.Content.Children;
        var index = children.IndexOf(body);
        if (index >= 0)
            children.Insert(index + 1, panel);
        else
            children.Add(panel);
        body.Visibility = Visibility.Collapsed;
    }

    private static void RemoveRenderedReply(AssistantMessageUi assistantMessage)
    {
        var children = assistantMessage.Content.Children;
        foreach (var rendered in children.OfType<FrameworkElement>().Where(c => c.Tag as string == RenderedReplyTag).ToList())
            children.Remove(rendered);
    }
}
