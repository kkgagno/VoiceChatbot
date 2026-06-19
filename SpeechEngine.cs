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

public class SpeechEngine : IDisposable
{
    // Components
    private bool _disposed;
    private CancellationTokenSource? _listenCts;
    private System.Timers.Timer? _listenTimeout;
    private WhisperFactory? _whisperFactory;
    private WhisperProcessor? _whisperProcessor;
    private WaveInEvent? _waveIn;
    private SileroVad? _voiceActivityDetector;
    private SileroVad? _remoteVoiceActivityDetector;
    private readonly object _remoteVadLock = new();
    private MemoryStream? _audioBuffer;
    private NAudio.Wave.WaveOutEvent? _waveOut;
    private ManualResetEvent? _playbackStopSignal;
    private System.Diagnostics.Process? _kokoroProcess;
    private System.Diagnostics.Process? _kokoroServerProcess;
    private readonly object _kokoroServerLock = new();
    private const string KokoroServerUrl = "http://127.0.0.1:8765";
    private const string RemoteKokoroSpeechUrl = "http://192.168.4.22:8880/v1/audio/speech";
    private readonly SemaphoreSlim _whisperLock = new(1, 1);
    private bool _isRecording;
    private bool _isProcessing;

    // Silence detection
    private int _silenceThreshold;
    private const int AudioBucketMilliseconds = 100;
    private const int MIN_VOICE_BUCKETS = 1; // ~100ms is enough for a one-word reply
    private const int SHORT_UTTERANCE_MAX_VOICE_BUCKETS = 8; // up to ~800ms of actual voice
    private const int SHORT_UTTERANCE_SILENCE_BUCKETS = 5; // ~500ms pause after a one-word response
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
    public string WakeWord { get; set; } = "hey assistant";
    public string VoiceName { get; set; } = "am_onyx (American Male)";
    public int SpeechRate { get; set; } = 1;
    public int Volume { get; set; } = 80;
    public bool TtsEnabled { get; set; } = true;
    public int MicDeviceIndex { get; set; } = -1;
    public string WhisperModelPath { get; set; } = "";
    public string TranscriptionBackend { get; set; } = "Whisper.net";
    public string ExternalNpuTranscriberCommand { get; set; } = "";

    // State
    public VoiceState CurrentState { get; private set; } = VoiceState.Idle;
    public string InitError { get; private set; } = "";
    public bool IsInitialized { get; private set; }
    public string LastTranscriptionBackendUsed { get; private set; } = "Whisper.net";

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

        try
        {
            var vadPath = Path.Combine(AppContext.BaseDirectory, "Resources", "Models", "silero_vad.onnx");
            if (File.Exists(vadPath))
            {
                _voiceActivityDetector = new SileroVad(vadPath);
                _remoteVoiceActivityDetector = new SileroVad(vadPath);
            }
            else
                InitError += "Silero voice detector model was not found. ";
        }
        catch (Exception ex)
        {
            InitError += $"Silero voice detector failed: {ex.Message}. ";
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

        // Start persistent Kokoro server in the background so TTS stays warm.
        var kokoroWarmupThread = new Thread(() => EnsureKokoroServerStarted(waitForReady: false));
        kokoroWarmupThread.IsBackground = true;
        kokoroWarmupThread.Start();

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

        _whisperFactory = WhisperFactory.FromPath(foundModel);

        var langCode = InputLanguage;
        if (langCode == "en-US" || langCode == "en-GB") langCode = "en";
        if (langCode.Contains('-')) langCode = langCode.Split('-')[0];

        _whisperProcessor = _whisperFactory.CreateBuilder()
            .WithLanguage(langCode)
            .Build();

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

        // Re-init with the new model
        _whisperProcessor?.Dispose();
        _whisperFactory?.Dispose();
        WhisperModelPath = targetPath;
        InitWhisper();
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
        _voiceActivityDetector?.Reset();
        _logThrottle = 0;

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

        // Neural VAD distinguishes human speech from steady AC, typing, footsteps,
        // and most animal/mechanical sounds. RMS remains only as a fallback.
        var speechProbability = _voiceActivityDetector?.ProcessPcm16(e.Buffer, e.BytesRecorded) ?? -1f;
        var voiceThreshold = Math.Max(0.004f, NoiseGate / 5000f);
        bool isVoice = speechProbability >= 0
            ? speechProbability >= 0.55f
            : rmsNorm > voiceThreshold;

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

        // Only process if: we heard voice, then silence, and have enough audio
        if (_voiceDetected && _silenceBucketCount >= RequiredSilenceBuckets && _audioBuffer?.Length > 16000)
        {
            StopRecordingAndProcess();
        }
    }

    private int RequiredSilenceBuckets
    {
        get
        {
            var configured = Math.Clamp((int)Math.Round(SilenceTimeout * 1000 / AudioBucketMilliseconds), 5, 100);
            return _voiceBucketCount <= SHORT_UTTERANCE_MAX_VOICE_BUCKETS
                ? Math.Min(configured, SHORT_UTTERANCE_SILENCE_BUCKETS)
                : configured;
        }
    }

    public async Task<bool> ContainsSpeechWavAsync(Stream stream, CancellationToken ct)
    {
        if (_remoteVoiceActivityDetector == null)
            return true;

        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        var wav = memory.ToArray();
        var dataOffset = FindWavDataOffset(wav);
        if (dataOffset < 0 || dataOffset >= wav.Length)
            return false;

        lock (_remoteVadLock)
        {
            _remoteVoiceActivityDetector.Reset();
            var speechFrames = 0;
            const int bytesPerFrame = SileroVad.FrameSamples * 2;
            for (var offset = dataOffset; offset + bytesPerFrame <= wav.Length; offset += bytesPerFrame)
            {
                var frame = new byte[bytesPerFrame];
                Buffer.BlockCopy(wav, offset, frame, 0, bytesPerFrame);
                if (_remoteVoiceActivityDetector.ProcessPcm16(frame, frame.Length) >= 0.55f)
                {
                    speechFrames++;
                    if (speechFrames >= 3)
                        return true;
                }
            }
            return false;
        }
    }

    private static int FindWavDataOffset(byte[] wav)
    {
        for (var index = 12; index + 8 <= wav.Length;)
        {
            var chunkSize = BitConverter.ToInt32(wav, index + 4);
            if (wav[index] == (byte)'d' && wav[index + 1] == (byte)'a' &&
                wav[index + 2] == (byte)'t' && wav[index + 3] == (byte)'a')
                return index + 8;
            index += 8 + Math.Max(0, chunkSize);
        }
        return -1;
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
                    text = text.Trim();

                    // Wake word check
                    if (!AutoDetect && !string.IsNullOrWhiteSpace(WakeWord))
                    {
                        if (text.IndexOf(WakeWord, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            // No wake word detected - go back to listening
                            _isProcessing = false;
                            SetState(VoiceState.Idle);
                            ListeningTimedOut?.Invoke();
                            return;
                        }

                        // Wake word found - strip it from the text
                        var idx = text.IndexOf(WakeWord, StringComparison.OrdinalIgnoreCase);
                        text = text.Substring(idx + WakeWord.Length).Trim();
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            // Only wake word was spoken, wait for the actual question
                            _isProcessing = false;
                            SetState(VoiceState.Idle);
                            ListeningTimedOut?.Invoke();
                            return;
                        }
                    }

                    if (IsIgnoredWhisperText(text))
                    {
                        Log?.Invoke($"Ignored non-speech transcription: {text}");
                        ListeningTimedOut?.Invoke();
                        return;
                    }

                    Log?.Invoke($"Transcription backend used: {LastTranscriptionBackendUsed}");
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

    public void ReadyForNextSpeech()
    {
        _isProcessing = false;
    }

    // ==================== TTS ====================

    public void Speak(string text)
    {

        if (!TtsEnabled)
        {
            SetState(VoiceState.Idle);
            SpeechFinished?.Invoke();
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            SetState(VoiceState.Idle);
            SpeechFinished?.Invoke();
            return;
        }

        // Stop listening on a separate call to avoid blocking
        try { _waveIn?.StopRecording(); } catch { }
        _isRecording = false;
        _isProcessing = true;
        SetState(VoiceState.Speaking);

        var clean = text; // MainWindow already prepares speech-safe text.

        var kokoroVoice = VoiceName;
        if (string.IsNullOrEmpty(kokoroVoice))
            kokoroVoice = "af_bella";
        else
            kokoroVoice = kokoroVoice.Split('(')[0].Trim();

        // Safety: if voice name ended up empty after split, default to af_bella
        if (string.IsNullOrWhiteSpace(kokoroVoice))
            kokoroVoice = "af_bella";

        // Determine lang code from voice prefix: af_/am_ = American 'a', bf_/bm_ = British 'b'
        var kokoroLang = kokoroVoice.StartsWith("bf_") || kokoroVoice.StartsWith("bm_") ? "b" : "a";

        var thread = new Thread(() => DoKokoroSpeak(clean, kokoroVoice, kokoroLang));
        thread.IsBackground = true;
        thread.Start();
    }

    public async Task<string?> CreateSpeechAudioFileAsync(string text, string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(text) || !TtsEnabled)
            return null;

        Directory.CreateDirectory(outputDirectory);
        var kokoroVoice = VoiceName;
        if (string.IsNullOrEmpty(kokoroVoice))
            kokoroVoice = "af_bella";
        else
            kokoroVoice = kokoroVoice.Split('(')[0].Trim();

        if (string.IsNullOrWhiteSpace(kokoroVoice))
            kokoroVoice = "af_bella";

        var kokoroLang = kokoroVoice.StartsWith("bf_") || kokoroVoice.StartsWith("bm_") ? "b" : "a";
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

        StopSpeaking();
        try { _waveIn?.StopRecording(); } catch { }
        _isRecording = false;
        _isProcessing = true;
        SetState(VoiceState.Speaking);
        PlayWavOnThread(filePath, deleteAfterPlayback: false);
    }

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
                    var external = await TranscribeWithRyzenAiAsync(wavStream, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(external))
                    {
                        LastTranscriptionBackendUsed = "AMD Ryzen AI Whisper";
                        return external;
                    }

                    if (IsRyzenAiRequired())
                        return "";
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"AMD Ryzen AI Whisper transcription failed: {ex.Message}");
                    if (IsRyzenAiRequired())
                        throw;
                }
            }
        }

        if (_whisperProcessor == null)
            throw new InvalidOperationException("Whisper is not initialized.");

        await _whisperLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (wavStream.CanSeek)
                wavStream.Position = 0;

            var result = new StringBuilder();
            await foreach (var segment in _whisperProcessor.ProcessAsync(wavStream, ct).ConfigureAwait(false))
            {
                var text = CleanWhisperSegment(segment.Text);
                if (!string.IsNullOrWhiteSpace(text))
                    result.Append(text);
            }

            var cleaned = CleanTranscriptText(result.ToString());
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
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                    ? $"External NPU transcriber exited with code {process.ExitCode}."
                    : error);
            }

            var cleaned = CleanTranscriptText(stdout.ToString());
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

    private void DoKokoroSpeak(string text, string voice, string lang)
    {
        try
        {
            var wavFile = GenerateKokoroAudioSync(text, voice, lang);
            if (wavFile != null)
            {
                PlayWavSync(wavFile);
                return;
            }
        }
        catch (Exception ex)
        {
        }
        SetState(VoiceState.Idle);
        SpeechFinished?.Invoke();
    }

    private string? GenerateKokoroAudioSync(string text, string voice, string lang = "a")
    {
        var remoteWav = Path.Combine(Path.GetTempPath(), $"tts_remote_{Guid.NewGuid():N}.wav");
        try
        {
            if (TryGenerateKokoroViaRemote(text, voice, lang, remoteWav))
                return remoteWav;
        }
        catch
        {
            try { File.Delete(remoteWav); } catch { }
        }

        var tempWav = Path.Combine(Path.GetTempPath(), $"tts_{Guid.NewGuid():N}.wav");

        try
        {
            if (TryGenerateKokoroViaServer(text, voice, lang, tempWav))
                return tempWav;
        }
        catch
        {
            try { File.Delete(tempWav); } catch { }
        }

        // Fallback: old one-shot Python path if remote and persistent local server are not available.
        return GenerateKokoroAudioOneShotSync(text, voice, lang);
    }

    private bool TryGenerateKokoroViaRemote(string text, string voice, string lang, string outputPath)
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
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        using var content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
        using var response = client.PostAsync(RemoteKokoroSpeechUrl, content).GetAwaiter().GetResult();

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
        using var content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
        using var response = client.PostAsync(KokoroServerUrl + "/tts", content).GetAwaiter().GetResult();
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

                var psi = new ProcessStartInfo
                {
                    FileName = pythonExe,
                    Arguments = $"\"{scriptPath}\" --host 127.0.0.1 --port 8765 --preload a,b",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

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
            Thread.Sleep(250);
        }

        return false;
    }

    private static bool IsKokoroServerHealthy(int timeoutMs)
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };
            using var response = client.GetAsync(KokoroServerUrl + "/health").GetAwaiter().GetResult();
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

    private void PlayWavSync(string filePath)
    {
        try
        {
            using var reader = new NAudio.Wave.WaveFileReader(filePath);
            _waveOut = new NAudio.Wave.WaveOutEvent();
            _waveOut.Volume = Math.Max(0.01f, Math.Min(Volume / 100f, 1f));
            var done = new ManualResetEvent(false);
            _waveOut.PlaybackStopped += (s, e) =>
            {
                done.Set();
            };
            _waveOut.Init(reader);
            _waveOut.Play();
            done.WaitOne(TimeSpan.FromMinutes(10));
        }
        catch (Exception ex)
        {
        }
        finally
        {
            try { File.Delete(filePath); } catch { }
            try { _waveOut?.Dispose(); } catch { }
            _waveOut = null;
            SetState(VoiceState.Idle);
            SpeechFinished?.Invoke();
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

    private static string? FindKokoroScript(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", "Kokoro", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "VoiceChatbot",
                fileName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindPythonExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("VOICECHATBOT_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "Python", "Python313", "python.exe"),
            Path.Combine(localAppData, "Programs", "Python", "Python312", "python.exe"),
            Path.Combine(localAppData, "Programs", "Python", "Python311", "python.exe"),
            Path.Combine(programFiles, "Python313", "python.exe"),
            Path.Combine(programFiles, "Python312", "python.exe"),
            Path.Combine(programFiles, "Python311", "python.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? "python.exe";
    }

    private void PlayWavOnThread(string filePath, bool deleteAfterPlayback = true)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var reader = new NAudio.Wave.WaveFileReader(filePath);
                var done = new ManualResetEvent(false);
                _playbackStopSignal = done;
                _waveOut = new NAudio.Wave.WaveOutEvent();
                _waveOut.Volume = Math.Max(0.01f, Math.Min(Volume / 100f, 1f));
                _waveOut.PlaybackStopped += (s, e) =>
                {
                    done.Set();
                };
                _waveOut.Init(reader);
                _waveOut.Play();
                done.WaitOne(TimeSpan.FromMinutes(10));
            }
            catch (Exception ex)
            {
            }
            finally
            {
                if (deleteAfterPlayback)
                {
                    try { File.Delete(filePath); } catch { }
                }
                try { _waveOut?.Dispose(); } catch { }
                _waveOut = null;
                _playbackStopSignal = null;
                SetState(VoiceState.Idle);
                SpeechFinished?.Invoke();
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
        _voiceActivityDetector?.Dispose();
        _remoteVoiceActivityDetector?.Dispose();
        _listenCts?.Dispose();
    }
}
