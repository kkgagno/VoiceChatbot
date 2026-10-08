using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>One piece of an assistant reply: Markdown prose, or a fenced code block shown with its own Copy button.</summary>
public sealed record ReplySegment(bool IsCode, string Text, string Language = "");

/// <summary>
/// WPF-free Markdown helpers for assistant replies: split a reply into prose and fenced code, clean
/// prose for display without breaking its Markdown, and turn Markdown into plain text for the phone,
/// memories and speech. Fences are found the forgiving way the chat always has: any ``` starts or
/// ends one, even mid-line, and an unclosed fence runs to the end of the reply.
/// </summary>
public static class MarkdownText
{
    private const string Fence = "```";
    private const RegexOptions Options = RegexOptions.CultureInvariant;
    private const RegexOptions LineOptions = RegexOptions.CultureInvariant | RegexOptions.Multiline;

    private static readonly Regex LanguagePattern = new("^[A-Za-z0-9_+.#-]*$", Options);
    // Single-line code spans: `code` or ``code with ` inside``.
    private static readonly Regex InlineCode = new(@"(`+)(?!`)(.+?)(?<!`)\1(?!`)", Options);
    private static readonly Regex ProtectedSpan = new("(\\d+)", Options);

    // Display cleanup (Markdown stays).
    private static readonly Regex Hashtag = new(@"(?<=^|\s)#[A-Za-z][\w-]*", LineOptions);
    private static readonly Regex HtmlBreak = new(@"<br\s*/?>", Options | RegexOptions.IgnoreCase);
    // Tags such as <b>, </div> or the <unused49> placeholders some templates leak. Autolinks
    // (<https://...>) and e-mail addresses are not tags and stay.
    private static readonly Regex HtmlTag = new(@"</?[A-Za-z][A-Za-z0-9]*(?:\s[^<>\n]*)?/?>", Options);
    private static readonly Regex InnerSpaces = new(@"(?<=\S)[ \t]{2,}(?=\S)", Options);
    private static readonly Regex ExtraBlankLines = new(@"\n{3,}", Options);

    // Markdown syntax, removed for plain text.
    private static readonly Regex Image = new(@"!\[([^\]\n]*)\]\((?:[^()\n]|\([^)\n]*\))*\)", Options);
    private static readonly Regex Link = new(@"\[([^\]\n]+)\]\((?:[^()\n]|\([^)\n]*\))*\)", Options);
    private static readonly Regex AutoLink = new(@"<((?:https?|mailto):[^<>\s]+)>", Options | RegexOptions.IgnoreCase);
    private static readonly Regex Heading = new(@"^[ ]{0,3}#{1,6}[ \t]+(.*?)(?:[ \t]+#+)?[ \t]*$", LineOptions);
    private static readonly Regex TableSeparator = new(@"^[ \t]*\|?[ \t]*:?-{2,}:?[ \t]*(?:\|[ \t]*:?-{2,}:?[ \t]*)+\|?[ \t]*(?:\n|$)", LineOptions);
    private static readonly Regex Rule = new(@"^[ ]{0,3}(?:([-*_])(?:[ \t]*\1){2,}|={3,})[ \t]*(?:\n|$)", LineOptions);
    private static readonly Regex Quote = new(@"^[ \t]*(?:>[ \t]?)+", LineOptions);
    private static readonly Regex Task = new(@"^([ \t]*)(?:[-*+]|\d+[.)])[ \t]+\[([ xX])\][ \t]+", LineOptions);
    private static readonly Regex Bullet = new(@"^([ \t]*)[-*+][ \t]+", LineOptions);
    private static readonly Regex Numbered = new(@"^([ \t]*)\d+[.)][ \t]+", LineOptions);
    private static readonly Regex BoldItalic = new(@"\*\*\*(?=\S)(.+?)(?<=\S)\*\*\*", Options);
    private static readonly Regex BoldStars = new(@"\*\*(?=\S)(.+?)(?<=\S)\*\*", Options);
    private static readonly Regex BoldUnderscores = new(@"(?<![\w\\])__(?=\S)(.+?)(?<=\S)__(?!\w)", Options);
    private static readonly Regex ItalicStar = new(@"(?<![\w*\\])\*(?=[^\s*])(.+?)(?<=[^\s*\\])\*(?![\w*])", Options);
    private static readonly Regex ItalicUnderscore = new(@"(?<![\w\\])_(?=[^\s_])(.+?)(?<=[^\s_])_(?!\w)", Options);
    private static readonly Regex Strikethrough = new(@"~~(?=\S)(.+?)(?<=\S)~~", Options);
    private static readonly Regex Escape = new(@"\\([\\`*_{}\[\]()#+\-.!|>~<])", Options);

    private readonly record struct FencedBlock(int Start, int End, int CodeStart, int CodeEnd, string Language, int Indent);

    /// <summary>
    /// Splits a reply into prose and fenced code segments, in order. Code loses its fences and the
    /// indentation of an indented fence (inside a list item); empty segments are left out.
    /// </summary>
    public static IReadOnlyList<ReplySegment> SplitFencedCode(string? text)
    {
        var segments = new List<ReplySegment>();
        if (string.IsNullOrEmpty(text))
            return segments;

        var position = 0;
        var proseIndent = 0;
        foreach (var block in FindFencedBlocks(text))
        {
            AddProse(segments, text[position..block.Start], proseIndent);

            var code = TidyCode(text[block.CodeStart..block.CodeEnd], block.Indent);
            if (code.Length > 0)
                segments.Add(new ReplySegment(true, code, block.Language));

            position = block.End;
            // Text right after an indented fence usually continues the same list item.
            proseIndent = block.Indent;
        }

        if (position < text.Length)
            AddProse(segments, text[position..], proseIndent);
        return segments;
    }

    /// <summary>
    /// Display cleanup that keeps Markdown: drops emojis, hashtags and stray HTML-like tags (such as
    /// &lt;unused49&gt;), turns &lt;br&gt; into a line break and squeezes runs of spaces between words.
    /// Fenced code and inline code spans are left exactly as written; indentation is kept so nested
    /// lists still nest.
    /// </summary>
    public static string CleanForDisplay(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        return MapProse(text.Replace("\r\n", "\n"), prose =>
        {
            var cleaned = ProtectInlineCode(prose, out var spans);
            cleaned = RemoveEmojis(cleaned);
            cleaned = Hashtag.Replace(cleaned, "");
            cleaned = HtmlBreak.Replace(cleaned, "\n");
            cleaned = HtmlTag.Replace(cleaned, "");
            cleaned = InnerSpaces.Replace(cleaned, " ");
            cleaned = ExtraBlankLines.Replace(cleaned, "\n\n");
            return RestoreInlineCode(cleaned, spans, keepBackticks: true);
        }).Trim();
    }

    /// <summary>
    /// Applies <paramref name="transform"/> to the prose only: fenced code blocks and inline code spans
    /// are passed through exactly as written.
    /// </summary>
    public static string MapProseOutsideCode(string? text, Func<string, string> transform)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        return MapProse(text, prose =>
        {
            var protectedProse = ProtectInlineCode(prose, out var spans);
            return RestoreInlineCode(transform(protectedProse), spans, keepBackticks: true);
        });
    }

    /// <summary>
    /// Markdown to readable plain text (phone remote, memories, the plain-text view): no emphasis
    /// marks, headings or link syntax; bullets become "•", task boxes ☐/☑, code keeps its content
    /// without fences.
    /// </summary>
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown?.Trim() ?? "";

        var parts = new List<string>();
        foreach (var segment in SplitFencedCode(markdown.Replace("\r\n", "\n")))
        {
            var part = segment.IsCode ? segment.Text : StripProse(segment.Text, forSpeech: false);
            if (!string.IsNullOrWhiteSpace(part))
                parts.Add(part);
        }

        return string.Join("\n\n", parts).Trim();
    }

    /// <summary>
    /// Markdown to text worth speaking: syntax, list markers and task boxes removed, link text kept,
    /// code blocks left out. Table pipes and URLs are handled later by the speech cleanup.
    /// </summary>
    public static string StripForSpeech(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown?.Trim() ?? "";

        var parts = new List<string>();
        foreach (var segment in SplitFencedCode(markdown.Replace("\r\n", "\n")))
        {
            if (segment.IsCode)
                continue;

            var part = StripProse(segment.Text, forSpeech: true);
            if (!string.IsNullOrWhiteSpace(part))
                parts.Add(part);
        }

        return string.Join("\n\n", parts).Trim();
    }

    /// <summary>Only http, https and mailto links are opened; "www." links get https.</summary>
    public static bool TryGetSafeLinkUri(string? url, [NotNullWhen(true)] out Uri? uri)
    {
        uri = null;
        var candidate = url?.Trim() ?? "";
        if (candidate.Length == 0)
            return false;

        if (candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            candidate = "https://" + candidate;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
            return false;

        if (parsed.Scheme != Uri.UriSchemeHttp &&
            parsed.Scheme != Uri.UriSchemeHttps &&
            parsed.Scheme != Uri.UriSchemeMailto)
            return false;

        uri = parsed;
        return true;
    }

    private static List<FencedBlock> FindFencedBlocks(string text)
    {
        var blocks = new List<FencedBlock>();
        var position = 0;
        while (position < text.Length)
        {
            var fenceStart = text.IndexOf(Fence, position, StringComparison.Ordinal);
            if (fenceStart < 0)
                break;

            var codeStart = fenceStart + Fence.Length;
            var lineEnd = text.IndexOf('\n', codeStart);
            string language;
            if (lineEnd >= 0)
            {
                language = text[codeStart..lineEnd].Trim();
                codeStart = lineEnd + 1;
            }
            else
            {
                language = text[codeStart..].Trim();
                codeStart = text.Length;
            }

            // Not a language name: the code starts right after the fence (```print("hi")```).
            if (!LanguagePattern.IsMatch(language))
            {
                language = "";
                codeStart = fenceStart + Fence.Length;
            }

            var fenceEnd = text.IndexOf(Fence, codeStart, StringComparison.Ordinal);
            var codeEnd = fenceEnd >= 0 ? fenceEnd : text.Length;
            var end = fenceEnd >= 0 ? fenceEnd + Fence.Length : text.Length;
            blocks.Add(new FencedBlock(fenceStart, end, codeStart, codeEnd, language, IndentBefore(text, fenceStart)));
            position = end;
        }

        return blocks;
    }

    /// <summary>Applies a transform to the prose between fenced blocks; fenced blocks stay verbatim.</summary>
    private static string MapProse(string text, Func<string, string> transform)
    {
        var builder = new StringBuilder(text.Length);
        var position = 0;
        foreach (var block in FindFencedBlocks(text))
        {
            builder.Append(transform(text[position..block.Start]));
            builder.Append(text, block.Start, block.End - block.Start);
            position = block.End;
        }

        if (position < text.Length)
            builder.Append(transform(text[position..]));
        return builder.ToString();
    }

    /// <summary>Spaces between the start of the line and the fence, or 0 when the fence is mid-line.</summary>
    private static int IndentBefore(string text, int index)
    {
        var count = 0;
        for (var i = index - 1; i >= 0; i--)
        {
            if (text[i] == '\n')
                return count;
            if (text[i] != ' ' && text[i] != '\t')
                return 0;
            count += text[i] == '\t' ? 4 : 1;
        }

        return count;
    }

    private static void AddProse(List<ReplySegment> segments, string prose, int indent)
    {
        var trimmed = RemoveIndent(prose.Replace("\r\n", "\n"), indent).Trim();
        if (trimmed.Length > 0)
            segments.Add(new ReplySegment(false, trimmed));
    }

    private static string TidyCode(string code, int indent)
    {
        var lines = RemoveIndent(code.Replace("\r\n", "\n"), indent).Split('\n');
        var first = 0;
        var last = lines.Length - 1;
        while (first <= last && string.IsNullOrWhiteSpace(lines[first]))
            first++;
        while (last >= first && string.IsNullOrWhiteSpace(lines[last]))
            last--;

        return first > last ? "" : string.Join("\n", lines[first..(last + 1)]).TrimEnd();
    }

    /// <summary>Removes up to <paramref name="indent"/> leading spaces from every line.</summary>
    private static string RemoveIndent(string text, int indent)
    {
        if (indent <= 0)
            return text;

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var remove = 0;
            while (remove < indent && remove < lines[i].Length && lines[i][remove] == ' ')
                remove++;
            lines[i] = lines[i][remove..];
        }

        return string.Join("\n", lines);
    }

    private static string StripProse(string prose, bool forSpeech)
    {
        var text = ProtectInlineCode(prose, out var spans);

        text = Image.Replace(text, "$1");
        text = Link.Replace(text, "$1");
        text = AutoLink.Replace(text, "$1");
        text = Quote.Replace(text, "");
        text = Heading.Replace(text, "$1");
        text = TableSeparator.Replace(text, "");
        text = Rule.Replace(text, "");
        text = Task.Replace(text, m => forSpeech
            ? m.Groups[1].Value
            : m.Groups[1].Value + (m.Groups[2].Value == " " ? "☐ " : "☑ "));
        text = Bullet.Replace(text, forSpeech ? "$1" : "$1• ");
        if (forSpeech)
            text = Numbered.Replace(text, "$1");

        text = BoldItalic.Replace(text, "$1");
        text = BoldStars.Replace(text, "$1");
        text = BoldUnderscores.Replace(text, "$1");
        text = ItalicStar.Replace(text, "$1");
        text = ItalicUnderscore.Replace(text, "$1");
        text = Strikethrough.Replace(text, "$1");
        text = Escape.Replace(text, "$1");
        text = ExtraBlankLines.Replace(text, "\n\n");

        return RestoreInlineCode(text, spans, keepBackticks: false).Trim();
    }

    private static string ProtectInlineCode(string text, out List<string> spans)
    {
        var found = new List<string>();
        var result = InlineCode.Replace(text, m =>
        {
            found.Add(m.Value);
            return $"{found.Count - 1}";
        });
        spans = found;
        return result;
    }

    private static string RestoreInlineCode(string text, List<string> spans, bool keepBackticks)
    {
        if (spans.Count == 0)
            return text;

        return ProtectedSpan.Replace(text, m =>
        {
            var index = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (index < 0 || index >= spans.Count)
                return "";

            var span = spans[index];
            return keepBackticks ? span : InlineCode.Match(span).Groups[2].Value.Trim();
        });
    }

    /// <summary>Same character rules the chat display has always used for emojis.</summary>
    private static string RemoveEmojis(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var category = char.GetUnicodeCategory(c);
            if (category == UnicodeCategory.Surrogate || category == UnicodeCategory.OtherSymbol)
                continue;
            if (c >= '☀' && c <= '➿') continue;   // Misc symbols
            if (c >= '︀' && c <= '﻿') continue;   // Variation selectors
            builder.Append(c);
        }

        return builder.ToString();
    }
}
