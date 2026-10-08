using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceChatbot;

public enum ClipboardPasteKind
{
    /// <summary>Nothing this app pastes specially: let the text box do its normal paste.</summary>
    Nothing,
    /// <summary>Paste the text into the message box.</summary>
    Text,
    /// <summary>Attach the clipboard picture to the next message.</summary>
    Image
}

/// <summary>
/// Decides what Ctrl+V in the message box does. Excel, Word and other apps put copied cells or text on
/// the clipboard both as text and as a picture of the selection, so text always wins: a picture is
/// attached only when the clipboard has no text at all (a screenshot, a copied image).
/// </summary>
public static class ClipboardPastePolicy
{
    // Format names WPF reports for a picture ("Bitmap" also covers DIBs through auto-conversion).
    private static readonly string[] ImageFormats =
    [
        "Bitmap",
        "PNG",
        "DeviceIndependentBitmap",
        "DeviceIndependentBitmapV5",
        "System.Windows.Media.Imaging.BitmapSource",
        "System.Drawing.Bitmap"
    ];

    public static ClipboardPasteKind Decide(IEnumerable<string>? formats, string? text)
    {
        // Any text, even blank cells, is pasted as text rather than turned into a screenshot.
        if (!string.IsNullOrEmpty(text))
            return ClipboardPasteKind.Text;

        return formats?.Any(IsImageFormat) == true
            ? ClipboardPasteKind.Image
            : ClipboardPasteKind.Nothing;
    }

    public static bool IsImageFormat(string? format) =>
        !string.IsNullOrWhiteSpace(format) &&
        ImageFormats.Contains(format, StringComparer.OrdinalIgnoreCase);
}
