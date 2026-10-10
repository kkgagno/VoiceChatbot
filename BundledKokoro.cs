using System;
using System.IO;
using System.Linq;
using KokoroSharp;
using KokoroSharp.Core;
using KokoroSharp.Processing;

namespace VoiceChatbot;

/// <summary>
/// Kokoro text-to-speech built into the app (KokoroSharp with ONNX Runtime on the processor), so spoken
/// answers work without Python. The installer puts the model in kokoro\kokoro.onnx and the voices in
/// kokoro\voices next to the exe. Loaded on first use (or by <see cref="WarmUp"/>); thread-safe.
/// </summary>
internal static class BundledKokoro
{
    private static readonly object Gate = new();
    private static KokoroWavSynthesizer? _synthesizer;
    private static string _loadError = "";

    public static string ModelPath => Path.Combine(AppContext.BaseDirectory, "kokoro", "kokoro.onnx");

    /// <summary>kokoro\voices (installed app), else voices (a development build, where KokoroSharp copies them).</summary>
    public static string? VoicesFolder =>
        new[] { Path.Combine(AppContext.BaseDirectory, "kokoro", "voices"), Path.Combine(AppContext.BaseDirectory, "voices") }
            .FirstOrDefault(Directory.Exists);

    public static bool IsInstalled => File.Exists(ModelPath) && VoicesFolder != null;

    /// <summary>Loads the model in the background so the first answer is not delayed by it.</summary>
    public static void WarmUp()
    {
        if (IsInstalled)
            System.Threading.Tasks.Task.Run(() => TryLoad(out _));
    }

    /// <summary>
    /// Speaks <paramref name="text"/> with <paramref name="voiceId"/> (such as "am_onyx") at <paramref name="speed"/>
    /// into a 24 kHz WAV file. False with <paramref name="error"/> when Kokoro is not installed or failed.
    /// </summary>
    public static bool TrySynthesizeToWav(string text, string voiceId, double speed, string outputPath, out string error)
    {
        var synthesizer = TryLoad(out error);
        if (synthesizer == null)
            return false;

        var voice = FindVoice(voiceId) ?? FindVoice("am_onyx") ?? KokoroVoiceManager.Voices.FirstOrDefault();
        if (voice == null)
        {
            error = "no Kokoro voices were found";
            return false;
        }

        // Whole sentences per step (the default first step is short, for live playback).
        var config = new KokoroTTSPipelineConfig(new DefaultSegmentationConfig { MaxFirstSegmentLength = 510 })
        {
            Speed = (float)Math.Clamp(speed, 0.5, 2.0)
        };

        byte[] pcm;
        lock (Gate)
            pcm = synthesizer.SynthesizeAsync(text, voice, config).GetAwaiter().GetResult();
        if (pcm.Length == 0)
        {
            error = "Kokoro returned no audio";
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        KokoroWavSynthesizer.SaveAudioToFile(pcm, outputPath);
        return true;
    }

    private static KokoroWavSynthesizer? TryLoad(out string error)
    {
        lock (Gate)
        {
            error = _loadError;
            if (_synthesizer != null || _loadError.Length > 0)
                return _synthesizer;

            try
            {
                var voices = VoicesFolder;
                if (!File.Exists(ModelPath) || voices == null)
                {
                    _loadError = error = $"the built-in Kokoro files are missing ({ModelPath})";
                    return null;
                }

                KokoroVoiceManager.LoadVoicesFromPath(voices);
                _synthesizer = KokoroWavSynthesizer.LoadModel(ModelPath);
                AppLog.Info($"Built-in Kokoro loaded ({KokoroVoiceManager.Voices.Count} voices).");
                return _synthesizer;
            }
            catch (Exception ex)
            {
                _loadError = error = ex.GetBaseException().Message;
                AppLog.Warn("Built-in Kokoro could not load.", ex);
                return null;
            }
        }
    }

    private static KokoroVoice? FindVoice(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        lock (Gate)
            return KokoroVoiceManager.Voices.FirstOrDefault(v => v.Name.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
