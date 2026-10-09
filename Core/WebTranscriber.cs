using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VoiceChatbot;

/// <summary>
/// Rules for the web transcriber (the phone remote's /transcribe page, usable from any browser on the LAN):
/// request size limits, the name of the file a session is saved to on the PC, and the values its JavaScript
/// shares with the desktop Live Transcriber (chunking thresholds, summary styles, live-notes timing).
/// </summary>
public static class WebTranscriber
{
    public const string PagePath = "/transcribe";
    public const string ApiPrefix = "/api/transcriber";

    /// <summary>Longest transcript the endpoints accept (characters). About 650 hours of speech, far above a long day.</summary>
    public const int MaxTranscriptChars = 4_000_000;

    /// <summary>Longest notes (summary) the endpoints accept (characters).</summary>
    public const int MaxNotesChars = 400_000;

    /// <summary>Request body limit for one audio chunk: 20 seconds of 16 kHz mono WAV is 640 KB.</summary>
    public const long ChunkMaxBodyBytes = 8L * 1024 * 1024;

    /// <summary>Request body limit for the text endpoints: the longest transcript plus notes as UTF-8 JSON.</summary>
    public const long TextMaxBodyBytes = 16L * 1024 * 1024;

    /// <summary>How far in the future (clock difference between the browser and the PC) a session start may be.</summary>
    public static TimeSpan MaxClockAhead { get; } = TimeSpan.FromMinutes(5);

    /// <summary>The oldest session start that is accepted (a transcript restored after a reload can be days old).</summary>
    public static TimeSpan MaxSessionAge { get; } = TimeSpan.FromDays(30);

    /// <summary>The longest recording time of a session the endpoints accept from the browser.</summary>
    public static TimeSpan MaxSessionLength { get; } = TimeSpan.FromDays(7);

    /// <summary>
    /// The time recorded so far in the browser's session (milliseconds), or null when it is missing, zero or
    /// implausible (negative, or longer than <see cref="MaxSessionLength"/>).
    /// </summary>
    public static TimeSpan? ResolveSessionLength(long? elapsedMs) =>
        elapsedMs is long ms && ms > 0 && ms < (long)MaxSessionLength.TotalMilliseconds
            ? TimeSpan.FromMilliseconds(ms)
            : null;

    /// <summary>
    /// When the session started, for its saved file name and document date: the browser's start time
    /// (Unix milliseconds) when it is plausible, otherwise <paramref name="now"/>. A start slightly in the
    /// future (clocks differ) counts as now. The result has <paramref name="now"/>'s offset.
    /// </summary>
    public static DateTimeOffset ResolveSessionStart(long? startedAtUnixMs, DateTimeOffset now)
    {
        if (startedAtUnixMs is not long ms || ms <= 0)
            return now;

        DateTimeOffset start;
        try
        {
            start = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        catch (ArgumentOutOfRangeException)
        {
            return now;
        }

        if (start > now + MaxClockAhead || start < now - MaxSessionAge)
            return now;
        return start > now ? now : start.ToOffset(now.Offset);
    }

    /// <summary>
    /// The file name for a new web session in the transcripts folder:
    /// <see cref="LiveTranscriptText.AutoSaveFileName"/>, or with "_2", "_3"... before ".md" when that name is
    /// already taken, so a web session never overwrites another session's file. Only digits and the fixed
    /// "transcript_" prefix come from the input; nothing the browser sends becomes part of a path.
    /// </summary>
    public static string NewSaveFileName(DateTime sessionStart, Func<string, bool> isTaken)
    {
        var name = LiveTranscriptText.AutoSaveFileName(sessionStart);
        if (!isTaken(name))
            return name;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{stem}_{n}{extension}";
            if (!isTaken(candidate))
                return candidate;
        }

        return $"{stem}_{Guid.NewGuid():N}{extension}";
    }

    /// <summary>Why the transcript or notes are too long for the web transcriber, or null when both fit.</summary>
    public static string? CheckLength(string? transcript, string? notes)
    {
        if ((transcript?.Length ?? 0) > MaxTranscriptChars)
            return $"The transcript is too long ({transcript!.Length:N0} characters; the limit is {MaxTranscriptChars:N0}). Save it and clear it to start a new one.";
        if ((notes?.Length ?? 0) > MaxNotesChars)
            return $"The notes are too long ({notes!.Length:N0} characters; the limit is {MaxNotesChars:N0}).";
        return null;
    }

    /// <summary>
    /// The settings the page's JavaScript uses, taken from the same Core classes as the desktop Live Transcriber:
    /// the chunking thresholds (<see cref="SpeechChunkerOptions"/>), summary styles, the notes layout's headings
    /// (<see cref="TranscriptNotes"/>), live-notes timing and limits.
    /// </summary>
    public static WebTranscriberConfig CreateConfig()
    {
        var chunk = new SpeechChunkerOptions();
        return new WebTranscriberConfig(
            new WebTranscriberChunkConfig(
                chunk.SampleRate,
                chunk.FrameLength.TotalMilliseconds,
                chunk.SpeechThreshold,
                chunk.MinChunk.TotalMilliseconds,
                chunk.PauseToCut.TotalMilliseconds,
                chunk.MaxChunk.TotalMilliseconds,
                chunk.MinSpeech.TotalMilliseconds,
                chunk.PreRoll.TotalMilliseconds,
                chunk.CutSearch.TotalMilliseconds),
            TranscriptSummaryStyles.Names.ToArray(),
            TranscriptSummaryStyles.Summary,
            new WebTranscriberNotesConfig(
                TranscriptNotes.SummarySoFarHeading,
                TranscriptNotes.NotesByTimeHeading,
                TranscriptSummaryStyles.Names.Select(TranscriptSummaryStyles.GetHeading).ToArray()),
            LiveNotesPolicy.IntervalChoicesMinutes.ToArray(),
            LiveNotesPolicy.DefaultIntervalMinutes,
            LiveNotesPolicy.CheckEvery.TotalMilliseconds,
            LiveNotesPolicy.DefaultMinNewWords,
            TranscriptSummarizer.SinglePassLimit,
            TranscriptSummarizer.PartLength,
            MaxTranscriptChars,
            MaxNotesChars,
            LiveTranscriptText.DocumentTitle,
            PhoneRemotePin.BrowserStorageKey,
            ApiPrefix);
    }

    /// <summary>
    /// <see cref="CreateConfig"/> as JSON (camelCase). The default encoder escapes &lt;, &gt;, &amp; and quotes,
    /// so the text is safe inside an inline script.
    /// </summary>
    public static string ConfigJson() =>
        JsonSerializer.Serialize(CreateConfig(), new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
}

/// <summary>The chunking thresholds of <see cref="SpeechChunkerOptions"/> in milliseconds, for the page's JavaScript chunker.</summary>
public sealed record WebTranscriberChunkConfig(
    int SampleRate,
    double FrameMs,
    double Threshold,
    double MinChunkMs,
    double PauseMs,
    double MaxChunkMs,
    double MinSpeechMs,
    double PreRollMs,
    double CutSearchMs);

/// <summary>
/// The headings of the notes layout (<see cref="TranscriptNotes"/>), so the page can tell notes by time from other
/// text the way the PC does. <see cref="StyleHeadings"/> are in the order of the styles ("SUMMARY", "ACTION ITEMS"...).
/// </summary>
public sealed record WebTranscriberNotesConfig(
    string SummarySoFarHeading,
    string NotesByTimeHeading,
    string[] StyleHeadings);

/// <summary>What the web transcriber page needs to behave like the desktop Live Transcriber.</summary>
public sealed record WebTranscriberConfig(
    WebTranscriberChunkConfig Chunk,
    string[] Styles,
    string DefaultStyle,
    WebTranscriberNotesConfig Notes,
    int[] Intervals,
    int DefaultInterval,
    double CheckEveryMs,
    int MinNewWords,
    int SinglePassLimit,
    int PartLength,
    int MaxTranscriptChars,
    int MaxNotesChars,
    string DocumentTitle,
    string PinStorageKey,
    string Api);
