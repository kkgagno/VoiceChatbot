using System.Text;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Some local models write plain-text planning notes about how they will answer before, or instead of,
/// the reply, without &lt;think&gt; tags ("The user said "hi". This is a short greeting. Wait, they might
/// want more. Let's try: "Hi! What can I help you with?""). This finds such a reply and keeps only the
/// actual answer, so the notes are neither shown, saved nor spoken (see also <see cref="ReasoningText"/>).
/// Ordinary replies are left unchanged: the reply must open like notes and contain at least two
/// different planning cues after that opening.
/// </summary>
public static class PlanningNotes
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    // Markup or quotes a reply may start with, and an interjection before the first words.
    private const string Lead = @"\A[\s*_#>""'“`]*";
    private const string Interjection = @"(?:okay|ok|alright|all right|hmm+|so|well|right)\b[\s,.!:;-]*(?:so\b[\s,]*)?";

    // How notes open: talking about the user in the third person, or about what to answer.
    private static readonly Regex Opening = new(
        Lead + "(?:" + Interjection + @"the user\b|(?:" + Interjection + ")?(?:" +
        @"the user (?:just )?(?:said|says|asked|asks|is asking|wants|wanted|wrote|writes|greeted|greets|is greeting|is saying|sent|mentioned|seems|might|may|probably|likely|would like|needs)\b" +
        @"|we (?:need|have) to (?:answer|respond|reply|produce|provide|give|write|say|figure out|come up with|greet|acknowledge)\b" +
        @"|i need to (?:figure out|respond|reply|answer|think|work out|come up with|decide|craft|write|give)\b))",
        Options);

    // Planning cues; a reply is notes when at least two different ones follow its opening.
    private static readonly Regex[] Cues =
    {
        new(@"(?<![\w'’])wait\s*[,.!:]", Options),
        new(@"\bactually\s*,", Options),
        new(@"\blet['’]?s\b|\blet us\b", Options),
        new(@"\b(?:i|we) should\b", Options),
        new(@"\bi['’]?ll (?:say|respond|reply|answer|go with|keep|ask|greet|mention|offer|write|draft|add)\b", Options),
        new(@"\bthe best (?:response|reply|answer|approach)\b|\b(?:reply|response|answer) (?:is|would be|seems|should be) (?:best|better|good|fine|enough|appropriate|ideal)\b", Options),
        new(@"\bthey (?:might|may|probably|likely)\b", Options),
        new(@"\bsounds like\b", Options),
        new(@"\b(?:respond|reply|answer)(?:ing)? (?:with|by)\b", Options),
        new(@"\bkeep (?:it|this|things|the (?:reply|response|answer))\s+(?:short|simple|brief|concise|friendly|light|casual|conversational|natural)\b", Options),
        new(@"\bthe user['’]?s? (?:said|says|asked|asks|is asking|wants|wanted|wrote|greeted|mentioned|might|may|probably|likely|seems|just)\b", Options),
    };

    // Inside notes, a paragraph with one of these is a note too. Narrower than Cues where answers use
    // the same words ("Let's get started!", "That sounds like fun!").
    private static readonly Regex[] NoteParagraphCues =
    {
        new(@"(?<![\w'’])wait\s*[,.!:]", Options),
        new(@"\bactually\s*,", Options),
        new(@"\blet['’]?s (?:try|go with|keep|say|respond|reply|answer|draft|craft|produce|output|aim|think|greet|ask|offer|mention|acknowledge|make (?:it|sure)|write (?:a|the|something))\b", Options),
        new(@"\b(?:i|we) should\b", Options),
        Cues[4],
        Cues[5],
        new(@"\bthey (?:might|may|probably|likely) (?:want|be|just|need|prefer|like|expect|ask|mean|enjoy|appreciate)\b", Options),
        new(@"\bsounds like (?:a |an )?(?:greeting|question|request|test|joke|follow-up|they|the user)\b", Options),
        Cues[8],
        Cues[9],
        new(@"\bthe user\b", Options),
    };

    // "Final answer:", "Response:", "Reply:" at the start of a line or after a sentence.
    private static readonly Regex Label = new(
        @"(?:^|(?<=[.!?]\s))[ \t*_#>]*(?:final (?:answer|response|reply)|answer|response|reply)[*_ \t]*:[*_]*[ \t]*",
        Options | RegexOptions.Multiline);

    // "Let's try: "Hi!"", "I'll say "Hi!"", "respond with "Hi!"".
    private static readonly Regex QuotedCandidate = new(
        @"(?:let['’]?s (?:try|go with|say|use)|i['’]?ll (?:say|go with|use)|(?:respond|reply|answer) with|something like)" +
        @"[^""“\n]{0,40}?[:\-–—]?\s*[*_]*\s*(?:""(?<q>[^""]+)""|“(?<q>[^”]+)”)",
        Options);

    private static readonly Regex BlankLine = new(@"\n[ \t]*\n", RegexOptions.Compiled);

    /// <summary>
    /// True when the text so far opens like planning notes ("The user said", "Okay, the user",
    /// "We need to answer", "I need to figure out"). Used while a reply streams, before the cues arrive.
    /// </summary>
    public static bool LooksLikeStart(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Opening.IsMatch(text);

    /// <summary>True when the whole reply is planning notes (opening plus at least two different cues).</summary>
    public static bool LooksLikeNotes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var opening = Opening.Match(text);
        if (!opening.Success)
            return false;

        var rest = text[(opening.Index + opening.Length)..];
        var kinds = 0;
        foreach (var cue in Cues)
        {
            if (cue.IsMatch(rest) && ++kinds >= 2)
                return true;
        }

        return false;
    }

    /// <summary>
    /// When <paramref name="text"/> is planning notes, returns true with the actual reply in
    /// <paramref name="answer"/>: the paragraphs after the notes; else the text after a label such as
    /// "Final answer:"; else the last quoted candidate after "Let's try:", "I'll say" or "respond with";
    /// else "" (only notes). <paramref name="notes"/> is the hidden text. Returns false, with the text
    /// unchanged in <paramref name="answer"/>, for an ordinary reply.
    /// </summary>
    public static bool TryExtractAnswer(string? text, out string answer, out string notes)
    {
        answer = text ?? "";
        notes = "";
        if (!LooksLikeNotes(text))
            return false;

        var source = text!.Replace("\r\n", "\n");
        var paragraphs = SplitParagraphs(source);
        var lastNote = paragraphs.FindLastIndex(IsNoteParagraph);
        if (lastNote < paragraphs.Count - 1)
        {
            answer = Unquote(RemoveLeadingLabel(string.Join("\n\n", paragraphs.Skip(lastNote + 1))));
            if (answer.Length > 0)
            {
                notes = string.Join("\n\n", paragraphs.Take(lastNote + 1));
                return true;
            }
        }

        notes = source.Trim();
        answer = AnswerAfterLabel(source) ?? LastQuotedCandidate(source) ?? "";
        return true;
    }

    // Paragraphs split at blank lines; a fenced code block stays in one piece.
    private static List<string> SplitParagraphs(string text)
    {
        var paragraphs = new List<string>();
        var current = new StringBuilder();
        var inCode = false;
        foreach (var line in text.Split('\n'))
        {
            if (!inCode && line.Trim().Length == 0)
            {
                AddParagraph();
                continue;
            }

            if (current.Length > 0)
                current.Append('\n');
            current.Append(line);
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inCode = !inCode;
        }

        AddParagraph();
        return paragraphs;

        void AddParagraph()
        {
            var paragraph = current.ToString().Trim();
            if (paragraph.Length > 0)
                paragraphs.Add(paragraph);
            current.Clear();
        }
    }

    private static bool IsNoteParagraph(string paragraph)
    {
        if (paragraph.Contains("```", StringComparison.Ordinal))
            return false;
        if (Opening.IsMatch(paragraph))
            return true;

        foreach (var cue in NoteParagraphCues)
        {
            if (cue.IsMatch(paragraph))
                return true;
        }

        return false;
    }

    private static string RemoveLeadingLabel(string text)
    {
        var label = Label.Match(text);
        return label.Success && label.Index == 0 ? text[label.Length..] : text;
    }

    // The text after the last label, up to the end of its paragraph, without note lines.
    private static string? AnswerAfterLabel(string text)
    {
        var labels = Label.Matches(text);
        for (var i = labels.Count - 1; i >= 0; i--)
        {
            var rest = text[(labels[i].Index + labels[i].Length)..].TrimStart(' ', '\t', '\n');
            var end = BlankLine.Match(rest);
            if (end.Success)
                rest = rest[..end.Index];

            var lines = rest.Split('\n').Where(line => !IsNoteParagraph(line.Trim()));
            var answer = Unquote(string.Join("\n", lines));
            if (answer.Length > 0)
                return answer;
        }

        return null;
    }

    private static string? LastQuotedCandidate(string text)
    {
        var matches = QuotedCandidate.Matches(text);
        for (var i = matches.Count - 1; i >= 0; i--)
        {
            var quoted = matches[i].Groups["q"].Value.Trim();
            if (quoted.Any(char.IsLetterOrDigit))
                return quoted;
        }

        return null;
    }

    // Removes quotes (and bold markers) around an answer that is one quoted string.
    private static string Unquote(string text)
    {
        var result = text.Trim();
        while (true)
        {
            var before = result;
            if (result.Length >= 4 && result.StartsWith("**", StringComparison.Ordinal) && result.EndsWith("**", StringComparison.Ordinal))
                result = result[2..^2].Trim();
            if (result.Length >= 2 && result[0] == '"' && result[^1] == '"' && result.Count(c => c == '"') == 2)
                result = result[1..^1].Trim();
            if (result.Length >= 2 && result[0] == '“' && result[^1] == '”' && result.IndexOf('“', 1) < 0)
                result = result[1..^1].Trim();
            if (result == before)
                return result;
        }
    }
}
