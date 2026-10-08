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

    private const RegexOptions DumpOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
    private static readonly Regex ChannelThought = new(@"<\|?channel\|?>\s*thought|thought\s*<\|?channel\|?>|thoughtthought", DumpOptions);
    private static readonly Regex DumpOpening = new(@"\A(?:\*\*)?(?:User asks|The user is asking|Context|Self-Correction|Correction|Previous response)\b", DumpOptions);
    private static readonly Regex[] DumpMarkers =
    {
        new(@"\buser asks\b", DumpOptions),
        new(@"\bthe user (?:is asking|asks|wants|said)\b", DumpOptions),
        new(@"\bself-correction\b", DumpOptions),
        new(@"(?<![\w-])correction\s*(?:\*\*)?\s*:", DumpOptions),
        new(@"(?<![\w-])context\s*(?:\*\*)?\s*:", DumpOptions),
        new(@"\bprevious response\b", DumpOptions),
        new(@"\bsystem prompt\b", DumpOptions),
        new(@"\bprompt history\b", DumpOptions),
        new(@"\bvideo/conversation\b", DumpOptions),
    };

    /// <summary>
    /// True when a reply is the model's leaked planning notes ("User asks: ... Context: ...
    /// Self-Correction: ...") or channel/thought tokens instead of an answer. A normal reply that only
    /// starts with "Context" or "Correction" is not enough: it must open with such a label and
    /// contain at least three different reasoning markers.
    /// </summary>
    public static bool LooksLikeLeakedReasoning(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var sample = text.TrimStart();
        if (ChannelThought.IsMatch(sample))
            return true;
        if (!DumpOpening.IsMatch(sample))
            return false;

        var markers = 0;
        foreach (var marker in DumpMarkers)
        {
            if (marker.IsMatch(sample) && ++markers >= 3)
                return true;
        }

        return false;
    }

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
