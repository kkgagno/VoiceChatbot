using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VoiceChatbot;

/// <summary>
/// Sizes and text layout for OCR: how large to render a PDF page (about 200 DPI, within the OCR
/// engine's largest image), how far to scale down a big photo, and how pages are joined ("Page n").
/// </summary>
public static class OcrText
{
    /// <summary>The resolution scanned PDF pages are rendered at for OCR.</summary>
    public const int PdfRenderDpi = 200;

    /// <summary>
    /// Scales <paramref name="width"/> x <paramref name="height"/> down (never up) so neither side is
    /// larger than <paramref name="max"/>, keeping the aspect ratio. Sides are at least 1.
    /// </summary>
    public static (uint Width, uint Height) FitWithin(uint width, uint height, uint max)
    {
        if (width == 0 || height == 0 || max == 0)
            return (Math.Max(1u, width), Math.Max(1u, height));
        if (width <= max && height <= max)
            return (width, height);

        var scale = Math.Min((double)max / width, (double)max / height);
        var w = (uint)Math.Clamp(Math.Round(width * scale), 1, max);
        var h = (uint)Math.Clamp(Math.Round(height * scale), 1, max);
        return (w, h);
    }

    /// <summary>
    /// The pixel size to render a PDF page at: <paramref name="dpi"/> dots per inch of its size in
    /// device-independent pixels (1/96 inch), scaled down to fit <paramref name="max"/>. A page with
    /// no size gets a letter page's size.
    /// </summary>
    public static (uint Width, uint Height) PdfRenderSize(double widthDips, double heightDips, int dpi, uint max)
    {
        if (!(widthDips > 0) || !(heightDips > 0) || double.IsInfinity(widthDips) || double.IsInfinity(heightDips))
        {
            widthDips = 8.5 * 96;
            heightDips = 11 * 96;
        }

        var scale = Math.Max(1, dpi) / 96.0;
        var width = (uint)Math.Clamp(Math.Round(widthDips * scale), 1, uint.MaxValue / 2);
        var height = (uint)Math.Clamp(Math.Round(heightDips * scale), 1, uint.MaxValue / 2);
        return FitWithin(width, height, max);
    }

    /// <summary>Joins page texts as "Page 1\n...\n\nPage 2\n...", skipping pages with no text.</summary>
    public static string JoinPages(IEnumerable<(int Page, string Text)> pages)
    {
        var output = new StringBuilder();
        foreach (var (page, text) in pages.OrderBy(p => p.Page))
        {
            var trimmed = text?.Trim() ?? "";
            if (trimmed.Length == 0)
                continue;
            if (output.Length > 0)
                output.Append("\n\n");
            output.Append("Page ").Append(page).Append('\n').Append(trimmed);
        }
        return output.ToString();
    }

    /// <summary>True when OCR output holds real words (not just a few stray marks from a photo).</summary>
    public static bool HasWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Count(w => w.Count(char.IsLetterOrDigit) >= 2);
        return words >= 1 && DocumentFileTypes.CountLetters(text) >= 3;
    }
}
