using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace VoiceChatbot;

/// <summary>
/// What the web transcriber needs from the desktop app. <see cref="SummarizeAsync"/> sends one request to the
/// chat model and may be called from any thread (MainWindow moves it to the UI thread). <see cref="SendToChatAsync"/>
/// makes the transcript and notes the chat's context and returns the message to show in the browser.
/// </summary>
public sealed record PhoneRemoteTranscriberHooks(
    Func<TranscriptSummaryRequest, CancellationToken, Task<string>> SummarizeAsync,
    Func<string, string, Task<string>> SendToChatAsync,
    string TranscriptsFolder);

/// <summary>Summarize / Re-summarize all: fresh notes from the whole transcript, in sections of the live-notes interval.</summary>
public sealed record TranscriberSummarizeRequest(string? Transcript, string? Style, int? IntervalMinutes, long? ElapsedMs);

/// <summary>
/// A live-notes update (the text said since the last one, <see cref="NewText"/>), or with <see cref="IsFinal"/> the
/// notes written on Stop, which also need the whole <see cref="Transcript"/> for the full summary.
/// </summary>
public sealed record TranscriberNotesRequest(
    string? Notes,
    string? NewText,
    string? Transcript,
    string? Style,
    long? ElapsedMs,
    bool? IsFinal);

public sealed record TranscriberSaveRequest(
    string? Transcript,
    string? Notes,
    string? NotesStyle,
    long? StartedAt,
    long? ElapsedMs,
    string? SaveId);

public sealed record TranscriberSendRequest(string? Transcript, string? Notes);

/// <summary>
/// The web transcriber: GET /transcribe (the page, public like "/") and the PIN-protected /api/transcriber/*
/// endpoints it calls. Chunks are transcribed by the same Whisper callback as the rest of the remote (SpeechEngine
/// runs one transcription at a time, so web chunks, the desktop transcriber and voice chat simply take turns) and
/// cleaned like the desktop transcriber's. Notes are written by the same Core code as the desktop transcriber's
/// (<see cref="TranscriptNotesWriter"/>), each as a background job the page polls, so a phone that sleeps for a
/// moment does not lose them.
/// </summary>
public sealed partial class PhoneRemoteServer
{
    private const int MaxRememberedSaves = 2000;

    private readonly PhoneRemoteTranscriberHooks? _transcriber;
    private readonly TranscriberJobs _summaryJobs = new(maxRunning: 2);
    // saveId (random, handed out by /save) -> the file name the server chose for that browser session.
    private readonly ConcurrentDictionary<string, string> _transcriptSaves = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _transcriptSaveLock = new(1, 1);

    private void MapTranscriberRoutes(WebApplication app)
    {
        app.MapGet(WebTranscriber.PagePath, (HttpContext context) =>
        {
            var nonce = PhoneRemoteTranscriberPage.NewNonce();
            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = PhoneRemoteTranscriberPage.ContentSecurityPolicy(nonce);
            headers.CacheControl = "no-store";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            return Results.Content(PhoneRemoteTranscriberPage.Build(nonce), "text/html; charset=utf-8");
        });

        var api = WebTranscriber.ApiPrefix;

        // One recorded chunk (16 kHz mono WAV, form field "audio") -> its cleaned text.
        app.MapPost(api + "/chunk", async (HttpRequest request, CancellationToken ct) =>
        {
            if (!IsAuthorized(request))
                return Results.Unauthorized();
            if (!request.HasFormContentType)
                return Error(StatusCodes.Status400BadRequest, "Expected form data with an audio file.");

            var form = await request.ReadFormAsync(ct);
            var audio = form.Files.GetFile("audio");
            if (audio == null || audio.Length == 0)
                return Error(StatusCodes.Status400BadRequest, "No audio was sent.");

            try
            {
                await using var stream = audio.OpenReadStream();
                var text = await _transcribeAsync(stream, ct);
                return Results.Json(new { text = LiveTranscriptText.CleanChunk(text) });
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return Results.Empty; // the browser went away
            }
            catch (Exception ex)
            {
                AppLog.Warn("Web transcriber: a chunk could not be transcribed.", ex);
                return Error(StatusCodes.Status500InternalServerError, $"Transcription failed: {Shorten(ex.Message)}");
            }
        });

        // Summarize / Re-summarize all: a background job that writes fresh notes by time from the whole transcript
        // (one section per interval, then the full summary at the top) and returns its state.
        app.MapPost(api + "/summarize", (TranscriberSummarizeRequest body, HttpRequest request) =>
        {
            if (!IsAuthorized(request))
                return Results.Unauthorized();
            if (_transcriber is not { } hooks)
                return TranscriberUnavailable();

            var transcript = (body.Transcript ?? "").Trim();
            if (transcript.Length == 0)
                return Error(StatusCodes.Status400BadRequest, "No transcript to summarize yet.");
            if (WebTranscriber.CheckLength(transcript, null) is { } tooLong)
                return Error(StatusCodes.Status413PayloadTooLarge, tooLong);

            var style = TranscriptSummaryStyles.Normalize(body.Style);
            var window = TimeSpan.FromMinutes(LiveNotesPolicy.NormalizeIntervalMinutes(body.IntervalMinutes ?? 0));
            var length = WebTranscriber.ResolveSessionLength(body.ElapsedMs);
            return StartJob(style, async (progress, ct) => new TranscriberJobOutput(
                await TranscriptNotesWriter.RebuildAsync(transcript, style, window, length, hooks.SummarizeAsync, progress, ct)));
        });

        // Live notes: a background job that adds a section on only the text said since the last update and refreshes
        // the summary so far; with isFinal (on Stop) the last section and a full summary of the whole transcript.
        app.MapPost(api + "/notes", (TranscriberNotesRequest body, HttpRequest request) =>
        {
            if (!IsAuthorized(request))
                return Results.Unauthorized();
            if (_transcriber is not { } hooks)
                return TranscriberUnavailable();
            var tooLong = WebTranscriber.CheckLength(body.NewText, body.Notes) ?? WebTranscriber.CheckLength(body.Transcript, null);
            if (tooLong != null)
                return Error(StatusCodes.Status413PayloadTooLarge, tooLong);

            var final = body.IsFinal == true;
            var notes = body.Notes ?? "";
            var newText = body.NewText ?? "";
            var transcript = body.Transcript ?? "";
            if (newText.Trim().Length == 0 && (!final || transcript.Trim().Length == 0))
                return Error(StatusCodes.Status400BadRequest, "Nothing new to add to the notes yet.");

            var style = TranscriptSummaryStyles.Normalize(body.Style);
            var elapsed = WebTranscriber.ResolveSessionLength(body.ElapsedMs) ?? TimeSpan.Zero;
            return StartJob(style, async (progress, ct) =>
            {
                var result = final
                    ? await TranscriptNotesWriter.FinishAsync(notes, newText, transcript, elapsed, style, hooks.SummarizeAsync, progress, ct)
                    : await TranscriptNotesWriter.UpdateAsync(notes, newText, elapsed, style, hooks.SummarizeAsync, progress, ct);
                if (result.SummaryError is not { } error)
                    return new TranscriberJobOutput(result.Notes);

                AppLog.Warn("Web transcriber: the summary at the top of the notes could not be refreshed.", error);
                return new TranscriberJobOutput(result.Notes, $"The summary at the top was not refreshed: {DescribeSummaryError(error)}");
            });
        });

        // A job's state and, once it has finished, its result (polled by the page about once a second).
        app.MapGet(api + "/jobs/{id}", (string id, HttpRequest request) =>
        {
            if (!IsAuthorized(request))
                return Results.Unauthorized();

            return _summaryJobs.Get(id) is { } job
                ? Results.Json(JobJson(job.Snapshot()))
                : Error(StatusCodes.Status404NotFound, "The PC no longer knows about these notes (the app may have restarted). Try again.");
        });

        app.MapPost(api + "/jobs/{id}/cancel", (string id, HttpRequest request) =>
        {
            if (!IsAuthorized(request))
                return Results.Unauthorized();

            return Results.Json(new { cancelled = _summaryJobs.Cancel(id) });
        });

        // Saves the session in the transcripts folder under a name the server chooses (see WebTranscriber.NewSaveFileName).
        app.MapPost(api + "/save", async (TranscriberSaveRequest body, HttpRequest request) =>
        {
            if (!IsAuthorized(request))
                return Results.Unauthorized();
            if (_transcriber is not { } hooks)
                return TranscriberUnavailable();

            var transcript = body.Transcript ?? "";
            var notes = body.Notes ?? "";
            if (string.IsNullOrWhiteSpace(transcript) && string.IsNullOrWhiteSpace(notes))
                return Error(StatusCodes.Status400BadRequest, "Nothing to save yet.");
            if (WebTranscriber.CheckLength(transcript, notes) is { } tooLong)
                return Error(StatusCodes.Status413PayloadTooLarge, tooLong);

            try
            {
                var (saveId, fileName) = await SaveWebTranscriptAsync(hooks.TranscriptsFolder, body, transcript, notes);
                return Results.Json(new { saveId, fileName });
            }
            catch (Exception ex)
            {
                AppLog.Warn("Web transcriber: could not save the session.", ex);
                return Error(StatusCodes.Status500InternalServerError, $"Could not save on the PC: {FriendlyErrors.Describe(ex)}");
            }
        });

        // Send to chat: the transcript and notes become the desktop and remote chat's transcription context.
        app.MapPost(api + "/send-to-chat", async (TranscriberSendRequest body, HttpRequest request) =>
        {
            if (!IsAuthorized(request))
                return Results.Unauthorized();
            if (_transcriber is not { } hooks)
                return TranscriberUnavailable();

            var transcript = (body.Transcript ?? "").Trim();
            var notes = (body.Notes ?? "").Trim();
            if (transcript.Length == 0 && notes.Length == 0)
                return Error(StatusCodes.Status400BadRequest, "Nothing to send yet. Record or paste a transcript first.");
            if (WebTranscriber.CheckLength(transcript, notes) is { } tooLong)
                return Error(StatusCodes.Status413PayloadTooLarge, tooLong);

            try
            {
                var message = await hooks.SendToChatAsync(transcript, notes);
                return Results.Json(new { message });
            }
            catch (Exception ex)
            {
                AppLog.Warn("Web transcriber: send to chat failed.", ex);
                return Error(StatusCodes.Status500InternalServerError, $"Could not send it to the chat: {Shorten(ex.Message)}");
            }
        });
    }

    /// <summary>
    /// Writes the session document. The first save of a browser session picks a new, unused file name and hands
    /// out a random id for it; later saves with that id rewrite the same file (one file per session, like the
    /// desktop transcriber). An unknown id (for example after the app restarted) gets a new file.
    /// </summary>
    private async Task<(string SaveId, string FileName)> SaveWebTranscriptAsync(
        string transcriptsFolder, TranscriberSaveRequest body, string transcript, string notes)
    {
        var now = DateTimeOffset.Now;
        var start = WebTranscriber.ResolveSessionStart(body.StartedAt, now).LocalDateTime;
        var document = LiveTranscriptText.BuildDocument(
            LiveTranscriptText.DocumentTitle,
            start,
            WebTranscriber.ResolveSessionLength(body.ElapsedMs),
            transcript,
            notes,
            TranscriptNotes.DocumentHeading(notes, body.NotesStyle),
            markdown: true);

        var folder = Path.GetFullPath(transcriptsFolder);
        await _transcriptSaveLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(folder);
            var saveId = body.SaveId ?? "";
            if (saveId.Length == 0 || !_transcriptSaves.TryGetValue(saveId, out var fileName))
            {
                if (_transcriptSaves.Count >= MaxRememberedSaves)
                    _transcriptSaves.Clear();

                fileName = WebTranscriber.NewSaveFileName(start, name => File.Exists(Path.Combine(folder, name)));
                saveId = Guid.NewGuid().ToString("N");
                _transcriptSaves[saveId] = fileName;
            }

            // The name is built from the date only; this guards against it ever leaving the folder.
            var path = Path.GetFullPath(Path.Combine(folder, fileName));
            if (!string.Equals(Path.GetDirectoryName(path), folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The transcript file name is not valid.");

            await File.WriteAllTextAsync(path, document);
            AppLog.Info($"Web transcript saved to {path}.");
            return (saveId, fileName);
        }
        finally
        {
            _transcriptSaveLock.Release();
        }
    }

    // Starts a notes job and answers with its state, or 409 when too many are running.
    private IResult StartJob(string style, Func<IProgress<string>, CancellationToken, Task<TranscriberJobOutput>> work)
    {
        var job = _summaryJobs.TryStartWithWarning(style, work, DescribeSummaryError, out var refusal);
        return job == null ? Error(StatusCodes.Status409Conflict, refusal) : Results.Json(JobJson(job.Snapshot()));
    }

    private static object JobJson(TranscriberJobSnapshot job) => new
    {
        id = job.Id,
        style = job.Label,
        state = job.State.ToString().ToLowerInvariant(),
        progress = job.Progress,
        result = job.Result,
        error = job.Error,
        warning = job.Warning
    };

    private static string DescribeSummaryError(Exception ex) => Shorten(FriendlyErrors.Describe(ex));

    private static IResult TranscriberUnavailable() =>
        Error(StatusCodes.Status503ServiceUnavailable, "The web transcriber is not available in this version of the desktop app.");

    private static IResult Error(int statusCode, string message) => Results.Json(new { error = message }, statusCode: statusCode);

    private static string Shorten(string? text, int max = 300)
    {
        var line = (text ?? "").ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..(max - 3)].TrimEnd() + "...";
    }
}
