using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Removes markdown formatting from one sentence of raw model output so it can be spoken.
/// Mirrors the formatting steps of the chat display cleaner (emoji, emphasis, headings,
/// bullets, numbered lists, links, HTML tags) for text that has not been through it yet.
/// </summary>
public static class SpeechMarkdown
{
    public static string ToPlainText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Surrogate or UnicodeCategory.OtherSymbol)
                continue;
            if (c is >= '\u2600' and <= '\u27BF')   // Misc symbols and dingbats
                continue;
            if (c is >= '\uFE00' and <= '\uFEFF')   // Variation selectors
                continue;
            sb.Append(c);
        }

        var cleaned = sb.ToString();
        cleaned = Regex.Replace(cleaned, "`+", "");
        cleaned = Regex.Replace(cleaned, "\\*\\*(.+?)\\*\\*", "$1");
        cleaned = Regex.Replace(cleaned, "__(.+?)__", "$1");
        cleaned = Regex.Replace(cleaned, "\\*(.+?)\\*", "$1");
        cleaned = Regex.Replace(cleaned, "(?<!\\w)_(.+?)_(?!\\w)", "$1");
        cleaned = Regex.Replace(cleaned, "^\\s*#{1,6}\\s+", "", RegexOptions.Multiline);
        cleaned = Regex.Replace(cleaned, "#\\w+", "");
        cleaned = Regex.Replace(cleaned, "^\\s*[-*_]{3,}\\s*$", "", RegexOptions.Multiline);
        cleaned = Regex.Replace(cleaned, "^\\s*[-*+\u2022]\\s+", "", RegexOptions.Multiline);
        cleaned = Regex.Replace(cleaned, "^\\s*\\d+[.)]\\s+", "", RegexOptions.Multiline);
        cleaned = Regex.Replace(cleaned, "^\\s*>\\s?", "", RegexOptions.Multiline);
        cleaned = Regex.Replace(cleaned, "!?\\[([^\\]]+)\\]\\([^)]+\\)", "$1");
        cleaned = Regex.Replace(cleaned, "<[^>]+>", "");
        // Stray emphasis markers left by a sentence split inside **bold** text.
        cleaned = Regex.Replace(cleaned, "\\*+", "");
        cleaned = Regex.Replace(cleaned, "\\n{3,}", "\n\n");
        cleaned = Regex.Replace(cleaned, "[ \\t]{2,}", " ");
        return cleaned.Trim();
    }
}
