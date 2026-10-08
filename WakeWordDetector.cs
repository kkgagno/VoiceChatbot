using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using NAudio.Wave;

namespace VoiceChatbot;

/// <summary>
/// Always-on wake word detection with openWakeWord's models, independent of Whisper. Runs the models
/// in-process with ONNX Runtime (WakeWordOnnxModels, OpenWakeWordPipeline) on a background thread,
/// fed 16 kHz mono PCM from the selected microphone. While paused the microphone stays open but its
/// audio is dropped, so it never hears the assistant. All events are raised on background threads.
/// </summary>
public sealed class WakeWordDetector : IDisposable
{
    private const int BufferMilliseconds = 80; // one openWakeWord frame (1280 samples at 16 kHz)
    private const int MaxQueuedBuffers = 64;   // ~5 s; older audio is dropped if detection falls behind

    private readonly object _lock = new();
    private Session? _session;
    private volatile bool _paused;
    private bool _disposed;

    // One run: models, worker thread and microphone. A new Start replaces it; late callbacks from an
    // old run are ignored.
    private sealed class Session
    {
        public Session(string model, double threshold, int deviceIndex)
        {
            Model = model;
            Threshold = threshold;
            DeviceIndex = deviceIndex;
        }

        public string Model { get; }
        public double Threshold { get; }
        public int DeviceIndex { get; }
        public readonly BlockingCollection<(byte[] Pcm, long Ticks)> Audio = new(MaxQueuedBuffers);
        public readonly CancellationTokenSource Cancel = new();
        public WaveInEvent? WaveIn;
        public volatile bool Closed;
    }

    /// <summary>The wake word was heard (and the detector is not paused).</summary>
    public event Action<WakeWordEvent>? Detected;

    /// <summary>Status or StatusDetail changed. Read the properties for the current values.</summary>
    public event Action<WakeWordStatus>? StatusChanged;

    /// <summary>The detector stopped because of a problem; the text is meant for the user.</summary>
    public event Action<string>? Error;

    public WakeWordStatus Status { get; private set; } = WakeWordStatus.Off;
    public string StatusDetail { get; private set; } = "";
    public string Model { get; private set; } = WakeWordProtocol.DefaultModel;

    /// <summary>True from Start until Stop, or until the detector stops because of an error.</summary>
    public bool IsRunning => _session != null;

    /// <summary>
    /// Starts detection, replacing any current run. Returns at once; the models load and the
    /// microphone opens in the background and report progress through StatusChanged.
    /// </summary>
    public void Start(string model, double threshold, int micDeviceIndex)
    {
        if (_disposed)
            return;

        var session = new Session(
            WakeWordProtocol.NormalizeModel(model),
            WakeWordProtocol.ClampThreshold(threshold),
            micDeviceIndex);

        Session? previous;
        lock (_lock)
        {
            previous = _session;
            _session = session;
            Model = session.Model;
        }

        if (previous != null)
            CloseSession(previous);

        SetStatus(session, WakeWordStatus.Starting, "");
        var worker = new Thread(() => Run(session))
        {
            IsBackground = true,
            Name = "Wake word detector",
            Priority = ThreadPriority.BelowNormal
        };
        worker.Start();
    }

    /// <summary>Stops listening to the audio. Use while the app listens or speaks.</summary>
    public void Pause()
    {
        _paused = true;
        var session = _session;
        if (session != null && Status == WakeWordStatus.Listening)
            SetStatus(session, WakeWordStatus.Paused, "");
    }

    /// <summary>Listens again. What was heard before a pause of more than a second is forgotten.</summary>
    public void Resume()
    {
        _paused = false;
        var session = _session;
        if (session != null && Status == WakeWordStatus.Paused)
            SetStatus(session, WakeWordStatus.Listening, "");
    }

    /// <summary>Stops detection: closes the microphone and releases the models.</summary>
    public void Stop()
    {
        Session? session;
        lock (_lock)
        {
            session = _session;
            _session = null;
        }

        if (session != null)
            CloseSession(session);

        SetStatus(null, WakeWordStatus.Off, "");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    // ==================== Detection thread ====================

    // Loads the models, opens the microphone, then scores every 80 ms frame until the run closes.
    private void Run(Session session)
    {
        WakeWordOnnxModels? models = null;
        try
        {
            try
            {
                models = new WakeWordOnnxModels(WakeWordOnnxModels.DefaultFolder, WakeWordProtocol.ModelFileName(session.Model));
            }
            catch (Exception ex)
            {
                Fail(session, $"The \"{WakeWordProtocol.DisplayName(session.Model)}\" wake word model could not be loaded: {ex.Message}");
                return;
            }

            var pipeline = new OpenWakeWordPipeline(models);
            var gate = new WakeWordGate(session.Threshold);
            var frames = new WakeWordFrameAssembler();
            if (session.Closed)
                return;

            ThreadPool.QueueUserWorkItem(_ => OpenMicrophone(session));

            foreach (var (pcm, ticks) in session.Audio.GetConsumingEnumerable(session.Cancel.Token))
            {
                var now = (double)ticks / Stopwatch.Frequency;
                frames.Add(pcm, frame =>
                {
                    if (gate.FrameArrived(now))
                        pipeline.Reset(); // the audio was paused; forget what came before

                    var score = pipeline.Process(frame);
                    if (!gate.IsWake(score, now))
                        return;

                    // Forget this utterance so it cannot fire again once the cooldown ends.
                    pipeline.Reset();
                    // A frame queued just before Pause can still score; the app is busy by then.
                    if (!_paused && !session.Closed && ReferenceEquals(session, _session))
                        Detected?.Invoke(new WakeWordEvent(session.Model, score));
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Stop or a new Start.
        }
        catch (Exception ex)
        {
            Fail(session, $"Wake word detection stopped: {ex.Message}");
        }
        finally
        {
            models?.Dispose();
        }
    }

    // Reports a problem with the current run, then closes it.
    private void Fail(Session session, string message)
    {
        lock (_lock)
        {
            if (session.Closed || !ReferenceEquals(session, _session))
                return;
            _session = null;
            Status = WakeWordStatus.Error;
            StatusDetail = message;
        }

        // Not on this thread: it may be the detection thread or the device's capture thread.
        ThreadPool.QueueUserWorkItem(_ => CloseSession(session));
        try { StatusChanged?.Invoke(WakeWordStatus.Error); } catch { }
        try { Error?.Invoke(message); } catch { }
    }

    private void CloseSession(Session session)
    {
        WaveInEvent? waveIn;
        lock (_lock)
        {
            if (session.Closed)
                return;
            session.Closed = true;
            waveIn = session.WaveIn;
            session.WaveIn = null;
        }

        if (waveIn != null)
            CloseMicrophone(waveIn);
        // Ends the detection thread, which then releases the models.
        try { session.Cancel.Cancel(); } catch { }
    }

    // ==================== Microphone ====================

    // Thread pool, once the models are loaded.
    private void OpenMicrophone(Session session)
    {
        if (session.Closed)
            return;

        WaveInEvent? waveIn = null;
        string problem = "";
        try
        {
            waveIn = OpenWaveIn(session, session.DeviceIndex);
        }
        catch (Exception ex)
        {
            problem = ex.Message;
            try
            {
                // Same fallback as SpeechEngine.StartListening.
                var fallbackDevice = session.DeviceIndex == -1 && WaveInEvent.DeviceCount > 0 ? 0 : -1;
                if (fallbackDevice != session.DeviceIndex)
                    waveIn = OpenWaveIn(session, fallbackDevice);
            }
            catch (Exception fallbackEx)
            {
                problem += $" Fallback microphone also failed: {fallbackEx.Message}";
            }
        }

        if (waveIn == null)
        {
            Fail(session, $"Microphone could not start: {problem}");
            return;
        }

        var keep = false;
        lock (_lock)
        {
            if (!session.Closed)
            {
                session.WaveIn = waveIn;
                keep = true;
            }
        }

        if (!keep)
        {
            CloseMicrophone(waveIn);
            return;
        }

        SetStatus(session, _paused ? WakeWordStatus.Paused : WakeWordStatus.Listening, "");
    }

    private WaveInEvent OpenWaveIn(Session session, int deviceNumber)
    {
        var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(OpenWakeWordPipeline.SampleRate, 16, 1),
            BufferMilliseconds = BufferMilliseconds,
            DeviceNumber = deviceNumber
        };

        try
        {
            waveIn.DataAvailable += (_, e) => QueueAudio(session, e);
            waveIn.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null && !session.Closed)
                    Fail(session, $"The microphone stopped: {e.Exception.Message}");
            };
            waveIn.StartRecording();
            return waveIn;
        }
        catch
        {
            try { waveIn.Dispose(); } catch { }
            throw;
        }
    }

    // Capture thread, every ~80 ms: only copies the audio; the detection thread does the work.
    private void QueueAudio(Session session, WaveInEventArgs e)
    {
        if (_paused || session.Closed || e.BytesRecorded <= 0)
            return;

        var pcm = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, pcm, 0, e.BytesRecorded);
        try
        {
            // Full means detection has fallen far behind; drop this buffer rather than block capture.
            session.Audio.TryAdd((pcm, Stopwatch.GetTimestamp()));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void CloseMicrophone(WaveInEvent waveIn)
    {
        // On the thread pool: the caller may be the UI thread or the device's own capture thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { waveIn.StopRecording(); } catch { }
            try { waveIn.Dispose(); } catch { }
        });
    }

    // ==================== Status ====================

    // session == null sets the status unconditionally (Stop); otherwise only for the current run.
    private void SetStatus(Session? session, WakeWordStatus status, string detail)
    {
        lock (_lock)
        {
            if (session != null && !ReferenceEquals(session, _session))
                return;
            Status = status;
            StatusDetail = detail;
        }

        try { StatusChanged?.Invoke(status); } catch { }
    }
}
