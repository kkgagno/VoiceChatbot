using System;
using System.Buffers.Binary;

namespace VoiceChatbot;

/// <summary>
/// Length and loudness of a 16-bit PCM WAV recording (what the phone remote sends), so an empty transcript
/// can be explained: a silent recording points at the phone's microphone, a loud one at transcription.
/// </summary>
public readonly record struct WavLevels(bool IsPcm16, double Seconds, double Peak, double Rms)
{
    /// <summary>Below this peak (share of full scale) the microphone delivered nothing usable.</summary>
    public const double SilentPeak = 0.01;
    /// <summary>Below this peak speech is usually too quiet for Whisper.</summary>
    public const double QuietPeak = 0.04;
    public const double TooShortSeconds = 0.3;

    public static readonly WavLevels Unknown = new(false, 0, 0, 0);

    /// <summary>Reads the RIFF header and measures the samples; <see cref="Unknown"/> for anything but 16-bit PCM.</summary>
    public static WavLevels Analyze(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
            return Unknown;

        int channels = 0, sampleRate = 0, bits = 0, format = 0;
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var id = wav.Slice(offset, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(offset + 4, 4)), int.MaxValue);
            var body = offset + 8;
            if (id.SequenceEqual("fmt "u8) && body + 16 <= wav.Length)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.Slice(body + 4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.Slice(body + 14, 2));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format != 1 || bits != 16 || channels <= 0 || sampleRate <= 0)
                    return Unknown;

                // A streamed WAV may claim more data than it has.
                var data = wav[body..Math.Min(wav.Length, body + size)];
                var samples = data.Length / 2;
                if (samples == 0)
                    return new WavLevels(true, 0, 0, 0);

                var peak = 0;
                double sumSquares = 0;
                for (var i = 0; i + 1 < data.Length; i += 2)
                {
                    int sample = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(i, 2));
                    var magnitude = Math.Abs(sample);
                    if (magnitude > peak)
                        peak = magnitude;
                    sumSquares += (double)sample * sample;
                }

                return new WavLevels(true,
                    samples / (double)(sampleRate * channels),
                    Math.Min(1.0, peak / 32768.0),
                    Math.Sqrt(sumSquares / samples) / 32768.0);
            }

            // Chunks are padded to an even length.
            offset = body + size + (size & 1);
        }

        return Unknown;
    }

    /// <summary>
    /// Why a recording gave no words, for the person who spoke, or null when it should have been fine
    /// (then the speech was not understood, or the format is unknown).
    /// </summary>
    public string? ExplainEmptyTranscript()
    {
        if (!IsPcm16)
            return null;
        if (Seconds < TooShortSeconds)
            return "The recording was too short to hear anything. Hold the button while you talk.";
        if (Peak < SilentPeak)
            return "The recording reached the PC but was silent: the phone's microphone gave no sound. " +
                   "Check that this app or browser may use the microphone, and that no other app (a call, a voice assistant) is holding it.";
        if (Peak < QuietPeak)
            return "The recording was very quiet, so no words could be made out. Speak closer to the phone or louder.";
        return null;
    }

    public override string ToString() =>
        IsPcm16 ? $"{Seconds:0.0} s, peak {Peak:P0}" : "unknown format";
}
