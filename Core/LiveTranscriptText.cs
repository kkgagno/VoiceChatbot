using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>Text helpers for the Live Transcriber: timestamps, word counts and the saved document.</summary>
public static class LiveTranscriptText
{
    private static readonly Regex TimestampPrefix = new(@"^[ \t]*\[\d{1,3}:\d{2}(?::\d{2})?\][ \t]?", RegexOptions.Multiline);
    private static readonly Regex Word = new(@"\b[\w']+\b");

    /// <summary>"03:07" under an hour, "1:02:03" from an hour on. Negative times count as zero.</summary>
    public static string FormatTimestamp(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            elapsed = TimeSpan.Zero;

        var totalHours = (int)elapsed.TotalHours;
        return totalHours > 0
            ? $"{totalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    /// <summary>
    /// One transcribed chunk as the transcript shows it: sentences Whisper repeated are removed and whitespace is
    /// collapsed to single spaces. Used by the desktop Live Transcriber and the web transcriber's chunk endpoint.
    /// </summary>
    public static string CleanChunk(string? text) => CollapseWhitespace(TranscriptCleanup.CollapseRepeatedSentences(text));

    /// <summary>"[mm:ss] text" for one transcribed chunk, or "" for blank text.</summary>
    public static string FormatLine(TimeSpan elapsed, string? text)
    {
        var clean = CollapseWhitespace(text);
        return clean.Length == 0 ? "" : $"[{FormatTimestamp(elapsed)}] {clean}";
    }

    /// <summary>The transcript without the "[mm:ss]" line prefixes.</summary>
    public static string StripTimestamps(string? transcript) => TimestampPrefix.Replace(transcript ?? "", "");

    /// <summary>Spoken words in the transcript; the "[mm:ss]" prefixes are not counted.</summary>
    public static int CountWords(string? transcript) => Word.Matches(StripTimestamps(transcript)).Count;

    /// <summary>Title of the saved document.</summary>
    public const string DocumentTitle = "Live transcript";

    /// <summary>File name for an automatically saved session, e.g. transcript_20261008_143005.md.</summary>
    public static string AutoSaveFileName(DateTime sessionStart) => $"transcript_{sessionStart:yyyyMMdd_HHmmss}.md";

    /// <summary>Suggested name for "Save..." (or a browser download), e.g. transcript_20261008_1430.md or .txt.</summary>
    public static string ExportFileName(DateTime sessionStart, bool markdown = true) =>
        $"transcript_{sessionStart:yyyyMMdd_HHmm}{(markdown ? ".md" : ".txt")}";

    /// <summary>True unless the file name ends in .txt: Save writes Markdown for every other extension.</summary>
    public static bool IsMarkdownFileName(string? fileName) =>
        !(fileName ?? "").Trim().EndsWith(".txt", StringComparison.OrdinalIgnoreCase);

    /// <summary>Content type for serving the saved document (web download).</summary>
    public static string ExportContentType(bool markdown) =>
        markdown ? "text/markdown; charset=utf-8" : "text/plain; charset=utf-8";

    /// <summary>
    /// The saved document: title, date (and length), the summary when there is one, then the transcript.
    /// Markdown puts a blank line between lines so each transcript line stays its own paragraph when rendered.
    /// </summary>
    public static string BuildDocument(
        string title,
        DateTime date,
        TimeSpan? length,
        string? transcript,
        string? summary,
        string? summaryHeading = null,
        bool markdown = true)
    {
        var sb = new StringBuilder();
        var lineBreak = markdown ? "\n\n" : "\n";
        AppendHeading(sb, title, 1, markdown);

        var details = $"{date:dddd, d MMMM yyyy HH:mm}";
        if (length is { } span && span > TimeSpan.Zero)
            details += $" · {FormatTimestamp(span)} recorded";
        sb.Append(markdown ? $"_{details}_" : details).Append("\n\n");

        var summaryLines = Lines(summary);
        if (summaryLines.Count > 0)
        {
            AppendHeading(sb, string.IsNullOrWhiteSpace(summaryHeading) ? "Summary" : summaryHeading.Trim(), 2, markdown);
            sb.Append(string.Join(lineBreak, summaryLines)).Append("\n\n");
        }

        AppendHeading(sb, "Transcript", 2, markdown);
        var transcriptLines = Lines(transcript);
        sb.Append(transcriptLines.Count > 0 ? string.Join(lineBreak, transcriptLines) : "(empty)").Append('\n');
        return sb.ToString();
    }

    private static void AppendHeading(StringBuilder sb, string text, int level, bool markdown)
    {
        if (markdown)
        {
            sb.Append('#', level).Append(' ').Append(text).Append("\n\n");
            return;
        }

        sb.Append(text).Append('\n').Append(level == 1 ? '=' : '-', Math.Max(3, text.Length)).Append("\n\n");
    }

    private static List<string> Lines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Trim().Length > 0)
            .ToList();

    private static string CollapseWhitespace(string? text) =>
        Regex.Replace(text ?? "", @"\s+", " ").Trim();
}
