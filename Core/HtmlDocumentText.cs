using System;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Plain text from a saved HTML file or an HTML email body. Unlike <see cref="HtmlText"/> (tuned for
/// web pages: it drops navigation and footers), this keeps all visible text: scripts, styles and
/// comments go, entities are decoded, paragraphs and headings stay separated by a blank line, list
/// items start with "- ", table cells are separated by " | " and &lt;pre&gt; keeps its line breaks.
/// </summary>
public static class HtmlDocumentText
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(10);

    private static readonly Regex Comments = new(@"<!--.*?(?:-->|$)", Options, RegexTimeout);
    private static readonly Regex HiddenBlocks = new(
        @"<(script|style|noscript|template|head|object|iframe|svg|math|select)\b(?:[^>""']|""[^""]*""|'[^']*')*>.*?</\1\s*>",
        Options, RegexTimeout);
    private static readonly Regex Tags = new(
        @"<(/?)([a-zA-Z][a-zA-Z0-9:-]*)((?:[^>""']|""[^""]*""|'[^']*')*)>|<![^>]*>|<\?[^>]*>",
        Options, RegexTimeout);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex AltText = new(@"\balt\s*=\s*(?:""([^""]*)""|'([^']*)')", Options, RegexTimeout);
    private static readonly Regex MetaCharset = new(
        @"<meta\b[^>]*?charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]+)", Options, RegexTimeout);

    /// <summary>The charset an HTML file names in its first bytes (&lt;meta charset&gt;), or null.</summary>
    public static string? DetectCharset(ReadOnlySpan<byte> start)
    {
        var head = Encoding.Latin1.GetString(start[..Math.Min(start.Length, 4096)]);
        var match = MetaCharset.Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }

    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        try
        {
            return Convert(html);
        }
        catch (RegexMatchTimeoutException)
        {
            // Pathological markup: fall back to dropping every tag.
            var crude = Regex.Replace(html, "<[^>]*>", " ");
            return Normalize(WebUtility.HtmlDecode(crude));
        }
    }

    private static string Convert(string html)
    {
        var title = HtmlText.ExtractTitle(html);
        var text = Comments.Replace(html, " ");
        text = HiddenBlocks.Replace(text, " ");

        var writer = new BlockWriter(text.Length / 2);
        var inCell = false; // inside a table cell, paragraphs and line breaks only separate words
        void BlockBreak(int level)
        {
            if (inCell)
                writer.Text(" ".AsSpan(), preformatted: false);
            else
                writer.Break(level);
        }

        var pre = 0;
        var listItemEnd = -1; // output length when the last </li> was seen
        var position = 0;
        foreach (Match tag in Tags.Matches(text))
        {
            writer.Text(text.AsSpan(position, tag.Index - position), pre > 0);
            position = tag.Index + tag.Length;

            if (!tag.Groups[2].Success)
                continue; // <!DOCTYPE>, <?xml?>

            var closing = tag.Groups[1].Value == "/";
            var name = tag.Groups[2].Value.ToLowerInvariant();
            switch (name)
            {
                case "pre" or "textarea":
                    pre = Math.Max(0, pre + (closing ? -1 : 1));
                    BlockBreak(2);
                    break;
                case "table" or "tr" or "tbody" or "thead" or "tfoot" or "caption":
                    inCell = false;
                    writer.Break(name == "table" ? 2 : 1);
                    break;
                case "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "ul" or "ol"
                    or "dl" or "section" or "article" or "header" or "footer" or "main" or "nav" or "aside"
                    or "figure" or "form" or "fieldset" or "address" or "body" or "hr":
                    BlockBreak(2);
                    break;
                case "br" or "div" or "dt" or "dd" or "figcaption" or "option" or "summary"
                    or "details" or "center" or "legend":
                    BlockBreak(1);
                    break;
                case "li":
                    BlockBreak(1);
                    if (closing)
                    {
                        listItemEnd = writer.Length;
                    }
                    else if (!inCell)
                    {
                        // The next item of a list starts on the next line, even after a <p> in the last one.
                        if (writer.Length == listItemEnd)
                            writer.SetBreak(1);
                        writer.Bullet();
                    }
                    break;
                case "td" or "th":
                    inCell = !closing;
                    if (!closing)
                        writer.Cell();
                    break;
                case "img" when !closing:
                    var alt = AltText.Match(tag.Groups[3].Value);
                    var altText = alt.Success ? (alt.Groups[1].Success ? alt.Groups[1].Value : alt.Groups[2].Value) : "";
                    if (!string.IsNullOrWhiteSpace(altText))
                        writer.Text((" " + altText + " ").AsSpan(), preformatted: false);
                    break;
                default:
                    // Inline tags (a, span, b, font...) join their text with the text around them.
                    break;
            }
        }

        writer.Text(text.AsSpan(position), pre > 0);
        var body = Normalize(writer.ToString());
        if (title.Length > 0 && !body.StartsWith(title, StringComparison.Ordinal))
            body = body.Length == 0 ? title : title + "\n\n" + body;
        return body;
    }

    /// <summary>
    /// Collects text with block breaks that only take effect before the next text, so nested blocks
    /// (div in div, li then /li) never stack up blank lines, and cell separators only go between cells.
    /// </summary>
    private sealed class BlockWriter
    {
        private readonly StringBuilder _output;
        private int _pendingBreak;   // 0 none, 1 new line, 2 blank line
        private bool _pendingCell;
        private bool _pendingBullet;
        private bool _lineHasText;

        public BlockWriter(int capacity) => _output = new StringBuilder(capacity);

        public void Break(int level)
        {
            _pendingBreak = Math.Max(_pendingBreak, level);
            _pendingCell = false;
        }

        public int Length => _output.Length;

        /// <summary>Sets the pending break to exactly this level (a lower one than Break would keep).</summary>
        public void SetBreak(int level)
        {
            if (_pendingBreak > 0)
                _pendingBreak = level;
        }

        public void Bullet() => _pendingBullet = true;

        public void Cell() => _pendingCell = true;

        public void Text(ReadOnlySpan<char> raw, bool preformatted)
        {
            if (raw.IsEmpty)
                return;

            var decoded = WebUtility.HtmlDecode(raw.ToString()).Replace('\u00A0', ' ');
            var text = preformatted ? decoded.Replace("\r\n", "\n").Replace('\r', '\n') : Whitespace.Replace(decoded, " ");
            if (!preformatted && (!_lineHasText || _pendingBreak > 0))
                text = text.TrimStart();
            if (text.Length == 0 || (!preformatted && text == " " && !_lineHasText))
                return;

            if (_pendingBreak > 0 && _output.Length > 0)
            {
                _output.Append(_pendingBreak >= 2 ? "\n\n" : "\n");
                _lineHasText = false;
            }
            _pendingBreak = 0;

            if (_pendingBullet)
                _output.Append("- ");
            else if (_pendingCell && _lineHasText)
                _output.Append(" | ");
            _pendingBullet = false;
            _pendingCell = false;

            _output.Append(text);
            _lineHasText = !text.EndsWith('\n');
        }

        public override string ToString() => _output.ToString();
    }

    /// <summary>Trims each line, collapses spaces and keeps at most one blank line in a row.</summary>
    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var raw in lines)
        {
            var line = Regex.Replace(raw, @"[ \t\f\v]+", " ").Trim();
            if (line.Length == 0)
            {
                blank++;
                continue;
            }

            if (output.Length > 0)
                output.Append(blank > 0 ? "\n\n" : "\n");
            output.Append(line);
            blank = 0;
        }

        return output.ToString();
    }
}
