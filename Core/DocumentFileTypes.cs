using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>How DocumentTextService reads a file type.</summary>
public enum DocumentReader
{
    /// <summary>No reader for the extension: read as text when the bytes look like text, else try a Windows filter.</summary>
    Unknown,
    Text,
    Pdf,
    Word,
    /// <summary>Word 97-2003 .doc: read directly, then through a Windows filter.</summary>
    OldWord,
    Excel,
    PowerPoint,
    OpenDocument,
    Rtf,
    Html,
    /// <summary>A MIME message: .eml, or a .mht web archive.</summary>
    Email,
    /// <summary>Outlook .msg: read directly, then through a Windows filter.</summary>
    OutlookMessage,
    /// <summary>A picture whose text is read with OCR.</summary>
    Image,
    /// <summary>Only readable through a Windows text filter (IFilter): old .xls/.ppt, Publisher, WordPerfect...</summary>
    WindowsFilter,
    /// <summary>Known to have no text to read here: audio, video, archives, Apple iWork files.</summary>
    Unsupported
}

/// <summary>Why a read gave no text, so a summary can group failures ("3 photos without text").</summary>
public enum DocumentReadProblem
{
    None,
    NotFound,
    /// <summary>The file is empty or holds no text.</summary>
    NoText,
    /// <summary>OCR ran on a picture (or a scanned page) and found no words.</summary>
    NoTextInImage,
    /// <summary>A type that cannot be read here (audio, archive, unknown binary).</summary>
    Unsupported,
    /// <summary>Scanned pages or a picture, and Windows OCR has no language (and no Tesseract is set up).</summary>
    NeedsOcr,
    /// <summary>An old binary format and no Windows text filter is installed for it.</summary>
    NeedsFilter,
    /// <summary>A .heic or .webp picture and its Windows image extension is not installed.</summary>
    NeedsImageExtension,
    PasswordProtected,
    /// <summary>A OneDrive online-only file that could not be downloaded.</summary>
    OnlineOnly,
    /// <summary>Any other error (damaged file, access denied...).</summary>
    Failed
}

/// <summary>What the first bytes of a file say it is, whatever its extension.</summary>
public enum SniffedFormat { Unknown, Pdf, Zip, CompoundFile, Rtf, Html }

/// <summary>
/// Decides how a file is read (DocumentReader), which types the knowledge folder indexes
/// (KnowledgeExtensions), what to call a type in a summary (GetTypeName) and words the errors for
/// files that cannot be read, so each gets advice that fits it.
/// </summary>
public static class DocumentFileTypes
{
    /// <summary>Scanned PDFs are OCR'd up to this many pages; later pages are left out and the user is told.</summary>
    public const int MaxOcrPages = 8;

    /// <summary>
    /// The knowledge folder OCRs scanned PDFs and multi-page TIFFs up to this many pages instead: it reads
    /// in the background with progress, and a long scanned declaration or set of bylaws has to be
    /// searchable past its first pages.
    /// </summary>
    public const int MaxKnowledgeOcrPages = 200;

    /// <summary>How many leading bytes <see cref="LooksLikeText"/> needs to classify a file.</summary>
    public const int TextSniffBytes = 8192;

    /// <summary>A PDF page whose text layer has fewer letters than this is treated as scanned and OCR'd.</summary>
    public const int MinPdfPageLetters = 40;

    /// <summary>
    /// The extensions the knowledge folder indexes: every type DocumentTextService reads without
    /// extra software (PDF, Word, Excel, PowerPoint, OpenDocument, RTF, text, web pages, emails,
    /// Outlook messages and pictures, which are read with Windows OCR). Old .xls and .ppt are read
    /// through the Windows text filter for Office files.
    /// </summary>
    public static IReadOnlyList<string> KnowledgeExtensions { get; } = new[]
    {
        ".pdf",
        ".docx", ".docm", ".doc", ".rtf", ".odt",
        ".txt", ".text", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".log",
        ".html", ".htm", ".mht", ".mhtml", ".eml", ".msg",
        ".xlsx", ".xlsm", ".xls", ".ods",
        ".pptx", ".pptm", ".ppsx", ".ppt", ".odp",
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif"
    };

    private static readonly HashSet<string> KnowledgeSet = new(KnowledgeExtensions, StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Notes, data and logs
        ".txt", ".text", ".md", ".markdown", ".rst", ".csv", ".tsv", ".json", ".jsonl", ".ndjson",
        ".xml", ".log", ".srt", ".vtt", ".tex", ".nfo",
        // Configuration
        ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".config", ".properties", ".env", ".reg",
        // Scripts and source code
        ".bat", ".cmd", ".ps1", ".psm1", ".psd1", ".sh", ".bash", ".zsh", ".fish",
        ".py", ".cs", ".csx", ".vb", ".fs", ".xaml", ".csproj", ".props", ".targets", ".sln",
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".java", ".kt", ".kts", ".gradle",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".cxx", ".go", ".rs", ".rb", ".php", ".swift",
        ".lua", ".pl", ".r", ".sql", ".dart", ".scala",
        ".html", ".htm", ".css", ".scss", ".less", ".svg"
    };

    private static readonly Dictionary<string, DocumentReader> Readers = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = DocumentReader.Pdf,
        [".docx"] = DocumentReader.Word, [".docm"] = DocumentReader.Word, [".dotx"] = DocumentReader.Word, [".dotm"] = DocumentReader.Word,
        [".doc"] = DocumentReader.OldWord, [".dot"] = DocumentReader.OldWord,
        [".xlsx"] = DocumentReader.Excel, [".xlsm"] = DocumentReader.Excel, [".xltx"] = DocumentReader.Excel, [".xltm"] = DocumentReader.Excel,
        [".pptx"] = DocumentReader.PowerPoint, [".pptm"] = DocumentReader.PowerPoint, [".ppsx"] = DocumentReader.PowerPoint,
        [".ppsm"] = DocumentReader.PowerPoint, [".potx"] = DocumentReader.PowerPoint, [".potm"] = DocumentReader.PowerPoint,
        [".odt"] = DocumentReader.OpenDocument, [".ods"] = DocumentReader.OpenDocument, [".odp"] = DocumentReader.OpenDocument,
        [".ott"] = DocumentReader.OpenDocument, [".ots"] = DocumentReader.OpenDocument, [".otp"] = DocumentReader.OpenDocument,
        [".rtf"] = DocumentReader.Rtf,
        [".html"] = DocumentReader.Html, [".htm"] = DocumentReader.Html, [".xhtml"] = DocumentReader.Html,
        [".eml"] = DocumentReader.Email, [".mht"] = DocumentReader.Email, [".mhtml"] = DocumentReader.Email,
        [".msg"] = DocumentReader.OutlookMessage,
        [".xls"] = DocumentReader.WindowsFilter, [".xlt"] = DocumentReader.WindowsFilter, [".xlsb"] = DocumentReader.WindowsFilter,
        [".ppt"] = DocumentReader.WindowsFilter, [".pps"] = DocumentReader.WindowsFilter, [".pot"] = DocumentReader.WindowsFilter,
        [".pub"] = DocumentReader.WindowsFilter, [".wpd"] = DocumentReader.WindowsFilter, [".wps"] = DocumentReader.WindowsFilter,
        [".one"] = DocumentReader.WindowsFilter, [".vsd"] = DocumentReader.WindowsFilter, [".vsdx"] = DocumentReader.WindowsFilter,
        [".xps"] = DocumentReader.WindowsFilter, [".oxps"] = DocumentReader.WindowsFilter,
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jpe", ".jfif", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif" };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".aac", ".wma" };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".webm", ".wmv", ".m4v", ".3gp" };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".cab", ".iso" };

    private static readonly HashSet<string> AppleExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pages", ".numbers", ".key" };

    private static readonly Dictionary<string, string> TypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "PDF",
        [".docx"] = "Word", [".docm"] = "Word", [".dotx"] = "Word", [".dotm"] = "Word",
        [".doc"] = "old Word", [".dot"] = "old Word",
        [".xlsx"] = "Excel", [".xlsm"] = "Excel", [".xltx"] = "Excel", [".xltm"] = "Excel", [".xlsb"] = "Excel",
        [".xls"] = "old Excel", [".xlt"] = "old Excel",
        [".pptx"] = "PowerPoint", [".pptm"] = "PowerPoint", [".ppsx"] = "PowerPoint", [".ppsm"] = "PowerPoint",
        [".potx"] = "PowerPoint", [".potm"] = "PowerPoint",
        [".ppt"] = "old PowerPoint", [".pps"] = "old PowerPoint", [".pot"] = "old PowerPoint",
        [".odt"] = "OpenDocument text", [".ott"] = "OpenDocument text",
        [".ods"] = "OpenDocument spreadsheet", [".ots"] = "OpenDocument spreadsheet",
        [".odp"] = "OpenDocument presentation", [".otp"] = "OpenDocument presentation",
        [".rtf"] = "RTF",
        [".html"] = "web page", [".htm"] = "web page", [".xhtml"] = "web page",
        [".mht"] = "web archive", [".mhtml"] = "web archive",
        [".eml"] = "email",
        [".msg"] = "Outlook email",
        [".pub"] = "Publisher", [".wpd"] = "WordPerfect", [".wps"] = "Works", [".one"] = "OneNote",
        [".vsd"] = "Visio", [".vsdx"] = "Visio", [".xps"] = "XPS", [".oxps"] = "XPS",
        [".txt"] = "text", [".text"] = "text", [".md"] = "Markdown", [".markdown"] = "Markdown",
        [".csv"] = "CSV", [".tsv"] = "CSV", [".json"] = "JSON", [".xml"] = "XML", [".log"] = "log",
        [".pages"] = "Pages", [".numbers"] = "Numbers", [".key"] = "Keynote",
    };

    /// <summary>True for extensions that are always read as plain text.</summary>
    public static bool IsTextExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && TextExtensions.Contains(extension);

    /// <summary>True when the knowledge folder indexes files with this extension (".pdf", ".XLSX"...).</summary>
    public static bool IsKnowledgeExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && KnowledgeSet.Contains(extension);

    /// <summary>True for pictures whose text is read with OCR.</summary>
    public static bool IsImageExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && ImageExtensions.Contains(extension);

    /// <summary>The reader for an extension (with or without the file name around it).</summary>
    public static DocumentReader GetReader(string? extension)
    {
        var ext = NormalizeExtension(extension);
        if (ext.Length == 0)
            return DocumentReader.Unknown;
        if (Readers.TryGetValue(ext, out var reader))
            return reader;
        if (ImageExtensions.Contains(ext))
            return DocumentReader.Image;
        if (AudioExtensions.Contains(ext) || VideoExtensions.Contains(ext) || ArchiveExtensions.Contains(ext) ||
            AppleExtensions.Contains(ext) || ext == ".ico")
            return DocumentReader.Unsupported;
        return TextExtensions.Contains(ext) ? DocumentReader.Text : DocumentReader.Unknown;
    }

    /// <summary>True when DocumentTextService has a reader for the type (some need OCR or a Windows filter).</summary>
    public static bool CanRead(string? extension) =>
        GetReader(extension) is not (DocumentReader.Unknown or DocumentReader.Unsupported);

    /// <summary>
    /// A short name for a file's type, for summaries such as "12 PDF, 3 Excel, 40 image": "PDF",
    /// "Word", "old Word", "Excel", "PowerPoint", "email", "Outlook email", "web page", "image", "text"...
    /// Takes a path or an extension. Unknown types give their extension without the dot ("DAT"),
    /// or "file" when there is none.
    /// </summary>
    public static string GetTypeName(string? pathOrExtension)
    {
        var ext = NormalizeExtension(pathOrExtension);
        if (ext.Length == 0)
            return "file";
        if (TypeNames.TryGetValue(ext, out var name))
            return name;
        if (ImageExtensions.Contains(ext))
            return "image";
        if (AudioExtensions.Contains(ext))
            return "audio";
        if (VideoExtensions.Contains(ext))
            return "video";
        if (ArchiveExtensions.Contains(ext))
            return "archive";
        if (TextExtensions.Contains(ext))
            return "text";
        return ext.TrimStart('.').ToUpperInvariant();
    }

    /// <summary>
    /// Counts files by <see cref="GetTypeName"/>, most common first: "12 PDF, 8 Word, 3 Excel, 5 image".
    /// Types past <paramref name="maxTypes"/> are summed up as "and 4 other".
    /// </summary>
    public static string DescribeTypeCounts(IEnumerable<string> paths, int maxTypes = 8)
    {
        var groups = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .GroupBy(GetTypeName, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count == 0)
            return "";

        maxTypes = Math.Max(1, maxTypes);
        var parts = groups.Take(maxTypes).Select(g => $"{g.Count:N0} {g.Name}").ToList();
        var rest = groups.Skip(maxTypes).Sum(g => g.Count);
        if (rest > 0)
            parts.Add($"{rest:N0} other");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// The error for a file type that is known to have no text that can be read here (audio, video,
    /// archives, Apple iWork files, icons), or null when the type is readable or unknown.
    /// </summary>
    public static string? GetUnsupportedTypeMessage(string? extension)
    {
        var ext = NormalizeExtension(extension);
        if (ext.Length == 0)
            return null;

        if (AudioExtensions.Contains(ext) || VideoExtensions.Contains(ext))
            return "This is an audio or video file, not a document, so it has no text to read.";
        if (ArchiveExtensions.Contains(ext))
            return "Archives are not supported. Extract the file you need and attach that.";
        if (AppleExtensions.Contains(ext))
            return $"Apple {GetTypeName(ext)} files ({ext}) can't be read here. Export it as PDF, Word or Excel and use that.";
        if (ext == ".ico")
            return "This is an icon, so it has no text to read.";

        return null;
    }

    /// <summary>The error for a file with an unknown extension whose contents are not text.</summary>
    public static string DescribeUnsupportedType(string? extension)
    {
        var type = string.IsNullOrWhiteSpace(extension) ? "files without an extension" : $"{extension.ToLowerInvariant()} files";
        return $"Can't read {type}: this is not a document, spreadsheet, presentation, email, picture or plain-text file, and Windows has no text filter for it.";
    }

    /// <summary>
    /// The error for a type that only a Windows text filter (IFilter) reads, when none is installed
    /// for it: says which format it is and how to make it readable.
    /// </summary>
    public static string GetNoFilterMessage(string? extension)
    {
        var ext = NormalizeExtension(extension);
        return ext switch
        {
            ".doc" or ".dot" =>
                $"Old Word format ({ext}) and no Windows text filter is installed: save it as .docx or install the Microsoft Office filter pack.",
            ".xls" or ".xlt" =>
                $"Old Excel format ({ext}) and no Windows text filter is installed: save it as .xlsx or CSV, or install the Microsoft Office filter pack.",
            ".xlsb" =>
                "Excel binary workbook (.xlsb) and no Windows text filter is installed: save it as .xlsx, or install the Microsoft Office filter pack.",
            ".ppt" or ".pps" or ".pot" =>
                $"Old PowerPoint format ({ext}) and no Windows text filter is installed: save it as .pptx or PDF, or install the Microsoft Office filter pack.",
            ".msg" =>
                "Outlook message (.msg) that could not be read, and no Windows text filter is installed for it: open it in Outlook and use File > Save As to save it as text or HTML.",
            ".pub" =>
                "Publisher file (.pub) and no Windows text filter is installed: save it as PDF from Publisher, or install the Microsoft Office filter pack.",
            ".wpd" =>
                "WordPerfect file (.wpd) and no Windows text filter is installed: open it in WordPerfect or Word and save it as .docx or PDF.",
            ".wps" =>
                "Microsoft Works file (.wps) and no Windows text filter is installed: open it in Word and save it as .docx.",
            ".one" =>
                "OneNote section (.one) and no Windows text filter is installed: export the pages from OneNote as PDF or Word.",
            ".vsd" or ".vsdx" =>
                $"Visio drawing ({ext}) and no Windows text filter is installed: export it as PDF from Visio.",
            ".xps" or ".oxps" =>
                $"XPS document ({ext}) and no Windows text filter is installed: print it to PDF and use that.",
            _ => DescribeUnsupportedType(ext)
        };
    }

    /// <summary>For an online-only OneDrive file that Windows could not download.</summary>
    public const string OneDriveOnlineOnlyMessage =
        "This OneDrive file is online-only and could not be downloaded: right-click the folder and choose Always keep on this device.";

    /// <summary>For scanned pages or pictures when Windows OCR has no recognition language installed.</summary>
    public const string OcrLanguageMissingMessage =
        "Windows text recognition (OCR) has no language installed. Add one in Settings > Time & language > Language (Language & region on Windows 11), for example English (United States), then try again.";

    /// <summary>For a picture that OCR read but found no words in.</summary>
    public const string NoTextInImageMessage = "No text was found in this picture.";

    /// <summary>The error when Windows cannot open a picture of this type (a missing codec).</summary>
    public static string GetImageDecodeMessage(string? extension, string? detail = null)
    {
        var ext = NormalizeExtension(extension);
        if (ext is ".heic" or ".heif")
            return $"Windows can't open this {ext} photo: install HEIF Image Extensions (and HEVC Video Extensions) from the Microsoft Store, or save it as JPEG.";
        if (ext == ".webp")
            return "Windows can't open this .webp picture: install Webp Image Extensions from the Microsoft Store, or save it as PNG or JPEG.";
        return string.IsNullOrWhiteSpace(detail)
            ? "Windows could not open this picture; it may be damaged."
            : $"Windows could not open this picture; it may be damaged ({detail}).";
    }

    /// <summary>For a document that needs a password to open.</summary>
    public static string GetPasswordMessage(string? pathOrExtension) =>
        $"This {GetTypeName(pathOrExtension)} file is password-protected. Open it, remove the password, save it and try again.";

    /// <summary>
    /// True when a file's attributes say it is a cloud placeholder whose data is not on this PC
    /// (OneDrive online-only): Offline (0x1000), RecallOnOpen (0x40000) or RecallOnDataAccess (0x400000).
    /// </summary>
    public static bool IsCloudOnly(FileAttributes attributes) =>
        ((int)attributes & (0x1000 | 0x40000 | 0x400000)) != 0;

    /// <summary>Recognizes a file by its first bytes (PDF, zip, OLE compound file, RTF, HTML).</summary>
    public static SniffedFormat Sniff(ReadOnlySpan<byte> start)
    {
        if (start.Length >= 4 && start[0] == (byte)'%' && start[1] == (byte)'P' && start[2] == (byte)'D' && start[3] == (byte)'F')
            return SniffedFormat.Pdf;
        if (start.Length >= 4 && start[0] == (byte)'P' && start[1] == (byte)'K' && start[2] is 3 or 5 or 7 && start[3] is 4 or 6 or 8)
            return SniffedFormat.Zip;
        if (CompoundFile.HasSignature(start))
            return SniffedFormat.CompoundFile;

        // Text formats may start with a byte order mark or white space.
        var offset = start.Length >= 3 && start[0] == 0xEF && start[1] == 0xBB && start[2] == 0xBF ? 3 : 0;
        while (offset < start.Length && start[offset] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            offset++;
        var head = System.Text.Encoding.ASCII.GetString(start[offset..Math.Min(start.Length, offset + 256)]);
        if (head.StartsWith("{\\rtf", StringComparison.Ordinal))
            return SniffedFormat.Rtf;
        if (head.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
            (head.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && head.Contains("<html", StringComparison.OrdinalIgnoreCase)))
            return SniffedFormat.Html;
        return SniffedFormat.Unknown;
    }

    /// <summary>
    /// Sniffs the first bytes of a file with an unknown extension: text has no NUL bytes and almost no
    /// control characters (a UTF-8/UTF-16 byte order mark also counts as text).
    /// </summary>
    public static bool LooksLikeText(ReadOnlySpan<byte> sample)
    {
        if (sample.IsEmpty)
            return true;

        if (sample.Length >= 3 && sample[0] == 0xEF && sample[1] == 0xBB && sample[2] == 0xBF)
            return true;
        if (sample.Length >= 2 && ((sample[0] == 0xFF && sample[1] == 0xFE) || (sample[0] == 0xFE && sample[1] == 0xFF)))
            return true;

        var control = 0;
        foreach (var b in sample)
        {
            if (b == 0)
                return false;
            // Tab, LF, VT, FF, CR and ESC show up in real text files; other C0 controls do not.
            if (b < 0x20 && b is not (0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x1B))
                control++;
        }

        return control <= sample.Length / 100;
    }

    private static readonly Regex OcrPageLimitNotice = new(@"\(OCR is limited to (\d+) pages\)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// The page limit at which OCR stopped, from the "[... (OCR is limited to 8 pages) ...]" notice that
    /// starts a read with pages left out; 0 when the text does not start with such a notice.
    /// </summary>
    public static int OcrPageLimitIn(string? text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '[')
            return 0;

        var end = text.IndexOf(']');
        var match = OcrPageLimitNotice.Match(end > 0 ? text[..end] : text);
        return match.Success && int.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var limit)
            ? limit
            : 0;
    }

    /// <summary>
    /// What to tell the user (and the model) about a scanned PDF read with OCR: how many pages were
    /// OCR'd out of how many. <paramref name="totalPages"/> is 0 when the page count is unknown.
    /// </summary>
    public static string BuildOcrPageNotice(int ocrPages, int totalPages, int maxPages = MaxOcrPages)
    {
        if (ocrPages <= 0)
            return "";

        if (totalPages > ocrPages)
        {
            var skipped = totalPages - ocrPages == 1
                ? $"Page {totalPages} was not read."
                : $"Pages {ocrPages + 1}-{totalPages} were not read.";
            return $"Scanned PDF: OCR read the first {ocrPages} of {totalPages} pages (OCR is limited to {maxPages} pages). {skipped}";
        }

        if (totalPages <= 0 && ocrPages >= maxPages)
            return $"Scanned PDF: OCR read the first {ocrPages} pages (OCR is limited to {maxPages} pages). Any later pages were not read.";

        return ocrPages == 1
            ? "Scanned PDF: OCR read its 1 page."
            : $"Scanned PDF: OCR read all {ocrPages} pages.";
    }

    /// <summary>
    /// The notice for a PDF with both text pages and scanned pages: how many scanned pages OCR read,
    /// and which scanned pages were left out by the page limit.
    /// </summary>
    public static string BuildScannedPagesNotice(int ocrPages, IReadOnlyCollection<int> skippedPages, int maxPages = MaxOcrPages)
    {
        if (ocrPages <= 0 && skippedPages.Count == 0)
            return "";

        var read = ocrPages == 1 ? "OCR read 1 scanned page" : $"OCR read {ocrPages} scanned pages";
        if (skippedPages.Count == 0)
            return read + ".";

        var which = skippedPages.Count == 1
            ? $"Scanned page {skippedPages.First()} was not read."
            : $"Scanned pages {FormatPageRanges(skippedPages)} were not read.";
        return $"{read} (OCR is limited to {maxPages} pages). {which}";
    }

    /// <summary>Page numbers as ranges: 3, 4, 5, 9, 12 → "3-5, 9 and 12".</summary>
    public static string FormatPageRanges(IEnumerable<int> pages)
    {
        var sorted = pages.Where(p => p > 0).Distinct().OrderBy(p => p).ToList();
        var ranges = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1)
                j++;
            ranges.Add(j == i ? sorted[i].ToString() : $"{sorted[i]}-{sorted[j]}");
            i = j + 1;
        }

        return ranges.Count <= 1
            ? string.Join("", ranges)
            : string.Join(", ", ranges.Take(ranges.Count - 1)) + " and " + ranges[^1];
    }

    /// <summary>True when a PDF page's text layer is (nearly) empty, so the page is probably a scan.</summary>
    public static bool PdfPageNeedsOcr(string? pageText) => CountLetters(pageText) < MinPdfPageLetters;

    public static int CountLetters(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var count = 0;
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
                count++;
        }
        return count;
    }

    /// <summary>
    /// Builds the "Attach document" dialog filter: readable documents first, then pictures (read
    /// with OCR), then all files.
    /// </summary>
    public static string BuildOpenFileDialogFilter()
    {
        var documents = KnowledgeExtensions.Where(e => !ImageExtensions.Contains(e)).Select(e => "*" + e);
        var pictures = KnowledgeExtensions.Where(e => ImageExtensions.Contains(e)).Select(e => "*" + e);
        var docs = string.Join(";", documents);
        var pics = string.Join(";", pictures);
        return $"Documents|{docs}|Pictures (text read with OCR)|{pics}|All files (*.*)|*.*";
    }

    /// <summary>".pdf" from "C:\a\b.PDF" or ".PDF"; "" when there is none.</summary>
    private static string NormalizeExtension(string? pathOrExtension)
    {
        if (string.IsNullOrWhiteSpace(pathOrExtension))
            return "";
        var value = pathOrExtension.Trim();
        if (value.StartsWith('.') && value.LastIndexOf('.') == 0 && value.IndexOfAny(new[] { '\\', '/' }) < 0)
            return value.ToLowerInvariant();
        return Path.GetExtension(value).ToLowerInvariant();
    }
}
