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

    /// <summary>File name for an automatically saved session, e.g. transcript_20261008_143005.md.</summary>
    public static string AutoSaveFileName(DateTime sessionStart) => $"transcript_{sessionStart:yyyyMMdd_HHmmss}.md";

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

/// <summary>What the transcriber's Summarize button asks the chat model for.</summary>
public static class TranscriptSummaryStyles
{
    public const string Summary = "Summary";
    public const string ActionItems = "Action items";
    public const string MeetingNotes = "Meeting notes";
    public const string KeyPoints = "Key points";

    public static IReadOnlyList<string> Names { get; } = new[] { Summary, ActionItems, MeetingNotes, KeyPoints };

    /// <summary>The known style with this name (ignoring case), or <see cref="Summary"/>.</summary>
    public static string Normalize(string? name) =>
        Names.FirstOrDefault(n => string.Equals(n, name?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Summary;

    /// <summary>The request sent with the transcript for this style.</summary>
    public static string GetInstruction(string? name) => Normalize(name) switch
    {
        ActionItems =>
            "List the action items in this live transcript as a checklist. For each one give the task, who owns it and any " +
            "due date when the transcript says so. Then list open questions. If there are no action items, say so. Do not invent details.",
        MeetingNotes =>
            "Write meeting notes for this live transcript with these sections: Overview (two or three sentences), " +
            "Discussion (the main topics), Decisions, Action items (with owners and dates when stated) and Open questions. " +
            "Leave out a section that has nothing in it. Do not invent details.",
        KeyPoints =>
            "List the key points of this live transcript as short bullet points, most important first, " +
            "keeping names, dates and numbers exact. Do not invent details.",
        _ =>
            "Summarize this live transcript. Include important facts, decisions, action items, questions, names, dates, and numbers. " +
            "Keep it concise but useful for the main assistant to reference later.",
    };
}
