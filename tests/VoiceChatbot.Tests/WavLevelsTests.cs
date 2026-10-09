using System.Buffers.Binary;
using VoiceChatbot;
using Xunit;

public class WavLevelsTests
{
    private static byte[] Wav(short[] samples, int sampleRate = 16000)
    {
        var data = samples.Length * 2;
        var wav = new byte[44 + data];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + data);
        "WAVE"u8.CopyTo(wav.AsSpan(8));
        "fmt "u8.CopyTo(wav.AsSpan(12));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), data);
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), samples[i]);
        return wav;
    }

    private static short[] Tone(int count, short amplitude) =>
        Enumerable.Range(0, count).Select(i => (short)(i % 2 == 0 ? amplitude : -amplitude)).ToArray();

    [Fact]
    public void Analyze_MeasuresLengthAndPeak()
    {
        var levels = WavLevels.Analyze(Wav(Tone(16000, 16384)));

        Assert.True(levels.IsPcm16);
        Assert.Equal(1.0, levels.Seconds, 3);
        Assert.Equal(0.5, levels.Peak, 3);
        Assert.Null(levels.ExplainEmptyTranscript());
    }

    [Fact]
    public void ExplainEmptyTranscript_NamesSilenceQuietAndShortRecordings()
    {
        Assert.Contains("silent", WavLevels.Analyze(Wav(new short[16000])).ExplainEmptyTranscript());
        Assert.Contains("very quiet", WavLevels.Analyze(Wav(Tone(16000, 800))).ExplainEmptyTranscript());
        Assert.Contains("too short", WavLevels.Analyze(Wav(Tone(1600, 16384))).ExplainEmptyTranscript());
    }

    [Fact]
    public void Analyze_ReturnsUnknownForOtherData()
    {
        Assert.Equal(WavLevels.Unknown, WavLevels.Analyze(new byte[] { 1, 2, 3 }));
        Assert.Equal(WavLevels.Unknown, WavLevels.Analyze("not a wav file at all, just text"u8));
        Assert.Null(WavLevels.Unknown.ExplainEmptyTranscript());
    }
}
