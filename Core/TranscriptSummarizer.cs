using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

/// <summary>
/// Runs transcript summaries and live-notes updates through a "send this request to the chat model" callback,
/// splitting text that is too long for one request. Shared by the desktop Live Transcriber and the web version.
/// <para>
/// The awaits keep the caller's context (no ConfigureAwait(false)), so a callback started on the UI thread
/// continues there and may touch the UI.
/// </para>
/// </summary>
public static class TranscriptSummarizer
{
    /// <summary>Text up to this many characters goes to the model in one request.</summary>
    public const int SinglePassLimit = 24_000;

    /// <summary>Longer text is split into parts of about this many characters.</summary>
    public const int PartLength = 12_000;

    /// <summary>True when <paramref name="text"/> is too long for one request.</summary>
    public static bool NeedsSplitting(string? text, int limit = SinglePassLimit) => (text?.Trim().Length ?? 0) > limit;

    /// <summary>
    /// Splits a transcript into parts of at most <paramref name="partLength"/> characters, cutting only between
    /// lines (so each "[mm:ss]" line stays whole). A single line longer than that is cut between words.
    /// Blank lines are dropped; the parts keep the lines in order and lose no other text.
    /// </summary>
    public static IReadOnlyList<string> SplitIntoParts(string? text, int partLength = PartLength)
    {
        if (partLength < 1)
            throw new ArgumentOutOfRangeException(nameof(partLength));

        var parts = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
                parts.Add(current.ToString());
            current.Clear();
        }

        foreach (var rawLine in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (line.Trim().Length == 0)
                continue;

            foreach (var piece in SplitLongLine(line, partLength))
            {
                var needed = current.Length == 0 ? piece.Length : current.Length + 1 + piece.Length;
                if (needed > partLength)
                    Flush();
                if (current.Length > 0)
                    current.Append('\n');
                current.Append(piece);
            }
        }

        Flush();
        return parts;
    }

    /// <summary>
    /// Groups consecutive notes so each group's joined text stays within <paramref name="maxChars"/> where it can.
    /// Every group has at least two notes (the last one may have one when the count is odd and nothing fits),
    /// so combining the groups always shrinks the list.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> GroupForCombining(IReadOnlyList<string> notes, int maxChars = PartLength)
    {
        var groups = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        var length = 0;

        foreach (var note in notes)
        {
            if (current.Count >= 2 && length + note.Length > maxChars)
            {
                groups.Add(current);
                current = new List<string>();
                length = 0;
            }
            current.Add(note);
            length += note.Length;
        }

        if (current.Count > 0)
        {
            // A lone last note joins the group before it rather than being "combined" on its own.
            if (current.Count == 1 && groups.Count > 0)
                groups[^1] = groups[^1].Append(current[0]).ToList();
            else
                groups.Add(current);
        }
        return groups;
    }

    /// <summary>
    /// The full Summarize: one request when the transcript fits, otherwise notes on each part followed by a
    /// request that combines them into the chosen style. Parts whose reply is empty are left out.
    /// </summary>
    public static async Task<string> SummarizeAsync(
        string? transcript,
        string? style,
        Func<TranscriptSummaryRequest, CancellationToken, Task<string>> summarize,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var text = (transcript ?? "").Trim();
        if (text.Length == 0)
            return "";

        if (!NeedsSplitting(text))
            return TranscriptSummaryPrompts.StripPreamble(await summarize(TranscriptSummaryPrompts.Summary(text, style), ct));

        var parts = SplitIntoParts(text);
        var notes = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Summarizing part {i + 1} of {parts.Count}...");
            var note = TranscriptSummaryPrompts.StripPreamble(
                await summarize(TranscriptSummaryPrompts.Part(parts[i], i + 1, parts.Count, style), ct));
            if (note.Length > 0)
                notes.Add(note);
        }

        if (notes.Count == 0)
            return "";

        notes = await CombineInGroupsAsync(notes, style, summarize, progress, ct);
        if (notes.Count == 0)
            return "";

        ct.ThrowIfCancellationRequested();
        progress?.Report($"Combining the notes from {parts.Count} parts...");
        return TranscriptSummaryPrompts.StripPreamble(await summarize(TranscriptSummaryPrompts.Combine(notes, style), ct));
    }

    /// <summary>
    /// Very long sessions: while the notes together are longer than one request allows, consecutive notes are
    /// combined in groups (<see cref="GroupForCombining"/>). Returns notes that fit (or a single one), in order;
    /// empty when every combined reply was empty.
    /// </summary>
    public static async Task<List<string>> CombineInGroupsAsync(
        IReadOnlyList<string> notes,
        string? style,
        Func<TranscriptSummaryRequest, CancellationToken, Task<string>> summarize,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var current = notes.ToList();
        while (current.Count > 2 && current.Sum(n => n.Length) > SinglePassLimit)
        {
            var groups = GroupForCombining(current);
            var combined = new List<string>();
            for (var g = 0; g < groups.Count; g++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Combining notes ({g + 1} of {groups.Count})...");
                var merged = TranscriptSummaryPrompts.StripPreamble(
                    await summarize(TranscriptSummaryPrompts.Combine(groups[g], style, isFinal: false), ct));
                if (merged.Length > 0)
                    combined.Add(merged);
            }

            current = combined;
        }

        return current;
    }

    /// <summary>
    /// Live notes as the older web page asks for them (the transcribers now use <see cref="TranscriptNotesWriter"/>):
    /// merges <paramref name="newText"/> (only what was said since the last update) into
    /// <paramref name="currentNotes"/> and returns the full updated notes. New text that is too long for one
    /// request is merged one part at a time. Throws when the model returns nothing, so the caller keeps the
    /// previous notes.
    /// </summary>
    public static async Task<string> UpdateNotesAsync(
        string? currentNotes,
        string? newText,
        string? style,
        Func<TranscriptSummaryRequest, CancellationToken, Task<string>> summarize,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var notes = (currentNotes ?? "").Trim();
        var text = (newText ?? "").Trim();
        if (text.Length == 0)
            return notes;

        var parts = NeedsSplitting(text) ? SplitIntoParts(text) : new[] { text };
        for (var i = 0; i < parts.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (parts.Count > 1)
                progress?.Report($"Updating notes (part {i + 1} of {parts.Count})...");

            var request = TranscriptSummaryPrompts.LiveNotes(notes, parts[i], style, isFinal: i == parts.Count - 1);
            var updated = TranscriptSummaryPrompts.StripPreamble(await summarize(request, ct));
            if (updated.Length == 0)
                throw new InvalidOperationException("The model returned no notes.");
            notes = updated;
        }

        return notes;
    }

    // A line longer than the part length is cut at the last space that fits, or hard when there is none.
    private static IEnumerable<string> SplitLongLine(string line, int partLength)
    {
        var rest = line;
        while (rest.Length > partLength)
        {
            var cut = rest.LastIndexOf(' ', partLength);
            if (cut <= 0)
                cut = partLength;
            var piece = rest[..cut].TrimEnd();
            if (piece.Length > 0)
                yield return piece;
            rest = rest[cut..].TrimStart();
        }

        if (rest.Length > 0)
            yield return rest;
    }
}
