using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VoiceChatbot;

/// <summary>
/// Splits model text into speakable sentences while it is still streaming in.
/// <see cref="Append"/> returns the sentences completed by a new piece of text and
/// <see cref="Flush"/> returns whatever is left at the end. Sentences end at . ! ? (plus any
/// closing quote or bracket) followed by whitespace, at blank lines, and at line breaks around
/// list items, headings and table rows. Decimals, versions, URLs, domain names and common
/// abbreviations do not end a sentence. Fenced ``` code blocks are never returned.
/// Returned text is raw (markdown is kept); callers clean it for speech.
/// </summary>
public sealed class SentenceChunker
{
    public const int DefaultMinLength = 25;
    public const int DefaultMaxLength = 300;

    // Marks the place where a code block was removed; always ends the current sentence.
    private const char CodeBreak = '\u0001';

    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "e.g", "i.e", "etc", "vs", "cf", "al", "approx", "ca", "est", "misc",
        "mr", "mrs", "ms", "dr", "prof", "sr", "jr", "st", "mt", "rev", "gen", "capt", "lt", "sgt",
        "inc", "ltd", "corp", "dept", "univ", "assn", "bros",
        "fig", "figs", "vol", "vols", "ch", "pp", "ph.d",
        "jan", "feb", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec"
    };

    private readonly StringBuilder _raw = new();   // incoming text not yet checked for code fences
    private readonly StringBuilder _text = new();  // prose waiting to be split into sentences
    private int _scan;                             // next index in _text to examine
    private string _linePrefix = "";               // start of the current line that was already returned
    private bool _inCode;
    private bool _stopped;
    private bool _returnedAny;
    private int _heldEnd;                          // EveryPieceAtLeastMinLength: end of a piece waiting for text after it

    /// <summary>Sentences shorter than this are merged into the next one (except the first sentence).</summary>
    public int MinLength { get; init; } = DefaultMinLength;

    /// <summary>
    /// When true, no piece is shorter than <see cref="MinLength"/> unless the whole text is: the first
    /// sentences are merged too, and a piece is only returned once at least MinLength characters follow it,
    /// so a short last sentence is joined to the piece before it instead of being returned on its own.
    /// </summary>
    public bool EveryPieceAtLeastMinLength { get; init; }

    /// <summary>A sentence longer than this may also end at a comma, semicolon or colon.</summary>
    public int MaxLength { get; init; } = DefaultMaxLength;

    /// <summary>When true, everything from the first ``` fence on is ignored (only the intro is spoken).</summary>
    public bool StopAtFirstCodeBlock { get; init; }

    /// <summary>True once a ``` code fence has been seen.</summary>
    public bool SawCodeBlock { get; private set; }

    /// <summary>Splits a complete text in one go.</summary>
    public static List<string> Split(string? text, int minLength = DefaultMinLength)
    {
        var chunker = new SentenceChunker { MinLength = minLength };
        var sentences = new List<string>(chunker.Append(text));
        sentences.AddRange(chunker.Flush());
        return sentences;
    }

    /// <summary>Adds streamed text and returns the sentences it completed (often none).</summary>
    public IReadOnlyList<string> Append(string? delta)
    {
        if (string.IsNullOrEmpty(delta) || _stopped)
            return Array.Empty<string>();

        _raw.Append(delta);
        MoveProseOutOfRaw(final: false);
        var sentences = new List<string>();
        Scan(sentences, final: false);
        return sentences;
    }

    /// <summary>Returns the remaining text as the last sentence(s) and resets the chunker.</summary>
    public IReadOnlyList<string> Flush()
    {
        var sentences = new List<string>();
        if (!_stopped)
            MoveProseOutOfRaw(final: true);

        Scan(sentences, final: true);
        Emit(sentences, _text.Length, force: true);

        _raw.Clear();
        _text.Clear();
        _scan = 0;
        _linePrefix = "";
        _inCode = false;
        _stopped = false;
        _returnedAny = false;
        _heldEnd = 0;
        return sentences;
    }

    // ---------------- Code fences ----------------

    // Moves text from _raw to _text, dropping fenced code. A trailing run of one or two
    // backticks stays in _raw until we know whether it starts a fence.
    private void MoveProseOutOfRaw(bool final)
    {
        while (_raw.Length > 0 && !_stopped)
        {
            var raw = _raw.ToString();
            var fence = raw.IndexOf("```", StringComparison.Ordinal);
            if (fence < 0)
            {
                var keep = final ? 0 : CountTrailingBackticks(raw);
                if (!_inCode)
                    _text.Append(raw, 0, raw.Length - keep);
                _raw.Remove(0, raw.Length - keep);
                return;
            }

            var fenceEnd = fence;
            while (fenceEnd < raw.Length && raw[fenceEnd] == '`')
                fenceEnd++;

            if (!_inCode)
            {
                _text.Append(raw, 0, fence);
                _text.Append(CodeBreak);
                SawCodeBlock = true;
                if (StopAtFirstCodeBlock)
                {
                    _stopped = true;
                    _raw.Clear();
                    return;
                }
            }

            _inCode = !_inCode;
            _raw.Remove(0, fenceEnd);
        }
    }

    private static int CountTrailingBackticks(string text)
    {
        var count = 0;
        while (count < text.Length && text[text.Length - 1 - count] == '`')
            count++;
        return count;
    }

    // ---------------- Sentence boundaries ----------------

    private enum Decision { NeedMore, NoSplit, Split }

    private void Scan(List<string> sentences, bool final)
    {
        while (true)
        {
            if (ReleaseHeldPiece(sentences))
                continue;
            if (_scan >= _text.Length)
                return;

            var c = _text[_scan];
            Decision decision;
            int end;

            if (c == CodeBreak)
            {
                // Text before a code block is spoken on its own; the marker itself is dropped.
                Emit(sentences, _scan, force: true);
                _text.Remove(0, 1);
                _scan = 0;
                continue;
            }

            if (c is '.' or '!' or '?' or '\u2026')
                (decision, end) = CheckSentenceEnd(_scan, final);
            else if (IsFullWidthSentenceEnd(c))
                (decision, end) = CheckFullWidthSentenceEnd(_scan, final);
            else if (c == '\n')
                (decision, end) = CheckLineBreak(_scan, final);
            else if (c is ',' or ';' or ':' && _scan >= MaxLength)
                (decision, end) = CheckClauseBreak(_scan, final);
            else
                (decision, end) = (Decision.NoSplit, _scan + 1);

            if (decision == Decision.NeedMore)
                return;

            if (decision == Decision.Split)
            {
                if (EveryPieceAtLeastMinLength)
                    HoldPiece(end);
                else if (Emit(sentences, end, force: false))
                    continue;
            }

            _scan = Math.Max(end, _scan + 1);
        }
    }

    // EveryPieceAtLeastMinLength: remembers the first place where a long enough piece could end.
    private void HoldPiece(int end)
    {
        if (_heldEnd > 0)
            return;

        var piece = _text.ToString(0, end).Trim();
        if (piece.Length >= MinLength && piece.Any(char.IsLetterOrDigit))
            _heldEnd = end;
    }

    // Returns the held piece once enough text follows it that the next piece cannot be too short.
    private bool ReleaseHeldPiece(List<string> sentences) =>
        _heldEnd > 0 && _text.Length - _heldEnd >= MinLength && Emit(sentences, _heldEnd, force: true);

    // Returns the sentence text[0..end) when it is long enough (or forced) and removes it from the buffer.
    private bool Emit(List<string> sentences, int end, bool force)
    {
        var sentence = _text.ToString(0, end).Trim();
        var speakable = sentence.Any(char.IsLetterOrDigit);
        if (!force && (!speakable || (_returnedAny && sentence.Length < MinLength)))
            return false;

        if (speakable)
        {
            sentences.Add(sentence);
            _returnedAny = true;
        }

        RememberLinePrefix(end);
        _text.Remove(0, end);
        _scan = 0;
        _heldEnd = 0;
        return true;
    }

    private void RememberLinePrefix(int removedLength)
    {
        var removed = _text.ToString(0, removedLength);
        var lastBreak = removed.LastIndexOf('\n');
        var prefix = lastBreak >= 0 ? removed[(lastBreak + 1)..] : _linePrefix + removed;
        // Only the start of a line matters for list/heading detection.
        _linePrefix = prefix.Length > 16 ? prefix[..16] : prefix;
    }

    private (Decision, int) CheckSentenceEnd(int index, bool final)
    {
        var text = _text;
        var end = index;
        while (end < text.Length && text[end] is '.' or '!' or '?' or '\u2026')
            end++;
        var punctuation = text.ToString(index, end - index);

        while (end < text.Length && IsCloser(text[end]))
            end++;

        if (end >= text.Length)
            return final ? (Decision.Split, end) : (Decision.NeedMore, index);

        // "3.5", "example.com", "v1.2.3", "e.g.," - no whitespace means no sentence end.
        if (!char.IsWhiteSpace(text[end]))
            return (Decision.NoSplit, end);

        var next = end;
        while (next < text.Length && char.IsWhiteSpace(text[next]) && text[next] != '\n')
            next++;
        var lineEnds = next < text.Length && text[next] == '\n';

        var isDotsOnly = punctuation.All(ch => ch is '.' or '\u2026');
        if (!isDotsOnly)
            return (Decision.Split, end);

        // An ellipsis usually trails off mid-thought; only a line break ends the sentence there.
        if (punctuation.Length > 1 || punctuation[0] == '\u2026')
        {
            if (lineEnds)
                return (Decision.Split, end);
            if (next >= text.Length && !final)
                return (Decision.NeedMore, index);
            return (Decision.NoSplit, end);
        }

        var word = WordBefore(index);
        if (IsAbbreviation(word))
            return (Decision.NoSplit, end);

        // "1. First step" at the start of a line is a list marker, not a sentence.
        if (word.Length > 0 && word.All(char.IsDigit) && IsLineStartBefore(index - word.Length))
            return (Decision.NoSplit, end);

        if (lineEnds)
            return (Decision.Split, end);

        // A lower-case word after the dot means the sentence goes on ("approx. five", "a.k.a. the").
        if (next >= text.Length)
            return final ? (Decision.Split, end) : (Decision.NeedMore, index);
        return char.IsLower(text[next]) ? (Decision.NoSplit, end) : (Decision.Split, end);
    }

    // Chinese/Japanese full stops need no space after them.
    private (Decision, int) CheckFullWidthSentenceEnd(int index, bool final)
    {
        var end = index;
        while (end < _text.Length && (IsFullWidthSentenceEnd(_text[end]) || IsCloser(_text[end])))
            end++;
        return end >= _text.Length && !final ? (Decision.NeedMore, index) : (Decision.Split, end);
    }

    private static bool IsFullWidthSentenceEnd(char c) => c is '\u3002' or '\uFF01' or '\uFF1F';

    private (Decision, int) CheckLineBreak(int index, bool final)
    {
        // Lines that are list items, headings or table rows always end a sentence.
        if (IsStructuralLine(CurrentLineBefore(index)))
            return (Decision.Split, index + 1);

        var text = _text;
        var next = index + 1;
        while (next < text.Length && text[next] is ' ' or '\t' or '\r')
            next++;

        if (next >= text.Length)
            return final ? (Decision.Split, index + 1) : (Decision.NeedMore, index);

        // Blank line = paragraph break.
        if (text[next] == '\n')
            return (Decision.Split, index + 1);

        // A list, heading or table starting on the next line also ends the sentence.
        var lineStart = StartsStructuralLine(text, next);
        if (lineStart == null)
            return final ? (Decision.NoSplit, index + 1) : (Decision.NeedMore, index);
        return lineStart.Value ? (Decision.Split, index + 1) : (Decision.NoSplit, index + 1);
    }

    private (Decision, int) CheckClauseBreak(int index, bool final)
    {
        var after = index + 1;
        if (after >= _text.Length)
            return final ? (Decision.Split, after) : (Decision.NeedMore, index);
        return char.IsWhiteSpace(_text[after]) ? (Decision.Split, after) : (Decision.NoSplit, after);
    }

    private static bool IsCloser(char c) =>
        c is '"' or '\'' or ')' or ']' or '}' or '*' or '_' or '\u201D' or '\u2019' or '\u00BB' or
            '\u300D' or '\u300F' or '\uFF09';

    private string WordBefore(int index)
    {
        var start = index;
        while (start > 0 && (char.IsLetterOrDigit(_text[start - 1]) || _text[start - 1] == '.'))
            start--;
        return _text.ToString(start, index - start).TrimStart('.');
    }

    private static bool IsAbbreviation(string word)
    {
        if (word.Length == 0)
            return false;

        if (Abbreviations.Contains(word))
            return true;

        // Initials and dotted short forms: "J.", "U.S.", "a.m.", "e.g.".
        var parts = word.Split('.');
        return parts.All(p => p.Length == 1 && char.IsLetter(p[0]));
    }

    private string CurrentLineBefore(int index)
    {
        var text = _text.ToString(0, index);
        var lastBreak = text.LastIndexOf('\n');
        return lastBreak >= 0 ? text[(lastBreak + 1)..] : _linePrefix + text;
    }

    private bool IsLineStartBefore(int index) =>
        CurrentLineBefore(index).All(char.IsWhiteSpace);

    private static bool IsStructuralLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
            return false;

        return StartsStructuralLine(trimmed + " ", 0) == true;
    }

    // true = a list item, heading, table row or quote starts at index; false = ordinary text;
    // null = not enough text yet to tell.
    private static bool? StartsStructuralLine(StringBuilder text, int index) =>
        StartsStructuralLine(text.ToString(index, Math.Min(12, text.Length - index)), 0);

    private static bool? StartsStructuralLine(string text, int index)
    {
        if (index >= text.Length)
            return null;

        var c = text[index];
        if (c is '#' or '|' or '>')
            return true;

        if (c is '-' or '*' or '+' or '\u2022')
        {
            if (index + 1 >= text.Length)
                return null;
            return text[index + 1] is ' ' or '\t';
        }

        if (!char.IsDigit(c))
            return false;

        var i = index;
        while (i < text.Length && char.IsDigit(text[i]) && i - index < 4)
            i++;
        if (i >= text.Length)
            return null;
        if (text[i] is not ('.' or ')'))
            return false;
        if (i + 1 >= text.Length)
            return null;
        return text[i + 1] is ' ' or '\t';
    }
}
