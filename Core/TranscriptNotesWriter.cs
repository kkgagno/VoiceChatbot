using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

/// <summary>
/// The outcome of a live-notes update or of the notes written on Stop. <see cref="Notes"/> is the new text for the
/// notes pane. When the summary at the top could not be written (<see cref="SummaryError"/>), the new section is
/// still there and the previous top block is kept.
/// </summary>
public sealed record TranscriptNotesResult(string Notes, bool SectionAdded, Exception? SummaryError = null)
{
    public bool SummaryFailed => SummaryError != null;
}

/// <summary>One stretch of a transcript for Re-summarize all: the section label and the lines said in it.</summary>
public sealed record TranscriptTimeWindow(string Label, string Text);

/// <summary>
/// Writes the Live Transcriber's notes (layout in <see cref="TranscriptNotes"/>) through a "send this request to
/// the chat model" callback. Shared by the desktop window and the web transcriber:
/// <list type="bullet">
/// <item><see cref="UpdateAsync"/>: a live update while recording. Notes on only the new text become a new section;
/// then the "SUMMARY SO FAR" at the top is rewritten from all section notes.</item>
/// <item><see cref="FinishAsync"/>: on Stop. A last section for the remaining text, then a full summary in the
/// chosen style at the top.</item>
/// <item><see cref="RebuildAsync"/>: Summarize / Re-summarize all. Fresh notes from the whole transcript.</item>
/// </list>
/// Existing sections are never rewritten. The awaits keep the caller's context (no ConfigureAwait(false)), like
/// <see cref="TranscriptSummarizer"/>.
/// </summary>
public static class TranscriptNotesWriter
{
    /// <summary>The bullet a section gets when the model wrote nothing for it during Re-summarize all.</summary>
    public const string NoNotesBullet = "- (The chat model wrote no notes for this part.)";

    /// <summary>
    /// A live update: one request writes detailed notes on <paramref name="newText"/> (only what was said since the
    /// last update), added as a section from its first "[mm:ss]" time to <paramref name="elapsed"/>; a second
    /// request rewrites the "SUMMARY SO FAR" from all section notes. Throws when the first request fails or returns
    /// nothing (the caller keeps the previous notes); when only the second fails, the result has the new section,
    /// the old top block and <see cref="TranscriptNotesResult.SummaryError"/>. Notes without the layout (older
    /// notes or text a user typed) are kept verbatim as the top block on this first update.
    /// </summary>
    public static async Task<TranscriptNotesResult> UpdateAsync(
        string? currentNotes,
        string? newText,
        TimeSpan elapsed,
        string? style,
        Func<TranscriptSummaryRequest, CancellationToken, Task<string>> summarize,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var notes = TranscriptNotes.Parse(currentNotes);
        var text = (newText ?? "").Trim();
        if (text.Length == 0)
            return new TranscriptNotesResult(notes.Render(), SectionAdded: false);

        var keepUserText = !notes.IsStructured;
        notes = await AddSectionAsync(notes, text, elapsed, style, summarize, progress, ct);
        if (keepUserText)
            return new TranscriptNotesResult(notes.Render(), SectionAdded: true);

        try
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report("Updating the summary so far...");
            var top = await WriteSummarySoFarAsync(notes, style, summarize, progress, ct);
            if (top.Length == 0)
                throw new InvalidOperationException("The model returned an empty summary.");
            return new TranscriptNotesResult(notes.WithTop(TranscriptNotes.SummarySoFarHeading, top).Render(), SectionAdded: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new TranscriptNotesResult(notes.Render(), SectionAdded: true, ex);
        }
    }

    /// <summary>
    /// On Stop, after the last chunks are transcribed: a final section for <paramref name="newText"/> (when there is
    /// any), then the top block becomes a full summary of <paramref name="transcript"/> in the chosen style, under
    /// the style's heading. A transcript up to <see cref="TranscriptSummarizer.SinglePassLimit"/> characters is sent
    /// as it is; a longer one is summarized from the section notes. Errors as in <see cref="UpdateAsync"/>.
    /// </summary>
    public static async Task<TranscriptNotesResult> FinishAsync(
        string? currentNotes,
        string? newText,
        string? transcript,
        TimeSpan elapsed,
        string? style,
        Func<TranscriptSummaryRequest, CancellationToken, Task<string>> summarize,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var notes = TranscriptNotes.Parse(currentNotes);
        var text = (newText ?? "").Trim();
        var keepUserText = !notes.IsStructured;
        var added = false;
        if (text.Length > 0)
        {
            notes = await AddSectionAsync(notes, text, elapsed, style, summarize, progress, ct);
            added = true;
        }

        // Text without the layout is kept verbatim (nothing the user wrote is replaced without asking).
        if (keepUserText || (transcript ?? "").Trim().Length == 0)
            return new TranscriptNotesResult(notes.Render(), added);

        try
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report("Writing the summary...");
            var top = await WriteFullSummaryAsync(transcript, notes, style, summarize, progress, ct);
            if (top.Length == 0)
                throw new InvalidOperationException("The model returned an empty summary.");
            return new TranscriptNotesResult(notes.WithTop(TranscriptSummaryStyles.GetHeading(style), top).Render(), added);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new TranscriptNotesResult(notes.Render(), added, ex);
        }
    }

    /// <summary>
    /// Summarize / Re-summarize all: fresh notes from the whole transcript. It is split into windows of
    /// <paramref name="window"/> by the "[mm:ss]" line times (<see cref="SplitIntoWindows"/>), each window gets
    /// section notes (progress "Section 3 of 12..."), then the top block is a full summary as in
    /// <see cref="FinishAsync"/> ("Writing the summary..."). Returns the new notes text, or "" for an empty
    /// transcript. Throws on errors and cancellation; the caller keeps the previous notes.
    /// </summary>
    public static async Task<string> RebuildAsync(
        string? transcript,
        string? style,
        TimeSpan window,
        TimeSpan? sessionLength,
        Func<TranscriptSummaryRequest, CancellationToken, Task<string>> summarize,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var text = (transcript ?? "").Trim();
        if (text.Length == 0)
            return "";

        var windows = SplitIntoWindows(text, window, sessionLength);
        var sections = new List<TranscriptNotesSection>();
        for (var i = 0; i < windows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Section {i + 1} of {windows.Count}...");
            var body = await WriteSectionNotesAsync(windows[i].Text, windows[i].Label, style, summarize, ct);
            sections.Add(new TranscriptNotesSection(windows[i].Label, body.Length > 0 ? body : NoNotesBullet));
        }

        var notes = new TranscriptNotes(null, null, sections);
        ct.ThrowIfCancellationRequested();
        progress?.Report("Writing the summary...");
        var top = await WriteFullSummaryAsync(text, notes, style, summarize, progress, ct);
        if (top.Length == 0)
            throw new InvalidOperationException("The model returned an empty summary.");
        return notes.WithTop(TranscriptSummaryStyles.GetHeading(style), top).Render();
    }

    /// <summary>
    /// Notes on one stretch of transcript (the body of a section, "- " bullets). Text too long for one request is
    /// sent in pieces and their notes are joined. "" when the model wrote nothing.
    /// </summary>
    public static async Task<string> WriteSectionNotesAsync(
        string? text,
        string? label,
        string? style,
        Func<TranscriptSummaryRequest, CancellationToken, Task<string>> summarize,
        CancellationToken ct = default)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0)
            return "";

        var pieces = TranscriptSummarizer.NeedsSplitting(trimmed) ? TranscriptSummarizer.SplitIntoParts(trimmed) : new[] { trimmed };
        var bodies = new List<string>();
        for (var i = 0; i < pieces.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var reply = await summarize(TranscriptSummaryPrompts.SectionNotes(pieces[i], label, style, i + 1, pieces.Count), ct);
            var body = TranscriptNotes.CleanSectionBody(reply);
            if (body.Length > 0)
                bodies.Add(body);
        }
        return string.Join("\n", bodies);
    }

    /// <summary>
    /// The label of the section for text said since the last update: from its first "[mm:ss]" time (or, without
    /// one, the end of the last section) to <paramref name="elapsed"/> (or the latest time in it, when later).
    /// </summary>
    public static string RangeFor(string? newText, TimeSpan elapsed, TranscriptNotes? notes = null)
    {
        var start = LiveTranscriptText.FirstTimestamp(newText);
        if (start == null)
        {
            var last = notes?.Sections.LastOrDefault(s => TranscriptNotes.TryParseRange(s.Label, out _, out _));
            start = last != null && TranscriptNotes.TryParseRange(last.Label, out _, out var lastEnd) ? lastEnd : TimeSpan.Zero;
        }

        var end = elapsed;
        if (LiveTranscriptText.LastTimestamp(newText) is { } latest && latest > end)
            end = latest;
        return TranscriptNotes.FormatRange(start.Value, end);
    }

    /// <summary>
    /// Splits a transcript into windows of <paramref name="window"/> by the "[mm:ss]" line times: a window is
    /// labeled by its bounds ("05:00–10:00"); the last one ends at <paramref name="sessionLength"/> or its last
    /// line, whichever is later. Lines without a time stay with the line before them (at the start: with the first
    /// window). Windows nobody spoke in are left out. A transcript without any times is split by size
    /// (<see cref="TranscriptSummarizer.SplitIntoParts"/>) into "Part 1", "Part 2"... Blank lines are dropped.
    /// </summary>
    public static IReadOnlyList<TranscriptTimeWindow> SplitIntoWindows(string? transcript, TimeSpan window, TimeSpan? sessionLength = null)
    {
        if (window <= TimeSpan.Zero)
            window = TimeSpan.FromMinutes(LiveNotesPolicy.DefaultIntervalMinutes);

        var lines = (transcript ?? "").Replace("\r\n", "\n").Split('\n')
            .Select(line => line.TrimEnd())
            .Where(line => line.Trim().Length > 0)
            .ToList();

        if (!lines.Any(line => LiveTranscriptText.TryReadLineTimestamp(line, out _)))
        {
            return TranscriptSummarizer.SplitIntoParts(string.Join("\n", lines))
                .Select((part, i) => new TranscriptTimeWindow(TranscriptNotes.PartLabel(i + 1), part))
                .ToList();
        }

        var groups = new List<(long Index, TimeSpan Last, List<string> Lines)>();
        var lead = new List<string>();
        foreach (var line in lines)
        {
            if (LiveTranscriptText.TryReadLineTimestamp(line, out var at))
            {
                var index = at.Ticks / window.Ticks;
                if (groups.Count == 0 || groups[^1].Index != index)
                {
                    groups.Add((index, at, new List<string>(lead) { line }));
                    lead.Clear();
                }
                else
                {
                    var current = groups[^1];
                    current.Lines.Add(line);
                    groups[^1] = (current.Index, at > current.Last ? at : current.Last, current.Lines);
                }
                continue;
            }

            if (groups.Count == 0)
                lead.Add(line);
            else
                groups[^1].Lines.Add(line);
        }

        var windows = new List<TranscriptTimeWindow>();
        for (var i = 0; i < groups.Count; i++)
        {
            var (index, last, groupLines) = groups[i];
            var start = TimeSpan.FromTicks(index * window.Ticks);
            var end = i < groups.Count - 1
                ? start + window
                : sessionLength is { } length && length > last ? length : last;
            windows.Add(new TranscriptTimeWindow(TranscriptNotes.FormatRange(start, end), string.Join("\n", groupLines)));
        }
        return windows;
    }

    // Section notes on the new text, added as a new section at the end. Throws when the model wrote nothing.
    private static async Task<TranscriptNotes> AddSectionAsync(
        TranscriptNotes notes,
        string text,
        TimeSpan elapsed,
        string? style,
        Func<TranscriptSummaryRequest, CancellationToken, Task<string>> summarize,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var range = RangeFor(text, elapsed, notes);
        progress?.Report($"Writing notes on {range}...");
        var body = await WriteSectionNotesAsync(text, range, style, summarize, ct);
        if (body.Length == 0)
            throw new InvalidOperationException("The model returned no notes.");
        return notes.AddSection(new TranscriptNotesSection(range, body));
    }

    // "SUMMARY SO FAR" from the section notes (combined in groups first when they are very long).
    private static async Task<string> WriteSummarySoFarAsync(
        TranscriptNotes notes,
        string? style,
        Func<TranscriptSummaryRequest, CancellationToken, Task<string>> summarize,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var sectionNotes = await TranscriptSummarizer.CombineInGroupsAsync(notes.SectionTexts(), style, summarize, progress, ct);
        if (sectionNotes.Count == 0)
            return "";

        ct.ThrowIfCancellationRequested();
        var earlier = notes.TopIsUserText ? notes.Top : null;
        return TranscriptNotes.CleanTop(await summarize(TranscriptSummaryPrompts.SummarySoFar(sectionNotes, style, earlier), ct));
    }

    // The full summary for the top block: from the transcript when it fits in one request, otherwise from the
    // section notes (or, without any, the transcript in parts).
    private static async Task<string> WriteFullSummaryAsync(
        string? transcript,
        TranscriptNotes notes,
        string? style,
        Func<TranscriptSummaryRequest, CancellationToken, Task<string>> summarize,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var text = (transcript ?? "").Trim();
        var earlier = notes.TopIsUserText ? notes.Top : null;
        if (!TranscriptSummarizer.NeedsSplitting(text))
            return TranscriptNotes.CleanTop(await summarize(TranscriptSummaryPrompts.Summary(text, style, earlier), ct));

        var sectionNotes = notes.SectionTexts().ToList();
        if (sectionNotes.Count == 0)
            return TranscriptNotes.CleanTop(await TranscriptSummarizer.SummarizeAsync(text, style, summarize, progress, ct));

        if (earlier != null)
            sectionNotes.Insert(0, "Earlier notes:\n" + earlier);
        sectionNotes = await TranscriptSummarizer.CombineInGroupsAsync(sectionNotes, style, summarize, progress, ct);
        if (sectionNotes.Count == 0)
            return "";

        ct.ThrowIfCancellationRequested();
        return TranscriptNotes.CleanTop(await summarize(TranscriptSummaryPrompts.Combine(sectionNotes, style), ct));
    }
}
