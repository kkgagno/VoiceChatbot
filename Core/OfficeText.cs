using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VoiceChatbot;

/// <summary>The kind of document inside a zip-based Office or OpenDocument file.</summary>
public enum ZipDocumentKind
{
    Unknown,
    Word,
    Excel,
    PowerPoint,
    OpenDocumentText,
    OpenDocumentSpreadsheet,
    OpenDocumentPresentation
}

/// <summary>
/// Text from zip-based office files, read straight from their XML: Word (.docx), Excel (.xlsx,
/// .xlsm: every sheet as "Sheet: name" then its rows as comma-separated cells), PowerPoint (.pptx:
/// "Slide n" then its text and speaker notes) and OpenDocument (.odt, .ods, .odp). Output is capped
/// at <c>maxChars</c>; a cut-off spreadsheet or deck says so in its last line.
/// </summary>
public static class OfficeText
{
    public const int DefaultMaxChars = 4_000_000;
    private const int MaxSharedStringChars = 30_000_000;
    private const int MaxEmptyColumnsKept = 20;
    private const int MaxRepeatedRows = 100;

    private const string OdfOffice = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private const string OdfText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string OdfTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string OdfDraw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string OdfPresentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false,
        CloseInput = true
    };

    private enum CellFormat { General, Date, Time, DateTime, Percent }

    // ==================== Detection ====================

    /// <summary>Which kind of document a zip holds, from its parts (the file extension is not needed).</summary>
    public static ZipDocumentKind DetectKind(ZipArchive zip)
    {
        var entries = EntryMap(zip);
        var main = MainPartPath(entries);
        if (main.StartsWith("word/", StringComparison.OrdinalIgnoreCase) || entries.ContainsKey("word/document.xml"))
            return ZipDocumentKind.Word;
        if (main.StartsWith("xl/", StringComparison.OrdinalIgnoreCase) || entries.ContainsKey("xl/workbook.xml"))
            return ZipDocumentKind.Excel;
        if (main.StartsWith("ppt/", StringComparison.OrdinalIgnoreCase) || entries.ContainsKey("ppt/presentation.xml"))
            return ZipDocumentKind.PowerPoint;

        if (entries.ContainsKey("content.xml"))
        {
            var mime = entries.TryGetValue("mimetype", out var mimeEntry) ? ReadSmallText(mimeEntry) : "";
            if (mime.Contains("spreadsheet", StringComparison.OrdinalIgnoreCase))
                return ZipDocumentKind.OpenDocumentSpreadsheet;
            if (mime.Contains("presentation", StringComparison.OrdinalIgnoreCase))
                return ZipDocumentKind.OpenDocumentPresentation;
            if (mime.Contains("opendocument", StringComparison.OrdinalIgnoreCase))
                return ZipDocumentKind.OpenDocumentText;

            // No mimetype entry: look at what the body holds.
            return DetectOpenDocumentBody(entries["content.xml"]);
        }

        return ZipDocumentKind.Unknown;
    }

    /// <summary>Reads whatever kind of document the zip holds; "" for an unknown zip.</summary>
    public static string Extract(ZipArchive zip, int maxChars = DefaultMaxChars) => DetectKind(zip) switch
    {
        ZipDocumentKind.Word => ExtractDocx(zip, maxChars),
        ZipDocumentKind.Excel => ExtractXlsx(zip, maxChars),
        ZipDocumentKind.PowerPoint => ExtractPptx(zip, maxChars),
        ZipDocumentKind.OpenDocumentText or ZipDocumentKind.OpenDocumentSpreadsheet or ZipDocumentKind.OpenDocumentPresentation
            => ExtractOpenDocument(zip, maxChars),
        _ => ""
    };

    // ==================== Word ====================

    /// <summary>The body text of a .docx, then its headers, footers, footnotes and endnotes.</summary>
    public static string ExtractDocx(ZipArchive zip, int maxChars = DefaultMaxChars)
    {
        var entries = EntryMap(zip);
        var main = MainPartPath(entries);
        if (!main.StartsWith("word/", StringComparison.OrdinalIgnoreCase) || !entries.ContainsKey(main))
            main = "word/document.xml";

        var parts = entries.Values
            .Where(e =>
                e.FullName.Equals(main, StringComparison.OrdinalIgnoreCase) ||
                IsWordPart(e.FullName, "word/header") ||
                IsWordPart(e.FullName, "word/footer") ||
                e.FullName.Equals("word/footnotes.xml", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.Equals("word/endnotes.xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Equals(main, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(e => e.FullName, StringComparer.OrdinalIgnoreCase);

        var output = new StringBuilder();
        foreach (var part in parts)
        {
            var xml = LoadXml(part);
            if (xml == null)
                continue;

            // A text box is stored twice (mc:Choice and an older mc:Fallback copy): read it once.
            xml.Descendants().Where(e => e.Name.LocalName == "Fallback").ToList().ForEach(e => e.Remove());

            if (xml.Root != null)
                AppendWordText(xml.Root, output);
            output.Append('\n');
            if (output.Length >= maxChars)
                break;
        }

        return Cap(Tidy(output.ToString()), maxChars, "document");
    }

    // Paragraphs start a new line, runs add their text, and a table row becomes one line with its
    // cells separated by " | ". Deleted text and field codes (delText, instrText) are not text.
    private static void AppendWordText(XElement element, StringBuilder output)
    {
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "t":
                    output.Append(child.Value);
                    break;
                case "tab":
                    output.Append('\t');
                    break;
                case "br" or "cr":
                    output.Append('\n');
                    break;
                case "p":
                    output.Append('\n');
                    AppendWordText(child, output);
                    break;
                case "tbl":
                    foreach (var row in child.Elements().Where(e => e.Name.LocalName == "tr"))
                    {
                        var cells = row.Elements()
                            .Where(e => e.Name.LocalName == "tc")
                            .Select(cell =>
                            {
                                var text = new StringBuilder();
                                AppendWordText(cell, text);
                                return string.Join(" ", text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                            })
                            .ToList();
                        if (cells.Any(c => c.Length > 0))
                            output.Append('\n').Append(string.Join(" | ", cells));
                    }
                    output.Append('\n');
                    break;
                case "delText" or "instrText" or "rPr" or "pPr" or "tblPr" or "tblGrid" or "sectPr":
                    break;
                default:
                    AppendWordText(child, output);
                    break;
            }
        }
    }

    private static bool IsWordPart(string name, string prefix) =>
        name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
        name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
        name.IndexOf('/', prefix.Length) < 0;

    // ==================== Excel ====================

    /// <summary>Every sheet as "Sheet: name" and then one line per non-empty row, cells separated by commas.</summary>
    public static string ExtractXlsx(ZipArchive zip, int maxChars = DefaultMaxChars)
    {
        var entries = EntryMap(zip);
        var workbookPath = MainPartPath(entries);
        if (!entries.ContainsKey(workbookPath))
            workbookPath = "xl/workbook.xml";
        if (!entries.TryGetValue(workbookPath, out var workbookEntry))
            return "";

        var workbook = LoadXml(workbookEntry);
        if (workbook == null)
            return "";

        var relationships = ReadRelationships(entries, workbookPath);
        var date1904 = workbook.Descendants().FirstOrDefault(e => e.Name.LocalName == "workbookPr")?
            .Attribute("date1904")?.Value is "1" or "true";

        var sharedStringsPath = relationships.Values.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings", StringComparison.OrdinalIgnoreCase)).Target
                                ?? CombinePart(workbookPath, "sharedStrings.xml");
        var stylesPath = relationships.Values.FirstOrDefault(r => r.Type.EndsWith("/styles", StringComparison.OrdinalIgnoreCase)).Target
                         ?? CombinePart(workbookPath, "styles.xml");

        var sharedStrings = entries.TryGetValue(sharedStringsPath, out var sharedEntry) ? ReadSharedStrings(sharedEntry) : new List<string>();
        var formats = entries.TryGetValue(stylesPath, out var stylesEntry) ? ReadCellFormats(stylesEntry) : new List<CellFormat>();

        var sheets = workbook.Descendants()
            .Where(e => e.Name.LocalName == "sheet")
            .Select(e => (
                Name: e.Attribute("name")?.Value ?? "Sheet",
                Id: e.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.Namespace != XNamespace.None)?.Value ?? ""))
            .ToList();

        var output = new StringBuilder();
        var truncated = false;
        var sheetNumber = 0;
        foreach (var sheet in sheets)
        {
            sheetNumber++;
            var path = relationships.TryGetValue(sheet.Id, out var rel) ? rel.Target : $"xl/worksheets/sheet{sheetNumber}.xml";
            if (!entries.TryGetValue(path, out var sheetEntry))
                continue;

            var rows = new StringBuilder();
            truncated = ReadWorksheet(sheetEntry, sharedStrings, formats, date1904, rows, maxChars - output.Length);
            if (rows.Length > 0)
            {
                if (output.Length > 0)
                    output.Append('\n');
                output.Append("Sheet: ").Append(sheet.Name).Append('\n').Append(rows);
            }

            if (truncated || output.Length >= maxChars)
            {
                truncated = true;
                break;
            }
        }

        var text = output.ToString().TrimEnd();
        return truncated ? text + "\n\n[Spreadsheet truncated: the rest of the rows were not read.]" : text;
    }

    /// <summary>Appends the sheet's rows; true when it stopped at <paramref name="budget"/> characters.</summary>
    private static bool ReadWorksheet(ZipArchiveEntry entry, List<string> sharedStrings, List<CellFormat> formats, bool date1904, StringBuilder output, int budget)
    {
        try
        {
            using var reader = XmlReader.Create(entry.Open(), ReaderSettings);
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "row")
                {
                    var row = (XElement)XNode.ReadFrom(reader);
                    var cells = new List<(int Column, string Value)>();
                    var nextColumn = 0;
                    foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c"))
                    {
                        var column = ColumnIndex(cell.Attribute("r")?.Value) ?? nextColumn;
                        nextColumn = column + 1;
                        var value = CellValue(cell, sharedStrings, formats, date1904);
                        if (value.Length > 0)
                            cells.Add((column, value));
                    }

                    var line = FormatRow(cells);
                    if (line.Length > 0)
                    {
                        if (output.Length + line.Length + 1 > budget)
                            return true;
                        output.Append(line).Append('\n');
                    }
                    continue;
                }

                reader.Read();
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        {
            // A damaged sheet: keep the rows read so far.
        }

        return false;
    }

    private static string CellValue(XElement cell, List<string> sharedStrings, List<CellFormat> formats, bool date1904)
    {
        var type = cell.Attribute("t")?.Value ?? "n";
        var raw = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
        switch (type)
        {
            case "s":
                return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) && index >= 0 && index < sharedStrings.Count
                    ? sharedStrings[index]
                    : "";
            case "inlineStr":
                var inline = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "is");
                return inline == null ? raw : RichText(inline);
            case "b":
                return raw == "1" ? "TRUE" : raw == "0" ? "FALSE" : raw;
            case "str" or "e" or "d":
                return raw;
        }

        if (raw.Length == 0)
            return "";
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return raw;

        var style = int.TryParse(cell.Attribute("s")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s >= 0 && s < formats.Count
            ? formats[s]
            : CellFormat.General;
        return FormatNumber(number, style, date1904);
    }

    private static string FormatNumber(double number, CellFormat format, bool date1904)
    {
        switch (format)
        {
            case CellFormat.Percent:
                return (number * 100).ToString("0.##########", CultureInfo.InvariantCulture) + "%";
            case CellFormat.Date or CellFormat.Time or CellFormat.DateTime:
                var date = SerialToDate(number, date1904);
                if (date == null)
                    break;
                return format switch
                {
                    CellFormat.Date => date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    CellFormat.Time => date.Value.ToString(date.Value.Second == 0 ? "HH:mm" : "HH:mm:ss", CultureInfo.InvariantCulture),
                    _ => date.Value.TimeOfDay == TimeSpan.Zero
                        ? date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                        : date.Value.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                };
        }

        return number.ToString("G15", CultureInfo.InvariantCulture);
    }

    /// <summary>An Excel date serial as a date (1900 system with its Feb 29 1900 quirk, or the 1904 system).</summary>
    public static DateTime? SerialToDate(double serial, bool date1904 = false)
    {
        if (double.IsNaN(serial) || serial < 0 || serial > 2958465)
            return null;

        try
        {
            if (date1904)
                return RoundToSecond(new DateTime(1904, 1, 1).AddDays(serial));

            // Serial 60 is Excel's made-up 1900-02-29; before it, OLE dates are one day ahead of Excel's.
            if (serial < 61 && serial >= 1)
                return RoundToSecond(DateTime.FromOADate(serial + 1));
            return RoundToSecond(DateTime.FromOADate(serial));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static DateTime RoundToSecond(DateTime value) =>
        new(value.Ticks + TimeSpan.TicksPerSecond / 2 - (value.Ticks + TimeSpan.TicksPerSecond / 2) % TimeSpan.TicksPerSecond);

    private static List<string> ReadSharedStrings(ZipArchiveEntry entry)
    {
        var strings = new List<string>();
        var total = 0;
        try
        {
            using var reader = XmlReader.Create(entry.Open(), ReaderSettings);
            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "si")
                {
                    var item = (XElement)XNode.ReadFrom(reader);
                    var text = total < MaxSharedStringChars ? RichText(item) : "";
                    total += text.Length;
                    strings.Add(text);
                    continue;
                }

                reader.Read();
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        {
            // Keep the strings read so far; later cells show empty.
        }

        return strings;
    }

    /// <summary>The text of a shared string or inline string: its runs, without phonetic guides.</summary>
    private static string RichText(XElement item)
    {
        var text = new StringBuilder();
        foreach (var t in item.Descendants().Where(e => e.Name.LocalName == "t"))
        {
            if (t.Ancestors().Any(a => a.Name.LocalName == "rPh"))
                continue;
            text.Append(t.Value);
        }
        return text.ToString();
    }

    private static List<CellFormat> ReadCellFormats(ZipArchiveEntry entry)
    {
        var formats = new List<CellFormat>();
        var styles = LoadXml(entry);
        if (styles == null)
            return formats;

        var custom = styles.Descendants()
            .Where(e => e.Name.LocalName == "numFmt")
            .Select(e => (Id: int.TryParse(e.Attribute("numFmtId")?.Value, out var id) ? id : -1, Code: e.Attribute("formatCode")?.Value ?? ""))
            .Where(f => f.Id >= 0)
            .GroupBy(f => f.Id)
            .ToDictionary(g => g.Key, g => g.First().Code);

        var cellXfs = styles.Descendants().FirstOrDefault(e => e.Name.LocalName == "cellXfs");
        if (cellXfs == null)
            return formats;

        foreach (var xf in cellXfs.Elements().Where(e => e.Name.LocalName == "xf"))
        {
            var id = int.TryParse(xf.Attribute("numFmtId")?.Value, out var value) ? value : 0;
            formats.Add(ClassifyNumberFormat(id, custom.TryGetValue(id, out var code) ? code : null));
        }

        return formats;
    }

    private static CellFormat ClassifyNumberFormat(int id, string? code)
    {
        if (code == null)
        {
            return id switch
            {
                9 or 10 => CellFormat.Percent,
                >= 14 and <= 17 => CellFormat.Date,
                >= 18 and <= 21 or >= 45 and <= 47 => CellFormat.Time,
                22 => CellFormat.DateTime,
                >= 27 and <= 36 or >= 50 and <= 58 => CellFormat.Date,
                _ => CellFormat.General
            };
        }

        // Look only at the format's own letters: drop "quoted text", [colors/locales] (but not [h]),
        // \escaped and _padding characters.
        var letters = new StringBuilder();
        var section = code.Split(';')[0];
        for (var i = 0; i < section.Length; i++)
        {
            var c = section[i];
            if (c == '"')
            {
                var close = section.IndexOf('"', i + 1);
                i = close < 0 ? section.Length : close;
                continue;
            }
            if (c == '[')
            {
                var close = section.IndexOf(']', i + 1);
                var inside = close < 0 ? "" : section[(i + 1)..close];
                if (inside.Length > 0 && inside.All(ch => ch is 'h' or 'H' or 'm' or 'M' or 's' or 'S'))
                    letters.Append(inside);
                i = close < 0 ? section.Length : close;
                continue;
            }
            if (c is '\\' or '_' or '*')
            {
                i++;
                continue;
            }
            letters.Append(char.ToLowerInvariant(c));
        }

        var f = letters.ToString();
        if (f.Contains('%'))
            return CellFormat.Percent;
        var hasDate = f.Contains('y') || f.Contains('d') || (f.Contains('m') && !f.Contains('h') && !f.Contains('s'));
        var hasTime = f.Contains('h') || f.Contains('s');
        if (f.Contains("general", StringComparison.Ordinal))
            return CellFormat.General;
        if (hasDate && hasTime)
            return CellFormat.DateTime;
        if (hasDate)
            return CellFormat.Date;
        return hasTime ? CellFormat.Time : CellFormat.General;
    }

    /// <summary>The zero-based column of a cell reference such as "C7" (2), or null.</summary>
    public static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
            return null;

        var column = 0;
        var letters = 0;
        foreach (var c in reference)
        {
            var upper = char.ToUpperInvariant(c);
            if (upper is < 'A' or > 'Z')
                break;
            column = column * 26 + (upper - 'A' + 1);
            letters++;
            if (letters > 3)
                return null;
        }

        return letters == 0 ? null : column - 1;
    }

    /// <summary>
    /// One spreadsheet row as comma-separated cells (quoted when they hold a comma or a quote). Empty
    /// cells between values keep their place, up to 20 in a row; trailing empty cells are dropped.
    /// </summary>
    public static string FormatRow(IReadOnlyList<(int Column, string Value)> cells)
    {
        var output = new StringBuilder();
        var previous = -1;
        foreach (var (column, raw) in cells.OrderBy(c => c.Column))
        {
            var value = raw.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (value.Length == 0)
                continue;

            if (previous >= 0)
            {
                var gap = Math.Clamp(column - previous, 1, MaxEmptyColumnsKept + 1);
                output.Append(',', gap);
            }
            else
            {
                output.Append(',', Math.Clamp(column, 0, MaxEmptyColumnsKept));
            }

            output.Append(value.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value);
            previous = Math.Max(previous, column);
        }

        return output.ToString();
    }

    // ==================== PowerPoint ====================

    /// <summary>Each slide as "Slide n" followed by its text, then "Notes:" and its speaker notes.</summary>
    public static string ExtractPptx(ZipArchive zip, int maxChars = DefaultMaxChars)
    {
        var entries = EntryMap(zip);
        var presentationPath = MainPartPath(entries);
        if (!entries.ContainsKey(presentationPath))
            presentationPath = "ppt/presentation.xml";

        var slidePaths = new List<string>();
        if (entries.TryGetValue(presentationPath, out var presentationEntry) && LoadXml(presentationEntry) is { } presentation)
        {
            var relationships = ReadRelationships(entries, presentationPath);
            foreach (var slideId in presentation.Descendants().Where(e => e.Name.LocalName == "sldId"))
            {
                var id = slideId.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.Namespace != XNamespace.None)?.Value;
                if (id != null && relationships.TryGetValue(id, out var rel) && entries.ContainsKey(rel.Target))
                    slidePaths.Add(rel.Target);
            }
        }

        if (slidePaths.Count == 0)
        {
            // No (readable) slide list: take ppt/slides/slideN.xml in number order.
            slidePaths = entries.Keys
                .Where(k => k.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && k.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && k.IndexOf('/', 11) < 0)
                .OrderBy(k => int.TryParse(Path.GetFileNameWithoutExtension(k)[5..], out var n) ? n : int.MaxValue)
                .ToList();
        }

        var output = new StringBuilder();
        var truncated = false;
        for (var i = 0; i < slidePaths.Count; i++)
        {
            if (LoadXml(entries[slidePaths[i]]) is not { } slide)
                continue;

            var lines = DrawingParagraphs(slide.Root, _ => true);
            var notes = new List<string>();
            var notesPath = ReadRelationships(entries, slidePaths[i]).Values
                .FirstOrDefault(r => r.Type.EndsWith("/notesSlide", StringComparison.OrdinalIgnoreCase)).Target;
            if (notesPath != null && entries.TryGetValue(notesPath, out var notesEntry) && LoadXml(notesEntry) is { } notesXml)
                notes = DrawingParagraphs(notesXml.Root, IsNotesBodyShape);

            if (output.Length > 0)
                output.Append('\n');
            output.Append("Slide ").Append(i + 1).Append('\n');
            foreach (var line in lines)
                output.Append(line).Append('\n');
            if (notes.Count > 0)
                output.Append("Notes: ").Append(string.Join("\n", notes)).Append('\n');

            if (output.Length >= maxChars)
            {
                truncated = i < slidePaths.Count - 1;
                break;
            }
        }

        var text = Tidy(output.ToString());
        if (text.Length > maxChars)
        {
            text = text[..maxChars];
            truncated = true;
        }
        return truncated ? text + "\n\n[Presentation truncated: the rest of the slides were not read.]" : text;
    }

    /// <summary>The text of each DrawingML paragraph (a:p) under <paramref name="root"/>, in document order.</summary>
    private static List<string> DrawingParagraphs(XElement? root, Func<XElement, bool> includeShape)
    {
        var lines = new List<string>();
        if (root != null)
            AppendDrawingText(root, includeShape, lines);
        return lines;
    }

    // DrawingML paragraphs (a:p) in document order; a table (a:tbl) gives one line per row with
    // its cells separated by " | ".
    private static void AppendDrawingText(XElement element, Func<XElement, bool> includeShape, List<string> lines)
    {
        foreach (var child in element.Elements())
        {
            var name = child.Name.LocalName;
            if (name is "sp" or "graphicFrame" && !includeShape(child))
                continue;

            if (IsDrawingMl(child) && name == "p")
            {
                var line = DrawingParagraphText(child);
                if (line.Length > 0)
                    lines.Add(line);
                continue;
            }

            if (IsDrawingMl(child) && name == "tbl")
            {
                foreach (var row in child.Elements().Where(e => e.Name.LocalName == "tr"))
                {
                    var cells = row.Elements()
                        .Where(e => e.Name.LocalName == "tc")
                        .Select(cell => string.Join(" ", cell.Descendants()
                            .Where(e => IsDrawingMl(e) && e.Name.LocalName == "p")
                            .Select(DrawingParagraphText)
                            .Where(t => t.Length > 0)))
                        .ToList();
                    if (cells.Any(c => c.Length > 0))
                        lines.Add(string.Join(" | ", cells));
                }
                continue;
            }

            AppendDrawingText(child, includeShape, lines);
        }
    }

    private static bool IsDrawingMl(XElement element) =>
        element.Name.NamespaceName.EndsWith("/drawingml/2006/main", StringComparison.Ordinal);

    private static string DrawingParagraphText(XElement paragraph)
    {
        var text = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (node.Name.LocalName == "t")
                text.Append(node.Value);
            else if (node.Name.LocalName == "br")
                text.Append('\n');
        }
        return text.ToString().Trim();
    }

    // In a notes page only the notes placeholder is notes text (not the slide image, number or footer).
    private static bool IsNotesBodyShape(XElement shape)
    {
        var placeholder = shape.Descendants().FirstOrDefault(e => e.Name.LocalName == "ph");
        if (placeholder == null)
            return true;
        var type = placeholder.Attribute("type")?.Value;
        return type is null or "body";
    }

    // ==================== OpenDocument ====================

    /// <summary>
    /// Text from an OpenDocument file's content.xml: paragraphs and headings (.odt), "Sheet: name"
    /// and comma-separated rows (.ods) or "Slide n" with its text and notes (.odp).
    /// </summary>
    public static string ExtractOpenDocument(ZipArchive zip, int maxChars = DefaultMaxChars)
    {
        var entries = EntryMap(zip);
        if (!entries.TryGetValue("content.xml", out var content) || LoadXml(content) is not { } xml)
            return "";

        var body = xml.Root?.Element(XName.Get("body", OdfOffice));
        if (body == null)
            return "";

        var output = new StringBuilder();
        var spreadsheet = body.Element(XName.Get("spreadsheet", OdfOffice));
        var presentation = body.Element(XName.Get("presentation", OdfOffice));
        if (spreadsheet != null)
        {
            foreach (var table in spreadsheet.Elements(XName.Get("table", OdfTable)))
            {
                var rows = new StringBuilder();
                OdfSheetRows(table, rows, maxChars - output.Length);
                if (rows.Length > 0)
                {
                    if (output.Length > 0)
                        output.Append('\n');
                    output.Append("Sheet: ").Append(table.Attribute(XName.Get("name", OdfTable))?.Value ?? "Sheet").Append('\n').Append(rows);
                }
                if (output.Length >= maxChars)
                    return output.ToString().TrimEnd() + "\n\n[Spreadsheet truncated: the rest of the rows were not read.]";
            }
            return output.ToString().TrimEnd();
        }

        if (presentation != null)
        {
            var number = 0;
            foreach (var page in presentation.Elements(XName.Get("page", OdfDraw)))
            {
                number++;
                if (output.Length > 0)
                    output.Append('\n');
                output.Append("Slide ").Append(number).Append('\n');
                OdfWalk(page, output, skipNotes: true);
                var notes = page.Element(XName.Get("notes", OdfPresentation));
                if (notes != null)
                {
                    var notesText = new StringBuilder();
                    OdfWalk(notes, notesText, skipNotes: false);
                    var trimmed = Tidy(notesText.ToString());
                    if (trimmed.Length > 0)
                        output.Append("Notes: ").Append(trimmed).Append('\n');
                }
                if (output.Length >= maxChars)
                    break;
            }
            return Cap(Tidy(output.ToString()), maxChars, "presentation");
        }

        OdfWalk(body, output, skipNotes: false);
        return Cap(Tidy(output.ToString()), maxChars, "document");
    }

    private static void OdfWalk(XElement element, StringBuilder output, bool skipNotes)
    {
        foreach (var child in element.Elements())
        {
            var ns = child.Name.NamespaceName;
            var name = child.Name.LocalName;
            if (ns == OdfText && name is "p" or "h")
            {
                output.Append(OdfParagraph(child)).Append('\n');
                if (name == "h")
                    output.Append('\n');
                continue;
            }

            if (ns == OdfText && name is "tracked-changes" or "sequence-decls" or "variable-decls" or "user-field-decls")
                continue;
            if (ns == OdfOffice && name is "annotation" or "forms")
                continue;
            if (ns == OdfPresentation && name == "notes" && skipNotes)
                continue;
            // Slide number, date and footer placeholders hold no slide text ("<number>").
            if (ns == OdfDraw && name == "frame" &&
                child.Attribute(XName.Get("class", OdfPresentation))?.Value is "page-number" or "date-time" or "footer" or "header")
                continue;

            if (ns == OdfTable && name == "table")
            {
                foreach (var row in child.Descendants(XName.Get("table-row", OdfTable)))
                {
                    var cells = row.Elements()
                        .Where(c => c.Name.LocalName is "table-cell" or "covered-table-cell")
                        .Select(OdfCellText)
                        .Where(t => t.Length > 0)
                        .ToList();
                    if (cells.Count > 0)
                        output.Append(string.Join(" | ", cells)).Append('\n');
                }
                output.Append('\n');
                continue;
            }

            OdfWalk(child, output, skipNotes);
            if (ns == OdfText && name is "list" or "section")
                output.Append('\n');
        }
    }

    private static void OdfSheetRows(XElement table, StringBuilder output, int budget)
    {
        foreach (var row in table.Descendants(XName.Get("table-row", OdfTable)))
        {
            var cells = new List<(int Column, string Value)>();
            var column = 0;
            foreach (var cell in row.Elements().Where(c => c.Name.LocalName is "table-cell" or "covered-table-cell"))
            {
                var repeat = RepeatCount(cell, "number-columns-repeated");
                var value = OdfCellText(cell);
                if (value.Length > 0)
                {
                    for (var r = 0; r < Math.Min(repeat, MaxEmptyColumnsKept); r++)
                        cells.Add((column + r, value));
                }
                column += repeat;
            }

            var line = FormatRow(cells);
            if (line.Length == 0)
                continue;

            var rowRepeat = Math.Min(RepeatCount(row, "number-rows-repeated"), MaxRepeatedRows);
            for (var r = 0; r < rowRepeat; r++)
            {
                if (output.Length + line.Length + 1 > budget)
                    return;
                output.Append(line).Append('\n');
            }
        }
    }

    private static int RepeatCount(XElement element, string attribute) =>
        int.TryParse(element.Attribute(XName.Get(attribute, OdfTable))?.Value, out var count) && count > 1 ? count : 1;

    private static string OdfCellText(XElement cell)
    {
        var paragraphs = cell.Elements()
            .Where(e => e.Name.NamespaceName == OdfText && e.Name.LocalName is "p" or "h")
            .Select(p => OdfParagraph(p).Trim())
            .Where(t => t.Length > 0);
        return string.Join(" ", paragraphs);
    }

    private static string OdfParagraph(XElement paragraph)
    {
        var text = new StringBuilder();
        AppendOdfInline(paragraph, text);
        return text.ToString();
    }

    private static void AppendOdfInline(XElement element, StringBuilder text)
    {
        foreach (var node in element.Nodes())
        {
            if (node is XText t)
            {
                text.Append(t.Value);
                continue;
            }

            if (node is not XElement child)
                continue;

            var ns = child.Name.NamespaceName;
            switch (child.Name.LocalName)
            {
                case "s" when ns == OdfText:
                    var count = int.TryParse(child.Attribute(XName.Get("c", OdfText))?.Value, out var c) ? Math.Clamp(c, 1, 100) : 1;
                    text.Append(' ', count);
                    break;
                case "tab" when ns == OdfText:
                    text.Append('\t');
                    break;
                case "line-break" when ns == OdfText:
                    text.Append('\n');
                    break;
                case "page-number" or "page-count" when ns == OdfText:
                case "note" when ns == OdfText:
                case "annotation" when ns == OdfOffice:
                case "annotation-end" when ns == OdfOffice:
                    break;
                case "p" or "h" when ns == OdfText:
                    // A text box inside a paragraph.
                    text.Append(' ');
                    AppendOdfInline(child, text);
                    text.Append(' ');
                    break;
                default:
                    AppendOdfInline(child, text);
                    break;
            }
        }
    }

    private static ZipDocumentKind DetectOpenDocumentBody(ZipArchiveEntry content)
    {
        var xml = LoadXml(content);
        var body = xml?.Root?.Element(XName.Get("body", OdfOffice));
        if (body?.Element(XName.Get("spreadsheet", OdfOffice)) != null)
            return ZipDocumentKind.OpenDocumentSpreadsheet;
        if (body?.Element(XName.Get("presentation", OdfOffice)) != null)
            return ZipDocumentKind.OpenDocumentPresentation;
        return body != null ? ZipDocumentKind.OpenDocumentText : ZipDocumentKind.Unknown;
    }

    // ==================== Zip and XML helpers ====================

    private static Dictionary<string, ZipArchiveEntry> EntryMap(ZipArchive zip)
    {
        var map = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/').TrimStart('/');
            map.TryAdd(name, entry);
        }
        return map;
    }

    /// <summary>The main document part named by _rels/.rels (e.g. "word/document.xml"), or "".</summary>
    private static string MainPartPath(Dictionary<string, ZipArchiveEntry> entries)
    {
        var rels = ReadRelationships(entries, "");
        var main = rels.Values.FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.OrdinalIgnoreCase));
        return main.Target ?? "";
    }

    /// <summary>A part's relationships by id, with targets resolved to zip paths.</summary>
    private static Dictionary<string, (string Type, string Target)> ReadRelationships(Dictionary<string, ZipArchiveEntry> entries, string partPath)
    {
        var result = new Dictionary<string, (string Type, string Target)>(StringComparer.Ordinal);
        var directory = partPath.Contains('/') ? partPath[..partPath.LastIndexOf('/')] : "";
        var fileName = partPath.Contains('/') ? partPath[(partPath.LastIndexOf('/') + 1)..] : partPath;
        var relsPath = (directory.Length > 0 ? directory + "/" : "") + "_rels/" + fileName + ".rels";
        if (!entries.TryGetValue(relsPath, out var entry) || LoadXml(entry) is not { } xml)
            return result;

        foreach (var rel in xml.Descendants().Where(e => e.Name.LocalName == "Relationship"))
        {
            var id = rel.Attribute("Id")?.Value;
            var target = rel.Attribute("Target")?.Value;
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(target) || rel.Attribute("TargetMode")?.Value == "External")
                continue;
            result[id] = (rel.Attribute("Type")?.Value ?? "", ResolvePartPath(directory, target));
        }

        return result;
    }

    private static string ResolvePartPath(string directory, string target)
    {
        target = Uri.UnescapeDataString(target.Replace('\\', '/'));
        var combined = target.StartsWith('/') ? target.TrimStart('/') : (directory.Length > 0 ? directory + "/" + target : target);
        var parts = new List<string>();
        foreach (var segment in combined.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
                continue;
            if (segment == "..")
            {
                if (parts.Count > 0)
                    parts.RemoveAt(parts.Count - 1);
                continue;
            }
            parts.Add(segment);
        }
        return string.Join("/", parts);
    }

    private static string CombinePart(string siblingPath, string fileName) =>
        siblingPath.Contains('/') ? siblingPath[..(siblingPath.LastIndexOf('/') + 1)] + fileName : fileName;

    private static XDocument? LoadXml(ZipArchiveEntry entry)
    {
        try
        {
            using var reader = XmlReader.Create(entry.Open(), ReaderSettings);
            return XDocument.Load(reader);
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    private static string ReadSmallText(ZipArchiveEntry entry)
    {
        try
        {
            using var reader = new StreamReader(entry.Open());
            var buffer = new char[200];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return "";
        }
    }

    /// <summary>Trims line ends, collapses runs of blank lines and trims the whole text.</summary>
    private static string Tidy(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0)
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

    private static string Cap(string text, int maxChars, string what) =>
        text.Length <= maxChars ? text : text[..maxChars] + $"\n\n[The {what} was truncated: the rest of its text was not read.]";
}
