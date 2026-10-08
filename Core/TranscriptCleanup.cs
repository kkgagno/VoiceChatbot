using System.Text;

namespace VoiceChatbot;

/// <summary>
/// Undoes Whisper's repetition failure, where one utterance comes back as the same sentence two or
/// more times ("This is bullshit.This is bullshit."). Only exact repeats (ignoring case, spacing and
/// punctuation) are removed; different sentences are always kept.
/// </summary>
public static class TranscriptCleanup
{
    private const string SentenceEnds = ".!?…";
    private const string ClosingMarks = "\"')]”’";

    /// <summary>Joins Whisper segments and removes repeats: <see cref="JoinSegments"/>, then <see cref="CollapseRepeatedSentences"/>.</summary>
    public static string Clean(IEnumerable<string?> segments) => CollapseRepeatedSentences(JoinSegments(segments));

    /// <summary>
    /// Joins segments with one space (none before a segment that starts with punctuation such as ","),
    /// skipping blank segments and a segment that repeats the one before it.
    /// </summary>
    public static string JoinSegments(IEnumerable<string?> segments)
    {
        var result = new StringBuilder();
        var previousKey = "";
        foreach (var raw in segments)
        {
            var segment = (raw ?? "").Trim();
            if (segment.Length == 0)
                continue;

            var key = Normalize(segment);
            if (key.Length > 0 && key == previousKey)
                continue;
            if (key.Length > 0)
                previousKey = key;

            if (result.Length > 0 && !StartsWithAttachedPunctuation(segment))
                result.Append(' ');
            result.Append(segment);
        }

        return result.ToString();
    }

    /// <summary>
    /// Keeps one copy of a sentence (or a run of sentences) that is repeated back to back:
    /// "X. X. X." gives "X.", "A. B. A. B." gives "A. B.". Text without such repeats comes back unchanged.
    /// </summary>
    public static string CollapseRepeatedSentences(string? text)
    {
        var trimmed = (text ?? "").Trim();
        var sentences = SplitSentences(trimmed);
        if (sentences.Count < 2)
            return trimmed;

        var keys = sentences.Select(Normalize).ToList();
        var removed = false;
        for (var start = 0; start < sentences.Count; start++)
        {
            // Try the shortest run first, and look again at the same position after each removal.
            for (var length = 1; start + 2 * length <= sentences.Count; length++)
            {
                if (!IsRepeatedRun(keys, start, length))
                    continue;

                sentences.RemoveRange(start + length, length);
                keys.RemoveRange(start + length, length);
                removed = true;
                length = 0;
            }
        }

        return removed ? string.Join(" ", sentences) : trimmed;
    }

    private static bool IsRepeatedRun(List<string> keys, int start, int length)
    {
        for (var i = 0; i < length; i++)
        {
            var key = keys[start + i];
            if (key.Length == 0 || key != keys[start + length + i])
                return false;
        }

        return true;
    }

    // Splits after ".", "!", "?" or "..." (plus closing quotes or brackets) that is followed by a space,
    // the end, or a capital letter ("that?What"). "3.5" and "e.g. this" stay one sentence.
    private static List<string> SplitSentences(string text)
    {
        var sentences = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!SentenceEnds.Contains(text[i]))
                continue;

            var end = i + 1;
            while (end < text.Length && (SentenceEnds.Contains(text[end]) || ClosingMarks.Contains(text[end])))
                end++;

            if (end < text.Length && !char.IsWhiteSpace(text[end]) && !char.IsUpper(text[end]))
                continue;

            var sentence = text[start..end].Trim();
            if (sentence.Length > 0)
                sentences.Add(sentence);
            start = end;
            i = end - 1;
        }

        var rest = text[start..].Trim();
        if (rest.Length > 0)
            sentences.Add(rest);
        return sentences;
    }

    private static bool StartsWithAttachedPunctuation(string segment) =>
        segment[0] is ',' or '.' or '!' or '?' or ';' or ':' or ')' or '\'' or '’' or '…' or '%';

    /// <summary>Lower case words separated by single spaces: "Hey, you! I'm here." gives "hey you im here".</summary>
    private static string Normalize(string text)
    {
        var result = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && result.Length > 0)
                    result.Append(' ');
                pendingSpace = false;
                result.Append(char.ToLowerInvariant(c));
            }
            else if (c is not ('\'' or '’'))
            {
                pendingSpace = true;
            }
        }

        return result.ToString();
    }
}
