using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace VoiceChatbot;

/// <summary>
/// Always-on wake word detection with openWakeWord, independent of Whisper. Runs
/// Tools\WakeWord\wakeword_server.py hidden, feeds it 16 kHz mono PCM from the selected microphone
/// and reports what it prints. While paused the microphone stays open but no audio is sent, so it
/// never hears the assistant. All events are raised on background threads.
/// </summary>
public sealed class WakeWordDetector : IDisposable
{
    private const string ScriptFolder = "WakeWord";
    private const string ScriptName = "wakeword_server.py";
    private const int BufferMilliseconds = 80; // one openWakeWord frame (1280 samples at 16 kHz)

    private readonly object _lock = new();
    private Session? _session;
    private volatile bool _paused;
    private bool _disposed;

    // One run of the Python process. A new Start replaces it; late callbacks from an old run are ignored.
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
        public Process? Process;
        public Stream? Stdin;
        public WaveInEvent? WaveIn;
        public volatile bool Closed;
        public volatile string LastError = "";   // message of an "error" event
        public volatile string LastStderr = "";  // last line Python wrote to stderr
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

    /// <summary>True from Start until Stop, or until the process ends by itself.</summary>
    public bool IsRunning => _session != null;

    /// <summary>
    /// Starts detection, replacing any current run. Returns at once; the Python process and the
    /// microphone open in the background and report progress through StatusChanged.
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
        Task.Run(() => LaunchProcess(session));
    }

    /// <summary>Stops sending audio. Use while the app listens or speaks.</summary>
    public void Pause()
    {
        _paused = true;
        var session = _session;
        if (session != null && Status == WakeWordStatus.Listening)
            SetStatus(session, WakeWordStatus.Paused, "");
    }

    /// <summary>Sends audio again. The helper clears what it heard before the pause.</summary>
    public void Resume()
    {
        _paused = false;
        var session = _session;
        if (session != null && Status == WakeWordStatus.Paused)
            SetStatus(session, WakeWordStatus.Listening, "");
    }

    /// <summary>Stops detection: closes the microphone and ends the Python process.</summary>
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

    // ==================== Process ====================

    private void LaunchProcess(Session session)
    {
        try
        {
            var script = PythonTools.FindToolScript(ScriptFolder, ScriptName);
            if (script == null)
            {
                SetStatus(session, WakeWordStatus.NotInstalled, $"{ScriptName} was not found next to the app.");
                return;
            }

            var psi = new ProcessStartInfo
            {
                FileName = PythonTools.FindPythonExecutable(),
                Arguments = WakeWordProtocol.BuildArguments(script, session.Model, session.Threshold),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // No byte order mark: stdin carries raw audio samples.
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Win32Exception ex)
            {
                Fail(session, $"Python was not found ({ex.Message}).", notInstalled: true);
                return;
            }

            if (process == null)
            {
                Fail(session, "Python could not be started.", notInstalled: false);
                return;
            }

            process.OutputDataReceived += (_, e) => OnOutputLine(session, e.Data);
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                    session.LastStderr = e.Data.Trim();
            };

            var keep = false;
            lock (_lock)
            {
                if (!session.Closed)
                {
                    session.Process = process;
                    session.Stdin = process.StandardInput.BaseStream;
                    keep = true;
                }
            }

            if (!keep)
            {
                KillProcess(process);
                return;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception ex)
        {
            Fail(session, $"Could not start the wake word detector: {ex.Message}", notInstalled: false);
        }
    }

    // Output reader thread. A null line means the process has ended.
    private void OnOutputLine(Session session, string? line)
    {
        try
        {
            if (line == null)
            {
                OnProcessEnded(session);
                return;
            }

            var evt = WakeWordProtocol.ParseEvent(line);
            if (evt == null || session.Closed)
                return;

            switch (evt.Kind)
            {
                case WakeWordEventKind.Ready:
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            OpenMicrophone(session);
                        }
                        catch (Exception ex)
                        {
                            Fail(session, $"Microphone could not start: {ex.Message}", notInstalled: false);
                        }
                    });
                    break;

                case WakeWordEventKind.Wake:
                    // A frame sent just before Pause can still report; the app is busy by then.
                    if (!_paused && session.WaveIn != null && ReferenceEquals(session, _session))
                        Detected?.Invoke(evt);
                    break;

                case WakeWordEventKind.Error:
                    var message = string.IsNullOrWhiteSpace(evt.Message) ? "The wake word detector failed." : evt.Message;
                    session.LastError = message;
                    Fail(session, message, WakeWordProtocol.LooksNotInstalled(message, code: evt.Code));
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Wake word output handling failed: {ex.Message}");
        }
    }

    private void OnProcessEnded(Session session)
    {
        if (session.Closed)
            return;

        // An "error" line was already reported and the session closed by Fail.
        if (session.LastError.Length > 0)
            return;

        int? exitCode = null;
        try
        {
            var process = session.Process;
            if (process != null && process.WaitForExit(2000))
                exitCode = process.ExitCode;
        }
        catch { }

        var message = session.LastStderr.Length > 0
            ? session.LastStderr
            : $"The wake word detector stopped (exit code {exitCode?.ToString() ?? "unknown"}).";
        Fail(session, message, WakeWordProtocol.LooksNotInstalled(message, exitCode));
    }

    // Reports a problem with the current run, then closes it.
    private void Fail(Session session, string message, bool notInstalled)
    {
        var status = notInstalled ? WakeWordStatus.NotInstalled : WakeWordStatus.Error;
        lock (_lock)
        {
            if (session.Closed || !ReferenceEquals(session, _session))
                return;
            _session = null;
            Status = status;
            StatusDetail = message;
        }

        // Not on this thread: it may be the process's own output reader.
        ThreadPool.QueueUserWorkItem(_ => CloseSession(session));
        try { StatusChanged?.Invoke(status); } catch { }
        try { Error?.Invoke(message); } catch { }
    }

    private void CloseSession(Session session)
    {
        Process? process;
        WaveInEvent? waveIn;
        lock (_lock)
        {
            session.Closed = true;
            process = session.Process;
            waveIn = session.WaveIn;
            session.Process = null;
            session.WaveIn = null;
            session.Stdin = null;
        }

        if (waveIn != null)
            CloseMicrophone(waveIn);
        // Killing the process also unblocks a capture thread stuck writing to its stdin.
        if (process != null)
            KillProcess(process);
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
        try { process.Dispose(); } catch { }
    }

    // ==================== Microphone ====================

    // Thread pool, after the helper reported "ready".
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
            Fail(session, $"Microphone could not start: {problem}", notInstalled: false);
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
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = BufferMilliseconds,
            DeviceNumber = deviceNumber
        };

        try
        {
            waveIn.DataAvailable += (_, e) => SendAudio(session, e);
            waveIn.RecordingStopped += (_, e) =>
            {
                if (e.Exception != null && !session.Closed)
                    Fail(session, $"The microphone stopped: {e.Exception.Message}", notInstalled: false);
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

    // Capture thread, every ~80 ms.
    private void SendAudio(Session session, WaveInEventArgs e)
    {
        if (_paused || session.Closed || e.BytesRecorded <= 0)
            return;

        var stdin = session.Stdin;
        if (stdin == null)
            return;

        try
        {
            stdin.Write(e.Buffer, 0, e.BytesRecorded);
            stdin.Flush();
        }
        catch
        {
            // The process has ended; OnProcessEnded reports why.
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
