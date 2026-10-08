using System;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Decides when a message forces a web search before the model answers. With tool calling on,
/// only explicit commands ("search the web for...", "look it up online") force one; otherwise the
/// model decides through the web_search tool. Without tools the older, looser phrases still apply.
/// </summary>
public static class WebSearchPhrases
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // The original trigger list (kept as-is for the no-tools path, scheduler and phone remote).
    private static readonly string[] LegacyPhrases =
    {
        "search online", "check the internet", "search the internet", "check online", "look online", "web search"
    };

    private static readonly Regex ExplicitCommand = new(
        @"\b(?:search|check|look)\s+(?:on\s+)?(?:the\s+)?(?:web|internet|online)\b" +
        @"|\blook\s+(?:it|this|that|them|these|those)\s+up\s+online\b" +
        @"|\blook\s+up\b[^.?!\n]{0,80}?\bonline\b" +
        @"|\b(?:do|run)\s+(?:a\s+)?(?:quick\s+)?(?:web|online|internet)\s+search\b" +
        @"|^\W*web\s+search\b" +
        @"|\bweb\s+search\s+(?:for|on|about)\b" +
        @"|\bgoogle\s+(?:it|this|that|for)\b",
        Options);

    // Removed from the text before it is sent to the search engine. Order matters: longer forms first.
    private static readonly (Regex Pattern, string Replacement)[] StripRules =
    {
        (new Regex(@"\blook\s+(?:it|this|that|them)\s+up\s+online\b", Options), " "),
        (new Regex(@"\blook\s+up\s+online\b(?:\s+(?:for|about))?", Options), " "),
        (new Regex(@"\blook\s+up\s+(.+?)\s+online\b", Options), " $1 "),
        (new Regex(@"\b(?:do|run)\s+(?:a\s+)?(?:quick\s+)?(?:web|online|internet)\s+search\b(?:\s+(?:for|about|on))?", Options), " "),
        (new Regex(@"\b(?:search|check|look)\s+(?:on\s+)?(?:the\s+)?(?:web|internet|online)\b(?:\s+(?:for|about))?", Options), " "),
        (new Regex(@"\bweb\s+search\b(?:\s+(?:for|about|on))?", Options), " "),
        (new Regex(@"\bgoogle\s+(?:it|this|that|for)\b", Options), " ")
    };

    /// <summary>The older, broader trigger phrases (any mention of e.g. "web search").</summary>
    public static bool IsLegacyTrigger(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim();
        foreach (var phrase in LegacyPhrases)
        {
            if (normalized.Contains(phrase, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>An explicit request to search, such as "search the web for X" or "look it up online".</summary>
    public static bool IsExplicitCommand(string? text) =>
        !string.IsNullOrWhiteSpace(text) && ExplicitCommand.IsMatch(Regex.Replace(text, @"\s+", " "));

    /// <summary>Removes the search command words, leaving the query. Returns the input if nothing is left.</summary>
    public static string StripCommandPhrases(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        var cleaned = text;
        foreach (var (pattern, replacement) in StripRules)
            cleaned = pattern.Replace(cleaned, replacement);

        cleaned = Regex.Replace(cleaned, @"\s+([,;:.?!])", "$1");
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        cleaned = cleaned.Trim(' ', ',', ';', ':', '-');
        return Regex.IsMatch(cleaned, @"[\p{L}\p{N}]") ? cleaned : text;
    }
}
