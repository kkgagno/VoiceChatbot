using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VoiceChatbot;

/// <summary>Which way a word's text runs on the page (the same values as PdfPig's TextOrientation).</summary>
public enum PdfWordOrientation
{
    /// <summary>Normal text, left to right.</summary>
    Horizontal,
    /// <summary>Turned a quarter clockwise: reads top to bottom.</summary>
    Rotate90,
    /// <summary>Upside down: reads right to left.</summary>
    Rotate180,
    /// <summary>Turned a quarter counter-clockwise: reads bottom to top.</summary>
    Rotate270,
    /// <summary>Any other angle; laid out by its upright bounding box.</summary>
    Other
}

/// <summary>
/// A word on a PDF page, or a filled-in form value: its text and the box around it in page coordinates
/// (points, y grows upwards). For horizontal PdfPig words the bottom is the baseline.
/// </summary>
public readonly record struct PdfLayoutWord(
    string Text,
    double Left,
    double Bottom,
    double Right,
    double Top,
    PdfWordOrientation Orientation = PdfWordOrientation.Horizontal);

/// <summary>
/// Turns the words of one PDF page into lines of text in reading order, the way the page looks rather
/// than the order the PDF happens to draw them in. Words that overlap vertically (by at least
/// <see cref="SameRowOverlap"/> of the smaller one, so superscripts and slightly uneven baselines
/// count) form one row; a row reads left to right with one space between words and
/// <see cref="ColumnSeparator"/> across a wide gap, so a form's label and its amount far to the right
/// stay on one line while columns stay apart. Rows go top to bottom, with a blank line for a large
/// vertical gap. Runs of dot leaders become "..." (underscore lines "___"). Text running another way
/// (a rotated margin note) is laid out on its own after the main text. Filled-in form fields are added
/// as extra words with <see cref="WithFormValues"/>. Pure logic: PdfPageText gets the words from PdfPig.
/// </summary>
public static class PdfLayoutText
{
    /// <summary>Put between two words on a row that are far apart (table and form columns).</summary>
    public const string ColumnSeparator = " | ";
    /// <summary>A gap at least this many word heights wide separates columns (a normal space is about half of one).</summary>
    public const double ColumnGapHeights = 2.0;
    /// <summary>Two words are on the same row when they overlap vertically by this share of the shorter one.</summary>
    public const double SameRowOverlap = 0.5;
    /// <summary>Rows this many times further apart than the page's usual row spacing get a blank line between them...</summary>
    public const double ParagraphPitches = 1.6;
    /// <summary>...when the gap between them is at least this many row heights.</summary>
    public const double MinParagraphGapHeights = 0.5;
    /// <summary>The text height assumed for form values on a page without words that run their way.</summary>
    public const double DefaultTextHeight = 8.0;

    private const string DotLeader = "...";
    private const string LineLeader = "___";
    // A word counts as "normal" height (a row's reference) within this range of the page's median height.
    private const double MinNormalHeight = 0.6;
    private const double MaxNormalHeight = 1.6;
    // An average character is about this many text heights wide (for placing a form value in its field).
    private const double CharWidthHeights = 0.55;

    /// <summary>The page as text: <see cref="BuildLines"/> joined with line breaks.</summary>
    public static string BuildText(IEnumerable<PdfLayoutWord>? words) => string.Join("\n", BuildLines(words));

    /// <summary>
    /// The page as lines in reading order (see the class summary); "" between paragraphs. Words without
    /// text or with an invalid box are skipped; an empty page gives no lines.
    /// </summary>
    public static IReadOnlyList<string> BuildLines(IEnumerable<PdfLayoutWord>? words)
    {
        var groups = new List<Box>?[5];
        var index = 0;
        foreach (var word in words ?? Enumerable.Empty<PdfLayoutWord>())
        {
            var orientation = word.Orientation is >= PdfWordOrientation.Horizontal and <= PdfWordOrientation.Other
                ? word.Orientation
                : PdfWordOrientation.Other;
            if (TryMakeBox(word, orientation, index++, out var box))
                (groups[(int)orientation] ??= new List<Box>()).Add(box);
        }

        // The way most of the text runs comes first; rotated notes and headers follow on their own.
        var lines = new List<string>();
        foreach (var group in groups.Where(g => g is { Count: > 0 }).OrderByDescending(g => g!.Count))
        {
            var groupLines = Layout(group!);
            if (groupLines.Count == 0)
                continue;
            if (lines.Count > 0)
                lines.Add("");
            lines.AddRange(groupLines);
        }

        return lines;
    }

    /// <summary>
    /// <paramref name="words"/> plus the values of filled-in form fields (<paramref name="fields"/>: the
    /// value, the field's box, and the way the page's text runs there), each placed inside its field at
    /// the height of the page's text so it lands on the row of its printed label: numbers
    /// right-aligned as forms show amounts, other text left-aligned, a multi-line value one line under
    /// another. Blank values and values the page already prints inside that field are left out.
    /// </summary>
    public static List<PdfLayoutWord> WithFormValues(IReadOnlyList<PdfLayoutWord>? words, IEnumerable<PdfLayoutWord>? fields)
    {
        var result = new List<PdfLayoutWord>(words ?? Array.Empty<PdfLayoutWord>());
        var pageWords = result.Count;
        var textHeights = new Dictionary<PdfWordOrientation, double>();

        foreach (var field in fields ?? Enumerable.Empty<PdfLayoutWord>())
        {
            if (!IsFinite(field))
                continue;

            var lines = (field.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
                .Select(CleanText)
                .Where(l => l.Length > 0)
                .ToList();
            if (lines.Count == 0 || PrintedInside(result, pageWords, field, string.Join(" ", lines)))
                continue;

            var orientation = field.Orientation is >= PdfWordOrientation.Horizontal and <= PdfWordOrientation.Rotate270
                ? field.Orientation
                : PdfWordOrientation.Horizontal;
            if (!textHeights.TryGetValue(orientation, out var textHeight))
                textHeights[orientation] = textHeight = TextHeight(result, pageWords, orientation);

            // Work in the field's upright frame (a field on a turned page runs the way its text does).
            var (left, bottom, right, top) = ToFrame(field, orientation);
            var height = top - bottom > 0 ? Math.Min(top - bottom, textHeight) : textHeight;
            for (var i = 0; i < lines.Count; i++)
            {
                // A one-line value sits in the middle of its field; more lines run down from its top.
                var lineTop = lines.Count == 1 ? (bottom + top + height) / 2 : top - i * height * 1.2;
                var width = Math.Min(right - left, lines[i].Length * height * CharWidthHeights);
                var (l, r) = IsNumberLike(lines[i]) ? (right - width, right) : (left, left + width);
                result.Add(FromFrame(lines[i], l, lineTop - height, r, lineTop, orientation));
            }
        }

        return result;
    }

    // The median height of the page's words that run this way, in their upright frame.
    private static double TextHeight(List<PdfLayoutWord> words, int count, PdfWordOrientation orientation)
    {
        var heights = new List<double>();
        for (var i = 0; i < count; i++)
        {
            var w = words[i];
            if (w.Orientation != orientation || !IsFinite(w) || !HasLetterOrDigit(w.Text ?? ""))
                continue;
            var (_, b, _, t) = ToFrame(w, orientation);
            if (t - b > 0)
                heights.Add(t - b);
        }

        return heights.Count > 0 ? Median(heights) : DefaultTextHeight;
    }

    // ==================== Rows ====================

    // A word in the upright frame of its orientation: x runs along the text, y up the glyphs.
    private sealed class Box(string text, double left, double bottom, double right, double top, int index)
    {
        public string Text { get; } = text;
        public double Left { get; } = left;
        public double Bottom { get; set; } = bottom;
        public double Right { get; } = right;
        public double Top { get; set; } = top;
        public int Index { get; } = index;
        public double Height => Top - Bottom;
        public double Center => (Top + Bottom) / 2;
    }

    private sealed class Row(Box first, bool normal)
    {
        public List<Box> Words { get; } = new() { first };
        // The last word added, and the last one of normal height: a new word joins the row when it
        // overlaps either, so slowly drifting baselines and superscripts both stay on the row.
        public Box Last { get; set; } = first;
        public Box? Reference { get; set; } = normal ? first : null;
    }

    private static bool TryMakeBox(PdfLayoutWord word, PdfWordOrientation orientation, int index, out Box box)
    {
        box = null!;
        var text = CleanText(word.Text);
        if (text.Length == 0 || !IsFinite(word))
            return false;

        var (l, b, r, t) = ToFrame(word, orientation);
        box = new Box(text, l, b, r, t, index);
        return true;
    }

    // The box turned so its text reads left to right with the glyphs upright.
    private static (double Left, double Bottom, double Right, double Top) ToFrame(PdfLayoutWord word, PdfWordOrientation orientation)
    {
        double l = Math.Min(word.Left, word.Right), r = Math.Max(word.Left, word.Right);
        double b = Math.Min(word.Bottom, word.Top), t = Math.Max(word.Bottom, word.Top);
        return orientation switch
        {
            PdfWordOrientation.Rotate90 => (-t, l, -b, r),
            PdfWordOrientation.Rotate180 => (-r, -t, -l, -b),
            PdfWordOrientation.Rotate270 => (b, -r, t, -l),
            _ => (l, b, r, t)
        };
    }

    // The page box of a box in the upright frame of <paramref name="orientation"/> (undoes ToFrame).
    private static PdfLayoutWord FromFrame(string text, double l, double b, double r, double t, PdfWordOrientation orientation) => orientation switch
    {
        PdfWordOrientation.Rotate90 => new PdfLayoutWord(text, b, -r, t, -l, orientation),
        PdfWordOrientation.Rotate180 => new PdfLayoutWord(text, -r, -t, -l, -b, orientation),
        PdfWordOrientation.Rotate270 => new PdfLayoutWord(text, -t, l, -b, r, orientation),
        _ => new PdfLayoutWord(text, l, b, r, t, orientation)
    };

    private static List<string> Layout(List<Box> boxes)
    {
        // Measure the text by its words: dot leaders, periods and dashes are much lower.
        var heights = boxes.Where(b => HasLetterOrDigit(b.Text)).Select(b => b.Height).Where(h => h > 0).ToList();
        if (heights.Count == 0)
            heights = boxes.Select(b => b.Height).Where(h => h > 0).ToList();
        // Boxes without height (broken font metrics): guess the text size from the width per character.
        var unit = heights.Count > 0
            ? Median(heights)
            : Math.Max(1, Median(boxes.Select(b => (b.Right - b.Left) / Math.Max(1, b.Text.Length)).ToList()) / CharWidthHeights);
        foreach (var box in boxes.Where(b => b.Height < unit * 0.1))
            box.Top = box.Bottom + unit;

        // Left to right: each word joins the row it overlaps most, else starts a row.
        var rows = new List<Row>();
        foreach (var box in boxes.OrderBy(b => b.Left).ThenByDescending(b => b.Top).ThenBy(b => b.Index))
        {
            Row? best = null;
            double bestScore = 0, bestDistance = double.MaxValue;
            foreach (var row in rows)
            {
                var score = Math.Max(Overlap(row.Last, box), row.Reference == null ? 0 : Overlap(row.Reference, box));
                if (score < SameRowOverlap)
                    continue;

                var distance = Math.Abs(row.Last.Center - box.Center);
                if (score > bestScore + 1e-9 || (Math.Abs(score - bestScore) <= 1e-9 && distance < bestDistance))
                {
                    best = row;
                    bestScore = score;
                    bestDistance = distance;
                }
            }

            var normal = box.Height >= unit * MinNormalHeight && box.Height <= unit * MaxNormalHeight;
            if (best == null)
            {
                rows.Add(new Row(box, normal));
                continue;
            }

            best.Words.Add(box);
            best.Last = box;
            if (normal)
                best.Reference = box;
        }

        // Top to bottom, by the middle of each row's normal-height words.
        var ordered = rows
            .Select(row => (Row: row, Core: CoreWords(row, unit)))
            .Select(r => (r.Row, r.Core, Center: r.Core.Average(w => w.Center), Bottom: r.Core.Min(w => w.Bottom), Top: r.Core.Max(w => w.Top),
                Left: r.Row.Words.Min(w => w.Left)))
            .OrderByDescending(r => r.Center)
            .ThenBy(r => r.Left)
            .ToList();

        // A blank line where rows are much further apart than usual on this page (a paragraph or
        // section break), and only where there is a real gap, not just a taller row.
        var rowHeight = Median(ordered.Select(r => r.Top - r.Bottom).Where(h => h > 0).DefaultIfEmpty(unit).ToList());
        var pitch = Median(Enumerable.Range(1, Math.Max(0, ordered.Count - 1))
            .Select(i => ordered[i - 1].Center - ordered[i].Center)
            .Where(d => d > 0)
            .ToList());
        var lines = new List<string>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0 && pitch > 0 &&
                ordered[i - 1].Center - ordered[i].Center > pitch * ParagraphPitches &&
                ordered[i - 1].Bottom - ordered[i].Top > rowHeight * MinParagraphGapHeights)
                lines.Add("");
            lines.Add(FormatRow(ordered[i].Row.Words, unit));
        }

        return lines;
    }

    private static List<Box> CoreWords(Row row, double unit)
    {
        var core = row.Words.Where(w => w.Height >= unit * MinNormalHeight && w.Height <= unit * MaxNormalHeight).ToList();
        return core.Count > 0 ? core : row.Words;
    }

    // How much two boxes overlap vertically, as a share of the shorter one.
    private static double Overlap(Box a, Box b)
    {
        var overlap = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        var shorter = Math.Min(a.Height, b.Height);
        return overlap <= 0 || shorter <= 0 ? 0 : overlap / shorter;
    }

    private static string FormatRow(List<Box> words, double unit)
    {
        var sorted = words.OrderBy(w => w.Left).ThenBy(w => w.Index).ToList();
        var rowUnit = Math.Max(unit, Median(sorted.Select(w => w.Height).ToList()));

        // Drop words drawn twice on the same spot (fake bold, a form value the page also prints).
        var kept = new List<Box>(sorted.Count);
        foreach (var word in sorted)
        {
            var duplicate = false;
            for (var i = kept.Count - 1; i >= 0 && i >= kept.Count - 3; i--)
            {
                if (string.Equals(kept[i].Text, word.Text, StringComparison.Ordinal) && SameSpot(kept[i], word))
                {
                    duplicate = true;
                    break;
                }
            }

            if (!duplicate)
                kept.Add(word);
        }

        var sb = new StringBuilder();
        var previousRight = double.NegativeInfinity;
        for (var i = 0; i < kept.Count; i++)
        {
            var word = kept[i];
            var text = word.Text;
            var right = word.Right;
            if (IsLeader(text))
            {
                // A run of dots or underscores (". . . . ." between a label and its amount) becomes one "...".
                var j = i;
                var length = 0;
                var underscores = false;
                while (j < kept.Count && IsLeader(kept[j].Text))
                {
                    length += kept[j].Text.Length;
                    underscores |= kept[j].Text.Contains('_');
                    right = Math.Max(right, kept[j].Right);
                    j++;
                }

                if (length >= 3)
                {
                    text = underscores ? LineLeader : DotLeader;
                    i = j - 1;
                }
                else
                {
                    right = word.Right;
                }
            }

            if (sb.Length > 0)
                sb.Append(word.Left - previousRight >= rowUnit * ColumnGapHeights ? ColumnSeparator : " ");
            sb.Append(text);
            previousRight = Math.Max(previousRight, right);
        }

        return sb.ToString();
    }

    private static bool SameSpot(Box a, Box b)
    {
        var width = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var height = Math.Min(a.Top, b.Top) - Math.Max(a.Bottom, b.Bottom);
        if (width <= 0 || height <= 0)
            return false;

        var smaller = Math.Min((a.Right - a.Left) * a.Height, (b.Right - b.Left) * b.Height);
        return smaller <= 0 || width * height >= smaller * 0.5;
    }

    private static bool HasLetterOrDigit(string text) => text.Any(char.IsLetterOrDigit);

    private static bool IsLeader(string text) => text.Length > 0 && text.All(c => c is '.' or '·' or '…' or '_');

    // ==================== Form values ====================

    // True when the page's own words inside the field already read like the value (a flattened form).
    private static bool PrintedInside(List<PdfLayoutWord> words, int count, PdfLayoutWord field, string value)
    {
        const double slack = 1;
        double left = Math.Min(field.Left, field.Right), right = Math.Max(field.Left, field.Right);
        double bottom = Math.Min(field.Bottom, field.Top), top = Math.Max(field.Bottom, field.Top);
        var inside = new List<(PdfLayoutWord Word, double Left, double Top)>();
        for (var i = 0; i < count; i++)
        {
            var w = words[i];
            var x = (w.Left + w.Right) / 2;
            var y = (w.Bottom + w.Top) / 2;
            if (x >= left - slack && x <= right + slack && y >= bottom - slack && y <= top + slack)
            {
                var (l, _, _, t) = ToFrame(w, w.Orientation);
                inside.Add((w, l, t));
            }
        }

        if (inside.Count == 0)
            return false;

        var printed = string.Concat(inside.OrderByDescending(w => w.Top).ThenBy(w => w.Left).Select(w => w.Word.Text));
        var wanted = LettersAndDigits(value);
        return wanted.Length > 0 && string.Equals(LettersAndDigits(printed), wanted, StringComparison.OrdinalIgnoreCase);
    }

    private static string LettersAndDigits(string? text) => new((text ?? "").Where(char.IsLetterOrDigit).ToArray());

    private static bool IsNumberLike(string text) =>
        text.Any(char.IsDigit) && text.All(c => char.IsDigit(c) || c is ',' or '.' or '$' or '-' or '(' or ')' or '%' or ' ' or '+');

    // ==================== Helpers ====================

    private static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var sb = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = sb.Length > 0;
                continue;
            }

            if (space)
                sb.Append(' ');
            sb.Append(c);
            space = false;
        }

        return sb.ToString();
    }

    private static bool IsFinite(PdfLayoutWord w) =>
        double.IsFinite(w.Left) && double.IsFinite(w.Right) && double.IsFinite(w.Bottom) && double.IsFinite(w.Top);

    private static double Median(List<double> values)
    {
        if (values.Count == 0)
            return 0;

        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
