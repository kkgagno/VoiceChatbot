using System.Text.Json;
using VoiceChatbot;
using Xunit;

public class WebTranscriberTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 14, 30, 5, TimeSpan.FromHours(2));

    [Fact]
    public void SessionStartUsesTheBrowserTimeWhenItIsPlausible()
    {
        var started = Now.AddMinutes(-42);
        var resolved = WebTranscriber.ResolveSessionStart(started.ToUnixTimeMilliseconds(), Now);

        Assert.Equal(started, resolved);
        Assert.Equal(Now.Offset, resolved.Offset);
    }

    [Fact]
    public void SessionStartFallsBackToNowForMissingOrImplausibleTimes()
    {
        Assert.Equal(Now, WebTranscriber.ResolveSessionStart(null, Now));
        Assert.Equal(Now, WebTranscriber.ResolveSessionStart(0, Now));
        Assert.Equal(Now, WebTranscriber.ResolveSessionStart(-5, Now));
        Assert.Equal(Now, WebTranscriber.ResolveSessionStart(long.MaxValue, Now));
        Assert.Equal(Now, WebTranscriber.ResolveSessionStart(Now.AddHours(3).ToUnixTimeMilliseconds(), Now));
        Assert.Equal(Now, WebTranscriber.ResolveSessionStart(Now.AddDays(-31).ToUnixTimeMilliseconds(), Now));
    }

    [Fact]
    public void ASessionStartSlightlyAheadOfThePcClockCountsAsNow()
    {
        Assert.Equal(Now, WebTranscriber.ResolveSessionStart(Now.AddMinutes(2).ToUnixTimeMilliseconds(), Now));
    }

    [Fact]
    public void SessionStartAcceptsATranscriptRestoredDaysLater()
    {
        var started = Now.AddDays(-3);
        Assert.Equal(started, WebTranscriber.ResolveSessionStart(started.ToUnixTimeMilliseconds(), Now));
    }

    [Fact]
    public void SessionLengthIsTheBrowsersTimeWhenItIsPlausible()
    {
        Assert.Equal(TimeSpan.FromMinutes(12.5), WebTranscriber.ResolveSessionLength(750_000));
        Assert.Null(WebTranscriber.ResolveSessionLength(null));
        Assert.Null(WebTranscriber.ResolveSessionLength(0));
        Assert.Null(WebTranscriber.ResolveSessionLength(-1));
        Assert.Null(WebTranscriber.ResolveSessionLength((long)TimeSpan.FromDays(8).TotalMilliseconds));
        Assert.Null(WebTranscriber.ResolveSessionLength(long.MaxValue));
    }

    [Fact]
    public void NewSaveFileNameIsTheDesktopAutoSaveNameWhenFree()
    {
        var start = new DateTime(2026, 10, 8, 14, 30, 5);
        Assert.Equal("transcript_20261008_143005.md", WebTranscriber.NewSaveFileName(start, _ => false));
    }

    [Fact]
    public void NewSaveFileNameNeverReusesATakenName()
    {
        var start = new DateTime(2026, 10, 8, 14, 30, 5);
        var taken = new HashSet<string> { "transcript_20261008_143005.md", "transcript_20261008_143005_2.md" };

        var name = WebTranscriber.NewSaveFileName(start, taken.Contains);

        Assert.Equal("transcript_20261008_143005_3.md", name);
    }

    [Fact]
    public void NewSaveFileNameIsAPlainFileName()
    {
        var name = WebTranscriber.NewSaveFileName(DateTime.MaxValue, _ => true);

        Assert.Equal(Path.GetFileName(name), name);
        Assert.StartsWith("transcript_", name);
        Assert.EndsWith(".md", name);
    }

    [Fact]
    public void CheckLengthAllowsSeveralMegabytesOfTranscript()
    {
        var transcript = new string('a', 2 * 1024 * 1024);
        Assert.Null(WebTranscriber.CheckLength(transcript, "notes"));
        Assert.Null(WebTranscriber.CheckLength(null, null));
    }

    [Fact]
    public void CheckLengthRefusesTooLongText()
    {
        Assert.NotNull(WebTranscriber.CheckLength(new string('a', WebTranscriber.MaxTranscriptChars + 1), null));
        Assert.NotNull(WebTranscriber.CheckLength("", new string('a', WebTranscriber.MaxNotesChars + 1)));
    }

    [Fact]
    public void BodyLimitsFitTheLongestTextAndChunk()
    {
        // 20 s of 16 kHz 16-bit mono WAV, and the longest transcript plus notes as UTF-8 (mostly 1-3 bytes a character).
        Assert.True(WebTranscriber.ChunkMaxBodyBytes > 20 * 16000 * 2 + 44);
        Assert.True(WebTranscriber.TextMaxBodyBytes >= 3L * (WebTranscriber.MaxTranscriptChars + WebTranscriber.MaxNotesChars));
    }

    [Fact]
    public void ConfigMirrorsTheDesktopTranscriber()
    {
        var config = WebTranscriber.CreateConfig();
        var chunk = new SpeechChunkerOptions();

        Assert.Equal(chunk.SampleRate, config.Chunk.SampleRate);
        Assert.Equal(chunk.FrameLength.TotalMilliseconds, config.Chunk.FrameMs);
        Assert.Equal(chunk.SpeechThreshold, config.Chunk.Threshold);
        Assert.Equal(2000, config.Chunk.MinChunkMs);
        Assert.Equal(500, config.Chunk.PauseMs);
        Assert.Equal(20000, config.Chunk.MaxChunkMs);
        Assert.Equal(chunk.MinSpeech.TotalMilliseconds, config.Chunk.MinSpeechMs);
        Assert.Equal(chunk.PreRoll.TotalMilliseconds, config.Chunk.PreRollMs);
        Assert.Equal(chunk.CutSearch.TotalMilliseconds, config.Chunk.CutSearchMs);
        Assert.Equal(TranscriptSummaryStyles.Names, config.Styles);
        Assert.Equal(TranscriptSummaryStyles.Summary, config.DefaultStyle);
        Assert.Equal(TranscriptNotes.SummarySoFarHeading, config.Notes.SummarySoFarHeading);
        Assert.Equal(TranscriptNotes.NotesByTimeHeading, config.Notes.NotesByTimeHeading);
        Assert.Equal(TranscriptSummaryStyles.Names.Select(TranscriptSummaryStyles.GetHeading), config.Notes.StyleHeadings);
        Assert.Equal(new[] { 5, 10, 15 }, config.Intervals);
        Assert.Equal(LiveNotesPolicy.IntervalChoicesMinutes, config.Intervals);
        Assert.Equal(LiveNotesPolicy.DefaultIntervalMinutes, config.DefaultInterval);
        Assert.Equal(LiveNotesPolicy.CheckEvery.TotalMilliseconds, config.CheckEveryMs);
        Assert.Equal(LiveNotesPolicy.DefaultMinNewWords, config.MinNewWords);
        Assert.Equal(TranscriptSummarizer.SinglePassLimit, config.SinglePassLimit);
        Assert.Equal(PhoneRemotePin.BrowserStorageKey, config.PinStorageKey);
        Assert.Equal("/api/transcriber", config.Api);
    }

    [Fact]
    public void ConfigJsonIsCamelCaseAndSafeInsideAScript()
    {
        using var json = JsonDocument.Parse(WebTranscriber.ConfigJson());
        var root = json.RootElement;

        Assert.Equal(16000, root.GetProperty("chunk").GetProperty("sampleRate").GetInt32());
        Assert.Equal("Summary", root.GetProperty("defaultStyle").GetString());
        Assert.Equal(4, root.GetProperty("styles").GetArrayLength());
        Assert.Equal("NOTES BY TIME", root.GetProperty("notes").GetProperty("notesByTimeHeading").GetString());
        Assert.Equal("ACTION ITEMS", root.GetProperty("notes").GetProperty("styleHeadings")[1].GetString());
        Assert.DoesNotContain("<", WebTranscriber.ConfigJson());
    }

    [Fact]
    public void CleanChunkRemovesWhisperRepeatsAndExtraSpace()
    {
        Assert.Equal("This is it.", LiveTranscriptText.CleanChunk("  This is it.This is it.\n This is it. "));
        Assert.Equal("one two", LiveTranscriptText.CleanChunk("one \t\n two"));
        Assert.Equal("", LiveTranscriptText.CleanChunk(null));
    }
}

public class PhoneRemoteTranscriberPageTests
{
    private static readonly string Page = PhoneRemoteTranscriberPage.Build("abc123");

    [Fact]
    public void PageMarksItsScriptAndStyleWithTheNonce()
    {
        Assert.Contains("<script nonce=\"abc123\">", Page);
        Assert.Contains("<style nonce=\"abc123\">", Page);
        Assert.DoesNotContain("__NONCE__", Page);
        Assert.DoesNotContain("__TRANSCRIBER_CONFIG__", Page);

        var policy = PhoneRemoteTranscriberPage.ContentSecurityPolicy("abc123");
        Assert.Contains("script-src 'nonce-abc123'", policy);
        Assert.Contains("connect-src 'self'", policy);
        Assert.Contains("default-src 'none'", policy);
    }

    [Fact]
    public void PageCarriesTheSharedConfig()
    {
        Assert.Contains("const CFG = " + WebTranscriber.ConfigJson() + ";", Page);
    }

    [Fact]
    public void PageNeverParsesTextAsHtml()
    {
        foreach (var sink in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "eval(", "new Function" })
            Assert.DoesNotContain(sink, Page);
    }

    [Fact]
    public void PageUsesTheRemotesPinKeyAndHeaderAndLinksBack()
    {
        Assert.Contains("CFG.pinStorageKey", Page);
        Assert.Contains("'" + PhoneRemoteServerPinHeader + "'", Page);
        Assert.Contains("href=\"/\"", Page);
    }

    [Fact]
    public void PageHasTheRecordingNotesAndOutputControls()
    {
        foreach (var id in new[] { "source", "startStop", "elapsed", "meterFill", "transcript", "notes", "style", "summarize",
                     "liveNotes", "interval", "copyTranscript", "copyNotes", "download", "savePc", "sendChat", "clear",
                     "fontDown", "fontUp", "divider", "pinBar" })
            Assert.Contains($"id=\"{id}\"", Page);

        Assert.Contains("getDisplayMedia", Page);
        Assert.Contains("echoCancellation: false, noiseSuppression: true, autoGainControl: true", Page);
        Assert.Contains("navigator.wakeLock", Page);
        Assert.Contains("beforeunload", Page);
    }

    [Fact]
    public void PageWritesNotesByTimeThroughJobsOnThePc()
    {
        // Live updates, the notes on Stop and Re-summarize all are background jobs the page polls.
        Assert.Contains("API + '/notes'", Page);
        Assert.Contains("API + '/summarize'", Page);
        Assert.Contains("API + '/jobs/'", Page);
        Assert.DoesNotContain("'/summarize/'", Page);
        Assert.Contains("isFinal: ticket.isFinal", Page);
        Assert.Contains("intervalMinutes: settings.intervalMinutes", Page);

        // The button and the confirmation match the desktop window.
        Assert.Contains("'Update notes now'", Page);
        Assert.Contains("'Re-summarize all'", Page);
        Assert.Contains("confirm('Replace the current notes with a fresh summary of the whole transcript?')", Page);
        Assert.Contains("el.notes.readOnly = ", Page);
    }

    [Fact]
    public void PageKeepsNotesAndLiveNotesProgressAcrossAReload()
    {
        Assert.Contains("processedLength: policy.processed.length", Page);
        Assert.Contains("policy.restore(transcript, saved.processedLength)", Page);
        Assert.Contains("elapsedMs: Math.round(sessionElapsedMs())", Page);
        Assert.Contains("timelineOriginMs = state.elapsedBaseMs", Page);
        Assert.Contains("'Restored your last session from '", Page);
    }

    [Fact]
    public void EachBuildGetsItsOwnNonce()
    {
        var a = PhoneRemoteTranscriberPage.NewNonce();
        var b = PhoneRemoteTranscriberPage.NewNonce();

        Assert.NotEqual(a, b);
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.Throws<ArgumentException>(() => PhoneRemoteTranscriberPage.Build(""));
    }

    // PhoneRemoteServer.PinHeader lives outside Core (it needs ASP.NET Core); the page must send the same header.
    private const string PhoneRemoteServerPinHeader = "X-Phone-Remote-Pin";
}
