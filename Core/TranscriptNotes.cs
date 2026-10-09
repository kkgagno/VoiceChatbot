using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// One section under "NOTES BY TIME": the notes on what was said in one stretch of the session. <see cref="Label"/>
/// is "00:00–05:12" (shown as "[00:00–05:12]") or "Part 2" for a transcript without timestamps; it is empty only
/// for text a user wrote between "NOTES BY TIME" and the first section.
/// </summary>
public sealed record TranscriptNotesSection(string Label, string Body);

/// <summary>
/// The Live Transcriber's notes pane, shared by the desktop window and the web page. Plain text laid out as:
/// <code>
/// SUMMARY SO FAR            (after Stop or Re-summarize all: the style's heading, e.g. ACTION ITEMS)
/// &lt;top block&gt;
///
/// NOTES BY TIME
/// [00:00–05:12]
/// - bullet ...
/// [05:12–10:20]
/// - bullet ...
/// </code>
/// <see cref="Parse"/> tolerates edits: everything before the "NOTES BY TIME" line is the top block, every
/// "[start–end]" (or "[Part n]") line starts a section, and any other text stays in the section it is in.
/// Text without this layout (older notes, or notes a user typed) is kept verbatim as the top block.
/// </summary>
public sealed class TranscriptNotes
{
    public const string SummarySoFarHeading = "SUMMARY SO FAR";
    public const string NotesByTimeHeading = "NOTES BY TIME";

    /// <summary>The dash between the two times of a section label.</summary>
    public const string RangeDash = "–";

    private const string Time = @"\d{1,3}:\d{2}(?::\d{2})?";
    private static readonly Regex HeaderLine = new(
        $@"^\s*\[\s*(?:(?<start>{Time})\s*[–—-]\s*(?<end>{Time})|(?<part>Part\s+(?<number>\d{{1,4}})))\s*\]\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex OtherBullet = new(@"^(\s*)[•*+·–—](?:\s+|$)", RegexOptions.CultureInvariant);
    private static readonly Regex ExtraBlankLines = new(@"\n{3,}", RegexOptions.CultureInvariant);

    public static TranscriptNotes Empty { get; } = new(null, "", Array.Empty<TranscriptNotesSection>(), isStructured: true);

    public TranscriptNotes(string? heading, string? top, IEnumerable<TranscriptNotesSection>? sections = null)
        : this(heading, top, sections, isStructured: true)
    {
    }

    private TranscriptNotes(string? heading, string? top, IEnumerable<TranscriptNotesSection>? sections, bool isStructured)
    {
        Heading = string.IsNullOrWhiteSpace(heading) ? null : heading.Trim();
        Top = Normalize(top).Trim();
        Sections = (sections ?? Array.Empty<TranscriptNotesSection>())
            .Select(s => new TranscriptNotesSection((s.Label ?? "").Trim(), TrimBlankLines(Normalize(s.Body))))
            .Where(s => s.Label.Length > 0 || s.Body.Length > 0)
            .ToList();
        IsStructured = isStructured;
    }

    /// <summary>"SUMMARY SO FAR" or a style heading such as "ACTION ITEMS"; null when the top block has none (text a user wrote).</summary>
    public string? Heading { get; }

    /// <summary>The top block without its heading line.</summary>
    public string Top { get; }

    public IReadOnlyList<TranscriptNotesSection> Sections { get; }

    /// <summary>False for non-empty text without the layout, which is kept verbatim as <see cref="Top"/>.</summary>
    public bool IsStructured { get; }

    public bool IsEmpty => Heading == null && Top.Length == 0 && Sections.Count == 0;

    /// <summary>True when at least one "[start–end]" or "[Part n]" section is there.</summary>
    public bool HasSections => Sections.Any(s => s.Label.Length > 0);

    /// <summary>A top block without a known heading: text a user wrote (or older notes). Never thrown away silently.</summary>
    public bool TopIsUserText => Heading == null && Top.Length > 0;

    /// <summary>True when the top block is a full summary in <paramref name="style"/> (written on Stop or by Re-summarize all).</summary>
    public bool HasFullSummary(string? style) =>
        Top.Length > 0 && string.Equals(Heading, TranscriptSummaryStyles.GetHeading(style), StringComparison.Ordinal);

    /// <summary>The headings recognized on the first line of the top block.</summary>
    public static IReadOnlyList<string> KnownHeadings { get; } =
        new[] { SummarySoFarHeading }.Concat(TranscriptSummaryStyles.Names.Select(TranscriptSummaryStyles.GetHeading)).ToList();

    /// <summary>Reads the notes pane. Never throws; any text ends up in the top block or a section.</summary>
    public static TranscriptNotes Parse(string? text)
    {
        var lines = Normalize(text).Split('\n');
        var notesByTime = Array.FindIndex(lines, IsNotesByTimeLine);
        var topEnd = notesByTime >= 0 ? notesByTime : lines.Length;

        // The heading is the first non-blank line, when it is one of the known headings.
        var first = 0;
        while (first < topEnd && lines[first].Trim().Length == 0)
            first++;
        var heading = first < topEnd ? AsKnownHeading(lines[first]) : null;

        if (notesByTime < 0 && heading == null)
        {
            var whole = Normalize(text).Trim();
            return whole.Length == 0 ? Empty : new TranscriptNotes(null, whole, null, isStructured: false);
        }

        var topStart = heading != null ? first + 1 : first;
        var top = string.Join("\n", lines[topStart..topEnd]);

        var sections = new List<TranscriptNotesSection>();
        if (notesByTime >= 0)
        {
            string? label = "";
            var body = new List<string>();
            void Flush()
            {
                if (label != null)
                    sections.Add(new TranscriptNotesSection(label, string.Join("\n", body)));
                body.Clear();
            }

            foreach (var line in lines[(notesByTime + 1)..])
            {
                if (TryReadHeader(line, out var next))
                {
                    Flush();
                    label = next;
                    continue;
                }
                body.Add(line);
            }
            Flush();
        }

        return new TranscriptNotes(heading, top, sections, isStructured: true);
    }

    /// <summary>The notes as the pane shows them (lines separated by "\n").</summary>
    public string Render()
    {
        if (!IsStructured)
            return Top;

        var lines = new List<string>();
        if (Heading != null)
            lines.Add(Heading);
        if (Top.Length > 0)
            lines.Add(Top);

        if (Sections.Count > 0)
        {
            if (lines.Count > 0)
                lines.Add("");
            lines.Add(NotesByTimeHeading);
            foreach (var section in Sections)
            {
                if (section.Label.Length > 0)
                    lines.Add($"[{section.Label}]");
                if (section.Body.Length > 0)
                    lines.Add(section.Body);
            }
        }

        return string.Join("\n", lines);
    }

    public override string ToString() => Render();

    /// <summary>The same sections under a new top block.</summary>
    public TranscriptNotes WithTop(string? heading, string? top) => new(heading, top, Sections, isStructured: true);

    /// <summary>
    /// The notes with <paramref name="section"/> added at the end. Earlier sections are never changed, and text
    /// without the layout stays verbatim as the top block.
    /// </summary>
    public TranscriptNotes AddSection(TranscriptNotesSection section) =>
        new(Heading, Top, Sections.Append(section), isStructured: true);

    /// <summary>The sections as the model gets them: each "[label]" line followed by its notes.</summary>
    public IReadOnlyList<string> SectionTexts() =>
        Sections
            .Where(s => s.Body.Length > 0)
            .Select(s => s.Label.Length > 0 ? $"[{s.Label}]\n{s.Body}" : s.Body)
            .ToList();

    /// <summary>"00:00–05:12" (h:mm:ss from an hour on, like the transcript lines). An end before the start becomes the start.</summary>
    public static string FormatRange(TimeSpan start, TimeSpan end)
    {
        if (start < TimeSpan.Zero)
            start = TimeSpan.Zero;
        if (end < start)
            end = start;
        return LiveTranscriptText.FormatTimestamp(start) + RangeDash + LiveTranscriptText.FormatTimestamp(end);
    }

    /// <summary>"Part 3": the label of a section of a transcript without timestamps.</summary>
    public static string PartLabel(int number) => $"Part {number}";

    /// <summary>Reads the times of a "00:00–05:12" label; false for "Part n" and anything else.</summary>
    public static bool TryParseRange(string? label, out TimeSpan start, out TimeSpan end)
    {
        start = end = TimeSpan.Zero;
        var match = HeaderLine.Match($"[{label}]");
        return match.Success && match.Groups["start"].Success &&
               LiveTranscriptText.TryParseTimestamp(match.Groups["start"].Value, out start) &&
               LiveTranscriptText.TryParseTimestamp(match.Groups["end"].Value, out end);
    }

    /// <summary>True for a section header line ("[00:00–05:12]" or "[Part 2]"); <paramref name="label"/> is the text inside the brackets.</summary>
    public static bool TryReadHeader(string? line, out string label)
    {
        label = "";
        var match = HeaderLine.Match(line ?? "");
        if (!match.Success)
            return false;

        label = match.Groups["part"].Success
            ? PartLabel(int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture))
            : match.Groups["start"].Value + RangeDash + match.Groups["end"].Value;
        return true;
    }

    /// <summary>
    /// A model reply used as a section's notes: no lead-in line, no blank lines, "- " bullets (also for "•", "*"
    /// and dash bullets) and no lines that would break the layout ("NOTES BY TIME" or a section header).
    /// </summary>
    public static string CleanSectionBody(string? reply)
    {
        var lines = TranscriptSummaryPrompts.StripPreamble(reply).Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Trim().Length > 0 && !IsLayoutLine(line))
            .Select(line => OtherBullet.Replace(line, "$1- "))
            .ToList();
        return string.Join("\n", lines).Trim();
    }

    /// <summary>
    /// A model reply used as the top block: no lead-in, no heading the model repeated, no lines that would break
    /// the layout, and at most one blank line in a row.
    /// </summary>
    public static string CleanTop(string? reply)
    {
        var lines = TranscriptSummaryPrompts.StripPreamble(reply).Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => !IsLayoutLine(line))
            .ToList();

        var first = lines.FindIndex(line => line.Trim().Length > 0);
        if (first >= 0 && AsKnownHeading(lines[first].Trim().Trim('#', '*', ' ')) != null)
            lines.RemoveAt(first);

        return ExtraBlankLines.Replace(string.Join("\n", lines), "\n\n").Trim();
    }

    /// <summary>
    /// The heading of the notes in the saved document: "Notes" when they have this layout (they carry their own
    /// headings), otherwise the style's name, as before.
    /// </summary>
    public static string DocumentHeading(string? notes, string? style)
    {
        var parsed = Parse(notes);
        return parsed.IsStructured && !parsed.IsEmpty ? "Notes" : TranscriptSummaryStyles.Normalize(style);
    }

    private static bool IsNotesByTimeLine(string line) =>
        string.Equals(line.Trim().TrimEnd(':').Trim(), NotesByTimeHeading, StringComparison.OrdinalIgnoreCase);

    private static bool IsLayoutLine(string line) => IsNotesByTimeLine(line) || TryReadHeader(line, out _);

    private static string? AsKnownHeading(string line)
    {
        var text = line.Trim().TrimEnd(':').Trim();
        return KnownHeadings.FirstOrDefault(h => string.Equals(h, text, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    // Leading and trailing blank lines go; trailing spaces on each line go; inner blank lines stay.
    private static string TrimBlankLines(string text)
    {
        var lines = text.Split('\n').Select(line => line.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0)
            lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }
}
