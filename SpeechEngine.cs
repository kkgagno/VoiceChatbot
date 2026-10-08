using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NAudio.Wave;
using Whisper.net;

namespace VoiceChatbot;

public enum VoiceState
{
    Idle,
    Listening,
    Processing,
    Speaking
}

public class AudioDeviceInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public override string ToString() => $"[{Index}] {Name}";
}

public partial class SpeechEngine : IDisposable
{
    // Components
    private bool _disposed;
    private CancellationTokenSource? _listenCts;
    private System.Timers.Timer? _listenTimeout;
    private WhisperFactory? _whisperFactory;
    private WhisperProcessor? _whisperProcessor;
    private WaveInEvent? _waveIn;
    private MemoryStream? _audioBuffer;
    private NAudio.Wave.WaveOutEvent? _waveOut;
    private ManualResetEvent? _playbackStopSignal;
    private CancellationTokenSource _speechStopCts = new();
    private System.Diagnostics.Process? _kokoroProcess;
    private System.Diagnostics.Process? _kokoroServerProcess;
    private readonly object _kokoroServerLock = new();
    private const string KokoroServerUrl = "http://127.0.0.1:8765";
    // The bundled server only writes WAV files into this folder and only answers requests that
    // carry this per-launch token, so other local programs and web pages cannot use it.
    private static readonly string KokoroServerOutputDirectory = Path.Combine(Path.GetTempPath(), "VoiceChatbot", "tts");
    private readonly string _kokoroServerToken = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    private DateTime _remoteKokoroRetryAfterUtc = DateTime.MinValue;
    private DateTime _kokoroServerRetryAfterUtc = DateTime.MinValue;
    private string _remoteKokoroFailedUrl = "";
    private readonly SemaphoreSlim _whisperLock = new(1, 1);
    // One Ryzen AI (NPU) command at a time: the NPU is not shared well between processes.
    private readonly SemaphoreSlim _ryzenLock = new(1, 1);
    private bool _isRecording;
    private bool _isProcessing;

    // Silence detection
    private int _silenceThreshold;
    private const int AudioBucketMilliseconds = 100;
    private const int MIN_VOICE_BUCKETS = 1; // ~100ms is enough for a one-word reply
    private const int PreRollBytes = 400 * 32;              // ~400 ms of 16 kHz 16-bit mono kept before speech
    private const int MaxUtteranceBytes = 30 * 1000 * 32;   // one utterance is cut off after ~30 s
    private int _silenceBucketCount;
    private int _voiceBucketCount;
    private bool _voiceDetected; // Only detect silence AFTER hearing voice
    private int _logThrottle; // throttle diagnostic logs

    // Events
    public event Action<VoiceState>? StateChanged;
    public event Action<string>? SpeechRecognized;
    public event Action<float>? VolumeLevelChanged;
    public event Action? ListeningTimedOut;
    public event Action? SpeechFinished;
    public event Action<string>? Log;

    // Settings
    public string InputLanguage { get; set; } = "en";
    public double SilenceTimeout { get; set; } = 2.4;
    public int NoiseGate { get; set; } = 30;
    public bool AutoDetect { get; set; } = true;
    public string WakeWord { get; set; } = WakeWordText.DefaultPhrase;
    public string VoiceName { get; set; } = "am_onyx (American Male)";
    public int SpeechRate { get; set; } = 1;
    public int Volume { get; set; } = 80;
    public bool TtsEnabled { get; set; } = true;
    public int MicDeviceIndex { get; set; } = -1;
    public string WhisperModelPath { get; set; } = "";
    public string TranscriptionBackend { get; set; } = "Whisper.net";
    private string _externalNpuTranscriberCommand = "";
    private bool _ryzenFailureReported;
    public string ExternalNpuTranscriberCommand
    {
        get => _externalNpuTranscriberCommand;
        set
        {
            if (value == _externalNpuTranscriberCommand)
                return;
            _externalNpuTranscriberCommand = value;
            _ryzenFailureReported = false;
        }
    }
    public string KokoroMode { get; set; } = KokoroEndpoint.ModeAuto;
    public string KokoroRemoteUrl { get; set; } = "";

    // State
    public VoiceState CurrentState { get; private set; } = VoiceState.Idle;
    public string InitError { get; private set; } = "";
    public bool IsInitialized { get; private set; }
    public string LastTranscriptionBackendUsed { get; private set; } = "Whisper.net";
    public string LastTtsBackendUsed { get; private set; } = "";
    public event Action<string>? TtsBackendUsed;
    /// <summary>Raised once when the Ryzen AI command does not exist on this PC; the engine has already switched to Whisper.net.</summary>
    public event Action<string>? RyzenTranscriberUnavailable;

    // Hardware
    public List<string> AvailableVoices { get; private set; } = new();
    public List<AudioDeviceInfo> AvailableMics { get; private set; } = new();
    public List<string> AvailableLanguages { get; } = new()
    {
        "en", "de", "fr", "es", "it", "pt", "ja", "ko", "zh",
        "ru", "pl", "nl", "sv", "da", "fi", "no", "tr", "ar",
        "hi", "th", "cs", "el", "he", "hu", "ro", "sk", "uk"
    };

    public void Initialize()
    {
        RefreshMicrophones();

        // Init Whisper
        try
        {
            InitWhisper();
        }
        catch (Exception ex)
        {
            InitError += $"Whisper init failed: {ex.Message}. ";
        }

        // Init TTS voices - Kokoro voices (local, high quality)
        AvailableVoices = new List<string>
        {
            "af_bella (American Female)",
            "af_nicole (American Female)",
            "af_sarah (American Female)",
            "af_sky (American Female)",
            "am_adam (American Male)",
            "am_michael (American Male)",
            "am_onyx (American Male)",
            "bf_emma (British Female)",
            "bf_isabella (British Female)",
            "bm_george (British Male)",
            "bm_lewis (British Male)"
        };

        // Start persistent Kokoro server in the background so TTS stays warm,
        // unless speech is configured to come only from a remote Kokoro host.
        if (KokoroEndpoint.NormalizeMode(KokoroMode) != KokoroEndpoint.ModeRemoteOnly)
        {
            var kokoroWarmupThread = new Thread(() => EnsureKokoroServerStarted(waitForReady: false));
            kokoroWarmupThread.IsBackground = true;
            kokoroWarmupThread.Start();
        }

        // Silence threshold from noise gate (0-100 -> actual sample threshold)
        _silenceThreshold = (int)(NoiseGate * 327.68); // 0-32768 range

        IsInitialized = true;
    }

    public void RefreshMicrophones()
    {
        try
        {
            AvailableMics.Clear();
            AvailableMics.Add(new AudioDeviceInfo { Index = -1, Name = "Default Microphone" });
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var cap = WaveInEvent.GetCapabilities(i);
                AvailableMics.Add(new AudioDeviceInfo { Index = i, Name = cap.ProductName });
            }
        }
        catch (Exception ex)
        {
            InitError += $"Mic enum failed: {ex.Message}. ";
            AvailableMics.Clear();
            AvailableMics.Add(new AudioDeviceInfo { Index = -1, Name = "Default" });
        }
    }

    private void InitWhisper()
    {
        // Look for ggml model file
        var modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceChatbot");
        Directory.CreateDirectory(modelDir);

        // Try user-specified path first, then default locations
        var modelPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(WhisperModelPath))
            modelPaths.Add(WhisperModelPath);
        modelPaths.Add(Path.Combine(modelDir, "ggml-base.bin"));
        modelPaths.Add(Path.Combine(modelDir, "ggml-tiny.bin"));
        modelPaths.Add(Path.Combine(modelDir, "ggml-small.bin"));
        modelPaths.Add(Path.Combine(modelDir, "ggml-medium.bin"));
        modelPaths.Add(Path.Combine(modelDir, "ggml-base.en.bin"));
        modelPaths.Add(Path.Combine(modelDir, "ggml-small.en.bin"));
        // Also check next to the exe
        modelPaths.Add("ggml-tiny.bin");
        modelPaths.Add("ggml-base.bin");
        modelPaths.Add("ggml-small.bin");
        modelPaths.Add("ggml-medium.bin");
        modelPaths.Add("ggml-base.en.bin");
        modelPaths.Add("ggml-small.en.bin");

        string? foundModel = null;
        foreach (var p in modelPaths)
        {
            if (File.Exists(p))
            {
                foundModel = p;
                break;
            }
        }

        if (foundModel == null)
        {
            InitError += "No Whisper model found. Download one from Settings. ";
            return;
        }

        LoadWhisperModel(foundModel);
    }

    private WhisperProcessor BuildWhisperProcessor(WhisperFactory factory)
    {
        var langCode = InputLanguage ?? "en";
        if (langCode == "en-US" || langCode == "en-GB") langCode = "en";
        if (langCode.Contains('-')) langCode = langCode.Split('-')[0];

        // Each utterance is transcribed on its own: no text from the previous one as the prompt
        // (WithNoContext), and one segment per 30 s window (WithSingleSegment), so an early timestamp
        // cannot make Whisper decode the same audio again and return the sentence twice.
        return factory.CreateBuilder()
            .WithLanguage(langCode)
            .WithNoContext()
            .WithSingleSegment()
            .Build();
    }

    /// <summary>
    /// Changes the recognition language. The Whisper processor is rebuilt under the transcription
    /// lock, so the new language is used from the next utterance without restarting the app.
    /// </summary>
    public async Task SetInputLanguageAsync(string language)
    {
        if (string.IsNullOrWhiteSpace(language) || string.Equals(language, InputLanguage, StringComparison.Ordinal))
            return;

        InputLanguage = language;
        await _whisperLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _whisperFactory == null)
                return;

            var rebuilt = BuildWhisperProcessor(_whisperFactory);
            var old = _whisperProcessor;
            _whisperProcessor = rebuilt;
            old?.Dispose();
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Could not switch the recognition language to {language}: {ex.Message}");
        }
        finally
        {
            _whisperLock.Release();
        }
    }

    /// <summary>
    /// Download a Whisper model. Call from UI on a background thread.
    /// </summary>
    public async Task DownloadModelAsync(string size = "base", IProgress<float>? progress = null)
    {
        var modelDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceChatbot");
        Directory.CreateDirectory(modelDir);

        var fileName = size == "tiny" ? "ggml-tiny.bin" :
                       size == "base" ? "ggml-base.bin" :
                       size == "small" ? "ggml-small.bin" :
                       size == "medium" ? "ggml-medium.bin" : "ggml-base.bin";

        var targetPath = Path.Combine(modelDir, fileName);

        // HuggingFace download URLs for Whisper models
        var urls = new Dictionary<string, string>
        {
            ["tiny"] = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-tiny.bin",
            ["base"] = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin",
            ["small"] = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small.bin",
            ["medium"] = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-medium.bin",
        };

        if (!urls.TryGetValue(size, out var url))
            url = urls["base"];

        var partialPath = targetPath + ".download";
        try { File.Delete(partialPath); } catch { }

        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await client.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Whisper model download returned HTTP {(int)response.StatusCode} from {url}");

        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        var bytesRead = 0L;

        using var stream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, read);
            bytesRead += read;
            if (totalBytes > 0)
                progress?.Report((float)bytesRead / totalBytes);
        }

        await fileStream.FlushAsync();
        fileStream.Close();
        File.Move(partialPath, targetPath, overwrite: true);

        // Re-init with the new model. A transcription may be using the processor right now, so wait for it.
        await _whisperLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _whisperProcessor?.Dispose();
            _whisperProcessor = null;
            _whisperFactory?.Dispose();
            _whisperFactory = null;
            WhisperModelPath = targetPath;
            InitWhisper();
        }
        finally
        {
            _whisperLock.Release();
        }
    }

    // ==================== Recording ====================

    public bool StartListening()
    {
        if (_isProcessing || _isRecording)
        {
            return false;
        }

        var canUseExternalTranscriber =
            IsRyzenAiSelected() &&
            !string.IsNullOrWhiteSpace(ExternalNpuTranscriberCommand);
        if (_whisperProcessor == null && !canUseExternalTranscriber)
        {
            Log?.Invoke("Voice input is not ready: no Whisper model is installed. Choose a Whisper model and click Download Model.");
            return false;
        }

        // Clean up any previous WaveIn
        if (_waveIn != null)
        {
            try { _waveIn.StopRecording(); } catch { }
            try { _waveIn.Dispose(); } catch { }
            _waveIn = null;
        }

        _silenceBucketCount = 0;
        _voiceBucketCount = 0;
        _voiceDetected = false;
        _logThrottle = 0;
        BeginWakeTurnIfStarting();

        // Audio buffer to store PCM data
        _audioBuffer = new MemoryStream();

        try
        {
            StartWaveInDevice(MicDeviceIndex);
            _isRecording = true;
            SetState(VoiceState.Listening);
            return true;
        }
        catch (Exception ex)
        {
            var firstError = ex.Message;
            try
            {
                _waveIn?.Dispose();
                _waveIn = null;

                var fallbackDevice = MicDeviceIndex == -1 && WaveInEvent.DeviceCount > 0
                    ? 0
                    : -1;
                if (fallbackDevice != MicDeviceIndex)
                {
                    StartWaveInDevice(fallbackDevice);
                    _isRecording = true;
                    SetState(VoiceState.Listening);
                    var fallbackName = fallbackDevice == -1
                        ? "Windows default microphone"
                        : WaveInEvent.GetCapabilities(fallbackDevice).ProductName;
                    Log?.Invoke($"Could not open the selected microphone ({firstError}). Using {fallbackName} instead.");
                    return true;
                }
            }
            catch (Exception fallbackEx)
            {
                firstError += $" Default microphone also failed: {fallbackEx.Message}";
            }

            _isRecording = false;
            _audioBuffer?.Dispose();
            _audioBuffer = null;
            try { _waveIn?.Dispose(); } catch { }
            _waveIn = null;
            SetState(VoiceState.Idle);
            Log?.Invoke($"Microphone could not start: {firstError}");
            ListeningTimedOut?.Invoke();
            return false;
        }
    }

    private void StartWaveInDevice(int deviceNumber)
    {
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 16, 1),
            BufferMilliseconds = 100,
            DeviceNumber = deviceNumber
        };

        _waveIn.DataAvailable += OnAudioDataAvailable;
        _waveIn.RecordingStopped += (_, e) =>
        {
            if (e.Exception != null && _isRecording)
                Log?.Invoke($"Microphone recording stopped unexpectedly: {e.Exception.Message}");
        };
        _waveIn.StartRecording();
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isRecording) return;

        // Write raw audio to buffer
        _audioBuffer?.Write(e.Buffer, 0, e.BytesRecorded);

        // Calculate volume - use RMS for better voice detection
        double sumSquares = 0;
        int sampleCount = e.BytesRecorded / 2;
        float peak = 0;
        for (int i = 0; i < e.BytesRecorded; i += 2)
        {
            short sample = (short)(e.Buffer[i + 1] << 8 | e.Buffer[i]);
            float normalized = Math.Abs(sample / 32768f);
            if (normalized > peak) peak = normalized;
            sumSquares += (double)sample * sample;
        }
        double rms = Math.Sqrt(sumSquares / sampleCount);
        float rmsNorm = (float)(rms / 32768.0);
        
        // Volume meter uses peak
        VolumeLevelChanged?.Invoke(peak * 100);

        // Voice detection: use RMS which is much more reliable than peak
        // RMS of 0.01-0.05 is typical quiet speech, 0.05+ is normal speech.
        // Keep the gate modest so short/quiet phrases do not sit in the buffer until the next utterance.
        var voiceThreshold = Math.Max(0.004f, NoiseGate / 5000f);
        bool isVoice = rmsNorm > voiceThreshold;
        if (IsWakeTurnChime())
            isVoice = false;

        // Log audio levels periodically (every 20 chunks = ~2 seconds)
        _logThrottle++;
        if (_logThrottle % 20 == 0)
        {
        }

        if (isVoice)
        {
            _voiceBucketCount++;
            _silenceBucketCount = 0;
            if (_voiceBucketCount >= MIN_VOICE_BUCKETS)
                _voiceDetected = true;
        }
        else if (_voiceDetected)
        {
            _silenceBucketCount++;
        }
        else if (_voiceBucketCount > 0)
        {
            _voiceBucketCount--;
        }

        TrimAudioBeforeSpeech();

        if (WakeTurnHeardNothing())
        {
            StopRecordingAndProcess();
            return;
        }

        // Only process if: we heard voice, then silence, and have enough audio
        if (_voiceDetected && _silenceBucketCount >= RequiredSilenceBuckets && _audioBuffer?.Length > 16000)
        {
            StopRecordingAndProcess();
            return;
        }

        // Someone talking without a pause (or steady noise above the gate) must not record forever.
        if (_voiceDetected && _audioBuffer?.Length >= MaxUtteranceBytes)
            StopRecordingAndProcess();
    }

    // Always the configured pause: a shorter one after a short opener cut sentences such as
    // "Hey Hermes ... start gemma" in two.
    private int RequiredSilenceBuckets =>
        Math.Clamp((int)Math.Round(SilenceTimeout * 1000 / AudioBucketMilliseconds), 5, 100);

    // Capture thread: until voice is heard keep only the last ~400 ms, so the words are not preceded
    // by seconds of silence or background noise.
    private void TrimAudioBeforeSpeech()
    {
        var buffer = _audioBuffer;
        if (_voiceDetected || buffer == null)
            return;

        try
        {
            var length = (int)buffer.Length;
            if (length <= PreRollBytes)
                return;

            var data = buffer.GetBuffer();
            Buffer.BlockCopy(data, length - PreRollBytes, data, 0, PreRollBytes);
            buffer.SetLength(PreRollBytes);
            buffer.Position = PreRollBytes;
        }
        catch (ObjectDisposedException)
        {
            // Listening was stopped meanwhile.
        }
    }

    private void StopRecordingAndProcess()
    {
        if (!_isRecording) return;

        _isRecording = false;

        try { _waveIn?.StopRecording(); } catch { }

        var audioData = _audioBuffer?.ToArray();
        _audioBuffer?.Dispose();
        _audioBuffer = null;

        if (audioData == null || audioData.Length < 16000 || _voiceBucketCount < MIN_VOICE_BUCKETS)
        {
            _isProcessing = false;
            SetState(VoiceState.Idle);
            ListeningTimedOut?.Invoke();
            return;
        }

        // Wrap raw PCM in a WAV format - Whisper needs a valid WAV stream
        var wavStream = new MemoryStream();
        // Write WAV header
        var wavData = audioData.ToArray();
        int sampleRate = 16000;
        short bitsPerSample = 16;
        short channels = 1;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        short blockAlign = (short)(channels * bitsPerSample / 8);

        using (var writer = new BinaryWriter(wavStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + wavData.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(wavData.Length);
            writer.Write(wavData);
        }
        wavStream.Position = 0;

        // Process with Whisper on background thread
        var wakeWordAlreadyHeard = _wakeWordAlreadyHeard;
        _isProcessing = true;
        SetState(VoiceState.Processing);

        _listenCts = new CancellationTokenSource();
        var token = _listenCts.Token;

        Task.Run(async () =>
        {
            try
            {
                var text = await TranscribeWavAsync(wavStream, token).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    // "Only respond after the wake word": no phrase or only the phrase ends the turn quietly.
                    var afterWakePhrase = ApplyWakePhrase(text.Trim(), wakeWordAlreadyHeard);
                    if (afterWakePhrase == null)
                    {
                        _isProcessing = false;
                        SetState(VoiceState.Idle);
                        ListeningTimedOut?.Invoke();
                        return;
                    }
                    text = afterWakePhrase;

                    if (IsIgnoredWhisperText(text))
                    {
                        Log?.Invoke($"Ignored non-speech transcription: {text}");
                        ListeningTimedOut?.Invoke();
                        return;
                    }

                    Log?.Invoke($"Transcription backend used: {LastTranscriptionBackendUsed}");
                    // Transcription is done; the chat turn that follows ends with ReadyForNextSpeech.
                    _isProcessing = false;
                    SpeechRecognized?.Invoke(text);
                }
                else
                {
                    ListeningTimedOut?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                ListeningTimedOut?.Invoke();
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Transcription failed: {ex.Message}");
                ListeningTimedOut?.Invoke();
            }
            finally
            {
                _isProcessing = false;
                try { wavStream.Dispose(); } catch { }
            }
        }, token);
    }

    public void StopListening()
    {
        _listenCts?.Cancel();

        if (_isRecording)
        {
            _isRecording = false;
            try { _waveIn?.StopRecording(); } catch { }
        }

        // Clean up WaveIn so a fresh one is created on next start
        try { _waveIn?.Dispose(); } catch { }
        _waveIn = null;

        _audioBuffer?.Dispose();
        _audioBuffer = null;
        _isProcessing = false;

        if (CurrentState == VoiceState.Listening)
            SetState(VoiceState.Idle);

    }

    /// <summary>
    /// A turn is over: allow the next recording, and leave the Processing state that a voice turn
    /// stays in after its transcription when nothing was spoken.
    /// </summary>
    public void ReadyForNextSpeech()
    {
        // _isProcessing is still set while a transcription runs; that Processing state is left alone.
        if (!_isProcessing && CurrentState == VoiceState.Processing)
            SetState(VoiceState.Idle);
        _isProcessing = false;
    }

    // ==================== TTS ====================
    // A finished reply is rendered in one Kokoro request (CreateSpeechAudioFileAsync) and played with
    // PlayAudioFile. "Start speaking before the reply finishes" speaks it in pieces through
    // SpeechSession (BeginSpeechSession). StopSpeaking stops both.

    /// <summary>
    /// Cancelled by the next StopSpeaking (Stop, Esc, the mic button, a replay). A reply that is still
    /// being rendered watches it, so it is dropped instead of played.
    /// </summary>
    public CancellationToken SpeechStopToken => Volatile.Read(ref _speechStopCts).Token;

    private (string Voice, string Lang) ResolveKokoroVoice()
    {
        // Voice names look like "am_onyx (American Male)"; Kokoro wants just the id.
        var voice = (VoiceName ?? "").Split('(')[0].Trim();
        if (string.IsNullOrWhiteSpace(voice))
            voice = "af_bella";

        return (voice, KokoroEndpoint.LanguageForVoice(voice));
    }

    public async Task<string?> CreateSpeechAudioFileAsync(string text, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(text) || !TtsEnabled)
            return null;

        Directory.CreateDirectory(outputDirectory);
        var (kokoroVoice, kokoroLang) = ResolveKokoroVoice();
        var wavFile = await Task.Run(() => GenerateKokoroAudioSync(text, kokoroVoice, kokoroLang)).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(wavFile) || !File.Exists(wavFile))
            return null;

        var savedPath = Path.Combine(outputDirectory, $"assistant_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.wav");
        File.Move(wavFile, savedPath);
        return savedPath;
    }

    public void PlayAudioFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        // A clip that is still playing (a replay) is replaced without raising SpeechFinished;
        // this playback raises it once when it ends.
        var previous = _playbackStopSignal;
        if (previous != null)
            _supersededPlaybackSignal = previous;
        StopSpeaking();
        try { _waveIn?.StopRecording(); } catch { }
        _isRecording = false;
        _isProcessing = true;
        SetState(VoiceState.Speaking);
        PlayWavOnThread(filePath, deleteAfterPlayback: false);
    }

    /// <summary>
    /// Transcribes one WAV clip. Safe to call from several places at once (voice input, the Live Transcriber,
    /// the phone remote and the web transcriber): Whisper.net runs one clip at a time under its lock and the
    /// Ryzen AI command one at a time under its own, so concurrent callers wait their turn instead of sharing
    /// the model.
    /// </summary>
    public async Task<string> TranscribeWavAsync(Stream wavStream, CancellationToken ct = default)
    {
        if (IsRyzenAiSelected())
        {
            if (string.IsNullOrWhiteSpace(ExternalNpuTranscriberCommand))
            {
                if (IsRyzenAiRequired())
                    throw new InvalidOperationException("AMD Ryzen AI Whisper is selected, but no Ryzen AI command is configured.");
            }
            else
            {
                try
                {
                    string external;
                    await _ryzenLock.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        external = await TranscribeWithRyzenAiAsync(wavStream, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _ryzenLock.Release();
                    }

                    if (!string.IsNullOrWhiteSpace(external))
                    {
                        LastTranscriptionBackendUsed = "AMD Ryzen AI Whisper";
                        return external;
                    }

                    if (IsRyzenAiRequired())
                        return "";
                }
                catch (RyzenCommandNotFoundException ex) when (_whisperProcessor != null)
                {
                    // Ryzen AI is not set up on this PC at all: stop trying it and use Whisper.net from now on.
                    TranscriptionBackend = "Whisper.net";
                    RyzenTranscriberUnavailable?.Invoke(ex.Message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Fall back to Whisper.net whenever it is available, so a broken Ryzen command
                    // never leaves the user unable to talk. Only report the failure once per command.
                    var canFallBack = _whisperProcessor != null;
                    if (!_ryzenFailureReported)
                    {
                        _ryzenFailureReported = true;
                        Log?.Invoke($"AMD Ryzen AI Whisper transcription failed: {ex.Message}" +
                                    (canFallBack ? " Using Whisper.net instead. Check the Ryzen AI command under Voice Input." : ""));
                    }

                    if (!canFallBack)
                        throw;
                }
            }
        }

        if (_whisperProcessor == null)
            throw new InvalidOperationException("Whisper is not initialized.");

        await _whisperLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Read under the lock: a model download or language change may have replaced it.
            var processor = _whisperProcessor ?? throw new InvalidOperationException("Whisper is not initialized.");
            if (wavStream.CanSeek)
                wavStream.Position = 0;

            var segments = new List<string>();
            await foreach (var segment in processor.ProcessAsync(wavStream, ct).ConfigureAwait(false))
            {
                var text = CleanWhisperSegment(segment.Text);
                if (!string.IsNullOrWhiteSpace(text))
                    segments.Add(text);
            }

            // Whisper can return the same sentence two or more times for one utterance.
            var cleaned = CleanTranscriptText(TranscriptCleanup.Clean(segments));
            if (CountWords(cleaned) < CountWords(string.Join(" ", segments)))
                AppLog.Info("Whisper repeated itself; the repeated sentences were removed.");
            LastTranscriptionBackendUsed = "Whisper.net";
            return IsIgnoredWhisperText(cleaned) ? "" : cleaned;
        }
        finally
        {
            _whisperLock.Release();
        }
    }

    public string GetTranscriptionBackendStatus()
    {
        if (IsRyzenAiSelected())
        {
            if (string.IsNullOrWhiteSpace(ExternalNpuTranscriberCommand))
            {
                return IsRyzenAiRequired()
                    ? "AMD Ryzen AI Whisper selected, command missing"
                    : "Whisper.net fallback; Ryzen AI command missing";
            }

            return IsRyzenAiRequired()
                ? "AMD Ryzen AI Whisper required"
                : "AMD Ryzen AI Whisper preferred with Whisper.net fallback";
        }

        return "Whisper.net";
    }

    private bool IsRyzenAiSelected()
    {
        return TranscriptionBackend.Contains("AMD Ryzen AI Whisper", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsRyzenAiRequired()
    {
        return IsRyzenAiSelected() && !TranscriptionBackend.Contains("fallback", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string> TranscribeWithRyzenAiAsync(Stream wavStream, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VoiceChatbot", "ryzen-ai-transcribe");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, $"chunk_{Guid.NewGuid():N}.wav");

        try
        {
            if (wavStream.CanSeek)
                wavStream.Position = 0;

            await using (var file = new FileStream(inputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                await wavStream.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            var command = BuildExternalTranscriberCommand(inputPath);
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = GetShellFileName(),
                Arguments = GetShellArguments(command),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    stdout.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    stderr.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var error = stderr.ToString().Trim();
                if (TranscriberCommand.IsNotFoundError(error, process.ExitCode))
                    throw new RyzenCommandNotFoundException(string.IsNullOrWhiteSpace(error)
                        ? $"the command exited with code {process.ExitCode}"
                        : error);
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"External NPU transcriber exited with code {process.ExitCode}."
                    : error);
            }

            var cleaned = TranscriptCleanup.CollapseRepeatedSentences(CleanTranscriptText(stdout.ToString()));
            return IsIgnoredWhisperText(cleaned) ? "" : cleaned;
        }
        finally
        {
            try { File.Delete(inputPath); } catch { }
        }
    }

    private string BuildExternalTranscriberCommand(string inputPath)
    {
        var quotedInput = QuoteShellArgument(inputPath);
        var command = ExternalNpuTranscriberCommand.Trim();
        return command.Contains("{input}", StringComparison.OrdinalIgnoreCase)
            ? command.Replace("{input}", quotedInput, StringComparison.OrdinalIgnoreCase)
            : $"{command} {quotedInput}";
    }

    private static string GetShellFileName()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "/bin/bash";
    }

    private static string GetShellArguments(string command)
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? $"/c {command}"
            : $"-lc {QuoteShellArgument(command)}";
    }

    private static string QuoteShellArgument(string value)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return "\"" + value.Replace("\"", "\\\"") + "\"";

        return "'" + value.Replace("'", "'\\''") + "'";
    }

    private static string CleanTranscriptText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var cleaned = Regex.Replace(text, @"\[[^\]]*\]", "");

        // Drop backend diagnostics that occasionally leak from whisper.cpp / Ryzen AI.
        cleaned = Regex.Replace(
            cleaned,
            @"(?im)^\s*(whisper_|ggml_|system_info:|main:|error:).*$",
            "");
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)whisper_vitisai_encode:\s*Vitis AI model inference completed\.?",
            "");
        cleaned = Regex.Replace(
            cleaned,
            @"(?i)whisper_vitisai_free:\s*releasing Vitis AI encoder context.*$",
            "",
            RegexOptions.Multiline);

        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned;
    }

    private static int CountWords(string text) => Regex.Matches(text, @"[\p{L}\p{N}]+").Count;

    private static string CleanWhisperSegment(string? text)
    {
        var segText = CleanTranscriptText(text);
        if (string.IsNullOrWhiteSpace(segText)) return "";
        if (segText == "[BLANK_AUDIO]" || segText == "[NOISE]" || segText == "[MUSIC]") return "";
        if (segText.StartsWith("[") && segText.EndsWith("]")) return "";
        if (segText.Equals("(inaudible)", StringComparison.OrdinalIgnoreCase)) return "";
        if (segText.Equals("Inaudible", StringComparison.OrdinalIgnoreCase)) return "";
        if (segText.Equals("(background sounds)", StringComparison.OrdinalIgnoreCase)) return "";
        if (segText.Equals("Background sounds", StringComparison.OrdinalIgnoreCase)) return "";
        if (segText.Equals("(light music)", StringComparison.OrdinalIgnoreCase)) return "";
        if (segText.Equals("Light music", StringComparison.OrdinalIgnoreCase)) return "";
        if (segText.Equals("light music", StringComparison.OrdinalIgnoreCase)) return "";
        return segText;
    }

    private static bool IsIgnoredWhisperText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var lowerText = text.ToLowerInvariant().Trim();
        var normalized = Regex.Replace(lowerText, @"[^\p{L}\p{N}' ]+", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized) || !normalized.Any(char.IsLetter))
            return true;

        var ignoredPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "inaudible",
            "background sounds",
            "background sound",
            "background noise",
            "light music",
            "music",
            "noise",
            "silence",
            "silent",
            "whistling",
            "whistle",
            "sigh",
            "sighing",
            "breathing",
            "breath",
            "sniff",
            "sniffing",
            "cough",
            "coughing",
            "click",
            "clicking",
            "keyboard",
            "keyboard clicking",
            "typing",
            "tap",
            "tapping",
            "clap",
            "clapping",
            "hmm",
            "hm",
            "mmm",
            "mm",
            "mhm",
            "mmhmm",
            "uh",
            "um",
            "umm",
            "ah",
            "eh",
            "er",
            "huh",
            "oh",
            "ooh",
            "woo",
            "whoa",
            "ha",
            "haha",
            "hehe",
            "thank you",
            "thanks",
            "thanks for watching",
            "subscribe",
            "like and subscribe",
            "you"
        };

        if (ignoredPhrases.Contains(normalized))
            return true;

        var words = Regex.Matches(normalized, @"[\p{L}\p{N}']+")
            .Select(m => m.Value.Trim('\''))
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .ToList();

        if (words.Count == 0)
            return true;

        var fillerWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hmm", "hm", "mmm", "mm", "mhm", "mmhmm", "uh", "um", "umm", "ah", "eh", "er",
            "huh", "oh", "ooh", "woo", "ha", "haha", "hehe", "sigh", "breath", "breathing",
            "whistle", "whistling", "click", "clicking", "keyboard", "typing", "noise", "music",
            "inaudible", "silence", "silent", "sniff", "sniffing", "cough", "coughing"
        };

        if (words.All(w => fillerWords.Contains(w)))
            return true;

        if (words.Count <= 2 && words.All(w => w.Length <= 2) && !words.Any(IsAllowedShortVoiceWord))
            return true;

        if (words.Count <= 3 && words.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1 &&
            !IsAllowedShortVoiceWord(words[0]))
            return true;

        return false;
    }

    private static bool IsAllowedShortVoiceWord(string word)
    {
        return word.Equals("no", StringComparison.OrdinalIgnoreCase)
               || word.Equals("go", StringComparison.OrdinalIgnoreCase)
               || word.Equals("ok", StringComparison.OrdinalIgnoreCase)
               || word.Equals("hi", StringComparison.OrdinalIgnoreCase)
               || word.Equals("up", StringComparison.OrdinalIgnoreCase)
               || word.Equals("on", StringComparison.OrdinalIgnoreCase)
               || word.Equals("off", StringComparison.OrdinalIgnoreCase)
               || word.Equals("yes", StringComparison.OrdinalIgnoreCase)
               || word.Equals("stop", StringComparison.OrdinalIgnoreCase)
               || word.Equals("continue", StringComparison.OrdinalIgnoreCase)
               || word.Equals("more", StringComparison.OrdinalIgnoreCase);
    }

    private string? GenerateKokoroAudioSync(string text, string voice, string lang = "a")
    {
        var mode = KokoroEndpoint.NormalizeMode(KokoroMode);
        var remoteBase = KokoroEndpoint.NormalizeBaseUrl(KokoroRemoteUrl);

        if (mode != KokoroEndpoint.ModeLocalOnly && remoteBase.Length > 0 && ShouldTryRemoteKokoro(remoteBase, mode))
        {
            var remoteWav = Path.Combine(Path.GetTempPath(), $"tts_remote_{Guid.NewGuid():N}.wav");
            try
            {
                if (TryGenerateKokoroViaRemote(text, voice, lang, remoteBase, remoteWav))
                {
                    _remoteKokoroRetryAfterUtc = DateTime.MinValue;
                    ReportTtsBackend($"Remote Kokoro ({new Uri(remoteBase).Authority})");
                    return remoteWav;
                }

                MarkRemoteKokoroFailed(remoteBase, "server returned no audio");
            }
            catch (Exception ex)
            {
                try { File.Delete(remoteWav); } catch { }
                MarkRemoteKokoroFailed(remoteBase, ex.GetBaseException().Message);
            }
        }

        if (mode == KokoroEndpoint.ModeRemoteOnly)
        {
            if (remoteBase.Length == 0)
                Log?.Invoke("[TTS] Kokoro is set to Remote only but no Kokoro host is configured.");
            return null;
        }

        // Local Kokoro only knows American and British English pipelines.
        var localLang = lang is "a" or "b" ? lang : "a";
        var tempWav = Path.Combine(KokoroServerOutputDirectory, $"tts_{Guid.NewGuid():N}.wav");

        try
        {
            if (TryGenerateKokoroViaServer(text, voice, localLang, tempWav))
            {
                ReportTtsBackend("Local Kokoro server");
                return tempWav;
            }
        }
        catch
        {
            try { File.Delete(tempWav); } catch { }
        }

        // Fallback: old one-shot Python path if remote and persistent local server are not available.
        var oneShot = GenerateKokoroAudioOneShotSync(text, voice, localLang);
        if (oneShot != null)
            ReportTtsBackend("Local Kokoro (one-shot)");
        return oneShot;
    }

    private bool ShouldTryRemoteKokoro(string remoteBase, string mode)
    {
        // After a failure in Auto mode, skip the remote host briefly so replies are not delayed
        // by repeated connection timeouts. Remote-only mode always tries.
        if (mode == KokoroEndpoint.ModeRemoteOnly)
            return true;

        return !string.Equals(_remoteKokoroFailedUrl, remoteBase, StringComparison.OrdinalIgnoreCase)
               || DateTime.UtcNow >= _remoteKokoroRetryAfterUtc;
    }

    private void MarkRemoteKokoroFailed(string remoteBase, string reason)
    {
        var firstFailure = !string.Equals(_remoteKokoroFailedUrl, remoteBase, StringComparison.OrdinalIgnoreCase)
                           || _remoteKokoroRetryAfterUtc == DateTime.MinValue;
        _remoteKokoroFailedUrl = remoteBase;
        _remoteKokoroRetryAfterUtc = DateTime.UtcNow.AddSeconds(60);
        if (firstFailure)
            Log?.Invoke($"[TTS] Remote Kokoro at {remoteBase} failed ({reason}).{(KokoroEndpoint.NormalizeMode(KokoroMode) == KokoroEndpoint.ModeAuto ? " Using local Kokoro for now." : "")}");
    }

    /// <summary>Clears the remote back-off so the next utterance retries the remote host immediately.</summary>
    public void ResetRemoteKokoroBackoff()
    {
        _remoteKokoroRetryAfterUtc = DateTime.MinValue;
        _remoteKokoroFailedUrl = "";
    }

    private void ReportTtsBackend(string backend)
    {
        if (backend == LastTtsBackendUsed)
            return;

        LastTtsBackendUsed = backend;
        TtsBackendUsed?.Invoke(backend);
    }

    private bool TryGenerateKokoroViaRemote(string text, string voice, string lang, string remoteBase, string outputPath)
    {
        var speed = 1.0 + (SpeechRate * 0.2);
        speed = Math.Max(0.5, Math.Min(2.0, speed));

        var remoteVoice = MapVoiceForRemoteKokoro(voice);
        var payload = new
        {
            model = "kokoro",
            input = text,
            voice = remoteVoice,
            response_format = "wav",
            speed,
            stream = false,
            return_download_link = false,
            lang_code = lang,
            volume_multiplier = 1.0
        };

        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
        using var response = KokoroEndpoint.Http.PostAsync(KokoroEndpoint.SpeechUrl(remoteBase), content).GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
            return false;

        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var audioBytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        if (audioBytes.Length < 100 || !contentType.Contains("audio", StringComparison.OrdinalIgnoreCase))
            return false;

        File.WriteAllBytes(outputPath, audioBytes);
        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 100;
    }

    private static string MapVoiceForRemoteKokoro(string voice)
    {
        return voice switch
        {
            "bf_isabella" => "bf_v0isabella",
            _ => voice
        };
    }

    private bool TryGenerateKokoroViaServer(string text, string voice, string lang, string outputPath)
    {
        if (!EnsureKokoroServerStarted(waitForReady: true))
            return false;

        var speed = 1.0 + (SpeechRate * 0.2);
        speed = Math.Max(0.5, Math.Min(2.0, speed));

        var payload = new
        {
            text,
            output = outputPath,
            voice,
            lang,
            speed
        };

        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        using var request = CreateKokoroServerRequest(System.Net.Http.HttpMethod.Post, "/tts");
        request.Content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
        using var response = client.SendAsync(request).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode || !body.Contains("\"ok\":true"))
        {
            try { File.Delete(outputPath); } catch { }
            return false;
        }

        return File.Exists(outputPath) && new FileInfo(outputPath).Length > 100;
    }

    private bool EnsureKokoroServerStarted(bool waitForReady)
    {
        if (IsKokoroServerHealthy(500))
            return true;

        // A server that could not start is not retried for every sentence.
        if (DateTime.UtcNow < _kokoroServerRetryAfterUtc)
            return false;

        lock (_kokoroServerLock)
        {
            if (_kokoroServerProcess != null && _kokoroServerProcess.HasExited)
            {
                try { _kokoroServerProcess.Dispose(); } catch { }
                _kokoroServerProcess = null;
            }

            if (_kokoroServerProcess == null)
            {
                var scriptPath = FindKokoroScript("kokoro_server.py");
                var pythonExe = FindPythonExecutable();
                if (string.IsNullOrWhiteSpace(scriptPath) || string.IsNullOrWhiteSpace(pythonExe))
                    return false;

                Directory.CreateDirectory(KokoroServerOutputDirectory);
                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                foreach (var argument in new[]
                {
                    scriptPath, "--host", "127.0.0.1", "--port", "8765", "--preload", "a,b",
                    "--out-dir", KokoroServerOutputDirectory, "--token", _kokoroServerToken
                })
                {
                    psi.ArgumentList.Add(argument);
                }

                var proc = Process.Start(psi);
                if (proc == null)
                    return false;

                _kokoroServerProcess = proc;
                proc.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data) && e.Data.Contains("KOKORO_SERVER_READY"))
                        Log?.Invoke("[TTS] Kokoro server ready");
                };
                proc.ErrorDataReceived += (s, e) => { };
                try { proc.BeginOutputReadLine(); proc.BeginErrorReadLine(); } catch { }
            }
        }

        if (!waitForReady)
            return true;

        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (IsKokoroServerHealthy(1000))
                return true;

            // It could not start, e.g. port 8765 is taken by a server from an earlier run that does
            // not know this run's token: use the one-shot fallback instead of waiting.
            Process? server;
            lock (_kokoroServerLock)
                server = _kokoroServerProcess;
            if (server == null || server.HasExited)
            {
                if (_kokoroServerRetryAfterUtc == DateTime.MinValue)
                    Log?.Invoke("[TTS] The local Kokoro server did not start (is port 8765 in use?). Using one-shot Kokoro for now.");
                _kokoroServerRetryAfterUtc = DateTime.UtcNow.AddMinutes(1);
                return false;
            }
            Thread.Sleep(250);
        }

        return false;
    }

    private System.Net.Http.HttpRequestMessage CreateKokoroServerRequest(System.Net.Http.HttpMethod method, string path)
    {
        var request = new System.Net.Http.HttpRequestMessage(method, KokoroServerUrl + path);
        request.Headers.Add("X-Kokoro-Token", _kokoroServerToken);
        return request;
    }

    private bool IsKokoroServerHealthy(int timeoutMs)
    {
        try
        {
            // A server without this run's token (another program, or an earlier run) answers 403.
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using var request = CreateKokoroServerRequest(System.Net.Http.HttpMethod.Get, "/health");
            using var response = client.SendAsync(request).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private string? GenerateKokoroAudioOneShotSync(string text, string voice, string lang = "a")
    {
        var tempWav = Path.Combine(Path.GetTempPath(), $"tts_{Guid.NewGuid():N}.wav");
        var tempText = Path.Combine(Path.GetTempPath(), $"tts_text_{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllText(tempText, text, new System.Text.UTF8Encoding(false));

            var speed = 1.0 + (SpeechRate * 0.2);
            speed = Math.Max(0.5, Math.Min(2.0, speed));

            var scriptPath = FindKokoroScript("kokoro_tts.py");
            var pythonExe = FindPythonExecutable();
            if (string.IsNullOrWhiteSpace(scriptPath) || string.IsNullOrWhiteSpace(pythonExe))
                return null;
            var arguments = $"\"{scriptPath}\" --file \"{tempText}\" --voice {voice} --lang {lang} --speed {speed:F1} --output \"{tempWav}\"";

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) { Log?.Invoke("[TTS] Could not start python!"); return null; }
            _kokoroProcess = proc; // track so StopSpeaking can kill it

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(300000); // 5 min timeout for long responses
            _kokoroProcess = null; // process finished on its own

            try { File.Delete(tempText); } catch { }

            if (!stdout.Contains("OK:"))
            {
                try { File.Delete(tempWav); } catch { }
                return null;
            }

            if (!File.Exists(tempWav) || new FileInfo(tempWav).Length < 100)
            {
                return null;
            }

            return tempWav;
        }
        catch
        {
            try { File.Delete(tempText); } catch { }
            try { File.Delete(tempWav); } catch { }
            return null;
        }
    }

    private async Task<string?> GenerateKokoroAudio(string text, string voice)
    {
        var tempWav = Path.Combine(Path.GetTempPath(), $"tts_{Guid.NewGuid():N}.wav");
        var tempText = Path.Combine(Path.GetTempPath(), $"tts_text_{Guid.NewGuid():N}.txt");

        try
        {
            await File.WriteAllTextAsync(tempText, text, new System.Text.UTF8Encoding(false));

            var speed = 1.0 + (SpeechRate * 0.2);
            speed = Math.Max(0.5, Math.Min(2.0, speed));

            var scriptPath = FindKokoroScript("kokoro_tts.py");
            var pythonExe = FindPythonExecutable();
            if (string.IsNullOrWhiteSpace(scriptPath) || string.IsNullOrWhiteSpace(pythonExe))
                return null;

            var arguments = $"\"{scriptPath}\" --file \"{tempText}\" --voice {voice} --speed {speed:F1} --output \"{tempWav}\"";

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return null;
            }

            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            var exited = await Task.Run(() => proc.WaitForExit(60000)); // 60s for first run (model loading)

            if (!exited)
            {
                try { proc.Kill(); } catch { }
                try { File.Delete(tempText); } catch { }
                try { File.Delete(tempWav); } catch { }
                return null;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            try { File.Delete(tempText); } catch { }

            if (!stdout.StartsWith("OK:"))
            {
                try { File.Delete(tempWav); } catch { }
                return null;
            }

            if (!File.Exists(tempWav) || new FileInfo(tempWav).Length < 100)
            {
                return null;
            }

            return tempWav;
        }
        catch (Exception ex)
        {
            try { File.Delete(tempText); } catch { }
            try { File.Delete(tempWav); } catch { }
            return null;
        }
    }

    private static string? FindKokoroScript(string fileName) => PythonTools.FindToolScript("Kokoro", fileName);

    private static string? FindPythonExecutable() => PythonTools.FindPythonExecutable();

    private void PlayWavOnThread(string filePath, bool deleteAfterPlayback = true)
    {
        // Published before the thread starts, so StopSpeaking right after PlayAudioFile stops this clip too.
        var done = new ManualResetEvent(false);
        _playbackStopSignal = done;
        var thread = new Thread(() =>
        {
            NAudio.Wave.WaveFileReader? reader = null;
            NAudio.Wave.WaveOutEvent? output = null;
            try
            {
                reader = new NAudio.Wave.WaveFileReader(filePath);
                output = new NAudio.Wave.WaveOutEvent();
                output.Volume = Math.Max(0.01f, Math.Min(Volume / 100f, 1f));
                output.PlaybackStopped += (s, e) =>
                {
                    try { done.Set(); } catch { }
                };
                output.Init(reader);
                _waveOut = output;
                if (!done.WaitOne(0))
                {
                    output.Play();
                    done.WaitOne(TimeSpan.FromMinutes(10));
                }
            }
            catch (Exception ex)
            {
                Log?.Invoke($"[TTS] Could not play speech: {ex.Message}");
            }
            finally
            {
                // Only clear the fields while they still belong to this clip; a newer one may own them.
                Interlocked.CompareExchange(ref _waveOut, null, output);
                try { output?.Stop(); } catch { }
                try { output?.Dispose(); } catch { }
                try { reader?.Dispose(); } catch { }
                if (deleteAfterPlayback)
                {
                    try { File.Delete(filePath); } catch { }
                }
                Interlocked.CompareExchange(ref _playbackStopSignal, null, done);
                // A newer clip or a speech session that took over playback raises SpeechFinished itself.
                if (!ReferenceEquals(Interlocked.CompareExchange(ref _supersededPlaybackSignal, null, done), done))
                {
                    SetState(VoiceState.Idle);
                    SpeechFinished?.Invoke();
                }
            }
        });
        thread.IsBackground = true;
        thread.Start();
    }

    private void SetState(VoiceState newState)
    {
        if (this.CurrentState != newState)
        {
            this.CurrentState = newState;
            this.StateChanged?.Invoke(newState);
        }
    }

    private static string CleanTextForSpeech(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Remove emojis
        var cleaned = Regex.Replace(text, @"[\U0001F600-\U0001F64F\U0001F300-\U0001F5FF\U0001F680-\U0001F6FF\U0001F1E0-\U0001F1FF\U00002702-\U000027B0\U0000FE00-\U0000FEFF\U0001F900-\U0001F9FF\U0001FA00-\U0001FA6F\U0001FA70-\U0001FAFF\u2600-\u26FF]", "");
        // Remove markdown code blocks
        cleaned = Regex.Replace(cleaned, @"```[\s\S]*?```", "");
        // Remove inline code
        cleaned = Regex.Replace(cleaned, @"`(.*?)`", "$1");
        // Remove bold/italic markers
        cleaned = Regex.Replace(cleaned, @"\*{1,3}(.*?)\*{1,3}", "$1");
        // Remove links
        cleaned = Regex.Replace(cleaned, @"\[(.*?)\]\(.*?\)", "$1");
        // Remove headers
        cleaned = Regex.Replace(cleaned, @"^#+\s+", "", RegexOptions.Multiline);
        // Collapse whitespace
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
        return cleaned.Trim();
    }

    public void StopSpeaking()
    {
        // A reply that is still being rendered is not played.
        try { Interlocked.Exchange(ref _speechStopCts, new CancellationTokenSource()).Cancel(); } catch { }
        // Cancel a sentence-by-sentence speech session: drops its queue and stops its clip.
        CancelSpeechSession();
        // Stop audio playback
        try { _waveOut?.Stop(); } catch { }
        try { _playbackStopSignal?.Set(); } catch { }
        // Kill Kokoro TTS process if still generating
        if (_kokoroProcess != null && !_kokoroProcess.HasExited)
        {
            try { _kokoroProcess.Kill(true); } catch { }
            try { _kokoroProcess.Dispose(); } catch { }
            _kokoroProcess = null;
        }
        SetState(VoiceState.Idle);
    }

    public void StopAll()
    {
        StopListening();
        StopSpeaking();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAll();
        if (_kokoroServerProcess != null && !_kokoroServerProcess.HasExited)
        {
            try { _kokoroServerProcess.Kill(true); } catch { }
            try { _kokoroServerProcess.Dispose(); } catch { }
            _kokoroServerProcess = null;
        }
        _listenTimeout?.Dispose();
        _waveIn?.Dispose();
        _whisperProcessor?.Dispose();
        _whisperFactory?.Dispose();
        _listenCts?.Dispose();
    }
}

/// <summary>The external transcriber command or script does not exist (shell "not recognized" / exit 9009).</summary>
public sealed class RyzenCommandNotFoundException : Exception
{
    public RyzenCommandNotFoundException(string message) : base(message) { }
}
