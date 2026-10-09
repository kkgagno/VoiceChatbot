using System.IO.Compression;
using System.Text;
using VoiceChatbot;
using Xunit;

public class OfficeTextTests
{
    private const string Rels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Pml = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string Dml = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static ZipArchive Zip(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        stream.Position = 0;
        return new ZipArchive(stream, ZipArchiveMode.Read);
    }

    private static string PackageRels(string target) =>
        $"""<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="{Rels}/officeDocument" Target="{target}"/></Relationships>""";

    private static ZipArchive Workbook(string sheet1, string? sheet2 = null, string? styles = null, string? shared = null, bool date1904 = false)
    {
        var entries = new List<(string, string)>
        {
            ("_rels/.rels", PackageRels("xl/workbook.xml")),
            ("xl/workbook.xml", $"""
                <workbook xmlns="{Main}" xmlns:r="{Rels}">
                  <workbookPr{(date1904 ? " date1904=\"1\"" : "")}/>
                  <sheets>
                    <sheet name="Budget" sheetId="1" r:id="rId1"/>
                    {(sheet2 != null ? "<sheet name=\"Contacts\" sheetId=\"2\" r:id=\"rId2\"/>" : "")}
                  </sheets>
                </workbook>
                """),
            ("xl/_rels/workbook.xml.rels", $"""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="{Rels}/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="{Rels}/worksheet" Target="/xl/worksheets/sheet2.xml"/>
                  <Relationship Id="rId3" Type="{Rels}/sharedStrings" Target="sharedStrings.xml"/>
                  <Relationship Id="rId4" Type="{Rels}/styles" Target="styles.xml"/>
                </Relationships>
                """),
            ("xl/worksheets/sheet1.xml", sheet1),
        };
        if (sheet2 != null)
            entries.Add(("xl/worksheets/sheet2.xml", sheet2));
        if (shared != null)
            entries.Add(("xl/sharedStrings.xml", shared));
        if (styles != null)
            entries.Add(("xl/styles.xml", styles));
        return Zip(entries.ToArray());
    }

    [Fact]
    public void ExcelSheetsBecomeNamedBlocksOfCommaSeparatedRows()
    {
        var shared = $"""
            <sst xmlns="{Main}">
              <si><t>Item</t></si>
              <si><t>Amount</t></si>
              <si><r><t>HOA </t></r><r><t>fee</t></r><rPh><t>ignored</t></rPh></si>
              <si><t>Roof, gutters</t></si>
            </sst>
            """;
        var styles = $"""
            <styleSheet xmlns="{Main}">
              <numFmts><numFmt numFmtId="164" formatCode="mm/dd/yyyy"/><numFmt numFmtId="165" formatCode="&quot;$&quot;#,##0.00"/></numFmts>
              <cellXfs>
                <xf numFmtId="0"/>
                <xf numFmtId="164"/>
                <xf numFmtId="165"/>
                <xf numFmtId="10"/>
                <xf numFmtId="14"/>
              </cellXfs>
            </styleSheet>
            """;
        var sheet1 = $"""
            <worksheet xmlns="{Main}"><sheetData>
              <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="inlineStr"><is><t>Due</t></is></c></row>
              <row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2" s="2"><v>350.5</v></c><c r="C2" s="1"><v>45383</v></c></row>
              <row r="3"/>
              <row r="4"><c r="A4" t="s"><v>3</v></c><c r="B4"><v>0.30000000000000004</v></c><c r="D4" s="3"><v>0.055</v></c></row>
              <row r="5"><c r="A5" t="b"><v>1</v></c><c r="B5" t="str"><v>formula text</v></c><c r="C5" t="e"><v>#DIV/0!</v></c></row>
              <row r="6"><c r="B6" s="4"><v>45658</v></c></row>
            </sheetData></worksheet>
            """;
        var sheet2 = $"""
            <worksheet xmlns="{Main}"><sheetData>
              <row r="1"><c r="A1" t="inlineStr"><is><t>Plumber</t></is></c><c r="B1" t="inlineStr"><is><t>He said "call first"</t></is></c></row>
            </sheetData></worksheet>
            """;

        using var zip = Workbook(sheet1, sheet2, styles, shared);
        var text = OfficeText.ExtractXlsx(zip);

        Assert.Equal(
            "Sheet: Budget\n" +
            "Item,Amount,Due\n" +
            "HOA fee,350.5,2024-04-01\n" +
            "\"Roof, gutters\",0.3,,5.5%\n" +
            "TRUE,formula text,#DIV/0!\n" +
            ",2025-01-01\n" +
            "\n" +
            "Sheet: Contacts\n" +
            "Plumber,\"He said \"\"call first\"\"\"",
            text);
        Assert.Equal(ZipDocumentKind.Excel, OfficeText.DetectKind(zip));
    }

    [Fact]
    public void ExcelOutputIsCappedAndSaysSo()
    {
        var rows = string.Concat(Enumerable.Range(1, 500).Select(i => $"<row r=\"{i}\"><c r=\"A{i}\" t=\"inlineStr\"><is><t>Row number {i} with some text</t></is></c></row>"));
        using var zip = Workbook($"<worksheet xmlns=\"{Main}\"><sheetData>{rows}</sheetData></worksheet>");

        var text = OfficeText.ExtractXlsx(zip, maxChars: 1000);

        Assert.StartsWith("Sheet: Budget\nRow number 1 with some text\n", text);
        Assert.EndsWith("[Spreadsheet truncated: the rest of the rows were not read.]", text);
        Assert.True(text.Length < 1200);
        Assert.DoesNotContain("Row number 500", text);
    }

    [Fact]
    public void ExcelDatesUseThe1904SystemWhenTheWorkbookSaysSo()
    {
        var styles = $"<styleSheet xmlns=\"{Main}\"><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/></cellXfs></styleSheet>";
        var sheet = $"<worksheet xmlns=\"{Main}\"><sheetData><row r=\"1\"><c r=\"A1\" s=\"1\"><v>0</v></c></row></sheetData></worksheet>";

        using var zip = Workbook(sheet, styles: styles, date1904: true);

        Assert.Equal("Sheet: Budget\n1904-01-01", OfficeText.ExtractXlsx(zip));
    }

    [Theory]
    [InlineData(1, "1900-01-01")]
    [InlineData(59, "1900-02-28")]
    [InlineData(61, "1900-03-01")]
    [InlineData(45383, "2024-04-01")]
    public void ExcelSerialDatesMatchExcel(double serial, string expected)
    {
        Assert.Equal(expected, OfficeText.SerialToDate(serial)!.Value.ToString("yyyy-MM-dd"));
    }

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("C7", 2)]
    [InlineData("Z9", 25)]
    [InlineData("AA10", 26)]
    [InlineData("XFD1", 16383)]
    public void CellReferencesGiveTheirColumn(string reference, int expected)
    {
        Assert.Equal(expected, OfficeText.ColumnIndex(reference));
    }

    [Fact]
    public void RowsKeepGapsButNotEndlessEmptyColumns()
    {
        Assert.Equal("a,,c", OfficeText.FormatRow(new[] { (0, "a"), (2, "c") }));
        Assert.Equal(",b", OfficeText.FormatRow(new[] { (1, "b") }));
        Assert.Equal("a" + new string(',', 21) + "z", OfficeText.FormatRow(new[] { (0, "a"), (5000, "z") }));
        Assert.Equal("", OfficeText.FormatRow(new[] { (0, " "), (3, "") }));
        Assert.Equal("two lines", OfficeText.FormatRow(new[] { (0, "two\nlines") }));
    }

    [Fact]
    public void PowerPointSlidesComeInPresentationOrderWithNotes()
    {
        static string Slide(params string[] paragraphs) =>
            $"""
            <p:sld xmlns:p="{Pml}" xmlns:a="{Dml}"><p:cSld><p:spTree>
              <p:sp><p:txBody>{string.Concat(paragraphs.Select(p => $"<a:p><a:r><a:t>{p}</a:t></a:r></a:p>"))}</p:txBody></p:sp>
            </p:spTree></p:cSld></p:sld>
            """;

        var notes = $"""
            <p:notes xmlns:p="{Pml}" xmlns:a="{Dml}"><p:cSld><p:spTree>
              <p:sp><p:nvSpPr><p:nvPr><p:ph type="sldImg"/></p:nvPr></p:nvSpPr></p:sp>
              <p:sp><p:nvSpPr><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>Mention the deadline</a:t></a:r></a:p></p:txBody></p:sp>
              <p:sp><p:nvSpPr><p:nvPr><p:ph type="sldNum" idx="5"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:fld type="slidenum"><a:t>2</a:t></a:fld></a:p></p:txBody></p:sp>
            </p:spTree></p:cSld></p:notes>
            """;

        using var zip = Zip(
            ("_rels/.rels", PackageRels("ppt/presentation.xml")),
            ("ppt/presentation.xml", $"""
                <p:presentation xmlns:p="{Pml}" xmlns:r="{Rels}"><p:sldIdLst>
                  <p:sldId id="256" r:id="rId3"/><p:sldId id="257" r:id="rId2"/>
                </p:sldIdLst></p:presentation>
                """),
            ("ppt/_rels/presentation.xml.rels", $"""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId2" Type="{Rels}/slide" Target="slides/slide1.xml"/>
                  <Relationship Id="rId3" Type="{Rels}/slide" Target="slides/slide2.xml"/>
                </Relationships>
                """),
            ("ppt/slides/slide1.xml", Slide("Budget", "Roof: $12,000")),
            ("ppt/slides/slide2.xml", Slide("Townhouse plan", "Repairs first")),
            ("ppt/slides/_rels/slide1.xml.rels", $"""
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="{Rels}/notesSlide" Target="../notesSlides/notesSlide1.xml"/>
                </Relationships>
                """),
            ("ppt/notesSlides/notesSlide1.xml", notes));

        Assert.Equal(
            "Slide 1\nTownhouse plan\nRepairs first\n\nSlide 2\nBudget\nRoof: $12,000\nNotes: Mention the deadline",
            OfficeText.ExtractPptx(zip));
        Assert.Equal(ZipDocumentKind.PowerPoint, OfficeText.DetectKind(zip));
    }

    [Fact]
    public void PowerPointWithoutASlideListReadsSlidesInNumberOrder()
    {
        static string Slide(string text) => $"<p:sld xmlns:p=\"{Pml}\" xmlns:a=\"{Dml}\"><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:sld>";

        using var zip = Zip(
            ("ppt/slides/slide10.xml", Slide("ten")),
            ("ppt/slides/slide2.xml", Slide("two")),
            ("ppt/slides/slide1.xml", Slide("one")));

        Assert.Equal("Slide 1\none\n\nSlide 2\ntwo\n\nSlide 3\nten", OfficeText.ExtractPptx(zip));
    }

    [Fact]
    public void WordBodyComesFirstThenHeadersAndFootnotesAndTextBoxesOnce()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var zip = Zip(
            ("_rels/.rels", PackageRels("word/document.xml")),
            ("word/document.xml", $"""
                <w:document xmlns:w="{W}" xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"><w:body>
                  <w:p><w:r><w:t>Inspection report</w:t></w:r></w:p>
                  <w:p><w:r><w:t>Roof:</w:t><w:tab/><w:t>good</w:t></w:r></w:p>
                  <w:p><mc:AlternateContent><mc:Choice><w:p><w:r><w:t>Box text</w:t></w:r></w:p></mc:Choice><mc:Fallback><w:p><w:r><w:t>Box text</w:t></w:r></w:p></mc:Fallback></mc:AlternateContent></w:p>
                  <w:p><w:r><w:delText>deleted</w:delText><w:instrText>PAGE</w:instrText></w:r></w:p>
                </w:body></w:document>
                """),
            ("word/header1.xml", $"<w:hdr xmlns:w=\"{W}\"><w:p><w:r><w:t>Smith Home Inspections</w:t></w:r></w:p></w:hdr>"),
            ("word/footnotes.xml", $"<w:footnotes xmlns:w=\"{W}\"><w:footnote><w:p><w:r><w:t>Seen from the ground.</w:t></w:r></w:p></w:footnote></w:footnotes>"));

        Assert.Equal(
            "Inspection report\nRoof:\tgood\n\nBox text\n\nSeen from the ground.\n\nSmith Home Inspections",
            OfficeText.ExtractDocx(zip));
        Assert.Equal(ZipDocumentKind.Word, OfficeText.DetectKind(zip));
    }

    private const string OdfManifest = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";

    private static string OdfContent(string body) => $"""
        <office:document-content xmlns:office="{OdfManifest}" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
            xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0" xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0"
            xmlns:presentation="urn:oasis:names:tc:opendocument:xmlns:presentation:1.0">
          <office:body>{body}</office:body>
        </office:document-content>
        """;

    [Fact]
    public void OpenDocumentTextKeepsParagraphsSpacesAndTables()
    {
        using var zip = Zip(
            ("mimetype", "application/vnd.oasis.opendocument.text"),
            ("content.xml", OdfContent("""
                <office:text>
                  <text:sequence-decls><text:sequence-decl text:name="Table"/></text:sequence-decls>
                  <text:h text:outline-level="1">Move-in checklist</text:h>
                  <text:p>Call the<text:s text:c="2"/>utility<text:tab/>company<text:line-break/>before Friday<text:note><text:note-body><text:p>note</text:p></text:note-body></text:note></text:p>
                  <text:list><text:list-item><text:p>Keys</text:p></text:list-item><text:list-item><text:p>Mailbox</text:p></text:list-item></text:list>
                  <table:table><table:table-row><table:table-cell><text:p>Room</text:p></table:table-cell><table:table-cell><text:p>Paint</text:p></table:table-cell></table:table-row></table:table>
                  <office:annotation><text:p>a comment</text:p></office:annotation>
                </office:text>
                """)));

        Assert.Equal(
            "Move-in checklist\n\nCall the  utility\tcompany\nbefore Friday\nKeys\nMailbox\n\nRoom | Paint",
            OfficeText.ExtractOpenDocument(zip));
        Assert.Equal(ZipDocumentKind.OpenDocumentText, OfficeText.DetectKind(zip));
    }

    [Fact]
    public void OpenDocumentSpreadsheetRowsAndRepeats()
    {
        using var zip = Zip(
            ("mimetype", "application/vnd.oasis.opendocument.spreadsheet"),
            ("content.xml", OdfContent("""
                <office:spreadsheet>
                  <table:table table:name="Bills">
                    <table:table-row><table:table-cell><text:p>Water</text:p></table:table-cell><table:table-cell table:number-columns-repeated="2"/><table:table-cell><text:p>40</text:p></table:table-cell><table:table-cell table:number-columns-repeated="1020"/></table:table-row>
                    <table:table-row table:number-rows-repeated="2"><table:table-cell><text:p>same</text:p></table:table-cell></table:table-row>
                    <table:table-row table:number-rows-repeated="1048570"><table:table-cell table:number-columns-repeated="1024"/></table:table-row>
                  </table:table>
                  <table:table table:name="Empty"><table:table-row><table:table-cell/></table:table-row></table:table>
                </office:spreadsheet>
                """)));

        Assert.Equal("Sheet: Bills\nWater,,,40\nsame\nsame", OfficeText.ExtractOpenDocument(zip));
        Assert.Equal(ZipDocumentKind.OpenDocumentSpreadsheet, OfficeText.DetectKind(zip));
    }

    [Fact]
    public void OpenDocumentPresentationSlidesAndNotes()
    {
        using var zip = Zip(
            ("content.xml", OdfContent("""
                <office:presentation>
                  <draw:page draw:name="page1"><draw:frame><draw:text-box><text:p>Welcome</text:p></draw:text-box></draw:frame>
                    <presentation:notes><draw:frame><draw:text-box><text:p>Smile</text:p></draw:text-box></draw:frame></presentation:notes></draw:page>
                  <draw:page draw:name="page2"><draw:frame><draw:text-box><text:p>Questions?</text:p></draw:text-box></draw:frame></draw:page>
                </office:presentation>
                """)));

        Assert.Equal(ZipDocumentKind.OpenDocumentPresentation, OfficeText.DetectKind(zip));
        Assert.Equal("Slide 1\nWelcome\nNotes: Smile\n\nSlide 2\nQuestions?", OfficeText.ExtractOpenDocument(zip));
    }

    [Fact]
    public void TablesInWordAndPowerPointBecomeRows()
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        using var word = Zip(
            ("word/document.xml", $"""
                <w:document xmlns:w="{W}"><w:body>
                  <w:p><w:r><w:t>Paint colors</w:t></w:r></w:p>
                  <w:tbl><w:tblPr/>
                    <w:tr><w:tc><w:p><w:r><w:t>Room</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Color</w:t></w:r></w:p></w:tc></w:tr>
                    <w:tr><w:tc><w:p><w:r><w:t>Kitchen</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Eggshell</w:t></w:r></w:p><w:p><w:r><w:t>white</w:t></w:r></w:p></w:tc></w:tr>
                  </w:tbl>
                  <w:p><w:r><w:t>Done.</w:t></w:r></w:p>
                </w:body></w:document>
                """));
        Assert.Equal("Paint colors\nRoom | Color\nKitchen | Eggshell white\n\nDone.", OfficeText.ExtractDocx(word));

        using var deck = Zip(
            ("ppt/slides/slide1.xml", $"""
                <p:sld xmlns:p="{Pml}" xmlns:a="{Dml}"><p:cSld><p:spTree>
                  <p:sp><p:txBody><a:p><a:r><a:t>Costs</a:t></a:r></a:p></p:txBody></p:sp>
                  <p:graphicFrame><a:graphic><a:graphicData><a:tbl>
                    <a:tr><a:tc><a:txBody><a:p><a:r><a:t>Locks</a:t></a:r></a:p></a:txBody></a:tc><a:tc><a:txBody><a:p><a:r><a:t>$180</a:t></a:r></a:p></a:txBody></a:tc></a:tr>
                  </a:tbl></a:graphicData></a:graphic></p:graphicFrame>
                </p:spTree></p:cSld></p:sld>
                """));
        Assert.Equal("Slide 1\nCosts\nLocks | $180", OfficeText.ExtractPptx(deck));
    }

    [Fact]
    public void UnknownOrDamagedZipsGiveNoText()
    {
        using var zip = Zip(("readme.txt", "hello"));
        Assert.Equal(ZipDocumentKind.Unknown, OfficeText.DetectKind(zip));
        Assert.Equal("", OfficeText.Extract(zip));

        using var broken = Zip(("_rels/.rels", PackageRels("xl/workbook.xml")), ("xl/workbook.xml", "<workbook><sheets><sheet"));
        Assert.Equal("", OfficeText.ExtractXlsx(broken));
    }
}
