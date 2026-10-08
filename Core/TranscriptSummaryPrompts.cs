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
    /// <summary>Merging the words said since the last update into the running live notes.</summary>
    LiveNotes,
}

/// <summary>
/// One request to the chat model: <see cref="UserMessage"/> is the complete user message (instruction plus
/// text). <see cref="IsFinal"/> is true for the request whose reply is the finished result.
/// </summary>
public sealed record TranscriptSummaryRequest(TranscriptSummaryKind Kind, string Style, string UserMessage)
{
    public bool IsFinal { get; init; } = true;
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

    /// <summary>The whole transcript in one request, in the style's own words.</summary>
    public static TranscriptSummaryRequest Summary(string? transcript, string? style)
    {
        var name = TranscriptSummaryStyles.Normalize(style);
        return new TranscriptSummaryRequest(
            TranscriptSummaryKind.Summary,
            name,
            ComposeUserMessage(TranscriptSummaryStyles.GetInstruction(name), "TRANSCRIPT", transcript));
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
    /// Live notes: the current notes plus only the words said since they were last updated. The model returns
    /// the full updated notes, keeping earlier points and merging the new ones in.
    /// </summary>
    public static TranscriptSummaryRequest LiveNotes(string? currentNotes, string? newTranscript, string? style, bool isFinal = true)
    {
        var name = TranscriptSummaryStyles.Normalize(style);
        var notes = string.IsNullOrWhiteSpace(currentNotes) ? "(none yet)" : currentNotes.Trim();
        var instruction =
            $"You keep running notes on a live transcript that is still going on, written as {TranscriptSummaryStyles.GetFormat(name)}. " +
            "Below are the CURRENT NOTES and the NEW TRANSCRIPT, which is only what was said since the notes were last updated. " +
            "Return the full updated notes: keep every earlier point, merge the new information into them " +
            "(update or correct an existing point instead of repeating it) and add new points where they belong. " +
            "If the new transcript adds nothing, return the current notes unchanged. " +
            "Keep names, dates and numbers exact and do not invent details. " +
            "Return only the notes, with no preamble, no explanation of what changed and no closing remarks.";

        var message = $"{instruction}\n\nCURRENT NOTES:\n{notes}\n\nNEW TRANSCRIPT:\n{(newTranscript ?? "").Trim()}";
        return new TranscriptSummaryRequest(TranscriptSummaryKind.LiveNotes, name, message) { IsFinal = isFinal };
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
