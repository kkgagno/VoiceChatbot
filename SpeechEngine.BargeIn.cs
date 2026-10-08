using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;

namespace VoiceChatbot;

public partial class SpeechEngine
{
    // ==================== Barge-in ====================
    // While speech plays, a second microphone capture listens for the user talking over it. When they
    // do, speech stops and BargeIn is raised. The audio that set it off is kept for the next
    // StartListening, so the first words of the interruption are not lost.

    private const int BargeInBufferMilliseconds = 50;
    private const int PcmBytesPerMillisecond = 32;          // 16 kHz, 16-bit, mono
    private const int BargeInPreRollMilliseconds = 700;     // audio kept from before the trigger
    private const int BargeInSeedWaitMilliseconds = 1000;   // how long the next recording waits for more voice
    private static readonly TimeSpan BargeInPreRollMaxAge = TimeSpan.FromSeconds(2);

    private readonly object _bargeInLock = new();
    private readonly Queue<byte[]> _bargeInRecentAudio = new();
    private WaveInEvent? _bargeInMonitor;
    private BargeInDetector? _bargeInDetector;
    private int _bargeInRecentBytes;
    private bool _bargeInEnabled;
    private bool _bargeInStarting;
    private bool _bargeInFailureReported;
    private BargeInAudio? _bargeInPreRoll;
    private int _bargeInSeedBytes; // pre-roll bytes at the start of _audioBuffer, until voice is heard

    private sealed record BargeInAudio(byte[] Pcm, DateTime CapturedUtc);

    /// <summary>
    /// Raised on a background thread when the user's voice interrupted speech. Speech has already
    /// stopped; the handler decides whether to start listening.
    /// </summary>
    public event Action? BargeIn;

    /// <summary>Listen while speech plays and stop speaking when the user starts talking.</summary>
    public bool BargeInEnabled
    {
        get => _bargeInEnabled;
        set
        {
            _bargeInEnabled = value;
            if (!value)
                StopBargeInMonitor();
        }
    }

    /// <summary>0 (needs loud, clear speech) to 100 (quiet speech is enough). Used from the next reply.</summary>
    public int BargeInSensitivity { get; set; } = BargeInDetector.DefaultSensitivity;

    /// <summary>
    /// Opens the monitoring microphone. Called on a playback thread right after playback starts;
    /// no-op when it is already open. Never throws, so it cannot cut playback short.
    /// </summary>
    internal void StartBargeInMonitor()
    {
        if (!BargeInEnabled || _disposed)
            return;

        lock (_bargeInLock)
        {
            if (_bargeInMonitor != null || _bargeInStarting || CurrentState != VoiceState.Speaking)
                return;
            _bargeInStarting = true;
        }

        WaveInEvent? monitor = null;
        string? problem = null;
        try
        {
            // Opening a device can take a moment (Bluetooth headsets switch profiles), so do it outside the lock.
            monitor = OpenBargeInMonitorWithFallback(out problem);
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }

        var reportProblem = false;
        lock (_bargeInLock)
        {
            _bargeInStarting = false;
            if (monitor != null)
            {
                _bargeInFailureReported = false;
                // Speech may have ended while the device was opening.
                if (BargeInEnabled && !_disposed && CurrentState == VoiceState.Speaking)
                {
                    _bargeInDetector = new BargeInDetector(BargeInSensitivity);
                    _bargeInRecentAudio.Clear();
                    _bargeInRecentBytes = 0;
                    _bargeInMonitor = monitor;
                    monitor = null;
                }
            }
            else if (!_bargeInFailureReported)
            {
                _bargeInFailureReported = true;
                reportProblem = true;
            }
        }

        if (monitor != null)
            CloseBargeInMonitor(monitor);
        if (reportProblem)
        {
            try { Log?.Invoke($"Interrupt by speaking could not open the microphone: {problem}"); } catch { }
        }
    }

    // Same fallback as StartListening: the default device, or the first device when the default fails.
    private WaveInEvent? OpenBargeInMonitorWithFallback(out string? problem)
    {
        try
        {
            problem = null;
            return OpenBargeInMonitor(MicDeviceIndex);
        }
        catch (Exception ex)
        {
            problem = ex.Message;
        }

        var fallbackDevice = MicDeviceIndex == -1 && WaveInEvent.DeviceCount > 0 ? 0 : -1;
        if (fallbackDevice == MicDeviceIndex)
            return null;

        try
        {
            var monitor = OpenBargeInMonitor(fallbackDevice);
            problem = null;
            return monitor;
        }
        catch (Exception ex)
        {
            problem += $" Fallback microphone also failed: {ex.Message}";
            return null;
        }
    }

    private WaveInEvent OpenBargeInMonitor(int deviceNumber)
    {
        var monitor = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = BargeInBufferMilliseconds,
            DeviceNumber = deviceNumber
        };

        try
        {
            // Buffers that arrive before the monitor is installed are ignored by OnBargeInAudio.
            monitor.DataAvailable += OnBargeInAudio;
            monitor.StartRecording();
            return monitor;
        }
        catch
        {
            monitor.DataAvailable -= OnBargeInAudio;
            try { monitor.Dispose(); } catch { }
            throw;
        }
    }

    /// <summary>Closes the monitoring microphone. Safe to call from any thread, including its own.</summary>
    private void StopBargeInMonitor()
    {
        WaveInEvent? monitor;
        lock (_bargeInLock)
        {
            monitor = _bargeInMonitor;
            _bargeInMonitor = null;
            _bargeInDetector = null;
            _bargeInRecentAudio.Clear();
            _bargeInRecentBytes = 0;
        }

        if (monitor != null)
            CloseBargeInMonitor(monitor);
    }

    private void CloseBargeInMonitor(WaveInEvent monitor)
    {
        monitor.DataAvailable -= OnBargeInAudio;
        // Close it on the thread pool: the caller may be the monitor's own capture thread or the UI thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { monitor.StopRecording(); } catch { }
            try { monitor.Dispose(); } catch { }
        });
    }

    // Runs on the monitor's capture thread for every ~50 ms buffer.
    private void OnBargeInAudio(object? sender, WaveInEventArgs e)
    {
        try
        {
            lock (_bargeInLock)
            {
                // Late buffers from a monitor that is closing are ignored.
                if (!ReferenceEquals(sender, _bargeInMonitor) || _bargeInDetector == null || e.BytesRecorded <= 0)
                    return;

                RememberBargeInAudio(e.Buffer, e.BytesRecorded);
                var rms = BargeInDetector.ComputeRms(e.Buffer, e.BytesRecorded);
                if (!_bargeInDetector.Process(rms, (double)e.BytesRecorded / PcmBytesPerMillisecond))
                    return;

                _bargeInPreRoll = new BargeInAudio(TakeBargeInAudio(), DateTime.UtcNow);
            }

            StopBargeInMonitor();
            ThreadPool.QueueUserWorkItem(_ => InterruptSpeechForBargeIn());
        }
        catch (Exception ex)
        {
            StopBargeInMonitor();
            Log?.Invoke($"Interrupt by speaking stopped: {ex.Message}");
        }
    }

    private void InterruptSpeechForBargeIn()
    {
        try
        {
            // Speech may have ended on its own meanwhile; then there is nothing to interrupt.
            if (_disposed || CurrentState != VoiceState.Speaking)
                return;

            StopSpeaking();
            BargeIn?.Invoke();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Interrupt by speaking failed: {ex.Message}");
        }
    }

    // Runs under _bargeInLock. Keeps roughly the last BargeInPreRollMilliseconds of monitor audio.
    private void RememberBargeInAudio(byte[] buffer, int count)
    {
        var copy = new byte[count];
        Buffer.BlockCopy(buffer, 0, copy, 0, count);
        _bargeInRecentAudio.Enqueue(copy);
        _bargeInRecentBytes += count;

        var keepBytes = BargeInPreRollMilliseconds * PcmBytesPerMillisecond;
        while (_bargeInRecentAudio.Count > 1 && _bargeInRecentBytes - _bargeInRecentAudio.Peek().Length >= keepBytes)
            _bargeInRecentBytes -= _bargeInRecentAudio.Dequeue().Length;
    }

    // Runs under _bargeInLock.
    private byte[] TakeBargeInAudio()
    {
        var audio = new byte[_bargeInRecentBytes];
        var offset = 0;
        foreach (var chunk in _bargeInRecentAudio)
        {
            Buffer.BlockCopy(chunk, 0, audio, offset, chunk.Length);
            offset += chunk.Length;
        }

        _bargeInRecentAudio.Clear();
        _bargeInRecentBytes = 0;
        return audio;
    }

    // Called by StartListening while _audioBuffer is new and empty, before the microphone starts.
    private void SeedRecordingWithBargeInAudio()
    {
        _bargeInSeedBytes = 0;
        var preRoll = Interlocked.Exchange(ref _bargeInPreRoll, null);
        if (preRoll == null || _audioBuffer == null || DateTime.UtcNow - preRoll.CapturedUtc > BargeInPreRollMaxAge)
            return;

        _audioBuffer.Write(preRoll.Pcm, 0, preRoll.Pcm.Length);
        _bargeInSeedBytes = preRoll.Pcm.Length;
    }

    // Called for every recorded buffer. The seeded words are kept once the microphone hears the user keep
    // talking. When it hears nothing for a moment (a lone "stop", or speaker echo that set it off) they are
    // dropped, so they never reach Whisper on their own or in front of a later sentence.
    private void KeepOrDropBargeInAudio()
    {
        var buffer = _audioBuffer;
        if (_bargeInSeedBytes <= 0 || buffer == null)
            return;

        if (_voiceDetected)
        {
            _bargeInSeedBytes = 0;
            return;
        }

        try
        {
            var length = (int)buffer.Length;
            if (length - _bargeInSeedBytes < BargeInSeedWaitMilliseconds * PcmBytesPerMillisecond)
                return;

            var data = buffer.GetBuffer();
            Buffer.BlockCopy(data, _bargeInSeedBytes, data, 0, length - _bargeInSeedBytes);
            buffer.SetLength(length - _bargeInSeedBytes);
            buffer.Position = buffer.Length;
        }
        catch (ObjectDisposedException)
        {
            // Listening was stopped meanwhile.
        }

        _bargeInSeedBytes = 0;
    }
}
