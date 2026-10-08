using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Removes the &lt;think&gt;...&lt;/think&gt; reasoning that reasoning models (DeepSeek R1, Qwen3, ...)
/// write before their answer, so it is not shown, saved in history or spoken.
/// </summary>
public static class ReasoningText
{
    private const string OpenTag = "<think>";
    private static readonly Regex ClosedBlock = new(@"<think>[\s\S]*?</think>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Removes every closed think block and a leading think block that is not closed (yet).
    /// With <paramref name="streaming"/> set, text that may still become a leading &lt;think&gt; tag
    /// ("&lt;", "&lt;th") is held back too, so a live view never shows half a tag.
    /// </summary>
    public static string StripThinking(string? text, bool streaming = false)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('<') < 0)
            return text ?? "";

        // Called for every streamed token, so skip the regex while no block has closed.
        var stripped = text.Contains("</think>", StringComparison.OrdinalIgnoreCase)
            ? ClosedBlock.Replace(text, "")
            : text;
        var lead = stripped.TrimStart();
        if (lead.StartsWith(OpenTag, StringComparison.OrdinalIgnoreCase))
            return "";
        if (streaming && lead.Length > 0 && lead.Length < OpenTag.Length &&
            OpenTag.StartsWith(lead, StringComparison.OrdinalIgnoreCase))
            return "";

        return stripped.Length == text.Length ? text : stripped.TrimStart();
    }
}
