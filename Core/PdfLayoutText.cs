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
/// stay on one line while table columns stay apart. Rows go top to bottom, with a blank line for a
/// large vertical gap. Columns of running text (a newsletter, a two-column policy) are read one after
/// the other instead, whether or not their lines line up. Runs of dot leaders become "..." (underscore
/// lines "___"). Text running another way (a rotated margin note) is laid out on its own after the
/// main text. Filled-in form fields are added as extra words with <see cref="WithFormValues"/>. Pure
/// logic: PdfPageText gets the words from PdfPig.
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
    /// <summary>Text whose baseline is within this many degrees of an axis runs along it (the OCR layer of a slightly crooked scan).</summary>
    public const double AxisSnapDegrees = 5.0;
    /// <summary>A gap free of text through at least this many consecutive rows can split them into columns.</summary>
    public const int MinColumnRows = 3;
    /// <summary>Columns of running text have at least this many words on a typical line (a form's amounts and a table's cells have fewer).</summary>
    public const int MinColumnLineWords = 4;

    private const string DotLeader = "...";
    private const string LineLeader = "___";
    // A word counts as "normal" height (a row's reference) within this range of the page's median height.
    private const double MinNormalHeight = 0.6;
    private const double MaxNormalHeight = 1.6;
    // An average character is about this many text heights wide (for placing a form value in its field).
    private const double CharWidthHeights = 0.55;
    // Columns inside columns, at most this deep.
    private const int MaxColumnDepth = 4;
    // A line of running text that falls short of its column's right edge by at most this share of the column's width fills it.
    private const double FullLineShortfall = 0.2;

    /// <summary>The page as text: <see cref="BuildLines"/> joined with line breaks.</summary>
    public static string BuildText(IEnumerable<PdfLayoutWord>? words) => string.Join("\n", BuildLines(words));

    /// <summary>
    /// The way text runs whose baseline points along (<paramref name="dx"/>, <paramref name="dy"/>) in
    /// page coordinates (y up): the nearest axis when within <see cref="AxisSnapDegrees"/> of it, so a
    /// slightly turned line is read with the straight ones around it, else <see cref="PdfWordOrientation.Other"/>.
    /// </summary>
    public static PdfWordOrientation OrientationOf(double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || (dx == 0 && dy == 0))
            return PdfWordOrientation.Other;

        var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        var quarters = Math.Round(angle / 90);
        if (Math.Abs(angle - quarters * 90) > AxisSnapDegrees)
            return PdfWordOrientation.Other;

        return quarters switch
        {
            0 => PdfWordOrientation.Horizontal,
            1 => PdfWordOrientation.Rotate270,
            -1 => PdfWordOrientation.Rotate90,
            _ => PdfWordOrientation.Rotate180
        };
    }

    /// <summary>
    /// The way text runs that is turned <paramref name="clockwiseDegrees"/> as displayed (a page's
    /// /Rotate, less a form field's own /MK /R turn), to the nearest quarter turn.
    /// </summary>
    public static PdfWordOrientation OrientationOfTurn(int clockwiseDegrees)
    {
        var quarters = ((int)Math.Round(clockwiseDegrees / 90.0) % 4 + 4) % 4;
        return quarters switch
        {
            1 => PdfWordOrientation.Rotate90,
            2 => PdfWordOrientation.Rotate180,
            3 => PdfWordOrientation.Rotate270,
            _ => PdfWordOrientation.Horizontal
        };
    }

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

        // Top to bottom; columns of running text one after the other.
        var ordered = Ordered(rows.Select(row => MakeLine(row.Words, unit)));
        var lines = new List<string>(ordered.Count);
        foreach (var block in Blocks(ordered, unit, 0))
        {
            if (lines.Count > 0)
                lines.Add("");
            lines.AddRange(FormatBlock(block, unit));
        }

        return lines;
    }

    // A row's words, placed by the middle of its normal-height words.
    private sealed record Line(List<Box> Words, double Center, double Bottom, double Top, double Left);

    private static Line MakeLine(List<Box> words, double unit)
    {
        var core = words.Where(w => w.Height >= unit * MinNormalHeight && w.Height <= unit * MaxNormalHeight).ToList();
        if (core.Count == 0)
            core = words;
        return new Line(words, core.Average(w => w.Center), core.Min(w => w.Bottom), core.Max(w => w.Top), words.Min(w => w.Left));
    }

    private static List<Line> Ordered(IEnumerable<Line> lines) =>
        lines.OrderByDescending(l => l.Center).ThenBy(l => l.Left).ToList();

    // Lines in reading order, a blank line where rows are much further apart than usual in this block
    // (a paragraph or section break), and only where there is a real gap, not just a taller row.
    private static List<string> FormatBlock(List<Line> block, double unit)
    {
        var rowHeight = Median(block.Select(r => r.Top - r.Bottom).Where(h => h > 0).DefaultIfEmpty(unit).ToList());
        var pitch = Median(Enumerable.Range(1, Math.Max(0, block.Count - 1))
            .Select(i => block[i - 1].Center - block[i].Center)
            .Where(d => d > 0)
            .ToList());
        var lines = new List<string>(block.Count);
        for (var i = 0; i < block.Count; i++)
        {
            if (i > 0 && pitch > 0 &&
                block[i - 1].Center - block[i].Center > pitch * ParagraphPitches &&
                block[i - 1].Bottom - block[i].Top > rowHeight * MinParagraphGapHeights)
                lines.Add("");
            lines.Add(FormatRow(block[i].Words, unit));
        }

        return lines;
    }

    // ==================== Columns ====================

    // The rows (top to bottom) as blocks to read one after another: rows that a gutter splits into
    // columns of running text give one block per column; all other rows read across the page, as one
    // block between the column blocks.
    private static List<List<Line>> Blocks(List<Line> rows, double unit, int depth)
    {
        var blocks = new List<List<Line>>();
        var plain = new List<Line>();
        var minGap = unit * ColumnGapHeights;
        double left = double.MaxValue, right = double.MinValue;
        foreach (var word in rows.SelectMany(r => r.Words))
        {
            left = Math.Min(left, word.Left);
            right = Math.Max(right, word.Right);
        }

        var i = 0;
        while (i < rows.Count)
        {
            var (end, gutter) = depth < MaxColumnDepth ? FindColumns(rows, i, minGap, left, right) : (i + 1, null);
            if (gutter is not { } g)
            {
                plain.AddRange(rows.GetRange(i, end - i));
                i = end;
                continue;
            }

            if (plain.Count > 0)
            {
                blocks.Add(plain);
                plain = new List<Line>();
            }

            var run = rows.GetRange(i, end - i);
            foreach (var leftSide in new[] { true, false })
            {
                var side = Ordered(run
                    .Select(r => r.Words.Where(w => (w.Right <= g) == leftSide).ToList())
                    .Where(words => words.Count > 0)
                    .Select(words => MakeLine(words, unit)));
                blocks.AddRange(Blocks(side, unit, depth + 1));
            }

            i = end;
        }

        if (plain.Count > 0)
            blocks.Add(plain);
        return blocks;
    }

    // The rows from start on that share a vertical gap at least minGap wide (rows on one side only
    // count, so columns whose lines do not line up are found too), and, when both sides are running
    // text, the middle of that gutter. A form's amounts and a table's cells have few words to a line,
    // so their rows stay rows, even when an amount sits half a row off its label, and so do rows with
    // such a column beside another gap; such a run is passed over whole (End without a gutter), as
    // the rows from the next one on would be too.
    private static (int End, double? Gutter) FindColumns(List<Line> rows, int start, double minGap, double left, double right)
    {
        var covered = new List<(double Left, double Right)>();
        var end = start;
        for (; end < rows.Count; end++)
        {
            var next = Cover(covered, rows[end].Words, minGap);
            // Every gap the rows so far had must stay open; one that closes ends the run.
            if (covered.Count > 1 && Gaps(covered).Any(gap => !Gaps(next).Any(n => n.Left >= gap.Left && n.Right <= gap.Right)))
                break;
            covered = next;
            // Text right across the page: no later row can open a gap.
            if (covered.Count == 1 && covered[0].Left <= left + minGap && covered[0].Right >= right - minGap)
                return (start + 1, null);
        }

        if (end - start < MinColumnRows)
            return (start + 1, null);

        double? gutter = null;
        foreach (var (gapLeft, gapRight) in Gaps(covered).OrderByDescending(g => g.Right - g.Left))
        {
            var middle = (gapLeft + gapRight) / 2;
            var (leftRows, leftText) = Side(rows, start, end, w => w.Right <= middle);
            var (rightRows, rightText) = Side(rows, start, end, w => w.Right > middle);
            if (leftText && rightText)
                gutter ??= middle;
            else if ((!leftText && leftRows * 2 >= end - start) || (!rightText && rightRows * 2 >= end - start))
                return (end, null);
        }

        return (covered.Count > 1 ? end : start + 1, gutter);
    }

    // How many rows have words on one side of a gutter, and whether they read as a column of running
    // text: a typical line has MinColumnLineWords words, and most lines fill the column (a table's
    // cells and a form's labels end wherever their text does).
    private static (int Rows, bool RunningText) Side(List<Line> rows, int start, int end, Func<Box, bool> onSide)
    {
        var words = new List<double>();
        var lefts = new List<double>();
        var rights = new List<double>();
        for (var r = start; r < end; r++)
        {
            var side = rows[r].Words.Where(w => onSide(w) && HasLetterOrDigit(w.Text)).ToList();
            if (side.Count == 0)
                continue;
            words.Add(side.Count);
            lefts.Add(side.Min(w => w.Left));
            rights.Add(side.Max(w => w.Right));
        }

        if (words.Count < 2 || Median(words) < MinColumnLineWords)
            return (words.Count, false);

        var columnLeft = lefts.Min();
        var columnRight = rights.Max();
        var fullFrom = columnRight - (columnRight - columnLeft) * FullLineShortfall;
        return (words.Count, rights.Count(r => r >= fullFrom) * 2 >= rights.Count);
    }

    // The x ranges the words cover together with those already covered, joined across gaps narrower than minGap.
    private static List<(double Left, double Right)> Cover(List<(double Left, double Right)> covered, List<Box> words, double minGap)
    {
        var all = covered.Concat(words.Select(w => (w.Left, w.Right))).OrderBy(r => r.Left).ToList();
        var merged = new List<(double Left, double Right)>(all.Count);
        foreach (var range in all)
        {
            if (merged.Count > 0 && range.Left - merged[^1].Right < minGap)
                merged[^1] = (merged[^1].Left, Math.Max(merged[^1].Right, range.Right));
            else
                merged.Add(range);
        }

        return merged;
    }

    private static IEnumerable<(double Left, double Right)> Gaps(List<(double Left, double Right)> covered) =>
        Enumerable.Range(1, Math.Max(0, covered.Count - 1)).Select(i => (covered[i - 1].Right, covered[i].Left));

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
