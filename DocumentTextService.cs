using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace VoiceChatbot;

public sealed class DocumentTextService
{
    private const int MaxDocumentContextChars = 240000;
    // Office, RTF, email and filter output is capped here; the knowledge index keeps less.
    private const int MaxExtractedChars = 4_000_000;
    // Old Office files and .msg files larger than this skip the built-in readers (they load the whole file).
    private const long MaxCompoundFileBytes = 100L * 1024 * 1024;

    /// <summary>
    /// Reads a document's text: PDF (with OCR for scanned pages), Word, Excel, PowerPoint,
    /// OpenDocument, RTF, HTML, emails (.eml, .msg), pictures (OCR) and plain text; other types go
    /// through their Windows text filter when one is installed. Runs on a worker thread, so a UI
    /// caller stays responsive. Cancelling throws OperationCanceledException and kills any running
    /// pdftoppm/tesseract process. Failures come back as a result with an Error and a Problem.
    /// Scans are OCR'd up to <paramref name="maxOcrPages"/> pages (the knowledge folder reads more).
    /// </summary>
    public Task<DocumentTextResult> ExtractAsync(string path, CancellationToken ct = default, IProgress<string>? progress = null,
        int maxOcrPages = DocumentFileTypes.MaxOcrPages) =>
        Task.Run(() => ExtractCoreAsync(path, progress, Math.Max(1, maxOcrPages), ct), CancellationToken.None);

    private static async Task<DocumentTextResult> ExtractCoreAsync(string path, IProgress<string>? progress, int maxOcrPages, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Fail(path, "File not found.", DocumentReadProblem.NotFound);

        // An online-only OneDrive file is downloaded when it is read; only explain when that fails.
        var cloudOnly = IsCloudOnly(path);
        DocumentTextResult result;
        try
        {
            result = await ReadAsync(path, progress, maxOcrPages, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Info($"Could not read {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            result = Fail(path, ex.Message, DocumentReadProblem.Failed);
        }

        if (cloudOnly && result.Problem is (DocumentReadProblem.Failed or DocumentReadProblem.NoText) && IsCloudOnly(path))
            return Fail(path, DocumentFileTypes.OneDriveOnlineOnlyMessage, DocumentReadProblem.OnlineOnly);
        return result;
    }

    private static bool IsCloudOnly(string path)
    {
        try
        {
            return DocumentFileTypes.IsCloudOnly(File.GetAttributes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<DocumentTextResult> ReadAsync(string path, IProgress<string>? progress, int maxOcrPages, CancellationToken ct)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var reader = DocumentFileTypes.GetReader(ext);
        switch (reader)
        {
            case DocumentReader.Unsupported:
                return Fail(path, DocumentFileTypes.GetUnsupportedTypeMessage(ext) ?? DocumentFileTypes.DescribeUnsupportedType(ext), DocumentReadProblem.Unsupported);
            case DocumentReader.Pdf:
                return await ExtractPdfDocumentAsync(path, progress, maxOcrPages, ct);
            case DocumentReader.Image:
                return await ExtractImageAsync(path, progress, maxOcrPages, ct);
            case DocumentReader.Text:
                return await ExtractTextFileAsync(path, ct);
            case DocumentReader.Html:
                return await ExtractHtmlAsync(path, ct);
            case DocumentReader.Email:
                return await ExtractEmailAsync(path, ct);
        }

        // Office-style files are recognized by their first bytes too: a .doc may really be RTF, HTML
        // or a .docx, an .xls may be a web page or tab-separated text, and a .docx that is an OLE
        // compound file is password-protected.
        var start = await ReadStartAsync(path, DocumentFileTypes.TextSniffBytes, ct);
        switch (DocumentFileTypes.Sniff(start))
        {
            case SniffedFormat.Pdf:
                return await ExtractPdfDocumentAsync(path, progress, maxOcrPages, ct);
            case SniffedFormat.Rtf:
                return await ExtractRtfAsync(path, ct);
            case SniffedFormat.Html:
                return await ExtractHtmlAsync(path, ct);
            case SniffedFormat.Zip:
                return ExtractZipDocument(path);
            case SniffedFormat.CompoundFile:
                return await ExtractCompoundFileAsync(path, reader, progress, ct);
        }

        switch (reader)
        {
            case DocumentReader.Rtf:
                // Not RTF inside: some programs save plain text as .rtf.
                return await ExtractTextFileAsync(path, ct);
            case DocumentReader.OutlookMessage when EmailText.LooksLikeEmail(Encoding.Latin1.GetString(start)):
                return await ExtractEmailAsync(path, ct);
        }

        // A tab-separated "spreadsheet", an old .doc that is really text, or an unknown text file.
        if (DocumentFileTypes.LooksLikeText(start))
            return await ExtractTextFileAsync(path, ct);

        if (reader is DocumentReader.Word or DocumentReader.Excel or DocumentReader.PowerPoint or DocumentReader.OpenDocument)
            return Fail(path, $"This {DocumentFileTypes.GetTypeName(ext)} file is damaged or is not really a {ext} file.", DocumentReadProblem.Failed);

        return await ExtractWithFilterAsync(path, reader, progress, ct);
    }

    // Files open in Word or Excel are still readable: share with writers too.
    private static FileStream OpenShared(string path, bool useAsync = false) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync);

    private static async Task<byte[]> ReadAllBytesSharedAsync(string path, CancellationToken ct)
    {
        await using var stream = OpenShared(path, useAsync: true);
        var output = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : 0);
        await stream.CopyToAsync(output, ct);
        return output.Length == output.Capacity ? output.GetBuffer() : output.ToArray();
    }

    private static async Task<byte[]> ReadStartAsync(string path, int count, CancellationToken ct)
    {
        await using var stream = OpenShared(path, useAsync: true);
        var buffer = new byte[count];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0)
                break;
            read += n;
        }

        return buffer[..read];
    }

    private static async Task<DocumentTextResult> ExtractTextFileAsync(string path, CancellationToken ct)
    {
        var text = TextDecoding.Decode(await ReadAllBytesSharedAsync(path, ct));
        return string.IsNullOrWhiteSpace(text)
            ? Fail(path, "The file is empty: there is no text in it.", DocumentReadProblem.NoText)
            : CreateResult(path, text, "");
    }

    private static async Task<DocumentTextResult> ExtractHtmlAsync(string path, CancellationToken ct)
    {
        var bytes = await ReadAllBytesSharedAsync(path, ct);
        var text = HtmlDocumentText.ToPlainText(TextDecoding.Decode(bytes, HtmlDocumentText.DetectCharset(bytes)));
        return string.IsNullOrWhiteSpace(text)
            ? Fail(path, "No readable text was found in this web page.", DocumentReadProblem.NoText)
            : CreateResult(path, text, "");
    }

    private static async Task<DocumentTextResult> ExtractEmailAsync(string path, CancellationToken ct)
    {
        var text = EmailText.Extract(await ReadAllBytesSharedAsync(path, ct), MaxExtractedChars);
        return string.IsNullOrWhiteSpace(text)
            ? Fail(path, "No readable text was found in this email.", DocumentReadProblem.NoText)
            : CreateResult(path, text, "");
    }

    private static async Task<DocumentTextResult> ExtractRtfAsync(string path, CancellationToken ct)
    {
        var raw = Encoding.Latin1.GetString(await ReadAllBytesSharedAsync(path, ct));
        var text = RtfText.LooksLikeRtf(raw) ? RtfText.ToPlainText(raw, MaxExtractedChars) : raw;
        return string.IsNullOrWhiteSpace(text)
            ? Fail(path, "No readable text was found in this RTF document.", DocumentReadProblem.NoText)
            : CreateResult(path, text, "");
    }

    /// <summary>Word, Excel, PowerPoint and OpenDocument files (whatever their extension says).</summary>
    private static DocumentTextResult ExtractZipDocument(string path)
    {
        ZipArchive zip;
        var stream = OpenShared(path);
        try
        {
            zip = new ZipArchive(stream, ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            stream.Dispose();
            return Fail(path, $"This {DocumentFileTypes.GetTypeName(path)} file is damaged: it could not be opened.", DocumentReadProblem.Failed);
        }

        using (zip)
        {
            var kind = OfficeText.DetectKind(zip);
            if (kind == ZipDocumentKind.Unknown)
            {
                var ext = Path.GetExtension(path);
                return DocumentFileTypes.GetReader(ext) is DocumentReader.Word or DocumentReader.Excel or DocumentReader.PowerPoint or DocumentReader.OpenDocument
                    ? Fail(path, $"This {DocumentFileTypes.GetTypeName(ext)} file is damaged: no document was found inside it.", DocumentReadProblem.Failed)
                    : Fail(path, DocumentFileTypes.DescribeUnsupportedType(ext), DocumentReadProblem.Unsupported);
            }

            var text = OfficeText.Extract(zip, MaxExtractedChars);
            if (!string.IsNullOrWhiteSpace(text))
                return CreateResult(path, text, "");

            var message = kind switch
            {
                ZipDocumentKind.Word => "No readable text was found in this Word document.",
                ZipDocumentKind.Excel or ZipDocumentKind.OpenDocumentSpreadsheet => "This spreadsheet has no cells with text or numbers.",
                ZipDocumentKind.PowerPoint or ZipDocumentKind.OpenDocumentPresentation => "No text was found on the slides of this presentation.",
                _ => "No readable text was found in this document."
            };
            return Fail(path, message, DocumentReadProblem.NoText);
        }
    }

    /// <summary>
    /// OLE compound files: Word 97-2003 and Outlook .msg are read directly; anything else (and
    /// anything those readers cannot handle) goes through its Windows text filter.
    /// </summary>
    private static async Task<DocumentTextResult> ExtractCompoundFileAsync(string path, DocumentReader reader, IProgress<string>? progress, CancellationToken ct)
    {
        string? refusal = null;
        if (new FileInfo(path).Length <= MaxCompoundFileBytes)
        {
            CompoundFile? file = null;
            try
            {
                file = CompoundFile.Open(await ReadAllBytesSharedAsync(path, ct));
            }
            catch (InvalidDataException ex)
            {
                AppLog.Info($"{Path.GetFileName(path)} is not a readable compound file: {ex.Message}");
            }

            if (file != null)
            {
                // A password-protected .docx/.xlsx/.pptx is a compound file holding an encrypted package.
                if (file.Find(file.Root, "EncryptedPackage") != null)
                    return Fail(path, DocumentFileTypes.GetPasswordMessage(path), DocumentReadProblem.PasswordProtected);

                try
                {
                    var text = WordBinaryText.IsWordDocument(file) ? WordBinaryText.Extract(file, MaxExtractedChars)
                        : OutlookMsgText.IsOutlookMessage(file) ? OutlookMsgText.Extract(file, MaxExtractedChars)
                        : "";
                    if (!string.IsNullOrWhiteSpace(text))
                        return CreateResult(path, text, "");
                }
                catch (NotSupportedException ex)
                {
                    // Encrypted, or a Word 6/95 file: a Windows filter may still read the old format.
                    refusal = ex.Message;
                    if (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
                        return Fail(path, DocumentFileTypes.GetPasswordMessage(path), DocumentReadProblem.PasswordProtected);
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException)
                {
                    AppLog.Info($"Built-in reader could not read {Path.GetFileName(path)}: {ex.Message}");
                }
            }
        }

        var filtered = await ExtractWithFilterAsync(path, reader, progress, ct);
        if (filtered.Problem == DocumentReadProblem.NeedsFilter && refusal != null)
            return filtered with { Error = $"{refusal} {filtered.Error}" };
        return filtered;
    }

    private static async Task<DocumentTextResult> ExtractWithFilterAsync(string path, DocumentReader reader, IProgress<string>? progress, CancellationToken ct)
    {
        var ext = Path.GetExtension(path);
        if (reader == DocumentReader.Unknown && !IFilterText.HasFilter(ext))
            return Fail(path, DocumentFileTypes.DescribeUnsupportedType(ext), DocumentReadProblem.Unsupported);

        progress?.Report("reading with the Windows text filter...");
        var result = await IFilterText.ExtractAsync(path, ct, MaxExtractedChars);
        return string.IsNullOrWhiteSpace(result.Text)
            ? Fail(path, result.Error, result.Problem)
            : CreateResult(path, result.Text, "");
    }

    // ==================== Pictures ====================

    private static async Task<DocumentTextResult> ExtractImageAsync(string path, IProgress<string>? progress, int maxOcrPages, CancellationToken ct)
    {
        progress?.Report("reading the text in the picture...");
        var engine = WindowsOcr.TryCreateEngine(out var ocrError);
        if (engine == null)
        {
            // No Windows OCR language: use Tesseract when it is installed.
            var tesseract = GetTesseractPath();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(tesseract) && File.Exists(tesseract) && ext is not (".heic" or ".heif"))
            {
                var run = await RunProcessAsync(tesseract, [path, "stdout", "-l", "eng", "--psm", "3"], Path.GetTempPath(),
                    new[] { Path.GetDirectoryName(tesseract) ?? "" }.Where(d => d.Length > 0).ToList(), ct);
                return OcrText.HasWords(run.Output)
                    ? CreateResult(path, "[Text read from a picture with OCR]\n\n" + run.Output, "")
                    : Fail(path, run.ExitCode == 0 ? DocumentFileTypes.NoTextInImageMessage : "OCR failed: " + run.Error.Trim(),
                        run.ExitCode == 0 ? DocumentReadProblem.NoTextInImage : DocumentReadProblem.Failed);
            }

            return Fail(path, ocrError, DocumentReadProblem.NeedsOcr);
        }

        try
        {
            var image = await WindowsOcr.RecognizeImageAsync(engine, path, maxOcrPages, progress, ct);
            return string.IsNullOrWhiteSpace(image.Text)
                ? Fail(path, image.Error, image.Problem)
                : CreateResult(path, "[Text read from a picture with OCR]\n\n" + image.Text, "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not IOException && ex is not UnauthorizedAccessException)
        {
            AppLog.Warn($"Windows OCR failed on {Path.GetFileName(path)}.", ex);
            return Fail(path, $"Windows could not read the text in this picture ({ex.Message}).", DocumentReadProblem.Failed);
        }
    }

    // ==================== PDF ====================

    private static async Task<DocumentTextResult> ExtractPdfDocumentAsync(string path, IProgress<string>? progress, int maxOcrPages, CancellationToken ct)
    {
        progress?.Report("reading PDF text...");
        List<string> pages;
        List<bool> pagesNeedOcr;
        int pageCount;
        try
        {
            (pages, pagesNeedOcr, pageCount) = ExtractPdfPages(path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (ex.GetType().Name.Contains("Encrypted", StringComparison.OrdinalIgnoreCase))
                return Fail(path, DocumentFileTypes.GetPasswordMessage(path), DocumentReadProblem.PasswordProtected);

            // The PDF library could not parse it; Windows may still render it for OCR.
            AppLog.Info($"PdfPig could not read {Path.GetFileName(path)}: {ex.Message}");
            pages = new List<string>();
            pagesNeedOcr = new List<bool>();
            pageCount = 0;
        }

        var text = NormalizeText(string.Join("\n\n", pages));
        // The column marks of the page layout are not part of the text's letters-to-symbols balance.
        var readable = text.Length > 0 && LooksLikeReadableText(text.Replace(PdfLayoutText.ColumnSeparator, " "));

        // Pages whose printed text layer is (nearly) empty are scans (form values alone do not make a
        // page readable); a garbled text layer means every page is.
        var scanned = readable
            ? Enumerable.Range(1, pages.Count).Where(p => pagesNeedOcr[p - 1]).ToList()
            : Enumerable.Range(1, pageCount > 0 ? pageCount : maxOcrPages).ToList();
        if (scanned.Count == 0)
            return CreateResult(path, text, "");

        var allScanned = !readable || scanned.Count == pages.Count;
        var toRead = scanned.Take(maxOcrPages).ToList();
        var ocr = await OcrPdfPagesAsync(path, toRead, pageCount, allScanned, progress, ct);
        var ocrLetters = ocr.Texts.Values.Sum(DocumentFileTypes.CountLetters);

        if (ocrLetters == 0)
        {
            // OCR is unavailable or found nothing: a PDF with real text pages is still worth reading.
            if (!allScanned)
            {
                var missing = ocr.Error.Length > 0 ? ocr.Error : "OCR found no text on them.";
                var pagesNotice = scanned.Count == 1
                    ? $"Page {scanned[0]} looks scanned and could not be read: {missing}"
                    : $"Pages {DocumentFileTypes.FormatPageRanges(scanned)} look scanned and could not be read: {missing}";
                return CreateResult(path, $"[{pagesNotice}]\n\n{text}", "") with { Notice = pagesNotice };
            }

            if (ocr.Error.Length > 0)
                return Fail(path, "This PDF is scanned (its pages are pictures). " + ocr.Error, ocr.Problem);
            return Fail(path, "No readable text was found. This PDF looks scanned, and OCR found no text on its pages.", DocumentReadProblem.NoTextInImage);
        }

        // Each page keeps whichever has more text: its text layer or its OCR.
        var lastPage = Math.Max(pageCount, ocr.Texts.Keys.DefaultIfEmpty(0).Max());
        var merged = new List<(int Page, string Text)>();
        for (var page = 1; page <= lastPage; page++)
        {
            var layer = readable && page <= pages.Count ? pages[page - 1] : "";
            var best = ocr.Texts.TryGetValue(page, out var read) && DocumentFileTypes.CountLetters(read) > DocumentFileTypes.CountLetters(layer)
                ? read
                : layer;
            merged.Add((page, best));
        }

        var ocrPagesRead = ocr.Texts.Count;
        var notice = allScanned
            ? DocumentFileTypes.BuildOcrPageNotice(ocrPagesRead, pageCount, maxOcrPages)
            : DocumentFileTypes.BuildScannedPagesNotice(ocrPagesRead, scanned.Skip(maxOcrPages).ToList(), maxOcrPages);
        // Put the page coverage in the text too, so the model (and the phone, which only keeps
        // Text) knows which pages were not read.
        var body = OcrText.JoinPages(merged);
        return CreateResult(path, string.IsNullOrWhiteSpace(notice) ? body : $"[{notice}]\n\n{body}", "") with { Notice = notice };
    }

    /// <summary>
    /// Each page's text layer as lines in reading order, laid out by position (PdfPageText), with the
    /// values of a fillable form placed beside their labels; and whether each page's printed text is
    /// so thin that the page is a scan for OCR.
    /// </summary>
    private static (List<string> Pages, List<bool> NeedsOcr, int PageCount) ExtractPdfPages(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var pages = new List<string>();
        var needsOcr = new List<bool>();
        using var stream = OpenShared(path);
        using var document = PdfDocument.Open(stream);
        var reader = new PdfPageText(document, path);
        for (var number = 1; number <= document.NumberOfPages; number++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (text, scan) = reader.Read(document.GetPage(number));
                pages.Add(text);
                needsOcr.Add(scan);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable page: OCR may still read it.
                AppLog.Info($"PdfPig could not read page {number} of {Path.GetFileName(path)}: {ex.Message}");
                pages.Add("");
                needsOcr.Add(true);
            }
        }

        return (pages, needsOcr, document.NumberOfPages);
    }

    private sealed record PdfOcrResult(Dictionary<int, string> Texts, string Error, DocumentReadProblem Problem);

    /// <summary>
    /// OCRs the given pages: with Windows OCR when it has a language, else with Poppler and
    /// Tesseract when they are installed.
    /// </summary>
    private static async Task<PdfOcrResult> OcrPdfPagesAsync(
        string path,
        IReadOnlyList<int> pages,
        int pageCount,
        bool allScanned,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report(allScanned && pageCount > pages.Count
            ? $"rendering the first {pages.Count} of {pageCount} scanned pages for OCR..."
            : "rendering scanned pages for OCR...");

        var engine = WindowsOcr.TryCreateEngine(out var error);
        var problem = DocumentReadProblem.NeedsOcr;
        if (engine != null)
        {
            try
            {
                var texts = await WindowsOcr.RecognizePdfPagesAsync(engine, path, pages, progress, ct);
                return new PdfOcrResult(texts, "", DocumentReadProblem.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (WindowsOcr.IsPasswordError(ex))
                    return new PdfOcrResult(new Dictionary<int, string>(), DocumentFileTypes.GetPasswordMessage(path), DocumentReadProblem.PasswordProtected);

                AppLog.Warn($"Windows could not render {Path.GetFileName(path)} for OCR.", ex);
                error = $"Windows could not render its pages for OCR ({ex.Message}).";
                problem = DocumentReadProblem.Failed;
            }
        }

        // Fallback: Poppler + Tesseract, when they are installed.
        var poppler = await OcrPdfPagesWithTesseractAsync(path, pages, progress, ct);
        if (poppler.Texts.Count > 0)
            return poppler;
        return new PdfOcrResult(poppler.Texts, error, problem);
    }

    private static async Task<PdfOcrResult> OcrPdfPagesWithTesseractAsync(
        string path,
        IReadOnlyList<int> pages,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var texts = new Dictionary<int, string>();
        var pdftoppm = GetPdftoppmPath();
        var tesseract = GetTesseractPath();
        if (string.IsNullOrWhiteSpace(pdftoppm) || !File.Exists(pdftoppm))
            return new PdfOcrResult(texts, "OCR unavailable: could not find pdftoppm.exe from Poppler.", DocumentReadProblem.NeedsOcr);
        if (string.IsNullOrWhiteSpace(tesseract) || !File.Exists(tesseract))
            return new PdfOcrResult(texts, "OCR unavailable: could not find tesseract.exe.", DocumentReadProblem.NeedsOcr);

        var toolDirectories = new[] { pdftoppm, tesseract }
            .Select(Path.GetDirectoryName)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tempDir = Path.Combine(Path.GetTempPath(), "VoiceChatbot", "document-ocr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            for (var i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"OCR page {i + 1} of {pages.Count}...");
                var page = pages[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
                var outputPrefix = Path.Combine(tempDir, "page-" + page);
                var render = await RunProcessAsync(
                    pdftoppm,
                    ["-f", page, "-l", page, "-r", OcrText.PdfRenderDpi.ToString(System.Globalization.CultureInfo.InvariantCulture), "-png", "-singlefile", path, outputPrefix],
                    tempDir,
                    toolDirectories,
                    ct);
                var image = outputPrefix + ".png";
                if (render.ExitCode != 0 || !File.Exists(image))
                {
                    if (texts.Count == 0 && i == 0)
                        return new PdfOcrResult(texts, "OCR render failed: " + render.Error.Trim(), DocumentReadProblem.Failed);
                    continue;
                }

                var ocr = await RunProcessAsync(tesseract, [image, "stdout", "-l", "eng", "--psm", "6"], tempDir, toolDirectories, ct);
                texts[pages[i]] = NormalizeText(ocr.Output);
            }

            return new PdfOcrResult(texts, "", DocumentReadProblem.None);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    public static string BuildContext(IEnumerable<DocumentTextResult> documents, string question = "")
    {
        var parts = documents
            .Select(d => (Document: d, Text: BuildRelevantText(d, question)))
            .Where(d => !string.IsNullOrWhiteSpace(d.Text))
            .Select(d => $"Document: {Path.GetFileName(d.Document.Path)}\nPath: {d.Document.Path}\nText:\n{d.Text}")
            .ToList();

        return parts.Count == 0
            ? ""
            : "Attached document context for this response. Use this document text as the authoritative source when answering questions about the attachment.\n\n" +
              string.Join("\n\n---\n\n", parts);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        IReadOnlyCollection<string> toolDirectories,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        AddToolDirectoriesToPath(start, toolDirectories);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        process.Start();
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch
        {
            // Cancelled (or failed): WaitForExitAsync only stops waiting, so end pdftoppm/tesseract
            // and anything they started instead of leaving them running in the background.
            TryKill(process);
            throw;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private static void AddToolDirectoriesToPath(ProcessStartInfo start, IReadOnlyCollection<string> toolDirectories)
    {
        if (toolDirectories.Count == 0)
            return;

        var currentPath = start.Environment.TryGetValue("PATH", out var path)
            ? path
            : Environment.GetEnvironmentVariable("PATH") ?? "";
        start.Environment["PATH"] = string.Join(Path.PathSeparator, toolDirectories) + Path.PathSeparator + currentPath;
    }

    private static string GetPdftoppmPath()
    {
        if (!OperatingSystem.IsWindows())
            return "pdftoppm";

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packageRoot = Path.Combine(
            localAppData,
            "Microsoft",
            "WinGet",
            "Packages",
            "oschwartz10612.Poppler_Microsoft.Winget.Source_8wekyb3d8bbwe");
        var exact = Path.Combine(packageRoot, "poppler-25.07.0", "Library", "bin", "pdftoppm.exe");
        return File.Exists(exact)
            ? exact
            : FindFirstFile(packageRoot, "pdftoppm.exe");
    }

    private static string GetTesseractPath()
    {
        if (!OperatingSystem.IsWindows())
            return "tesseract";

        var exact = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Tesseract-OCR",
            "tesseract.exe");
        return File.Exists(exact)
            ? exact
            : FindFirstFile(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "tesseract.exe");
    }

    private static string FindFirstFile(string root, string pattern)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.GetFiles(root, pattern, SearchOption.AllDirectories).FirstOrDefault() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static DocumentTextResult Fail(string path, string error, DocumentReadProblem problem) =>
        new(path, "", string.IsNullOrWhiteSpace(error) ? "No readable text was found." : error) { Problem = problem };

    private static DocumentTextResult CreateResult(string path, string text, string error)
    {
        var normalized = NormalizeText(text);
        return new DocumentTextResult(path, LimitText(normalized), error)
        {
            FullText = normalized
        };
    }

    private static string LimitText(string text)
    {
        if (text.Length <= MaxDocumentContextChars)
            return text;

        return text[..MaxDocumentContextChars] + "\n\n[Document text truncated for the normal chat context. Exact section lookups still use the full extracted document text.]";
    }

    private static string BuildRelevantText(DocumentTextResult document, string question)
    {
        var fullText = string.IsNullOrWhiteSpace(document.FullText) ? document.Text : document.FullText;
        if (string.IsNullOrWhiteSpace(fullText))
            return "";
        if (fullText.Length <= MaxDocumentContextChars)
            return fullText;

        if (TryFindSectionText(fullText, question, out var sectionTitle, out var sectionText))
        {
            var sectionBlock = $"{sectionTitle}\n{sectionText}".Trim();
            if (sectionBlock.Length <= MaxDocumentContextChars)
                return sectionBlock;

            if (AsksForLast(question))
                return sectionBlock[^MaxDocumentContextChars..] +
                       "\n\n[Document context focused on the end of the requested section.]";
            if (AsksForFirst(question))
                return sectionBlock[..MaxDocumentContextChars] +
                       "\n\n[Document context focused on the beginning of the requested section.]";

            var half = MaxDocumentContextChars / 2;
            return sectionBlock[..half] +
                   "\n\n[Middle of requested section omitted to fit chat context.]\n\n" +
                   sectionBlock[^half..];
        }

        var head = MaxDocumentContextChars / 2;
        var tail = MaxDocumentContextChars - head;
        return fullText[..head] +
               "\n\n[Middle of long document omitted to fit chat context. Ask about a section/chapter number or title for a focused excerpt.]\n\n" +
               fullText[^tail..];
    }

    public static bool TryAnswerExactSentenceQuestion(string question, IEnumerable<DocumentTextResult> documents, out string answer)
    {
        answer = "";
        if (!AsksForFirst(question) && !AsksForLast(question))
            return false;

        foreach (var document in documents)
        {
            var fullText = string.IsNullOrWhiteSpace(document.FullText) ? document.Text : document.FullText;
            if (!TryFindSectionText(fullText, question, out var sectionTitle, out var sectionText))
                continue;

            var sentences = ExtractSentences(sectionText);
            if (sentences.Count == 0)
                continue;

            var sentence = AsksForLast(question) ? sentences[^1] : sentences[0];
            var position = AsksForLast(question) ? "last" : "first";
            answer = $"The {position} sentence of {sectionTitle} is:\n\n{sentence}";
            return true;
        }

        return false;
    }

    private static bool TryFindSectionText(string text, string question, out string sectionTitle, out string sectionText)
    {
        sectionTitle = "";
        sectionText = "";
        if (string.IsNullOrWhiteSpace(text) || !TryGetRequestedSectionId(question, out var requested))
            return false;

        var matches = Regex.Matches(
            text,
            @"(?m)^\s*(?<id>[IVXLCDM]+|\d+)[\.\)]\s+(?<title>[^\n]{1,160})\s*$",
            RegexOptions.IgnoreCase);
        string bestTitle = "";
        string bestText = "";
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            if (!SectionIdsMatch(match.Groups["id"].Value, requested))
                continue;

            var start = match.Index + match.Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var candidateTitle = $"{match.Groups["id"].Value.Trim()}. {match.Groups["title"].Value.Trim()}";
            var candidateText = text[start..end].Trim();
            if (candidateText.Length > bestText.Length)
            {
                bestTitle = candidateTitle;
                bestText = candidateText;
            }
        }

        if (bestText.Length < 100)
            return false;

        sectionTitle = bestTitle;
        sectionText = bestText;
        return true;
    }

    private static bool TryGetRequestedSectionId(string question, out string sectionId)
    {
        sectionId = "";
        var match = Regex.Match(
            question,
            @"\b(?:section|chapter|part|story)\s+(?<id>[ivxlcdm]+|\d+)\b",
            RegexOptions.IgnoreCase);
        if (!match.Success)
            return false;

        sectionId = match.Groups["id"].Value.Trim();
        return sectionId.Length > 0;
    }

    private static bool SectionIdsMatch(string headingId, string requestedId)
    {
        headingId = headingId.Trim();
        requestedId = requestedId.Trim();
        if (headingId.Equals(requestedId, StringComparison.OrdinalIgnoreCase))
            return true;

        var headingNumber = RomanToInt(headingId);
        var requestedNumber = RomanToInt(requestedId);
        return headingNumber > 0 && requestedNumber > 0 && headingNumber == requestedNumber;
    }

    private static int RomanToInt(string value)
    {
        if (int.TryParse(value, out var number))
            return number;

        var map = new Dictionary<char, int>
        {
            ['I'] = 1, ['V'] = 5, ['X'] = 10, ['L'] = 50,
            ['C'] = 100, ['D'] = 500, ['M'] = 1000
        };
        var total = 0;
        var previous = 0;
        foreach (var c in value.ToUpperInvariant().Reverse())
        {
            if (!map.TryGetValue(c, out var current))
                return -1;

            if (current < previous)
                total -= current;
            else
            {
                total += current;
                previous = current;
            }
        }

        return total;
    }

    private static bool AsksForFirst(string question)
    {
        return Regex.IsMatch(question, @"\b(first|opening|beginning|starts?|start)\b", RegexOptions.IgnoreCase);
    }

    private static bool AsksForLast(string question)
    {
        return Regex.IsMatch(question, @"\b(last|final|ending|ends?|end)\b", RegexOptions.IgnoreCase);
    }

    private static List<string> ExtractSentences(string text)
    {
        var cleaned = NormalizeText(text);
        return Regex.Matches(
                cleaned,
                @"(?s)(?:[""'“‘]?\b[A-Z0-9][^.!?]{20,}?[.!?][""'”’]?)(?=\s+[""'“‘]?[A-Z0-9]|\s*$)")
            .Select(m => Regex.Replace(m.Value.Trim(), @"\s+", " "))
            .Where(s => !Regex.IsMatch(s, @"^(Illustration|Image|Figure)\b", RegexOptions.IgnoreCase))
            .ToList();
    }

    private static string NormalizeText(string text)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, @"[ \t]+", " ");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static bool LooksLikeReadableText(string text)
    {
        var normalized = NormalizeText(text);
        if (normalized.Length < 80)
            return true;

        var letters = normalized.Count(char.IsLetter);
        if (letters < normalized.Length * 0.25)
            return false;

        var words = Regex.Matches(normalized.ToLowerInvariant(), @"\b[\p{L}\p{N}']+\b")
            .Select(m => m.Value)
            .Take(500)
            .ToList();
        if (words.Count < 20)
            return false;

        var topWordShare = words
            .GroupBy(w => w)
            .Select(g => (double)g.Count() / words.Count)
            .DefaultIfEmpty(0)
            .Max();
        if (topWordShare > 0.25)
            return false;

        var repeatedShortRuns = Regex.IsMatch(
            normalized,
            @"\b([\p{L}\p{N}']{1,12})(?:\s+\1\b){8,}",
            RegexOptions.IgnoreCase);

        return !repeatedShortRuns;
    }
}

public sealed record DocumentTextResult(string Path, string Text, string Error)
{
    public string FullText { get; init; } = Text;

    /// <summary>Something the user should know about a successful read, such as OCR page coverage.</summary>
    public string Notice { get; init; } = "";

    /// <summary>Why there is no text (None when the read worked), for summaries such as "3 photos without text".</summary>
    public DocumentReadProblem Problem { get; init; } = string.IsNullOrWhiteSpace(Text) && !string.IsNullOrWhiteSpace(Error)
        ? DocumentReadProblem.Failed
        : DocumentReadProblem.None;
}
