using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VoiceChatbot;

/// <summary>Why a live-notes update is or is not due right now.</summary>
public enum LiveNotesCheck
{
    Due,
    Disabled,
    NotRecording,
    AlreadyRunning,
    /// <summary>A manual Summarize is running; live notes never overlap with it.</summary>
    SummaryRunning,
    WaitingForInterval,
    TooFewNewWords,
}

/// <summary>
/// One live-notes update in progress: the transcript as it was when the update started and the part of it
/// the notes do not cover yet. Hand it back to <see cref="LiveNotesPolicy.Complete"/>, <see cref="LiveNotesPolicy.Fail"/>
/// or <see cref="LiveNotesPolicy.Abandon"/>.
/// </summary>
public sealed class LiveNotesTicket
{
    internal LiveNotesTicket(int generation, string transcript, string newText, int newWords, bool isFinal)
    {
        Generation = generation;
        Transcript = transcript;
        NewText = newText;
        NewWords = newWords;
        IsFinal = isFinal;
    }

    public int Generation { get; }
    public string Transcript { get; }
    public string NewText { get; }
    public int NewWords { get; }
    /// <summary>The last update after recording stopped.</summary>
    public bool IsFinal { get; }
}

/// <summary>
/// Decides when the running notes of a live transcript are updated, and which text is new since the last update.
/// While recording, an update is due when live notes are on, the interval has passed since recording started or
/// the last update, at least <see cref="MinNewWords"/> words were added, and neither another update nor a
/// Summarize / Re-summarize all is running ("Update notes now" skips the interval and word checks). After Stop the
/// final notes are written when live notes are on or the notes already have sections (see <see cref="CheckFinal"/>).
/// Not thread-safe: use it from one thread (the UI thread). Times are whatever clock the caller uses consistently
/// (UTC recommended).
/// </summary>
public sealed class LiveNotesPolicy
{
    public const int DefaultMinNewWords = 40;
    public const int DefaultIntervalMinutes = 5;

    /// <summary>The interval picker's choices, in minutes. Five minutes is the shortest.</summary>
    public static IReadOnlyList<int> IntervalChoicesMinutes { get; } = new[] { 5, 10, 15 };

    /// <summary>How often the caller should ask <see cref="TryBegin"/> while recording.</summary>
    public static TimeSpan CheckEvery { get; } = TimeSpan.FromSeconds(15);

    private string _processed = "";
    private DateTime _clockStart = DateTime.MinValue;
    private int _generation;
    private LiveNotesTicket? _running;

    public bool Enabled { get; set; }

    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(DefaultIntervalMinutes);

    public int MinNewWords { get; init; } = DefaultMinNewWords;

    public bool IsRunning => _running != null;

    /// <summary>The transcript text the current notes cover (empty before the first update).</summary>
    public string ProcessedTranscript => _processed;

    /// <summary>Starts the interval again from <paramref name="now"/> (recording started).</summary>
    public void RestartClock(DateTime now) => _clockStart = now;

    /// <summary>
    /// The text added to <paramref name="transcript"/> since the notes were last updated. When the transcript was
    /// edited so it no longer starts with the covered text, the new text starts at the line where the covered
    /// text ended (a few words may be sent twice; none are skipped).
    /// </summary>
    public string GetNewText(string? transcript)
    {
        var text = transcript ?? "";
        if (_processed.Length == 0)
            return text.Trim();
        if (text.StartsWith(_processed, StringComparison.Ordinal))
            return text[_processed.Length..].Trim();

        var offset = Math.Min(_processed.Length, text.Length);
        var lineStart = offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;
        return text[lineStart..].Trim();
    }

    /// <summary>Spoken words added since the last update ("[mm:ss]" prefixes are not counted).</summary>
    public int CountNewWords(string? transcript) => LiveTranscriptText.CountWords(GetNewText(transcript));

    /// <summary>Whether a regular update is due while recording, and if not, why.</summary>
    public LiveNotesCheck Check(DateTime now, bool recording, bool summaryRunning, string? transcript)
    {
        if (!Enabled)
            return LiveNotesCheck.Disabled;
        if (!recording)
            return LiveNotesCheck.NotRecording;
        if (IsRunning)
            return LiveNotesCheck.AlreadyRunning;
        if (summaryRunning)
            return LiveNotesCheck.SummaryRunning;
        if (now - _clockStart < Interval)
            return LiveNotesCheck.WaitingForInterval;
        if (CountNewWords(transcript) < Math.Max(1, MinNewWords))
            return LiveNotesCheck.TooFewNewWords;
        return LiveNotesCheck.Due;
    }

    /// <summary>
    /// Whether the notes are finished after Stop (a last section for the new words, then a full summary at the top;
    /// see <see cref="TranscriptNotesWriter.FinishAsync"/>): when live notes are on or <paramref name="notes"/> already
    /// has sections, nothing else is running and the transcript has words. With no new words it is due only while the
    /// top is not yet a full summary in <paramref name="style"/>. Notes without the layout (text a user wrote) are
    /// only due with new words, since their text is kept.
    /// </summary>
    public LiveNotesCheck CheckFinal(bool summaryRunning, string? transcript, string? notes = null, string? style = null)
    {
        var parsed = TranscriptNotes.Parse(notes);
        if (!Enabled && !parsed.HasSections)
            return LiveNotesCheck.Disabled;
        if (IsRunning)
            return LiveNotesCheck.AlreadyRunning;
        if (summaryRunning)
            return LiveNotesCheck.SummaryRunning;
        if (LiveTranscriptText.CountWords(transcript) == 0)
            return LiveNotesCheck.TooFewNewWords;
        if (CountNewWords(transcript) == 0 && (!parsed.IsStructured || parsed.HasFullSummary(style)))
            return LiveNotesCheck.TooFewNewWords;
        return LiveNotesCheck.Due;
    }

    /// <summary>
    /// "Update notes now": due whenever nothing else is running and at least one word was added, whatever the
    /// interval, the live-notes switch or recording.
    /// </summary>
    public LiveNotesCheck CheckNow(bool summaryRunning, string? transcript)
    {
        if (IsRunning)
            return LiveNotesCheck.AlreadyRunning;
        if (summaryRunning)
            return LiveNotesCheck.SummaryRunning;
        if (CountNewWords(transcript) == 0)
            return LiveNotesCheck.TooFewNewWords;
        return LiveNotesCheck.Due;
    }

    /// <summary>Starts a regular update when one is due; otherwise returns null.</summary>
    public LiveNotesTicket? TryBegin(DateTime now, bool recording, bool summaryRunning, string? transcript) =>
        Check(now, recording, summaryRunning, transcript) == LiveNotesCheck.Due ? Begin(transcript, isFinal: false) : null;

    /// <summary>Starts the final notes after Stop when they are due (see <see cref="CheckFinal"/>); otherwise returns null.</summary>
    public LiveNotesTicket? TryBeginFinal(bool summaryRunning, string? transcript, string? notes = null, string? style = null) =>
        CheckFinal(summaryRunning, transcript, notes, style) == LiveNotesCheck.Due ? Begin(transcript, isFinal: true) : null;

    /// <summary>Starts an "Update notes now" update when one is possible (see <see cref="CheckNow"/>); otherwise returns null.</summary>
    public LiveNotesTicket? TryBeginNow(bool summaryRunning, string? transcript) =>
        CheckNow(summaryRunning, transcript) == LiveNotesCheck.Due ? Begin(transcript, isFinal: false) : null;

    /// <summary>True while <paramref name="ticket"/> is the update in progress (not reset or finished since).</summary>
    public bool IsCurrent(LiveNotesTicket ticket) => ReferenceEquals(_running, ticket) && ticket.Generation == _generation;

    /// <summary>
    /// The update succeeded and its notes were shown: the notes now cover the ticket's transcript and the
    /// interval starts again. Returns false (and changes nothing) for a ticket that is no longer current.
    /// </summary>
    public bool Complete(LiveNotesTicket ticket, DateTime now)
    {
        if (!IsCurrent(ticket))
            return false;

        _processed = ticket.Transcript;
        _clockStart = now;
        _running = null;
        return true;
    }

    /// <summary>The update failed: the notes stay as they were, and the next try waits a full interval.</summary>
    public void Fail(LiveNotesTicket ticket, DateTime now)
    {
        if (!IsCurrent(ticket))
            return;

        _clockStart = now;
        _running = null;
    }

    /// <summary>The update was cancelled or its result dropped: nothing changes, the next tick may try again.</summary>
    public void Abandon(LiveNotesTicket ticket)
    {
        if (IsCurrent(ticket))
            _running = null;
    }

    /// <summary>Summarize / Re-summarize all wrote notes covering <paramref name="transcript"/>; live notes continue from there.</summary>
    public void MarkSummarized(string? transcript, DateTime now)
    {
        _processed = transcript ?? "";
        _clockStart = now;
    }

    /// <summary>
    /// A restored session: the notes cover the first <paramref name="processedLength"/> characters of
    /// <paramref name="transcript"/> (the length of <see cref="ProcessedTranscript"/> when it was saved).
    /// </summary>
    public void Restore(string? transcript, int processedLength)
    {
        var text = transcript ?? "";
        _processed = text[..Math.Clamp(processedLength, 0, text.Length)];
    }

    /// <summary>Clear: forgets the covered text, drops an update in progress (its ticket is no longer current).</summary>
    public void Reset(DateTime now)
    {
        _generation++;
        _running = null;
        _processed = "";
        _clockStart = now;
    }

    /// <summary>
    /// The nearest interval choice (5, 10 or 15 minutes), so a stored 2 becomes 5; the default for zero, negative
    /// or missing values.
    /// </summary>
    public static int NormalizeIntervalMinutes(int minutes) =>
        minutes <= 0
            ? DefaultIntervalMinutes
            : IntervalChoicesMinutes.OrderBy(choice => Math.Abs(choice - minutes)).ThenBy(choice => choice).First();

    /// <summary>"Notes updated 2:41 PM" ("Notes updated 14:41" in cultures without AM/PM).</summary>
    public static string FormatUpdated(DateTime at, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var pattern = string.IsNullOrEmpty(culture.DateTimeFormat.AMDesignator) ? "H:mm" : "h:mm tt";
        return "Notes updated " + at.ToString(pattern, culture);
    }

    private LiveNotesTicket Begin(string? transcript, bool isFinal)
    {
        var text = transcript ?? "";
        var newText = GetNewText(text);
        _running = new LiveNotesTicket(_generation, text, newText, LiveTranscriptText.CountWords(newText), isFinal);
        return _running;
    }
}
