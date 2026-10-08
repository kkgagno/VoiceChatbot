using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceChatbot;

/// <summary>
/// What the transcriber's Summarize button (and Live notes) asks the chat model for. Shared by the desktop
/// Live Transcriber and the web version: the names are what the style picker shows and settings store.
/// </summary>
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

    /// <summary>The request sent with the whole transcript for this style (a one-pass Summarize).</summary>
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

    /// <summary>
    /// How the result of this style is laid out, without the request itself. Used where the model writes the
    /// style from something other than the whole transcript: live notes and combining the parts of a long one.
    /// </summary>
    public static string GetFormat(string? name) => Normalize(name) switch
    {
        ActionItems =>
            "a checklist of action items, each with the task, who owns it and any due date that was stated, " +
            "followed by a short list of open questions (if there are no action items yet, say so in one line)",
        MeetingNotes =>
            "meeting notes with these sections: Overview (two or three sentences), Discussion (the main topics), Decisions, " +
            "Action items (with owners and dates when stated) and Open questions, leaving out a section that has nothing in it",
        KeyPoints =>
            "short bullet points with the key points, most important first",
        _ =>
            "a concise summary with the important facts, decisions, action items, questions, names, dates and numbers",
    };
}
