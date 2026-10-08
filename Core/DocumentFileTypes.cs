using System;
using System.Collections.Generic;

namespace VoiceChatbot;

/// <summary>
/// Decides how an attached file is read (PDF, DOCX, plain text) and words the errors for files that
/// cannot be read, so a .yaml or .ps1 file is read as text and an .xlsx gets advice that fits it
/// instead of the "scanned PDF" message.
/// </summary>
public static class DocumentFileTypes
{
    /// <summary>Scanned PDFs are OCR'd up to this many pages; later pages are left out and the user is told.</summary>
    public const int MaxOcrPages = 8;

    /// <summary>How many leading bytes <see cref="LooksLikeText"/> needs to classify a file.</summary>
    public const int TextSniffBytes = 8192;

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

    private static readonly HashSet<string> SpreadsheetExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".xls", ".xlsx", ".xlsm", ".xlsb", ".ods", ".numbers" };

    private static readonly HashSet<string> PresentationExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".ppt", ".pptx", ".pps", ".ppsx", ".odp", ".key" };

    private static readonly HashSet<string> OtherWordProcessorExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".rtf", ".odt", ".pages", ".wpd" };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".ico" };

    private static readonly HashSet<string> AudioVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".aac", ".wma", ".mp4", ".mov", ".mkv", ".avi", ".webm", ".wmv" };

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".bz2", ".xz" };

    /// <summary>True for extensions that are always read as plain text.</summary>
    public static bool IsTextExtension(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && TextExtensions.Contains(extension);

    /// <summary>
    /// The error for a file type that is known not to be readable here (old .doc, spreadsheets, slides,
    /// images, media, archives), or null when the type is not one of those.
    /// </summary>
    public static string? GetUnsupportedTypeMessage(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        if (extension.Equals(".doc", StringComparison.OrdinalIgnoreCase))
            return "Old .doc files are not supported yet. Save it as .docx and attach that.";
        if (SpreadsheetExtensions.Contains(extension))
            return $"Spreadsheet files ({extension.ToLowerInvariant()}) are not supported yet. Save the sheet as CSV and attach that, or copy the cells and paste them into the message box.";
        if (PresentationExtensions.Contains(extension))
            return $"Presentation files ({extension.ToLowerInvariant()}) are not supported yet. Export it as PDF and attach that.";
        if (OtherWordProcessorExtensions.Contains(extension))
            return $"{extension.ToLowerInvariant()} files are not supported yet. Save it as .docx, PDF or .txt and attach that.";
        if (ImageExtensions.Contains(extension))
            return "This is an image, not a document. Use the Image button (or paste it) to attach it.";
        if (AudioVideoExtensions.Contains(extension))
            return "This is an audio or video file, not a document, so it has no text to read.";
        if (ArchiveExtensions.Contains(extension))
            return "Archives are not supported. Extract the file you need and attach that.";

        return null;
    }

    /// <summary>The error for a file with an unknown extension whose contents are not text.</summary>
    public static string DescribeUnsupportedType(string? extension)
    {
        var type = string.IsNullOrWhiteSpace(extension) ? "files without an extension" : $"{extension.ToLowerInvariant()} files";
        return $"Can't read {type}: this is not a PDF, a Word .docx or a plain-text file.";
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
}
