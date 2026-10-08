using System;
using System.Diagnostics;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace VoiceChatbot;

/// <summary>An audio source could not start; the message is written for the user.</summary>
public sealed class TranscriberAudioException : Exception
{
    public TranscriberAudioException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Live audio for the Live Transcriber, always delivered as 16 kHz mono 16-bit PCM:
/// the app's selected microphone, or what the PC plays (WASAPI loopback of the default output device).
/// </summary>
public abstract class TranscriberAudioSource : IDisposable
{
    public const int SampleRate = 16000;

    /// <summary>Raised on a capture thread. The buffer is only valid during the call.</summary>
    public event Action<TranscriberAudioSource, byte[], int>? AudioAvailable;

    /// <summary>Raised when capture ends by itself (for example the device was unplugged), with the error if any.</summary>
    public event Action<TranscriberAudioSource, Exception?>? Stopped;

    /// <summary>"the microphone (USB Mic)" or "PC audio (Speakers)", for status text.</summary>
    public string DisplayName { get; protected set; } = "";

    /// <summary>A note for the user when the source started differently than asked (e.g. a fallback device).</summary>
    public string? Notice { get; protected set; }

    public static TranscriberAudioSource Create(string source, int micDeviceIndex) =>
        source == TranscriberSettings.SourcePcAudio
            ? new PcAudioTranscriberSource()
            : new MicrophoneTranscriberSource(micDeviceIndex);

    /// <summary>Starts capturing. Throws <see cref="TranscriberAudioException"/> with a friendly message on failure.</summary>
    public abstract void Start();

    /// <summary>Stops delivering audio. Safe to call more than once.</summary>
    public abstract void Stop();

    public abstract void Dispose();

    protected void RaiseAudio(byte[] buffer, int count)
    {
        if (count > 0)
            AudioAvailable?.Invoke(this, buffer, count);
    }

    protected void RaiseStopped(Exception? error) => Stopped?.Invoke(this, error);
}

/// <summary>The microphone selected in the app (or the Windows default when that one cannot open).</summary>
public sealed class MicrophoneTranscriberSource : TranscriberAudioSource
{
    private readonly int _deviceIndex;
    private WaveInEvent? _waveIn;
    private volatile bool _running;

    public MicrophoneTranscriberSource(int deviceIndex)
    {
        _deviceIndex = deviceIndex;
    }

    public override void Start()
    {
        if (WaveInEvent.DeviceCount == 0)
            throw new TranscriberAudioException("No microphone was found. Connect one, or check Windows Sound settings.");

        try
        {
            Open(_deviceIndex);
        }
        catch (Exception first) when (_deviceIndex != -1)
        {
            CloseDevice();
            try
            {
                Open(-1);
                Notice = $"The selected microphone could not open ({first.Message}), so the Windows default microphone is used.";
            }
            catch (Exception second)
            {
                CloseDevice();
                throw new TranscriberAudioException($"The microphone could not start: {second.Message}", second);
            }
        }
        catch (Exception ex)
        {
            CloseDevice();
            throw new TranscriberAudioException($"The microphone could not start: {ex.Message}", ex);
        }
    }

    private void Open(int deviceIndex)
    {
        var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 100,
            DeviceNumber = deviceIndex
        };
        _waveIn = waveIn;
        waveIn.DataAvailable += (_, e) =>
        {
            if (_running)
                RaiseAudio(e.Buffer, e.BytesRecorded);
        };
        waveIn.RecordingStopped += (_, e) =>
        {
            if (_running)
            {
                _running = false;
                RaiseStopped(e.Exception);
            }
        };

        _running = true;
        waveIn.StartRecording();
        DisplayName = $"the microphone ({DeviceName(deviceIndex)})";
    }

    private static string DeviceName(int deviceIndex)
    {
        if (deviceIndex < 0)
            return "Windows default";

        try
        {
            return WaveInEvent.GetCapabilities(deviceIndex).ProductName;
        }
        catch
        {
            return $"device {deviceIndex}";
        }
    }

    public override void Stop()
    {
        _running = false;
        try { _waveIn?.StopRecording(); } catch { }
    }

    public override void Dispose()
    {
        Stop();
        CloseDevice();
    }

    private void CloseDevice()
    {
        _running = false;
        var waveIn = _waveIn;
        _waveIn = null;
        try { waveIn?.Dispose(); } catch { }
    }
}

/// <summary>
/// What the PC plays, recorded with WASAPI loopback from the default output device and converted to
/// 16 kHz mono. Loopback delivers no packets while nothing plays, so silence is filled in for those gaps:
/// that keeps timestamps in step and lets the pause detector see the pause.
/// </summary>
public sealed class PcAudioTranscriberSource : TranscriberAudioSource
{
    private static readonly TimeSpan GapBeforeFill = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaxGapFill = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private MMDevice? _device;
    private WasapiLoopbackCapture? _capture;
    private BufferedWaveProvider? _buffer;
    private ISampleProvider? _mono16k;
    private Timer? _gapTimer;
    private float[] _samples = new float[SampleRate];
    private byte[] _pcm = new byte[SampleRate * 2];
    private long _lastAudioTimestamp;
    private bool _running;

    public override void Start()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (Exception ex)
        {
            throw new TranscriberAudioException(
                "No speakers or headphones were found to record PC audio from. Check the output device in Windows Sound settings.", ex);
        }

        try
        {
            var capture = new WasapiLoopbackCapture(_device);
            // The mix format is usually WAVE_FORMAT_EXTENSIBLE (32-bit float); the sample converters want the plain form.
            var format = capture.WaveFormat.AsStandardWaveFormat();
            _buffer = new BufferedWaveProvider(format)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false,
                BufferDuration = TimeSpan.FromSeconds(5)
            };

            var samples = _buffer.ToSampleProvider();
            if (format.Channels > 1)
                samples = new DownmixToMono(samples);
            if (format.SampleRate != SampleRate)
                samples = new WdlResamplingSampleProvider(samples, SampleRate);
            _mono16k = samples;

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += (_, e) =>
            {
                bool wasRunning;
                lock (_gate)
                {
                    wasRunning = _running;
                    _running = false;
                }

                if (wasRunning)
                    RaiseStopped(e.Exception);
            };
            _capture = capture;

            lock (_gate)
            {
                _running = true;
                _lastAudioTimestamp = Stopwatch.GetTimestamp();
            }

            capture.StartRecording();
            _gapTimer = new Timer(_ => FillSilentGap(), null, 200, 200);
            DisplayName = $"PC audio ({_device.FriendlyName})";
        }
        catch (Exception ex)
        {
            Dispose();
            throw new TranscriberAudioException($"PC audio could not be recorded: {ex.Message}", ex);
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_gate)
        {
            if (!_running || _buffer == null || _mono16k == null)
                return;

            _lastAudioTimestamp = Stopwatch.GetTimestamp();
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);

            int read;
            while ((read = _mono16k.Read(_samples, 0, _samples.Length)) > 0)
            {
                for (var i = 0; i < read; i++)
                {
                    var value = (short)(Math.Clamp(_samples[i], -1f, 1f) * short.MaxValue);
                    _pcm[2 * i] = (byte)value;
                    _pcm[2 * i + 1] = (byte)(value >> 8);
                }

                RaiseAudio(_pcm, read * 2);
            }
        }
    }

    private void FillSilentGap()
    {
        lock (_gate)
        {
            if (!_running)
                return;

            var gap = Stopwatch.GetElapsedTime(_lastAudioTimestamp);
            if (gap < GapBeforeFill)
                return;

            if (gap > MaxGapFill)
                gap = MaxGapFill;
            var bytes = (int)(gap.TotalSeconds * SampleRate) * 2;
            if (_pcm.Length < bytes)
                _pcm = new byte[bytes];
            Array.Clear(_pcm, 0, bytes);
            _lastAudioTimestamp = Stopwatch.GetTimestamp();
            RaiseAudio(_pcm, bytes);
        }
    }

    public override void Stop()
    {
        lock (_gate)
            _running = false;

        try { _gapTimer?.Dispose(); } catch { }
        _gapTimer = null;
        try { _capture?.StopRecording(); } catch { }
    }

    public override void Dispose()
    {
        Stop();
        try { _capture?.Dispose(); } catch { }
        _capture = null;
        try { _device?.Dispose(); } catch { }
        _device = null;
    }

    /// <summary>Averages all channels into one (StereoToMonoSampleProvider only takes two).</summary>
    private sealed class DownmixToMono : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _buffer = Array.Empty<float>();

        public DownmixToMono(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var needed = count * _channels;
            if (_buffer.Length < needed)
                _buffer = new float[needed];

            var frames = _source.Read(_buffer, 0, needed) / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                var start = frame * _channels;
                for (var channel = 0; channel < _channels; channel++)
                    sum += _buffer[start + channel];
                buffer[offset + frame] = sum / _channels;
            }

            return frames;
        }
    }
}
