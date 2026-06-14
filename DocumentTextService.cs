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
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace VoiceChatbot;

public sealed class DocumentTextService
{
    private const int MaxDocumentContextChars = 240000;

    public async Task<DocumentTextResult> ExtractAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return new DocumentTextResult(path, "", "File not found.");

        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".pdf")
                return await ExtractPdfDocumentAsync(path, ct);

            var text = ext switch
            {
                ".docx" => ExtractDocx(path),
                ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".log" => await File.ReadAllTextAsync(path, ct),
                ".doc" => "",
                _ => ""
            };

            if (ext == ".doc")
                return new DocumentTextResult(path, "", "Old .doc files are not supported yet. Save it as .docx and attach that.");
            if (string.IsNullOrWhiteSpace(text))
                return new DocumentTextResult(path, "", "No readable text was found. If this is a scanned PDF, it will need OCR.");

            return CreateResult(path, text, "");
        }
        catch (Exception ex)
        {
            return new DocumentTextResult(path, "", ex.Message);
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

    private static string ExtractDocx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var parts = archive.Entries
            .Where(e =>
                e.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.StartsWith("word/header", StringComparison.OrdinalIgnoreCase) ||
                e.FullName.StartsWith("word/footer", StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

        var output = new StringBuilder();
        foreach (var part in parts)
        {
            using var stream = part.Open();
            var xml = XDocument.Load(stream);
            foreach (var node in xml.Descendants())
            {
                var name = node.Name.LocalName;
                if (name == "t")
                    output.Append(node.Value);
                else if (name is "tab")
                    output.Append('\t');
                else if (name is "br" or "cr" or "p")
                    output.AppendLine();
            }
        }

        return NormalizeText(output.ToString());
    }

    private static async Task<DocumentTextResult> ExtractPdfDocumentAsync(string path, CancellationToken ct)
    {
        var text = ExtractPdfText(path, ct);
        if (!string.IsNullOrWhiteSpace(text) && LooksLikeReadableText(text))
            return CreateResult(path, text, "");

        var ocr = await ExtractPdfWithOcrAsync(path, ct);
        if (!string.IsNullOrWhiteSpace(ocr.Text) && LooksLikeReadableText(ocr.Text))
            return CreateResult(path, ocr.Text, "");

        var error = string.IsNullOrWhiteSpace(ocr.Error)
            ? "No readable text was found. If this is a scanned PDF, OCR did not produce usable text."
            : ocr.Error;
        return new DocumentTextResult(path, "", error);
    }

    private static string ExtractPdfText(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var output = new StringBuilder();
        using var document = PdfDocument.Open(path);
        foreach (var page in document.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            output.AppendLine(page.Text);
            output.AppendLine();
        }

        return NormalizeText(output.ToString());
    }

    private static async Task<(string Text, string Error)> ExtractPdfWithOcrAsync(string path, CancellationToken ct)
    {
        var pdftoppm = GetPdftoppmPath();
        var tesseract = GetTesseractPath();
        if (string.IsNullOrWhiteSpace(pdftoppm) || !File.Exists(pdftoppm))
            return ("", "OCR unavailable: could not find pdftoppm.exe from Poppler.");
        if (string.IsNullOrWhiteSpace(tesseract) || !File.Exists(tesseract))
            return ("", "OCR unavailable: could not find tesseract.exe.");

        var tempDir = Path.Combine(Path.GetTempPath(), "VoiceChatbot", "document-ocr", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var outputPrefix = Path.Combine(tempDir, "page");
            var render = await RunProcessAsync(
                pdftoppm,
                ["-f", "1", "-l", "8", "-r", "200", "-png", path, outputPrefix],
                tempDir,
                ct);
            if (render.ExitCode != 0)
                return ("", "OCR render failed: " + render.Error);

            var images = Directory.GetFiles(tempDir, "page-*.png")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (images.Count == 0)
                return ("", "OCR render failed: Poppler did not produce page images.");

            var output = new StringBuilder();
            foreach (var image in images)
            {
                ct.ThrowIfCancellationRequested();
                var ocr = await RunProcessAsync(
                    tesseract,
                    [image, "stdout", "-l", "eng", "--psm", "6"],
                    tempDir,
                    ct);

                if (!string.IsNullOrWhiteSpace(ocr.Output))
                {
                    output.AppendLine(ocr.Output);
                    output.AppendLine();
                }
            }

            return (NormalizeText(output.ToString()), "");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
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

        AddKnownOcrToolDirectoriesToPath(start);
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static void AddKnownOcrToolDirectoriesToPath(ProcessStartInfo start)
    {
        var paths = new[] { GetPdftoppmPath(), GetTesseractPath() }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(Path.GetDirectoryName)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var currentPath = start.Environment.TryGetValue("PATH", out var path)
            ? path
            : Environment.GetEnvironmentVariable("PATH") ?? "";
        start.Environment["PATH"] = string.Join(Path.PathSeparator, paths) + Path.PathSeparator + currentPath;
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
}
