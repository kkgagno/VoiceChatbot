using System;
using System.Windows;

namespace VoiceChatbot;

/// <summary>
/// Shows a knowledge folder file's indexed text, exactly as the assistant reads it, read-only in a
/// monospace font with a Copy button: the way to check what a PDF form or table came out as.
/// Opened from View text in the Files list.
/// </summary>
public partial class KnowledgeTextWindow : Window
{
    // A TextBox gets slow with many megabytes of text; Copy still copies all of it.
    private const int MaxShownChars = 1_000_000;

    private readonly string _text;

    public KnowledgeTextWindow(string displayName, string text)
    {
        InitializeComponent();
        WindowTheme.UseThemedTitleBar(this);
        _text = text ?? "";

        Title = $"{displayName} - indexed text";
        FileText.Text = displayName;
        FileText.ToolTip = displayName;
        var lines = 1;
        foreach (var c in _text)
        {
            if (c == '\n')
                lines++;
        }

        InfoText.Text = $"What the assistant reads from this file: {_text.Length:N0} characters, {lines:N0} lines. " +
                        $"PDF pages are read row by row as they look on the page; \"{PdfLayoutText.ColumnSeparator.Trim()}\" marks a wide gap between columns, " +
                        "so a form's label and its amount share a line.";
        TextView.Text = _text.Length > MaxShownChars
            ? _text[..MaxShownChars] + $"\n\n[Showing the first {MaxShownChars:N0} of {_text.Length:N0} characters. Copy copies all of it.]"
            : _text;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_text);
            StatusText.Text = "Copied.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Copy failed: {FriendlyErrors.Describe(ex)}";
        }
    }

    private void Wrap_Changed(object sender, RoutedEventArgs e)
    {
        var wrap = WrapBox.IsChecked == true;
        TextView.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        TextView.HorizontalScrollBarVisibility = wrap ? System.Windows.Controls.ScrollBarVisibility.Disabled : System.Windows.Controls.ScrollBarVisibility.Auto;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
