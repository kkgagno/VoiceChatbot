using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;
using Microsoft.ML.OnnxRuntime;
using NAudio.Wave;

namespace VoiceChatbot;

/// <summary>
/// Kokoro text-to-speech built into the app (KokoroSharp with ONNX Runtime), so spoken answers work without
/// Python. The installer puts the model in kokoro\kokoro.onnx and the voices in kokoro\voices next to the exe.
/// It runs on the graphics card (DirectML, any DirectX 12 card) or on the processor, whichever speaks a test
/// sentence faster: <see cref="WarmUp"/> tries both when the app starts and keeps the faster. Speech asked
/// for while that runs waits for it instead of loading the model again. One synthesis at a time; thread-safe.
/// </summary>
internal static class BundledKokoro
{
    /// <summary><see cref="Device"/> when Kokoro runs on the graphics card.</summary>
    public const string GraphicsCard = "graphics card";

    /// <summary><see cref="Device"/> when Kokoro runs on the processor.</summary>
    public const string Processor = "processor";

    private const int SampleRate = 24000;

    // Different lengths, so the timed sentence is a new input shape for the graphics card, like a real reply.
    private const string WarmUpSentence = "Hello! This short sentence gets the voice ready.";
    private const string TestSentence =
        "Here is a quick test of the built-in voice. It checks whether the graphics card or the processor says this faster.";

    // Held while loading (and choosing the device) and while speaking.
    private static readonly object Gate = new();
    private static readonly object WarmUpGate = new();
    private static KokoroModel? _model;
    private static string _device = "";
    private static string _loadError = "";
    private static Task? _warmUp;

    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "kokoro", "kokoro.onnx");

    /// <summary>kokoro\voices (installed app), else voices (a development build, where KokoroSharp copies them).</summary>
    public static string? VoicesFolder =>
        new[] { Path.Combine(AppContext.BaseDirectory, "kokoro", "voices"), Path.Combine(AppContext.BaseDirectory, "voices") }
            .FirstOrDefault(Directory.Exists);

    public static bool IsInstalled => File.Exists(ModelPath) && VoicesFolder != null;

    /// <summary>Installed and not failed to load (it is then skipped for the other Kokoro paths).</summary>
    public static bool IsUsable => IsInstalled && Volatile.Read(ref _loadError).Length == 0;

    /// <summary>Where Kokoro runs: <see cref="GraphicsCard"/> or <see cref="Processor"/>; "" until it is loaded.</summary>
    public static string Device => Volatile.Read(ref _device);

    /// <summary>
    /// Loads Kokoro in the background, picks the faster of the graphics card and the processor, and speaks a
    /// short sentence (thrown away) with <paramref name="voiceId"/> so the first answer pays for none of it.
    /// Runs once; later calls return the same task, which ends when Kokoro is ready (or failed to load).
    /// </summary>
    public static Task WarmUp(string? voiceId = null)
    {
        if (!IsInstalled)
            return Task.CompletedTask;

        lock (WarmUpGate)
        {
            return _warmUp ??= Task.Run(() =>
            {
                lock (Gate)
                    LoadUnderGate(voiceId, out _);
            });
        }
    }

    /// <summary>
    /// Speaks <paramref name="text"/> with <paramref name="voiceId"/> (such as "am_onyx") at <paramref name="speed"/>
    /// into a 24 kHz WAV file. False with <paramref name="error"/> when Kokoro is not installed or failed.
    /// </summary>
    public static bool TrySynthesizeToWav(string text, string voiceId, double speed, string outputPath, out string error)
    {
        lock (Gate)
        {
            var model = LoadUnderGate(voiceId, out error);
            if (model == null)
                return false;

            var voice = FindVoice(voiceId) ?? FindVoice("am_onyx") ?? KokoroVoiceManager.Voices.FirstOrDefault();
            if (voice == null)
            {
                error = "no Kokoro voices were found";
                return false;
            }

            var rate = (float)Math.Clamp(speed, 0.5, 2.0);
            float[] samples;
            try
            {
                samples = Render(model, text, voice, rate);
                if (_device == GraphicsCard && samples.Length > 0 && !SoundsRight(samples) && text.Any(char.IsLetterOrDigit))
                    throw new InvalidDataException("it made silent or broken audio");
            }
            catch (Exception ex) when (_device == GraphicsCard)
            {
                // A driver update, a lost device or too little video memory: the processor takes over for good.
                AppLog.Warn($"Built-in Kokoro: the graphics card failed ({Describe(ex)}); using the processor from now on.");
                model = SwitchToProcessor(out error);
                if (model == null)
                    return false;
                samples = Render(model, text, voice, rate);
            }

            if (samples.Length == 0)
            {
                error = "Kokoro returned no audio";
                return false;
            }

            WriteWav(samples, outputPath);
            return true;
        }
    }

    // Caller holds Gate. Loads Kokoro the first time (testing it with voiceId); later calls return the loaded
    // model, or null with the first load's error (a failed load is not retried).
    private static KokoroModel? LoadUnderGate(string? voiceId, out string error)
    {
        error = _loadError;
        if (_model != null || _loadError.Length > 0)
            return _model;

        try
        {
            var voices = VoicesFolder;
            if (!File.Exists(ModelPath) || voices == null)
            {
                Volatile.Write(ref _loadError, error = $"the built-in Kokoro files are missing ({ModelPath})");
                return null;
            }

            KokoroVoiceManager.LoadVoicesFromPath(voices);
            _model = LoadOnTheFasterDevice(voiceId);
            AppLog.Info($"Built-in Kokoro loaded ({KokoroVoiceManager.Voices.Count} voices).");
            return _model;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _loadError, error = Describe(ex));
            AppLog.Warn("Built-in Kokoro could not load.", ex);
            return null;
        }
    }

    // Loads Kokoro on the graphics card and on the processor, speaks one untimed sentence on each (a model's
    // first run does one-time work), times the same test sentence on each and keeps the faster; the other
    // is closed. The graphics card is only used when its audio looks right: not empty, silent or broken, and
    // about as long as the processor's. Throws when neither works. With the voice that will speak, so its
    // language's word list is loaded too.
    private static KokoroModel LoadOnTheFasterDevice(string? voiceId)
    {
        var voice = FindVoice(voiceId) ?? FindVoice("am_onyx") ?? KokoroVoiceManager.Voices.FirstOrDefault();

        KokoroModel? card = null;
        var cardProblem = "";
        try
        {
            card = CreateModel(GraphicsCardOptions);
        }
        catch (Exception ex)
        {
            cardProblem = Describe(ex);
        }

        KokoroModel? processor = null;
        Exception? processorError = null;
        try
        {
            processor = CreateModel(ProcessorOptions);
        }
        catch (Exception ex) when (card != null)
        {
            processorError = ex;
        }

        if (voice == null)
        {
            // Nothing to test with (speech fails with "no voices" anyway): keep the processor.
            card?.Dispose();
            if (processor == null)
                throw processorError ?? new InvalidOperationException("no Kokoro voices were found");
            return Use(processor, Processor);
        }

        var processorTime = TimeSpan.Zero;
        var processorAudio = Array.Empty<float>();
        if (processor != null)
        {
            try
            {
                (processorTime, processorAudio) = WarmUpAndTime(processor, voice);
            }
            catch (Exception ex)
            {
                processorError = ex;
                processor.Dispose();
                processor = null;
            }
        }

        var cardTime = TimeSpan.Zero;
        if (card != null)
        {
            try
            {
                (cardTime, var cardAudio) = WarmUpAndTime(card, voice);
                if (!SoundsRight(cardAudio))
                    cardProblem = "it made silent or broken audio";
                else if (processorAudio.Length > 0 && Math.Abs(cardAudio.Length - processorAudio.Length) > processorAudio.Length / 4)
                    cardProblem = "its audio did not match the processor's";
            }
            catch (Exception ex)
            {
                cardProblem = Describe(ex);
            }

            if (cardProblem.Length > 0)
            {
                card.Dispose();
                card = null;
            }
        }

        if (card == null && processor == null)
        {
            AppLog.Warn($"Built-in Kokoro: graphics card not usable ({cardProblem}).");
            throw processorError ?? new InvalidOperationException("Kokoro did not work on the graphics card or the processor");
        }

        var useCard = card != null && (processor == null || cardTime < processorTime);
        var cardPart = card != null ? $"graphics card {Seconds(cardTime)}" : $"graphics card not usable ({cardProblem})";
        var processorPart = processor != null ? $"processor {Seconds(processorTime)}" : $"processor failed ({Describe(processorError!)})";
        AppLog.Info($"Built-in Kokoro: {cardPart}, {processorPart} for the test sentence; using the {(useCard ? GraphicsCard : Processor)}.");

        if (useCard)
        {
            processor?.Dispose();
            return Use(card!, GraphicsCard);
        }

        card?.Dispose();
        return Use(processor!, Processor);
    }

    private static (TimeSpan Time, float[] Audio) WarmUpAndTime(KokoroModel model, KokoroVoice voice)
    {
        Render(model, WarmUpSentence, voice, 1f);
        var clock = Stopwatch.StartNew();
        var audio = Render(model, TestSentence, voice, 1f);
        return (clock.Elapsed, audio);
    }

    private static KokoroModel Use(KokoroModel model, string device)
    {
        Volatile.Write(ref _device, device);
        return model;
    }

    // Caller holds Gate. Closes the graphics card model and loads Kokoro on the processor.
    private static KokoroModel? SwitchToProcessor(out string error)
    {
        error = "";
        _model?.Dispose();
        _model = null;
        try
        {
            _model = Use(CreateModel(ProcessorOptions), Processor);
            return _model;
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _loadError, error = Describe(ex));
            AppLog.Warn("Built-in Kokoro could not load on the processor.", ex);
            return null;
        }
    }

    private static KokoroModel CreateModel(Func<SessionOptions> makeOptions)
    {
        // The session copies its options when it is created.
        using var options = makeOptions();
        return new KokoroModel(ModelPath, options);
    }

    // DirectML on the first graphics adapter. ONNX Runtime's advice for DirectML: no memory pattern and one
    // operator at a time.
    private static SessionOptions GraphicsCardOptions()
    {
        var options = new SessionOptions
        {
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
        try
        {
            options.AppendExecutionProvider_DML(0);
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    // KokoroSharp's own processor settings.
    private static SessionOptions ProcessorOptions() => new()
    {
        EnableMemoryPattern = true,
        InterOpNumThreads = 8,
        IntraOpNumThreads = 8
    };

    // KokoroSharp's pipeline (KokoroWavSynthesizer.SynthesizeAsync) run on the calling thread: whole
    // sentences per step, trimmed, with KokoroSharp's pause after punctuation. Its own job thread does not
    // catch errors, so a graphics card error there would close the app; here it reaches the caller.
    private static float[] Render(KokoroModel model, string text, KokoroVoice voice, float speed)
    {
        var config = new KokoroTTSPipelineConfig(new DefaultSegmentationConfig { MaxFirstSegmentLength = 510 }) { Speed = speed };
        var tokens = Tokenizer.Tokenize(text.Trim(), voice.GetLangCode(), config.PreprocessText);
        var audio = new List<float>();
        foreach (var segment in config.SegmentationFunc(tokens))
        {
            if (segment.Length == 0)
                continue;

            audio.AddRange(KokoroPlayback.PostProcessSamples(model.Infer(segment, voice.Features, speed)));
            if (Tokenizer.PunctuationTokens.Contains(segment[^1]))
            {
                var pause = config.SecondsOfPauseBetweenProperSegments[Tokenizer.TokenToChar[segment[^1]]];
                audio.AddRange(new float[(int)(pause * SampleRate)]);
            }
        }

        return audio.ToArray();
    }

    // Real speech: something, no NaN or infinity, and not (nearly) silent.
    private static bool SoundsRight(float[] samples)
    {
        if (samples.Length == 0)
            return false;

        var peak = 0f;
        foreach (var sample in samples)
        {
            if (!float.IsFinite(sample))
                return false;
            peak = Math.Max(peak, Math.Abs(sample));
        }

        return peak >= 0.01f;
    }

    private static void WriteWav(float[] samples, string outputPath)
    {
        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var sample = float.IsFinite(samples[i]) ? Math.Clamp(samples[i], -1f, 1f) : 0f;
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(2 * i), (short)(sample * short.MaxValue));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        using var writer = new WaveFileWriter(outputPath, new WaveFormat(SampleRate, 16, 1));
        writer.Write(pcm, 0, pcm.Length);
    }

    private static KokoroVoice? FindVoice(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        lock (Gate)
            return KokoroVoiceManager.Voices.FirstOrDefault(v => v.Name.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(Exception ex) => ex.GetBaseException().Message;

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0#", CultureInfo.InvariantCulture) + " s";
}
