using VoiceChatbot;
using Xunit;

public class SpeechChunkerTests
{
    private const int Rate = 16000;

    // 16-bit mono PCM: a 440 Hz tone at about -12 dBFS for "speech", zeros for silence.
    private static byte[] Tone(int milliseconds, double amplitude = 0.25)
    {
        var samples = Rate * milliseconds / 1000;
        var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / Rate) * amplitude * short.MaxValue);
            bytes[2 * i] = (byte)value;
            bytes[2 * i + 1] = (byte)(value >> 8);
        }

        return bytes;
    }

    private static byte[] Silence(int milliseconds) => new byte[Rate * milliseconds / 1000 * 2];

    // Feeds audio the way WaveInEvent delivers it: 100 ms buffers.
    private static List<SpeechChunk> Feed(SpeechChunker chunker, byte[] audio, int bufferMs = 100)
    {
        var chunks = new List<SpeechChunk>();
        var step = Rate * bufferMs / 1000 * 2;
        for (var offset = 0; offset < audio.Length; offset += step)
            chunks.AddRange(chunker.Add(audio.AsSpan(offset, Math.Min(step, audio.Length - offset))));
        return chunks;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void RmsIsAFractionOfFullScale()
    {
        Assert.Equal(0.0, SpeechChunker.Rms(Silence(100)));
        Assert.Equal(0.0, SpeechChunker.Rms(ReadOnlySpan<byte>.Empty));
        // A sine wave's RMS is its amplitude / sqrt(2).
        Assert.InRange(SpeechChunker.Rms(Tone(100, 0.5)), 0.35, 0.36);
    }

    [Fact]
    public void SilenceOnlyNeverMakesAChunk()
    {
        var chunker = new SpeechChunker();
        Assert.Empty(Feed(chunker, Silence(30_000)));
        Assert.Null(chunker.Flush());
        Assert.Equal(TimeSpan.FromSeconds(30), chunker.Position);
    }

    [Fact]
    public void KeepsOnlyAShortPreRollBeforeSpeech()
    {
        var chunker = new SpeechChunker();
        Feed(chunker, Silence(5_000));
        Assert.Equal(TimeSpan.FromMilliseconds(300), chunker.Buffered);
        Assert.False(chunker.HasSpeech);
    }

    [Fact]
    public void CutsAtAPauseAfterTwoSecondsOfAudio()
    {
        var chunker = new SpeechChunker();
        var chunks = Feed(chunker, Concat(Silence(1_000), Tone(2_500), Silence(600), Tone(1_000)));

        var chunk = Assert.Single(chunks);
        // Starts with the 300 ms pre-roll before the speech and ends after the 500 ms pause.
        Assert.Equal(TimeSpan.FromMilliseconds(700), chunk.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(3_300), chunk.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(2_500), chunk.Speech);
        Assert.Equal(Rate * 2 * 3_300 / 1_000, chunk.Pcm.Length);
        Assert.True(chunker.HasSpeech);
    }

    [Fact]
    public void AShortPauseInsideTheFirstTwoSecondsDoesNotCut()
    {
        var chunker = new SpeechChunker();
        var chunks = Feed(chunker, Concat(Tone(800), Silence(600), Tone(400)));
        Assert.Empty(chunks);

        var rest = chunker.Flush();
        Assert.NotNull(rest);
        Assert.Equal(TimeSpan.Zero, rest!.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(1_800), rest.Duration);
    }

    [Fact]
    public void AShortUtteranceIsSentOnceTheSilenceMakesItTwoSecondsLong()
    {
        var chunker = new SpeechChunker();
        var chunks = Feed(chunker, Concat(Tone(600), Silence(3_000)));

        var chunk = Assert.Single(chunks);
        Assert.Equal(TimeSpan.FromSeconds(2), chunk.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(600), chunk.Speech);
    }

    [Fact]
    public void DropsAClickThatIsTooShortToBeSpeech()
    {
        var chunker = new SpeechChunker();
        var chunks = Feed(chunker, Concat(Silence(500), Tone(100), Silence(5_000), Tone(100), Silence(200)));
        Assert.Empty(chunks);
        Assert.Null(chunker.Flush());
    }

    [Fact]
    public void CutsNonStopSpeechAtTheMaximumLengthAtTheQuietestMoment()
    {
        var chunker = new SpeechChunker();
        // 18.5 s of speech, a quieter (but still voiced) 100 ms dip, then more speech.
        var chunks = Feed(chunker, Concat(Tone(18_500), Tone(100, 0.05), Tone(5_000)));

        var first = Assert.Single(chunks);
        Assert.Equal(TimeSpan.Zero, first.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(18_600), first.Duration);

        var rest = chunker.Flush();
        Assert.NotNull(rest);
        Assert.Equal(TimeSpan.FromMilliseconds(18_600), rest!.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(5_000), rest.Duration);
    }

    [Fact]
    public void NoChunkIsLongerThanTheMaximum()
    {
        var chunker = new SpeechChunker();
        var chunks = Feed(chunker, Tone(65_000));
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Duration <= TimeSpan.FromSeconds(20)));
        Assert.Equal(TimeSpan.FromSeconds(65), chunks.Aggregate(TimeSpan.Zero, (sum, c) => sum + c.Duration) + chunker.Buffered);
    }

    [Fact]
    public void FramesAreBuiltFromBuffersOfAnySize()
    {
        var audio = Concat(Silence(1_000), Tone(2_500), Silence(600), Tone(1_000));
        var bySmallBuffers = Feed(new SpeechChunker(), audio, bufferMs: 10);
        var byLargeBuffers = Feed(new SpeechChunker(), audio, bufferMs: 1_000);

        Assert.Single(bySmallBuffers);
        Assert.Single(byLargeBuffers);
        Assert.Equal(bySmallBuffers[0].Start, byLargeBuffers[0].Start);
        Assert.Equal(bySmallBuffers[0].Pcm, byLargeBuffers[0].Pcm);

        // A buffer with an odd number of bytes carries the half sample over.
        var chunker = new SpeechChunker();
        chunker.Add(audio.AsSpan(0, 3201));
        chunker.Add(audio.AsSpan(3201, 99));
        Assert.Equal(TimeSpan.FromMilliseconds(103.125), chunker.Position);
    }

    [Fact]
    public void FlushReturnsBufferedSpeechIncludingAPartialFrame()
    {
        var chunker = new SpeechChunker();
        chunker.Add(Tone(1_050));
        var chunk = chunker.Flush();
        Assert.NotNull(chunk);
        Assert.Equal(TimeSpan.FromMilliseconds(1_050), chunk!.Duration);
        Assert.Equal(TimeSpan.Zero, chunker.Buffered);
        Assert.Equal(TimeSpan.FromMilliseconds(1_050), chunker.Position);
        Assert.Null(chunker.Flush());
    }

    [Fact]
    public void SkipDropsTheBufferAndMovesThePositionOn()
    {
        var chunker = new SpeechChunker();
        Feed(chunker, Tone(1_000));
        chunker.Skip(TimeSpan.FromSeconds(4));

        Assert.False(chunker.HasSpeech);
        Assert.Equal(TimeSpan.Zero, chunker.Buffered);
        Assert.Equal(TimeSpan.FromSeconds(5), chunker.Position);

        var chunks = Feed(chunker, Concat(Tone(2_000), Silence(600)));
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(chunks).Start);
    }

    [Fact]
    public void AddSilenceLetsAQuietLoopbackDeviceEndAChunk()
    {
        var chunker = new SpeechChunker();
        Feed(chunker, Tone(2_200));
        var chunks = chunker.AddSilence(TimeSpan.FromMilliseconds(700));

        var chunk = Assert.Single(chunks);
        Assert.Equal(TimeSpan.FromMilliseconds(2_700), chunk.Duration);
    }

    [Fact]
    public void QuietSoundBelowTheThresholdIsSilence()
    {
        var chunker = new SpeechChunker(new SpeechChunkerOptions { SpeechThreshold = 0.05 });
        Assert.Empty(Feed(chunker, Tone(5_000, amplitude: 0.02)));
        Assert.Null(chunker.Flush());
    }

    [Theory]
    [InlineData(0, 0.004)]
    [InlineData(30, 0.006)]
    [InlineData(100, 0.02)]
    [InlineData(500, 0.02)]
    public void ThresholdFollowsTheNoiseSuppressionSetting(int noiseGate, double expected)
    {
        Assert.Equal(expected, SpeechChunkerOptions.ThresholdForNoiseGate(noiseGate), 6);
    }
}
