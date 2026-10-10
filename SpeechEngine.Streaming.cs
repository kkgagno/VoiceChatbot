using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceChatbot;

public partial class SpeechEngine
{
    // ==================== Sentence-by-sentence speech ====================

    private SpeechSession? _activeSession;              // the session StopSpeaking cancels
    private SpeechSession? _speakingSession;            // the session that put the engine in Speaking
    private ManualResetEvent? _supersededPlaybackSignal; // replay that a session took over from

    /// <summary>
    /// Starts speaking a reply sentence by sentence: call <see cref="SpeechSession.Enqueue"/> for each
    /// sentence as it is written, then <see cref="SpeechSession.Complete"/>. Replaces any earlier session.
    /// With <paramref name="builtInKokoro"/> every piece is made by the built-in Kokoro (the remote host is
    /// not tried in the middle of the reply). <paramref name="mayStartPlaying"/>, when given, is asked on the
    /// player thread right before the first piece plays; false cancels the session (the reply is not wanted
    /// any more). With <paramref name="keepWholeReplyWhenStopped"/>, a session that was completed and had
    /// started playing still makes its remaining pieces when it is stopped (<see cref="SpeechSession.Cancel"/>),
    /// so <see cref="SpeechSession.Completed"/> reports the whole reply for Replay/Download.
    /// </summary>
    public SpeechSession BeginSpeechSession(string outputDirectory, bool builtInKokoro = false, Func<bool>? mayStartPlaying = null,
        bool keepWholeReplyWhenStopped = false)
    {
        var (voice, lang) = ResolveKokoroVoice();
        var session = new SpeechSession(this, outputDirectory, voice, lang, builtInKokoro, mayStartPlaying, keepWholeReplyWhenStopped);
        Interlocked.Exchange(ref _activeSession, session)?.Abandon();
        session.Start();
        return session;
    }

    private void CancelSpeechSession()
    {
        // StopSpeaking sets Idle itself, so the cancelled session must not change the state later.
        Interlocked.Exchange(ref _speakingSession, null);
        Interlocked.Exchange(ref _activeSession, null)?.Cancel();
    }

    internal string? RenderSpeechClip(string text, string voice, string lang, bool builtInKokoro) =>
        TtsEnabled && !_disposed ? GenerateKokoroAudioSync(text, voice, lang, skipRemote: builtInKokoro) : null;

    internal float PlaybackVolume => Math.Max(0.01f, Math.Min(Volume / 100f, 1f));

    internal void ReportSpeechProblem(string message) => Log?.Invoke(message);

    // Runs on the session's player thread right before its first clip plays.
    internal void OnSessionPlaybackStarting(SpeechSession session)
    {
        if (session.IsCancelled)
            return;

        // Take over from a replay that is still playing without letting it raise SpeechFinished;
        // the session raises SpeechFinished itself when it ends.
        var replaySignal = _playbackStopSignal;
        if (replaySignal != null)
        {
            _supersededPlaybackSignal = replaySignal;
            try { _waveOut?.Stop(); } catch { }
            try { replaySignal.Set(); } catch { }
        }

        _speakingSession = session;
        try { _waveIn?.StopRecording(); } catch { }
        _isRecording = false;
        _isProcessing = true;
        SetState(VoiceState.Speaking);
    }

    // Runs once on the session's player thread when the session ends for any reason.
    internal void OnSessionEnded(SpeechSession session, bool raiseSpeechFinished)
    {
        Interlocked.CompareExchange(ref _activeSession, null, session);
        var wasSpeaking = Interlocked.CompareExchange(ref _speakingSession, null, session) == session;
        if (wasSpeaking ? CurrentState == VoiceState.Speaking : raiseSpeechFinished && CurrentState == VoiceState.Processing)
            SetState(VoiceState.Idle);

        // An abandoned session that was speaking raises no SpeechFinished, so nothing calls
        // ReadyForNextSpeech for it; without this StartListening keeps refusing (e.g. the mic button)
        // when the newer reply fails before it speaks.
        if (wasSpeaking && !raiseSpeechFinished)
            _isProcessing = false;

        if (raiseSpeechFinished)
            SpeechFinished?.Invoke();
    }
}

/// <summary>
/// One reply spoken sentence by sentence. A generator thread renders queued sentences with Kokoro one
/// at a time while a player thread plays finished clips in order, so sentence N+1 is rendered while
/// sentence N plays. When everything has played, the clips are joined into one WAV in the output
/// directory and <see cref="Completed"/> reports its path (null when cancelled or nothing was spoken).
/// A session made to keep the whole reply when stopped, and stopped after it was completed and started
/// playing, stops only its playback: the rest is still rendered, then joined and reported the same way.
/// <see cref="GetTiming"/> measures it from the moment the session was started.
/// </summary>
public sealed class SpeechSession
{
    private const int MaxRenderFailuresInARow = 2;

    private readonly SpeechEngine _engine;
    private readonly string _outputDirectory;
    private readonly string _voice;
    private readonly string _lang;
    private readonly bool _builtInKokoro;
    private readonly Func<bool>? _mayStartPlaying;
    private readonly bool _keepWholeReplyWhenStopped;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly BlockingCollection<string> _sentences = new();
    private readonly BlockingCollection<string> _clips = new();
    private readonly CancellationTokenSource _cts = new();          // stops the player (and the generator)
    private readonly CancellationTokenSource _generatorCts = new(); // stops the generator
    private readonly List<string> _tempFiles = new(); // every rendered clip; lock the list itself
    private readonly object _gate = new();
    private bool _cancelled;
    private bool _abandoned;
    private bool _completeRequested;
    private bool _startedPlayback;
    private bool _ended;
    private bool _savingAfterStop; // stopped, but the generator still renders the rest to keep the whole reply
    private Thread? _generator;
    private int _runningThreads = 2;
    private int _problemReported;
    // Timing (under _gate): when the first clip started playing, time spent rendering, audio rendered.
    private TimeSpan? _firstAudio;
    private TimeSpan _synthesis;
    private TimeSpan _audio;
    private int _renderedClips;

    /// <summary>Raised once when the session ends, with the combined WAV path or null.</summary>
    public event Action<string?>? Completed;

    /// <summary>
    /// Raised once when the session stops speaking (finished, stopped, or never started), before
    /// <see cref="Completed"/>; a stopped session that keeps the whole reply raises Completed later.
    /// </summary>
    public event Action? SpeechEnded;

    internal SpeechSession(SpeechEngine engine, string outputDirectory, string voice, string lang, bool builtInKokoro = false,
        Func<bool>? mayStartPlaying = null, bool keepWholeReplyWhenStopped = false)
    {
        _engine = engine;
        _outputDirectory = outputDirectory;
        _voice = voice;
        _lang = lang;
        _builtInKokoro = builtInKokoro;
        _mayStartPlaying = mayStartPlaying;
        _keepWholeReplyWhenStopped = keepWholeReplyWhenStopped;
    }

    /// <summary>
    /// How the reply was spoken so far, measured from the start of the session: final once
    /// <see cref="Completed"/> was raised. <paramref name="whileWriting"/>: the session started with the request.
    /// </summary>
    public SpeechTiming GetTiming(bool whileWriting = false)
    {
        lock (_gate)
            return new SpeechTiming(_firstAudio, _synthesis, _audio, _renderedClips, _engine.LastTtsBackendUsed, whileWriting, _cancelled);
    }

    /// <summary>True once the session was stopped early (StopSpeaking, Cancel, Abandon or a newer session).</summary>
    public bool IsCancelled
    {
        get { lock (_gate) return _cancelled; }
    }

    internal void Start()
    {
        _generator = new Thread(GenerateLoop) { IsBackground = true, Name = "Speech generator" };
        _generator.Start();
        new Thread(PlayLoop) { IsBackground = true, Name = "Speech player" }.Start();
    }

    /// <summary>Queues one sentence of speech-ready text.</summary>
    public void Enqueue(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        lock (_gate)
        {
            if (_cancelled || _ended || _completeRequested)
                return;
        }

        // The session can end (and free its queue) between the check above and the Add.
        try { _sentences.Add(text.Trim()); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    /// <summary>No more sentences will come; the session ends after the last one has played.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_cancelled || _ended || _completeRequested)
                return;
            _completeRequested = true;
        }

        try { _sentences.CompleteAdding(); } catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    /// <summary>
    /// Stops playback now and drops everything still queued; a session that keeps the whole reply when
    /// stopped (see <see cref="SpeechEngine.BeginSpeechSession"/>) still renders the rest for <see cref="Completed"/>.
    /// </summary>
    public void Cancel() => Stop(abandon: false);

    /// <summary>
    /// Like <see cref="Cancel"/>, and returns true when this session raises (or already raised)
    /// SpeechFinished because it had started playing or was completed; whoever ends the turn on
    /// SpeechFinished then must not end it a second time.
    /// </summary>
    public bool CancelAndCheckFinishReported()
    {
        Stop(abandon: false);
        lock (_gate)
            return !_abandoned && (_startedPlayback || _completeRequested);
    }

    /// <summary>
    /// Like <see cref="Cancel"/> but never raises SpeechFinished: use it when something else takes
    /// over the reply's speech (a newer session) or resets the UI itself.
    /// </summary>
    public void Abandon() => Stop(abandon: true);

    private void Stop(bool abandon)
    {
        bool stopGenerator;
        lock (_gate)
        {
            if (_cancelled || _ended)
                return;
            _cancelled = true;
            _abandoned = abandon;
            // A finished reply that was already playing is still rendered to the end, so it can be replayed.
            _savingAfterStop = !abandon && _keepWholeReplyWhenStopped && _startedPlayback && _completeRequested;
            stopGenerator = !_savingAfterStop;
        }

        // Wakes the threads; the player stops its clip itself so NAudio is only used from one thread.
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        if (stopGenerator)
        {
            try { _generatorCts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private void GenerateLoop()
    {
        var token = _generatorCts.Token;
        var failuresInARow = 0;
        try
        {
            foreach (var sentence in _sentences.GetConsumingEnumerable(token))
            {
                // When Kokoro keeps failing, skip the rest of the reply instead of retrying every sentence.
                if (failuresInARow >= MaxRenderFailuresInARow)
                    continue;

                string? clip = null;
                var started = _clock.Elapsed;
                try
                {
                    clip = _engine.RenderSpeechClip(sentence, _voice, _lang, _builtInKokoro);
                }
                catch (Exception ex)
                {
                    ReportProblemOnce($"[TTS] Could not render speech: {ex.Message}");
                }

                var length = string.IsNullOrWhiteSpace(clip) ? TimeSpan.Zero : SpeechEngine.GetAudioLength(clip);
                lock (_gate)
                {
                    _synthesis += _clock.Elapsed - started;
                    if (length > TimeSpan.Zero)
                    {
                        _audio += length;
                        _renderedClips++;
                    }
                }

                if (string.IsNullOrWhiteSpace(clip))
                {
                    failuresInARow++;
                    continue;
                }

                failuresInARow = 0;
                lock (_tempFiles)
                    _tempFiles.Add(clip);
                if (token.IsCancellationRequested)
                    break;
                _clips.Add(clip, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ReportProblemOnce($"[TTS] Speech generator stopped: {ex.Message}");
        }
        finally
        {
            try { _clips.CompleteAdding(); } catch { }
            ThreadFinished();
        }
    }

    private void PlayLoop()
    {
        var token = _cts.Token;
        var played = new List<string>();
        var finishedNormally = false;
        try
        {
            foreach (var clip in _clips.GetConsumingEnumerable(token))
            {
                if (!_startedPlayback)
                {
                    // Not wanted any more (for example the chat was cleared while the first piece was made):
                    // end like a stopped session.
                    if (_mayStartPlaying != null && !SafeMayStartPlaying())
                    {
                        Stop(abandon: false);
                        break;
                    }

                    lock (_gate)
                    {
                        if (_cancelled)
                            break;
                        _startedPlayback = true;
                        _firstAudio = _clock.Elapsed;
                    }

                    _engine.OnSessionPlaybackStarting(this);
                }

                PlayClip(clip, token);
                played.Add(clip);
                if (token.IsCancellationRequested)
                    break;
            }

            finishedNormally = !token.IsCancellationRequested;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ReportProblemOnce($"[TTS] Speech playback stopped: {ex.Message}");
        }
        finally
        {
            Finish(finishedNormally, played);
        }
    }

    private bool SafeMayStartPlaying()
    {
        try
        {
            return _mayStartPlaying!();
        }
        catch (Exception ex)
        {
            ReportProblemOnce($"[TTS] Speech check failed: {ex.Message}");
            return false;
        }
    }

    private void PlayClip(string path, CancellationToken token)
    {
        WaveFileReader? reader = null;
        WaveOutEvent? output = null;
        var done = new ManualResetEvent(false);
        try
        {
            reader = new WaveFileReader(path);
            output = new WaveOutEvent();
            output.PlaybackStopped += (_, _) =>
            {
                try { done.Set(); } catch { }
            };
            output.Init(reader);
            output.Volume = _engine.PlaybackVolume;

            if (token.IsCancellationRequested)
                return;

            output.Play();
            WaitHandle.WaitAny(new[] { done, token.WaitHandle }, TimeSpan.FromMinutes(10));
        }
        catch (Exception ex)
        {
            ReportProblemOnce($"[TTS] Could not play speech: {ex.Message}");
        }
        finally
        {
            try { output?.Stop(); } catch { }
            try { output?.Dispose(); } catch { }
            try { reader?.Dispose(); } catch { }
            try { done.Dispose(); } catch { }
        }
    }

    private void Finish(bool finishedNormally, List<string> played)
    {
        bool raiseSpeechFinished;
        bool savingAfterStop;
        lock (_gate)
        {
            _ended = true;
            finishedNormally &= !_cancelled;
            savingAfterStop = _savingAfterStop;
            // A cancelled session still reports the end of speech it started (auto-listen relies on it);
            // one that never started and was never completed leaves the UI to its caller.
            raiseSpeechFinished = !_abandoned && (_startedPlayback || _completeRequested);
        }

        try
        {
            _engine.OnSessionEnded(this, raiseSpeechFinished);
        }
        catch (Exception ex)
        {
            ReportProblemOnce($"[TTS] Speech end handling failed: {ex.Message}");
        }

        try { SpeechEnded?.Invoke(); } catch { }

        string? combinedPath = null;
        if (savingAfterStop)
        {
            // Stopped while a finished reply played: wait for the rest and keep every clip, played or not.
            _generator?.Join();
            List<string> rendered;
            lock (_tempFiles)
                rendered = new List<string>(_tempFiles);
            combinedPath = TryCombineClips(rendered);
        }
        else if (finishedNormally && played.Count > 0)
        {
            combinedPath = TryCombineClips(played);
        }

        try { Completed?.Invoke(combinedPath); } catch { }
        ThreadFinished();
    }

    private string? TryCombineClips(List<string> clips)
    {
        var existing = clips.Where(File.Exists).ToList();
        if (existing.Count == 0)
            return null;

        var outputPath = Path.Combine(_outputDirectory, $"assistant_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.wav");
        try
        {
            Directory.CreateDirectory(_outputDirectory);
            if (existing.Count == 1)
                File.Move(existing[0], outputPath);
            else
                WavJoiner.Join(existing, outputPath);
            return outputPath;
        }
        catch (Exception ex)
        {
            try { File.Delete(outputPath); } catch { }
            ReportProblemOnce($"[TTS] Could not save the spoken reply: {ex.Message}");
            return null;
        }
    }

    // The last thread to finish deletes the temporary clips and frees the queues.
    private void ThreadFinished()
    {
        if (Interlocked.Decrement(ref _runningThreads) != 0)
            return;

        string[] files;
        lock (_tempFiles)
            files = _tempFiles.ToArray();
        foreach (var file in files)
        {
            try { File.Delete(file); } catch { }
        }

        _sentences.Dispose();
        _clips.Dispose();
        _cts.Dispose();
        _generatorCts.Dispose();
    }

    private void ReportProblemOnce(string message)
    {
        if (Interlocked.Exchange(ref _problemReported, 1) != 0)
            return;

        try { _engine.ReportSpeechProblem(message); } catch { }
    }
}

/// <summary>Joins WAV clips into one file, converting clips to the first clip's format when they differ.</summary>
internal static class WavJoiner
{
    public static void Join(IReadOnlyList<string> inputs, string outputPath)
    {
        WaveFormat target;
        using (var first = new WaveFileReader(inputs[0]))
            target = OutputFormatFor(first.WaveFormat);

        var wroteAny = false;
        using (var writer = new WaveFileWriter(outputPath, target))
        {
            var buffer = new byte[target.AverageBytesPerSecond];
            foreach (var input in inputs)
            {
                try
                {
                    using var reader = new WaveFileReader(input);
                    var source = ConvertTo(reader, target);
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        writer.Write(buffer, 0, read);
                    wroteAny = true;
                }
                catch
                {
                    // Skip a clip that cannot be read or converted; the rest is still worth keeping.
                }
            }
        }

        if (!wroteAny)
            throw new InvalidDataException("None of the speech clips could be read.");
    }

    private static WaveFormat OutputFormatFor(WaveFormat format)
    {
        var standard = Standardize(format);
        var supported = (standard.Encoding == WaveFormatEncoding.Pcm && standard.BitsPerSample is 16 or 24) ||
                        (standard.Encoding == WaveFormatEncoding.IeeeFloat && standard.BitsPerSample == 32);
        return supported && standard.Channels is 1 or 2
            ? standard
            : new WaveFormat(standard.SampleRate, 16, Math.Clamp(standard.Channels, 1, 2));
    }

    private static WaveFormat Standardize(WaveFormat format) =>
        format is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : format;

    private static IWaveProvider ConvertTo(WaveFileReader reader, WaveFormat target)
    {
        var format = Standardize(reader.WaveFormat);
        IWaveProvider source = ReferenceEquals(format, reader.WaveFormat)
            ? reader
            : new RawSourceWaveStream(reader, format);
        if (format.Equals(target))
            return source;

        var samples = source.ToSampleProvider();
        if (samples.WaveFormat.Channels == 1 && target.Channels == 2)
            samples = new MonoToStereoSampleProvider(samples);
        else if (samples.WaveFormat.Channels == 2 && target.Channels == 1)
            samples = new StereoToMonoSampleProvider(samples);
        else if (samples.WaveFormat.Channels != target.Channels)
            throw new NotSupportedException($"Cannot convert {samples.WaveFormat.Channels}-channel audio.");

        if (samples.WaveFormat.SampleRate != target.SampleRate)
            samples = new WdlResamplingSampleProvider(samples, target.SampleRate);

        if (target.Encoding == WaveFormatEncoding.IeeeFloat)
            return new SampleToWaveProvider(samples);
        return target.BitsPerSample == 24
            ? new SampleToWaveProvider24(samples)
            : new SampleToWaveProvider16(samples);
    }
}
