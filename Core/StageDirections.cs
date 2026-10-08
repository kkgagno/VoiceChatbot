using System.Text;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Removes roleplay-style stage directions some models add to replies, such as
/// "(The AI responds with a warm, reassuring tone.)" or "*smiles*". Only lines (or a line's
/// leading segment) fully wrapped in parentheses, brackets or asterisks that read like narration
/// are removed; ordinary parentheticals and code are left alone.
/// </summary>
public static class StageDirections
{
    private const string Cues =
        @"\b(?:the\s+ai|ai|the\s+assistant|assistant|respond(?:s|ing)?|repl(?:y|ies|ying)|tone|voice|" +
        @"smil\w*|laugh\w*|chuckl\w*|giggl\w*|grin\w*|sigh\w*|paus\w*|nod\w*|wink\w*|whisper\w*|" +
        @"clears?\s+(?:my|his|her|its|their)?\s*throat|lean\w*|warmly|softly|gently|cheerfully|" +
        @"enthusiastically|thoughtfully|playfully|excitedly|reassuring\w*)\b";

    // Whole line wrapped in (...), [...], *...* or _..._.
    private static readonly Regex WholeLine = new(
        @"^\s*(?:\((?<t>[^()\n]{2,240})\)|\[(?<t>[^\[\]\n]{2,240})\]|\*{1,2}(?<t>[^*\n]{2,240})\*{1,2}|_(?<t>[^_\n]{2,240})_)\s*[.!]?\s*$",
        RegexOptions.Compiled);

    // A wrapped segment at the start of a line followed by the actual words: "(smiles) Hello!".
    private static readonly Regex LeadingSegment = new(
        @"^(?<indent>\s*)(?:\((?<t>[^()\n]{2,120})\)|\*{1,2}(?<t>[^*\n]{2,120})\*{1,2})\s+(?=\S)",
        RegexOptions.Compiled);

    private static readonly Regex CueRegex = new(Cues, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Strip(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        var lines = text.Replace("\r\n", "\n").Split('\n');
        var output = new StringBuilder(text.Length);
        var inCode = false;
        var removedAny = false;

        foreach (var line in lines)
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inCode = !inCode;

            var kept = line;
            if (!inCode)
            {
                var whole = WholeLine.Match(line);
                if (whole.Success && CueRegex.IsMatch(whole.Groups["t"].Value))
                {
                    removedAny = true;
                    continue;
                }

                var leading = LeadingSegment.Match(line);
                if (leading.Success && CueRegex.IsMatch(leading.Groups["t"].Value))
                {
                    kept = leading.Groups["indent"].Value + line[leading.Length..];
                    removedAny = true;
                }
            }

            output.Append(kept).Append('\n');
        }

        if (!removedAny)
            return text;

        var result = output.ToString().TrimEnd('\n');
        // Collapse the blank lines left behind by removed lines.
        result = Regex.Replace(result, @"\n{3,}", "\n\n");
        return result.Trim('\n');
    }
}
