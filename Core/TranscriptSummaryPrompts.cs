using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>Which step of a transcript summary a request is.</summary>
public enum TranscriptSummaryKind
{
    /// <summary>The whole transcript in one request.</summary>
    Summary,
    /// <summary>Notes on one part of a long transcript.</summary>
    Part,
    /// <summary>Combining the notes on the parts of a long transcript into the chosen style.</summary>
    Combine,
    /// <summary>Detailed notes on one stretch of a live transcript: a "[start–end]" section of the notes.</summary>
    SectionNotes,
    /// <summary>The brief "SUMMARY SO FAR" at the top of the notes, written from the section notes.</summary>
    SummarySoFar,
}

/// <summary>
/// One request to the chat model: <see cref="UserMessage"/> is the complete user message (instruction plus
/// text). <see cref="IsFinal"/> is true for the request whose reply is the finished result.
/// </summary>
public sealed record TranscriptSummaryRequest(TranscriptSummaryKind Kind, string Style, string UserMessage)
{
    public bool IsFinal { get; init; } = true;

    /// <summary>Part of a live-notes update while recording (not a summary the user asked for).</summary>
    public bool IsLiveUpdate =>
        Kind is TranscriptSummaryKind.SectionNotes or TranscriptSummaryKind.SummarySoFar;
}

/// <summary>
/// Builds the prompts for transcript summaries and live notes. Pure text, so the desktop Live Transcriber
/// and the web version send the same requests.
/// </summary>
public static class TranscriptSummaryPrompts
{
    /// <summary>System message used when the transcriber's System message box is empty.</summary>
    public const string DefaultSystemPrompt =
        "You are a live transcript summarizer. Preserve important context and do not invent details.";

    private const string NoPreamble =
        "Return only the result, with no preamble, no explanation and no closing remarks.";

    // "Here are the updated notes:", "Sure! Here is the summary:", "Updated notes:" on a line of their own.
    private static readonly Regex PreambleLine = new(
        @"^\s*(?:(?:sure|okay|ok|certainly|of course)\b[^\n]{0,40}?[.!,]?\s*)?" +
        @"(?:(?:here\s+(?:is|are)\b[^\n]{0,80})|(?:(?:the\s+)?(?:full\s+)?updated\s+(?:live\s+)?notes))\s*:\s*\n",
        RegexOptions.IgnoreCase);

    /// <summary>Instruction, a blank line, then "LABEL:" and the text.</summary>
    public static string ComposeUserMessage(string instruction, string label, string? text) =>
        $"{instruction.Trim()}\n\n{label}:\n{(text ?? "").Trim()}";

    /// <summary>
    /// The whole transcript in one request, in the style's own words. <paramref name="earlierNotes"/> is text a
    /// user wrote in the notes before they were kept by time; it is sent along so the summary covers it too.
    /// </summary>
    public static TranscriptSummaryRequest Summary(string? transcript, string? style, string? earlierNotes = null)
    {
        var name = TranscriptSummaryStyles.Normalize(style);
        var instruction = TranscriptSummaryStyles.GetInstruction(name);
        if (string.IsNullOrWhiteSpace(earlierNotes))
            return new TranscriptSummaryRequest(TranscriptSummaryKind.Summary, name, ComposeUserMessage(instruction, "TRANSCRIPT", transcript));

        instruction += " The EARLIER NOTES were written on this session before; include what matters from them too.";
        var message = $"{instruction.Trim()}\n\nEARLIER NOTES:\n{earlierNotes.Trim()}\n\nTRANSCRIPT:\n{(transcript ?? "").Trim()}";
        return new TranscriptSummaryRequest(TranscriptSummaryKind.Summary, name, message);
    }

    /// <summary>
    /// Detailed notes on one stretch of a live transcript (only the text said in it): the body of the section
    /// labeled <paramref name="label"/> ("05:12–10:20" or "Part 3"). A stretch too long for one request is sent
    /// in parts (<paramref name="part"/> of <paramref name="parts"/>), whose notes are joined.
    /// </summary>
    public static TranscriptSummaryRequest SectionNotes(string? text, string? label, string? style, int part = 1, int parts = 1)
    {
        var name = TranscriptSummaryStyles.Normalize(style);
        var range = (label ?? "").Trim();
        var what = TranscriptNotes.TryParseRange(range, out var start, out var end)
            ? $"Below is what was said from {LiveTranscriptText.FormatTimestamp(start)} to {LiveTranscriptText.FormatTimestamp(end)} of a live transcript"
            : range.Length > 0 ? $"Below is {range.ToLowerInvariant()} of a live transcript" : "Below is a stretch of a live transcript";
        if (parts > 1)
            what += $" (piece {part} of {parts} of it)";

        var instruction =
            $"{what}. Write detailed notes on this text only, as bullet points starting with \"- \": a bullet for every " +
            "topic, fact, decision, action item (with the owner and due date when they are stated), question, name, date " +
            "and number, in the order they came up. Usually write 3 to 10 bullets, depending on how much was said. " +
            "Never compress it into one sentence. If it was only small talk, write one bullet saying so. " +
            "Keep names, dates and numbers exact and do not invent details. " +
            "Return only the bullets, with no heading, no preamble and no closing remarks.";

        var heading = range.Length > 0 ? $"TRANSCRIPT {range}" : "TRANSCRIPT";
        if (parts > 1)
            heading += $" (PIECE {part} OF {parts})";
        return new TranscriptSummaryRequest(TranscriptSummaryKind.SectionNotes, name, ComposeUserMessage(instruction, heading, text))
        {
            IsFinal = false
        };
    }

    /// <summary>
    /// The brief summary at the top of the live notes ("SUMMARY SO FAR"), written in the style's format from the
    /// section notes (each "[start–end]" line with its bullets), not from the transcript. <paramref name="earlierNotes"/>
    /// is text a user wrote in the notes before they were kept by time.
    /// </summary>
    public static TranscriptSummaryRequest SummarySoFar(IReadOnlyList<string> sectionNotes, string? style, string? earlierNotes = null)
    {
        var name = TranscriptSummaryStyles.Normalize(style);
        var hasEarlier = !string.IsNullOrWhiteSpace(earlierNotes);
        var instruction =
            "Below are notes by time on a live transcript that is still going on, in order" +
            (hasEarlier ? ", after EARLIER NOTES written on it before" : "") + ". " +
            $"Write a brief summary of the session so far, written as {TranscriptSummaryStyles.GetFormat(name)}. " +
            "Keep it brief: the main topics, decisions, action items and open questions so far, not a copy of the notes. " +
            "Keep names, dates and numbers exact and do not invent details. " +
            "Return only the summary, with no heading, no preamble and no closing remarks.";

        var body = string.Join("\n\n", sectionNotes.Select(n => n.Trim()).Where(n => n.Length > 0));
        var message = hasEarlier
            ? $"{instruction}\n\nEARLIER NOTES:\n{earlierNotes!.Trim()}\n\nSECTION NOTES:\n{body}"
            : ComposeUserMessage(instruction, "SECTION NOTES", body);
        return new TranscriptSummaryRequest(TranscriptSummaryKind.SummarySoFar, name, message);
    }

    /// <summary>Notes on part <paramref name="number"/> of <paramref name="count"/> of a long transcript.</summary>
    public static TranscriptSummaryRequest Part(string? part, int number, int count, string? style)
    {
        var name = TranscriptSummaryStyles.Normalize(style);
        var instruction =
            $"This is part {number} of {count} of a long live transcript. Write detailed notes on this part only: " +
            "the topics, facts, decisions, action items (with owners and due dates when stated), open questions, " +
            "names, dates and numbers, in the order they came up. " +
            $"The notes on all parts will later be combined into {Describe(name)}, so keep everything that could matter for it. " +
            "Do not invent details. " + NoPreamble;
        return new TranscriptSummaryRequest(
            TranscriptSummaryKind.Part, name, ComposeUserMessage(instruction, $"TRANSCRIPT PART {number} OF {count}", part))
        {
            IsFinal = false
        };
    }

    /// <summary>Combines the notes on consecutive parts (in order) into one result in the chosen style.</summary>
    public static TranscriptSummaryRequest Combine(IReadOnlyList<string> partNotes, string? style, bool isFinal = true)
    {
        var name = TranscriptSummaryStyles.Normalize(style);
        var instruction =
            $"Below are notes on {partNotes.Count} consecutive parts of one long live transcript, in order. " +
            $"Combine them into one result written as {TranscriptSummaryStyles.GetFormat(name)}. " +
            "Merge points that repeat across parts, keep the order of events, and keep names, dates and numbers exact. " +
            "Do not invent details. " + NoPreamble;

        var body = new StringBuilder();
        for (var i = 0; i < partNotes.Count; i++)
        {
            if (i > 0)
                body.Append("\n\n");
            body.Append("Part ").Append(i + 1).Append(":\n").Append(partNotes[i].Trim());
        }

        return new TranscriptSummaryRequest(
            TranscriptSummaryKind.Combine, name, ComposeUserMessage(instruction, "PART NOTES", body.ToString()))
        {
            IsFinal = isFinal
        };
    }

    /// <summary>
    /// Removes a lead-in line such as "Here are the updated notes:" that models add despite being asked not to,
    /// and trims. Everything else is left as it is.
    /// </summary>
    public static string StripPreamble(string? reply)
    {
        var text = (reply ?? "").Replace("\r\n", "\n").Trim();
        var match = PreambleLine.Match(text + "\n");
        if (match.Success && match.Index == 0 && match.Length < text.Length)
            text = text[match.Length..].Trim();
        return text;
    }

    // "a summary", "action items", "meeting notes", "key points".
    private static string Describe(string style) =>
        style == TranscriptSummaryStyles.Summary ? "a summary" : style.ToLowerInvariant();
}
