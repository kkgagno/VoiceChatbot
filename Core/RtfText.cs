using System;
using System.Collections.Generic;
using System.Text;

namespace VoiceChatbot;

/// <summary>
/// A small RTF-to-text converter for .rtf files (WordPad, TextEdit, older Word). It follows groups
/// and control words, decodes \'hh bytes in the document's (or the current font's) code page and
/// \uN Unicode characters, keeps paragraph, line, tab and cell breaks, and skips everything that is
/// not body text: font, color and style tables, document info, pictures, objects, headers, footers,
/// field instructions (a link's text is kept, its URL is not) and any \* destination it does not know.
/// </summary>
public static class RtfText
{
    public const int DefaultMaxChars = 4_000_000;

    // Destinations whose content is never body text.
    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "header", "headerl", "headerr", "headerf",
        "footer", "footerl", "footerr", "footerf", "object", "objdata", "objclass", "fldinst", "datafield",
        "themedata", "colorschememapping", "datastore", "latentstyles", "listtable", "listoverridetable",
        "rsidtbl", "generator", "xmlnstbl", "mmathPr", "pgdsctbl", "revtbl", "filetbl", "nonshppict",
        "shpinst", "sp", "sn", "sv", "userprops", "docvar", "wgrffmtfilter", "falt", "panose", "bkmkstart",
        "bkmkend", "fontemb", "fontfile", "listtext", "pntext", "pntxta", "pntxtb", "author", "operator",
        "title", "subject", "keywords", "comment", "doccomm", "company", "category", "template", "xe", "tc",
        "atnid", "atnauthor", "annotation", "atrfstart", "atrfend", "protusertbl", "passwordhash",
        "background", "factoidname", "blipuid", "macpict", "wmetafile", "dibitmap", "wbitmap", "pmmetafile",
    };

    // \fcharset values and the Windows code pages they mean.
    private static readonly Dictionary<int, int> CharsetCodePages = new()
    {
        [0] = 1252, [1] = 0, [77] = 10000, [128] = 932, [129] = 949, [130] = 1361, [134] = 936, [136] = 950,
        [161] = 1253, [162] = 1254, [163] = 1258, [177] = 1255, [178] = 1256, [186] = 1257, [204] = 1251,
        [222] = 874, [238] = 1250, [254] = 437, [255] = 850,
    };

    private sealed class GroupState
    {
        public bool Skip;
        public int UnicodeSkip = 1;
        public int CodePage;
        public bool InFontTable;

        public GroupState Copy() => (GroupState)MemberwiseClone();
    }

    /// <summary>True when the text starts like an RTF document ("{\rtf").</summary>
    public static bool LooksLikeRtf(string? text) =>
        text != null && text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("{\\rtf", StringComparison.Ordinal);

    /// <summary>
    /// Converts RTF to plain text. <paramref name="rtf"/> is the file read as Latin-1 (one char per
    /// byte), which is what RTF's 7-bit syntax and its \'hh escapes expect. Text that is not RTF
    /// comes back unchanged.
    /// </summary>
    public static string ToPlainText(string? rtf, int maxChars = DefaultMaxChars)
    {
        if (string.IsNullOrEmpty(rtf))
            return "";
        if (!LooksLikeRtf(rtf))
            return rtf;

        var output = new StringBuilder(Math.Min(rtf.Length / 2, 1 << 20));
        var pendingBytes = new List<byte>();
        var stack = new Stack<GroupState>();
        var state = new GroupState { CodePage = 1252 };
        var documentCodePage = 1252;
        var fontCodePages = new Dictionary<int, int>();
        var defaultFont = -1;
        var currentFontNumber = -1; // the \fN being defined inside the font table
        var skipChars = 0;          // characters still to skip after a \uN
        var ignorableNext = false;  // "\*" was seen: the next control word starts an ignorable destination
        var groupStart = false;     // the next token is the first one in a new group

        void FlushBytes()
        {
            if (pendingBytes.Count == 0)
                return;
            var codePage = state.CodePage > 0 ? state.CodePage : documentCodePage;
            var encoding = TextDecoding.GetEncoding(codePage) ?? TextDecoding.Ansi;
            output.Append(encoding.GetString(pendingBytes.ToArray()));
            pendingBytes.Clear();
        }

        void Emit(string text)
        {
            FlushBytes();
            if (!state.Skip && !state.InFontTable)
                output.Append(text);
        }

        void EmitChar(char c)
        {
            FlushBytes();
            if (!state.Skip && !state.InFontTable)
                output.Append(c);
        }

        var i = 0;
        var length = rtf.Length;
        while (i < length && output.Length < maxChars)
        {
            var c = rtf[i];
            switch (c)
            {
                case '{':
                    FlushBytes();
                    stack.Push(state.Copy());
                    skipChars = 0;
                    groupStart = true;
                    ignorableNext = false;
                    i++;
                    continue;

                case '}':
                    FlushBytes();
                    if (stack.Count > 0)
                        state = stack.Pop();
                    skipChars = 0;
                    groupStart = false;
                    ignorableNext = false;
                    i++;
                    continue;

                case '\\':
                    break; // handled below

                case '\r' or '\n':
                    i++;
                    continue;

                default:
                    groupStart = false;
                    if (skipChars > 0)
                    {
                        skipChars--;
                        i++;
                        continue;
                    }

                    if (state.Skip || state.InFontTable)
                    {
                        i++;
                        continue;
                    }

                    if (c >= 0x80 && c <= 0xFF)
                        pendingBytes.Add((byte)c); // a raw 8-bit byte: decode it in the current code page
                    else
                        EmitChar(c);
                    i++;
                    continue;
            }

            // A control symbol or control word.
            if (i + 1 >= length)
                break;

            var next = rtf[i + 1];
            if (next == '\'')
            {
                // \'hh: one byte in the current code page. Bytes are collected so double-byte text decodes.
                if (i + 3 < length && TryHex(rtf[i + 2], rtf[i + 3], out var value))
                {
                    if (skipChars > 0)
                        skipChars--;
                    else if (!state.Skip && !state.InFontTable)
                        pendingBytes.Add(value);
                    i += 4;
                }
                else
                {
                    i += 2;
                }
                groupStart = false;
                continue;
            }

            if (!IsAsciiLetter(next))
            {
                // Control symbols.
                i += 2;
                groupStart = false;
                if (next == '*')
                {
                    ignorableNext = true;
                    continue;
                }

                if (skipChars > 0)
                {
                    skipChars--;
                    continue;
                }

                switch (next)
                {
                    case '\\' or '{' or '}':
                        EmitChar(next);
                        break;
                    case '~':
                        EmitChar(' ');
                        break;
                    case '_':
                        EmitChar('-');
                        break;
                    case '\r' or '\n':
                        Emit("\n");
                        break;
                    case '\t':
                        Emit("\t");
                        break;
                    // "\-" (optional hyphen), "\:" (index subentry) and others print nothing.
                }
                continue;
            }

            // Control word: \letters[-]digits[ ]
            var start = i + 1;
            var j = start;
            while (j < length && IsAsciiLetter(rtf[j]) && j - start < 32)
                j++;
            var word = rtf.Substring(start, j - start);

            var hasParameter = false;
            var parameter = 0;
            if (j < length && (rtf[j] == '-' || char.IsAsciiDigit(rtf[j])))
            {
                var negative = rtf[j] == '-';
                if (negative)
                    j++;
                var digitsStart = j;
                long number = 0;
                while (j < length && char.IsAsciiDigit(rtf[j]) && j - digitsStart < 10)
                {
                    number = number * 10 + (rtf[j] - '0');
                    j++;
                }
                hasParameter = j > digitsStart;
                parameter = (int)Math.Clamp(negative ? -number : number, int.MinValue, int.MaxValue);
            }
            if (j < length && rtf[j] == ' ')
                j++; // the delimiter space belongs to the control word
            i = j;

            var firstInGroup = groupStart;
            groupStart = false;
            var ignorable = ignorableNext;
            ignorableNext = false;

            if (word == "bin" && hasParameter && parameter > 0)
            {
                // Raw binary data follows; it is never text.
                i = (int)Math.Min(length, (long)i + parameter);
                continue;
            }

            // A destination starts a group: skip groups that are not body text.
            if (firstInGroup || ignorable)
            {
                if (word == "fonttbl")
                {
                    FlushBytes();
                    state.InFontTable = true;
                    continue;
                }

                if (SkippedDestinations.Contains(word) || ignorable)
                {
                    FlushBytes();
                    state.Skip = true;
                    continue;
                }
            }

            if (skipChars > 0 && word is not ("u" or "uc"))
            {
                skipChars--;
                continue;
            }

            switch (word)
            {
                case "ansicpg" when hasParameter:
                    FlushBytes();
                    documentCodePage = parameter > 0 ? parameter : 1252;
                    state.CodePage = documentCodePage;
                    break;
                case "mac":
                    documentCodePage = 10000;
                    state.CodePage = documentCodePage;
                    break;
                case "pc":
                    documentCodePage = 437;
                    state.CodePage = documentCodePage;
                    break;
                case "pca":
                    documentCodePage = 850;
                    state.CodePage = documentCodePage;
                    break;
                case "deff" when hasParameter:
                    defaultFont = parameter;
                    break;
                case "f" when hasParameter:
                    FlushBytes();
                    if (state.InFontTable)
                        currentFontNumber = parameter;
                    else
                        state.CodePage = fontCodePages.TryGetValue(parameter, out var fontPage) && fontPage > 0 ? fontPage : documentCodePage;
                    break;
                case "fcharset" when hasParameter && state.InFontTable && currentFontNumber >= 0:
                    if (CharsetCodePages.TryGetValue(parameter, out var page))
                        fontCodePages[currentFontNumber] = page;
                    break;
                case "cpg" when hasParameter && state.InFontTable && currentFontNumber >= 0:
                    fontCodePages[currentFontNumber] = parameter;
                    break;
                case "plain":
                    FlushBytes();
                    if (defaultFont >= 0 && fontCodePages.TryGetValue(defaultFont, out var defaultPage) && defaultPage > 0)
                        state.CodePage = defaultPage;
                    break;
                case "uc" when hasParameter:
                    state.UnicodeSkip = Math.Clamp(parameter, 0, 10);
                    break;
                case "u" when hasParameter:
                    FlushBytes();
                    var code = parameter < 0 ? parameter + 65536 : parameter;
                    if (code is >= 0 and <= 0xFFFF)
                        EmitChar((char)code);
                    skipChars = state.UnicodeSkip;
                    break;
                case "par" or "sect" or "page" or "row" or "nestrow":
                    Emit("\n");
                    break;
                case "line" or "softline":
                    Emit("\n");
                    break;
                case "tab":
                    Emit("\t");
                    break;
                case "cell" or "nestcell":
                    Emit(" | ");
                    break;
                case "emdash":
                    Emit("\u2014");
                    break;
                case "endash":
                    Emit("\u2013");
                    break;
                case "bullet":
                    Emit("\u2022");
                    break;
                case "lquote":
                    Emit("\u2018");
                    break;
                case "rquote":
                    Emit("\u2019");
                    break;
                case "ldblquote":
                    Emit("\u201C");
                    break;
                case "rdblquote":
                    Emit("\u201D");
                    break;
                case "emspace" or "enspace" or "qmspace":
                    Emit(" ");
                    break;
            }
        }

        FlushBytes();
        return Normalize(output.ToString());
    }

    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var raw in lines)
        {
            var line = System.Text.RegularExpressions.Regex.Replace(raw.Replace('\u00A0', ' '), " {2,}", " ").TrimEnd();
            if (line.EndsWith(" |", StringComparison.Ordinal))
                line = line[..^2].TrimEnd(); // the last cell of a table row
            if (line.Trim().Length == 0)
            {
                blank++;
                continue;
            }

            if (output.Length > 0)
                output.Append(blank > 0 ? "\n\n" : "\n");
            output.Append(line.Trim());
            blank = 0;
        }

        return output.ToString();
    }

    private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool TryHex(char high, char low, out byte value)
    {
        value = 0;
        var h = HexValue(high);
        var l = HexValue(low);
        if (h < 0 || l < 0)
            return false;
        value = (byte)(h * 16 + l);
        return true;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };
}
