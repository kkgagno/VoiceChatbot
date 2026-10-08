using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Finds a spoken text wake word in a transcript, ignoring case and punctuation, so Whisper's
/// "Hey, assistant." matches the wake word "hey assistant".
/// </summary>
public static class WakeWordText
{
    private static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    /// <summary>
    /// True when the transcript contains the wake word; <paramref name="remainder"/> is the text after
    /// its first occurrence without leading punctuation ("Hey assistant, what time is it?" gives
    /// "what time is it?"). A wake word without any letters or digits always matches.
    /// </summary>
    public static bool TryFind(string? transcript, string? wakeWord, out string remainder)
    {
        transcript ??= "";
        var wakeWords = Word.Matches(wakeWord ?? "").Select(m => m.Value).ToList();
        if (wakeWords.Count == 0)
        {
            remainder = TrimLeadingPunctuation(transcript);
            return true;
        }

        var words = Word.Matches(transcript);
        for (var start = 0; start + wakeWords.Count <= words.Count; start++)
        {
            var matched = true;
            for (var i = 0; i < wakeWords.Count && matched; i++)
                matched = string.Equals(words[start + i].Value, wakeWords[i], StringComparison.OrdinalIgnoreCase);
            if (!matched)
                continue;

            var last = words[start + wakeWords.Count - 1];
            remainder = TrimLeadingPunctuation(transcript[(last.Index + last.Length)..]);
            return true;
        }

        remainder = "";
        return false;
    }

    private static string TrimLeadingPunctuation(string text)
    {
        var i = 0;
        while (i < text.Length && (char.IsWhiteSpace(text[i]) || char.IsPunctuation(text[i])))
            i++;
        return text[i..].TrimEnd();
    }
}
