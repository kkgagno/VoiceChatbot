using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// </summary>
    public SpeechSession BeginSpeechSession(string outputDirectory)
    {
        var (voice, lang) = ResolveKokoroVoice();
        var session = new SpeechSession(this, outputDirectory, voice, lang);
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

    internal string? RenderSpeechClip(string text, string voice, string lang) =>
        TtsEnabled && !_disposed ? GenerateKokoroAudioSync(text, voice, lang) : null;

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

        if (raiseSpeechFinished)
            SpeechFinished?.Invoke();
    }
}

/// <summary>
/// One reply spoken sentence by sentence. A generator thread renders queued sentences with Kokoro one
/// at a time while a player thread plays finished clips in order, so sentence N+1 is rendered while
/// sentence N plays. When everything has played, the clips are joined into one WAV in the output
/// directory and <see cref="Completed"/> reports its path (null when cancelled or nothing was spoken).
/// </summary>
public sealed class SpeechSession
{
    private const int MaxRenderFailuresInARow = 2;

    private readonly SpeechEngine _engine;
    private readonly string _outputDirectory;
    private readonly string _voice;
    private readonly string _lang;
    private readonly BlockingCollection<string> _sentences = new();
    private readonly BlockingCollection<string> _clips = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<string> _tempFiles = new(); // every rendered clip; lock the list itself
    private readonly object _gate = new();
    private bool _cancelled;
    private bool _abandoned;
    private bool _completeRequested;
    private bool _startedPlayback;
    private bool _ended;
    private int _runningThreads = 2;
    private int _problemReported;

    /// <summary>Raised once when the session ends, with the combined WAV path or null.</summary>
    public event Action<string?>? Completed;

    internal SpeechSession(SpeechEngine engine, string outputDirectory, string voice, string lang)
    {
        _engine = engine;
        _outputDirectory = outputDirectory;
        _voice = voice;
        _lang = lang;
    }

    /// <summary>True once the session was stopped early (StopSpeaking, Cancel, Abandon or a newer session).</summary>
    public bool IsCancelled
    {
        get { lock (_gate) return _cancelled; }
    }

    internal void Start()
    {
        new Thread(GenerateLoop) { IsBackground = true, Name = "Speech generator" }.Start();
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

        try { _sentences.Add(text.Trim()); } catch (InvalidOperationException) { }
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

        try { _sentences.CompleteAdding(); } catch (InvalidOperationException) { }
    }

    /// <summary>Stops playback now and drops everything still queued.</summary>
    public void Cancel() => Stop(abandon: false);

    /// <summary>
    /// Like <see cref="Cancel"/> but never raises SpeechFinished: use it when something else takes
    /// over the reply's speech (a newer session) or resets the UI itself.
    /// </summary>
    public void Abandon() => Stop(abandon: true);

    private void Stop(bool abandon)
    {
        lock (_gate)
        {
            if (_cancelled || _ended)
                return;
            _cancelled = true;
            _abandoned = abandon;
        }

        // Wakes both threads; the player stops its clip itself so NAudio is only used from one thread.
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void GenerateLoop()
    {
        var token = _cts.Token;
        var failuresInARow = 0;
        try
        {
            foreach (var sentence in _sentences.GetConsumingEnumerable(token))
            {
                // When Kokoro keeps failing, skip the rest of the reply instead of retrying every sentence.
                if (failuresInARow >= MaxRenderFailuresInARow)
                    continue;

                string? clip = null;
                try
                {
                    clip = _engine.RenderSpeechClip(sentence, _voice, _lang);
                }
                catch (Exception ex)
                {
                    ReportProblemOnce($"[TTS] Could not render speech: {ex.Message}");
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
                    lock (_gate)
                    {
                        if (_cancelled)
                            break;
                        _startedPlayback = true;
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
        lock (_gate)
        {
            _ended = true;
            finishedNormally &= !_cancelled;
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

        string? combinedPath = null;
        if (finishedNormally && played.Count > 0)
            combinedPath = TryCombineClips(played);

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
