using System.Text;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Finds the spoken wake phrase ("hey onyx") in a Whisper transcript. Whisper spells it many ways
/// ("Hey, Onyx.", "hey onix", "Hi Onyx", "Hey Annex", "Hey on X"), so the match ignores case and
/// punctuation, accepts the usual spellings of "hey", and lets each other word differ a little
/// (mostly in its vowels) without matching unrelated words such as "Hey Annie" or "Hey only".
/// </summary>
public static class WakeWordText
{
    public const string DefaultPhrase = "hey onyx";

    private static readonly Regex Word = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);

    // How Whisper writes a spoken "hey" (and "hi").
    private static readonly HashSet<string> Greetings = new(StringComparer.OrdinalIgnoreCase)
    {
        "hey", "hay", "hi", "hei", "a", "he", "hej", "heh", "hiya"
    };

    /// <summary>True when the phrase has at least one letter or digit (a blank phrase matches anything).</summary>
    public static bool HasWords(string? wakeWord) => Word.IsMatch(wakeWord ?? "");

    /// <summary>
    /// True when the transcript contains the wake phrase; <paramref name="remainder"/> is the text after
    /// its first occurrence without leading punctuation ("Hey Onyx, what time is it?" gives
    /// "what time is it?"). A phrase without any letters or digits always matches.
    /// </summary>
    public static bool TryFind(string? transcript, string? wakeWord, out string remainder)
    {
        transcript ??= "";
        var phrase = Word.Matches(wakeWord ?? "").Select(m => m.Value.ToLowerInvariant()).ToList();
        if (phrase.Count == 0)
        {
            remainder = TrimLeadingPunctuation(transcript);
            return true;
        }

        var words = Word.Matches(transcript);
        for (var start = 0; start < words.Count; start++)
        {
            if (!TryMatchAt(words, start, phrase, out var last))
                continue;

            remainder = TrimLeadingPunctuation(transcript[(last.Index + last.Length)..]);
            return true;
        }

        remainder = "";
        return false;
    }

    // Matches the phrase word by word from words[start]; last is the transcript word that ends it.
    private static bool TryMatchAt(MatchCollection words, int start, List<string> phrase, out Match last)
    {
        last = words[start];
        var next = start;
        for (var i = 0; i < phrase.Count; i++)
        {
            if (next >= words.Count)
                return false;

            var word = words[next].Value;
            var matches = i == 0 && Greetings.Contains(phrase[0])
                ? Greetings.Contains(word)
                : WordMatches(word, phrase[i]);
            if (matches)
            {
                last = words[next];
                next++;
                continue;
            }

            // Whisper sometimes splits a name: "on X", "O Nyx". Only a short first piece is joined.
            if (i > 0 && next + 1 < words.Count && word.Length <= 2 &&
                MergedWordMatches(word + words[next + 1].Value, phrase[i]))
            {
                last = words[next + 1];
                next += 2;
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// True when a transcript word is the phrase word as Whisper may spell it: the same after
    /// <see cref="Phonetic"/> ("Onix", "Oniks" for "onyx"), only vowels changed in at most two places
    /// for words of four or more letters ("Annex", "Unix"), or one slip in a word of six or more letters.
    /// Words of three letters or fewer must match exactly.
    /// </summary>
    public static bool WordMatches(string? heard, string? target)
    {
        var a = Phonetic(heard);
        var b = Phonetic(target);
        if (a.Length == 0 || b.Length == 0)
            return false;
        if (a == b)
            return true;
        if (Letters(target) < 4 || a.Length < 3)
            return false;

        var distance = EditDistance(a, b);
        if (distance <= 2 && Consonants(a) == Consonants(b))
            return true;
        return b.Length >= 6 && distance <= 1;
    }

    private static bool MergedWordMatches(string heard, string target)
    {
        var a = Phonetic(heard);
        var b = Phonetic(target);
        return a == b || (Letters(target) >= 4 && EditDistance(a, b) <= 1 && Consonants(a) == Consonants(b));
    }

    /// <summary>
    /// Lower case, "y" as "i", "ph" as "f", "ck" as "k", "ks"/"cks"/"cs" as "x", doubled letters once:
    /// "Onyx", "Oniks" and "Onnix" all give "onix".
    /// </summary>
    private static string Phonetic(string? word)
    {
        var text = new string((word ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray())
            .Replace('y', 'i')
            .Replace("ph", "f")
            .Replace("ck", "k")
            .Replace("ks", "x")
            .Replace("cs", "x");

        var result = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (result.Length == 0 || result[^1] != c)
                result.Append(c);
        }

        return result.ToString();
    }

    private static int Letters(string? word) => (word ?? "").Count(char.IsLetterOrDigit);

    private static string Consonants(string phonetic) =>
        new(phonetic.Where(c => "aeiou".IndexOf(c) < 0).ToArray());

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static string TrimLeadingPunctuation(string text)
    {
        var i = 0;
        while (i < text.Length && (char.IsWhiteSpace(text[i]) || char.IsPunctuation(text[i])))
            i++;
        return text[i..].TrimEnd();
    }
}
