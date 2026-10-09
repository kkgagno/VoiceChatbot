using System.Diagnostics;
using Xunit;

namespace VoiceChatbot.Tests;

public class PdfLayoutTextTests
{
    private const double Size = 9;              // font size in points
    private const double Cap = Size * 0.7;      // a word's box height (baseline to top)
    private const double CharWidth = Size * 0.5;
    private const double Space = Size * 0.28;

    // The words of a line of text starting at x on baseline y, as a PDF reader finds them.
    private static List<PdfLayoutWord> Words(double x, double y, string text, double size = Size)
    {
        var words = new List<PdfLayoutWord>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var width = word.Length * size * 0.5;
            words.Add(new PdfLayoutWord(word, x, y, x + width, y + size * 0.7));
            x += width + size * 0.28;
        }

        return words;
    }

    private static double EndOf(List<PdfLayoutWord> words) => words[^1].Right;

    // A 1040-style row: line number, label, dot leaders (one "." word every 6 points, as forms draw
    // them), the line number box, and the amount right-aligned at the far right.
    private static List<PdfLayoutWord> FormRow(double y, string number, string label, string amount, bool withAmount = true)
    {
        var words = Words(40, y, number);
        var labelWords = Words(52, y, label);
        words.AddRange(labelWords);
        for (var x = EndOf(labelWords) + 6; x < 456; x += 6)
            words.Add(new PdfLayoutWord(".", x, y, x + 2.5, y + 1.2));
        words.AddRange(Words(462, y, number));
        if (withAmount)
            words.Add(new PdfLayoutWord(amount, 570 - amount.Length * CharWidth, y, 570, y + Cap));
        return words;
    }

    // The content-stream order of a PDF that prints all amounts first, then the labels.
    private static List<PdfLayoutWord> AmountsFirst(IEnumerable<PdfLayoutWord> words) =>
        words.OrderBy(w => w.Left > 500 ? 0 : 1).ThenBy(w => w.Bottom).ToList();

    // ==================== Rows ====================

    [Fact]
    public void FormRow_KeepsTheLabelAndItsFarAwayAmountOnOneLine()
    {
        var words = new List<PdfLayoutWord>();
        words.AddRange(FormRow(614, "9", "Add lines 1z through 8. This is your total income", "114,423"));
        words.AddRange(FormRow(600, "10", "Adjustments to income from Schedule 1, line 26", "2,165"));
        words.AddRange(FormRow(586, "11", "Subtract line 10 from line 9. This is your adjusted gross income", "112,258"));
        words.AddRange(FormRow(572, "12", "Standard deduction or itemized deductions", "25,900"));

        var lines = PdfLayoutText.BuildLines(AmountsFirst(words));

        Assert.Equal(new[]
        {
            "9 Add lines 1z through 8. This is your total income ... 9 | 114,423",
            "10 Adjustments to income from Schedule 1, line 26 ... 10 | 2,165",
            "11 Subtract line 10 from line 9. This is your adjusted gross income ... 11 | 112,258",
            "12 Standard deduction or itemized deductions ... 12 | 25,900"
        }, lines);
    }

    [Fact]
    public void Rows_ReadTopToBottomWhateverOrderTheyAreDrawnIn()
    {
        var words = new List<PdfLayoutWord>();
        words.AddRange(Words(40, 500, "third line"));
        words.AddRange(Words(40, 524, "first line"));
        words.AddRange(Words(40, 512, "second line"));
        words.Reverse();

        Assert.Equal("first line\nsecond line\nthird line", PdfLayoutText.BuildText(words));
    }

    [Fact]
    public void Words_OfALineReadLeftToRightWhateverOrderTheyAreDrawnIn()
    {
        var words = Words(40, 500, "the words of one line");
        words.Reverse();

        Assert.Equal("the words of one line", PdfLayoutText.BuildText(words));
    }

    [Fact]
    public void TwoColumns_StayApartOnEachRow()
    {
        var left = new[] { "The left column starts here and", "continues on its second line", "and ends." };
        var right = new[] { "The right column begins and", "keeps going on its second line", "until it ends." };
        var words = new List<PdfLayoutWord>();
        for (var i = 0; i < 3; i++)
            words.AddRange(Words(320, 700 - i * 12, right[i]));
        for (var i = 0; i < 3; i++)
            words.AddRange(Words(50, 700 - i * 12, left[i]));

        var lines = PdfLayoutText.BuildLines(words);

        Assert.Equal(new[]
        {
            "The left column starts here and | The right column begins and",
            "continues on its second line | keeps going on its second line",
            "and ends. | until it ends."
        }, lines);
    }

    [Fact]
    public void Superscripts_AndSlightlyUnevenBaselines_StayOnTheRow()
    {
        var words = Words(40, 500, "Total tax");
        words.Add(new PdfLayoutWord("1", 80, 504, 82.5, 507.5));           // a raised footnote mark in a small font
        words.AddRange(Words(85, 500, "see instructions"));
        words.Add(new PdfLayoutWord("$", 300, 500.8, 304.5, 507.1));       // drawn a little higher...
        words.Add(new PdfLayoutWord("1,250", 520, 498.4, 542.5, 504.7));    // ...and an amount a little lower
        words.AddRange(Words(40, 488, "Next row"));

        Assert.Equal("Total tax 1 see instructions | $ | 1,250\nNext row", PdfLayoutText.BuildText(words));
    }

    [Fact]
    public void ParagraphGaps_BecomeBlankLines_ButEvenlySpacedRowsDoNot()
    {
        var words = new List<PdfLayoutWord>();
        words.AddRange(Words(40, 700, "Heading"));
        words.AddRange(Words(40, 660, "First paragraph line one"));
        words.AddRange(Words(40, 649, "First paragraph line two"));
        words.AddRange(Words(40, 638, "First paragraph line three"));
        words.AddRange(Words(40, 610, "Second paragraph"));
        words.AddRange(Words(40, 599, "goes on here"));

        Assert.Equal(new[]
        {
            "Heading", "", "First paragraph line one", "First paragraph line two", "First paragraph line three",
            "", "Second paragraph", "goes on here"
        }, PdfLayoutText.BuildLines(words));
    }

    [Fact]
    public void EmptyAndInvalidInput_GivesNothing()
    {
        Assert.Empty(PdfLayoutText.BuildLines(null));
        Assert.Empty(PdfLayoutText.BuildLines(Array.Empty<PdfLayoutWord>()));
        Assert.Equal("", PdfLayoutText.BuildText(null));
        Assert.Empty(PdfLayoutText.BuildLines(new[]
        {
            new PdfLayoutWord("", 0, 0, 10, 10),
            new PdfLayoutWord("   ", 0, 0, 10, 10),
            new PdfLayoutWord(null!, 0, 0, 10, 10),
            new PdfLayoutWord("nan", double.NaN, 0, 10, 10),
            new PdfLayoutWord("inf", 0, 0, double.PositiveInfinity, 10),
        }));
    }

    [Fact]
    public void OddBoxes_AreReadDefensively()
    {
        var words = new List<PdfLayoutWord>
        {
            new("swapped", 70, 506.3, 40, 500),            // left/right and bottom/top the wrong way round
            new("flat", 75, 500, 90, 500),                  // no height (broken font metrics)
            new("tab\tand\nnewline", 95, 500, 160, 506.3),  // white space inside a word
        };

        Assert.Equal("swapped flat tab and newline", PdfLayoutText.BuildText(words));
    }

    [Fact]
    public void DotAndUnderscoreLeaders_Shrink()
    {
        var words = Words(40, 500, "Name ______________ Date ____ Total ..................... 12");
        words.AddRange(Words(40, 488, "It costs 5 . . or so, etc..."));

        Assert.Equal("Name ___ Date ___ Total ... 12\nIt costs 5 . . or so, etc...", PdfLayoutText.BuildText(words));
    }

    [Fact]
    public void WordsDrawnTwiceOnTheSameSpot_AppearOnce()
    {
        // Fake bold: the same word drawn again a fraction of a point to the right.
        var words = Words(40, 500, "Total income");
        words.Add(words[0] with { Left = words[0].Left + 0.3, Right = words[0].Right + 0.3 });

        Assert.Equal("Total income", PdfLayoutText.BuildText(words));
    }

    [Fact]
    public void RotatedNote_ComesAfterTheMainText()
    {
        var words = Words(40, 700, "Main text of the page");
        words.AddRange(Words(40, 688, "goes here"));
        // "Attach W-2 here" running up the left margin.
        words.Add(new PdfLayoutWord("Attach", 20, 300, 26.3, 327, PdfWordOrientation.Rotate270));
        words.Add(new PdfLayoutWord("W-2", 20, 330, 26.3, 343.5, PdfWordOrientation.Rotate270));
        words.Add(new PdfLayoutWord("here", 20, 346, 26.3, 364, PdfWordOrientation.Rotate270));

        Assert.Equal("Main text of the page\ngoes here\n\nAttach W-2 here", PdfLayoutText.BuildText(words));
    }

    [Fact]
    public void TurnedPage_ReadsInItsOwnDirection()
    {
        // A page turned a quarter clockwise: text runs top to bottom, the next line is to its left.
        var words = new List<PdfLayoutWord>
        {
            new("Second", 588, 700, 594.3, 727, PdfWordOrientation.Rotate90),
            new("line", 588, 680, 594.3, 697, PdfWordOrientation.Rotate90),
            new("First", 600, 705, 606.3, 727, PdfWordOrientation.Rotate90),
            new("line", 600, 685, 606.3, 702, PdfWordOrientation.Rotate90),
            new("here", 600, 665, 606.3, 682, PdfWordOrientation.Rotate90),
        };

        Assert.Equal("First line here\nSecond line", PdfLayoutText.BuildText(words));

        // Upside down: right to left, the next line above.
        var upsideDown = new List<PdfLayoutWord>
        {
            new("down", 480, 300, 500, 306.3, PdfWordOrientation.Rotate180),
            new("Upside", 505, 300, 532, 306.3, PdfWordOrientation.Rotate180),
            new("next", 510, 312, 530, 318.3, PdfWordOrientation.Rotate180),
        };
        Assert.Equal("Upside down\nnext", PdfLayoutText.BuildText(upsideDown));
    }

    [Fact]
    public void LargePdf_IsLaidOutQuickly()
    {
        // 200 dense pages: 60 rows of 20 words in two columns.
        var pages = Enumerable.Range(0, 200).Select(p =>
        {
            var words = new List<PdfLayoutWord>();
            for (var row = 0; row < 60; row++)
            {
                words.AddRange(Words(40, 760 - row * 11, $"page {p} row {row} left column words one two three four five"));
                words.AddRange(Words(330, 760 - row * 11, $"right column words six seven eight nine ten {row * p}"));
            }
            return words;
        }).ToList();

        var watch = Stopwatch.StartNew();
        var text = pages.Select(PdfLayoutText.BuildText).ToList();
        watch.Stop();

        Assert.Equal(60, text[199].Split('\n').Length);
        Assert.StartsWith("page 199 row 0 left column words one two three four five | right column", text[199]);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    // ==================== Form values ====================

    [Fact]
    public void FormValue_LandsOnTheRowOfItsLabel()
    {
        var words = new List<PdfLayoutWord>();
        words.AddRange(FormRow(600, "10", "Adjustments to income from Schedule 1, line 26", "", withAmount: false));
        words.AddRange(FormRow(586, "11", "Subtract line 10 from line 9. This is your adjusted gross income", "", withAmount: false));
        // The amount fields: a box as tall as the row, right of the line number box.
        var fields = new[]
        {
            new PdfLayoutWord("112,258", 475, 583, 575, 596),
            new PdfLayoutWord("2,165", 475, 597, 575, 610),
        };

        var lines = PdfLayoutText.BuildLines(PdfLayoutText.WithFormValues(words, fields));

        Assert.Equal(new[]
        {
            "10 Adjustments to income from Schedule 1, line 26 ... 10 | 2,165",
            "11 Subtract line 10 from line 9. This is your adjusted gross income ... 11 | 112,258"
        }, lines);
    }

    [Fact]
    public void FormValues_CheckboxesNamesAndMultiLineText()
    {
        var words = new List<PdfLayoutWord>();
        words.AddRange(Words(40, 700, "Filing Status"));
        words.AddRange(Words(140, 700, "Single"));
        words.AddRange(Words(220, 700, "Married filing jointly"));
        words.AddRange(Words(40, 680, "Your first name"));
        words.AddRange(Words(40, 656, "Home address"));
        var fields = new[]
        {
            new PdfLayoutWord("[X]", 206, 698, 216, 708),                        // checked box before "Married filing jointly"
            new PdfLayoutWord("Keith  A", 200, 676, 350, 689),                   // extra space inside the value
            new PdfLayoutWord("123 Main St\r\nSpringfield, NH", 150, 638, 400, 665),
            new PdfLayoutWord("   ", 200, 620, 350, 633),                        // blank: left out
            new PdfLayoutWord(null!, 200, 600, 350, 613),
        };

        var withValues = PdfLayoutText.WithFormValues(words, fields);

        Assert.Equal(words.Count + 4, withValues.Count);
        Assert.Equal(new[]
        {
            "Filing Status | Single | [X] Married filing jointly",
            "Your first name | Keith A",
            "Home address | 123 Main St",
            "Springfield, NH"
        }, PdfLayoutText.BuildLines(withValues));
    }

    [Fact]
    public void FormValue_AlreadyPrintedInItsField_IsNotAddedTwice()
    {
        var words = FormRow(586, "11", "Adjusted gross income", "112,258");
        var fields = new[]
        {
            new PdfLayoutWord("112258", 475, 583, 575, 596),       // the same number, printed with a comma
            new PdfLayoutWord("[X]", 200, 400, 210, 410),
        };
        words.Add(new PdfLayoutWord("X", 202, 401, 207, 407.3));    // a flattened check mark

        var withValues = PdfLayoutText.WithFormValues(words, fields);

        Assert.Equal(words.Count, withValues.Count);
        Assert.Equal(new[] { "11 Adjusted gross income ... 11 | 112,258", "X" }, PdfLayoutText.BuildLines(withValues));
    }

    [Fact]
    public void FormValue_DifferentFromWhatIsPrintedThere_IsAdded()
    {
        var words = Words(40, 500, "Amount");
        words.AddRange(Words(480, 500, "0.00"));                    // the printed default under a filled-in field
        var fields = new[] { new PdfLayoutWord("75.00", 470, 497, 575, 510) };

        Assert.Equal(words.Count + 1, PdfLayoutText.WithFormValues(words, fields).Count);
    }

    [Fact]
    public void FormValue_OnATurnedPage_RunsWithItsText()
    {
        // A page turned a quarter clockwise (PdfPig gives the words and the field box in turned page space).
        var words = new List<PdfLayoutWord>
        {
            new("11", 600, 742, 606.5, 751, PdfWordOrientation.Rotate90),
            new("Adjusted", 600, 705, 606.5, 739, PdfWordOrientation.Rotate90),
            new("gross", 600, 680, 606.5, 702, PdfWordOrientation.Rotate90),
            new("income", 600, 648, 606.5, 677, PdfWordOrientation.Rotate90),
            new("11", 600, 320, 606.5, 329, PdfWordOrientation.Rotate90),
        };
        var fields = new[] { new PdfLayoutWord("112,258", 597, 217, 610, 317, PdfWordOrientation.Rotate90) };

        Assert.Equal("11 Adjusted gross income | 11 | 112,258", PdfLayoutText.BuildText(PdfLayoutText.WithFormValues(words, fields)));
    }

    [Fact]
    public void FormValues_WithoutPageWords_StillGetPlaced()
    {
        Assert.Equal("Keith | 112,258", PdfLayoutText.BuildText(PdfLayoutText.WithFormValues(null, new[]
        {
            new PdfLayoutWord("Keith", 40, 500, 200, 512),
            new PdfLayoutWord("112,258", 300, 500, 500, 512),
            new PdfLayoutWord("bad", double.NaN, 500, 500, 512),
        })));
    }
}
