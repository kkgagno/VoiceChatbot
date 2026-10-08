using VoiceChatbot;
using Xunit;

public class OpenWakeWordPipelineTests
{
    // Stands in for the ONNX models with the same shapes: the mel model gives ceil(n / 160 - 3)
    // frames whose values are the sample at the start of the frame; each embedding repeats the first
    // value of its window.
    private sealed class FakeModels : IWakeWordModelRunner
    {
        public readonly List<float[]> MelInputs = new();
        public readonly List<(float[] Windows, int Count)> EmbedCalls = new();
        public readonly List<float[]> ScoreInputs = new();
        public float NextScore = 0.75f;

        public float[] MelSpectrogram(float[] samples)
        {
            MelInputs.Add(samples);
            var frames = (int)Math.Ceiling(samples.Length / 160.0 - 3);
            var result = new float[frames * OpenWakeWordPipeline.MelBins];
            for (var f = 0; f < frames; f++)
                for (var b = 0; b < OpenWakeWordPipeline.MelBins; b++)
                    result[f * OpenWakeWordPipeline.MelBins + b] = samples[f * 160];
            return result;
        }

        public float[] Embed(float[] windows, int count)
        {
            EmbedCalls.Add((windows, count));
            var size = OpenWakeWordPipeline.EmbeddingWindowFrames * OpenWakeWordPipeline.MelBins;
            var result = new float[count * OpenWakeWordPipeline.EmbeddingSize];
            for (var w = 0; w < count; w++)
                Array.Fill(result, windows[w * size], w * OpenWakeWordPipeline.EmbeddingSize, OpenWakeWordPipeline.EmbeddingSize);
            return result;
        }

        public float Score(float[] features)
        {
            ScoreInputs.Add(features);
            return NextScore;
        }
    }

    private static short[] Noise() => Enumerable.Range(0, OpenWakeWordPipeline.WarmupNoiseSamples).Select(i => (short)(i % 7)).ToArray();

    private static short[] Frame(short value) => Enumerable.Repeat(value, OpenWakeWordPipeline.FrameSamples).ToArray();

    [Fact]
    public void StartsLikeTheReference()
    {
        var models = new FakeModels();
        var pipeline = new OpenWakeWordPipeline(models, Noise);

        // 4 s of noise -> 397 mel frames -> 41 windows of 76 frames every 8 frames, embedded as one batch.
        Assert.Equal(OpenWakeWordPipeline.WarmupNoiseSamples, Assert.Single(models.MelInputs).Length);
        Assert.Equal(41, Assert.Single(models.EmbedCalls).Count);
        Assert.Equal(41, pipeline.Embeddings.Count);
        Assert.Equal(76, pipeline.MelFrames.Count);
        Assert.All(pipeline.MelFrames, frame => Assert.All(frame, v => Assert.Equal(1f, v)));
    }

    [Fact]
    public void FeedsTheMelModelEachFrameWith480SamplesOfContext()
    {
        var models = new FakeModels();
        var pipeline = new OpenWakeWordPipeline(models, Noise);

        pipeline.Process(Frame(10));
        pipeline.Process(Frame(20));

        // Straight after a reset there is no context: 1280 samples, 5 mel frames.
        Assert.Equal(1280, models.MelInputs[1].Length);
        Assert.Equal(76 + 5 + 8, pipeline.MelFrames.Count);
        var second = models.MelInputs[2];
        Assert.Equal(1760, second.Length);
        Assert.All(second.Take(480), v => Assert.Equal(10f, v));
        Assert.All(second.Skip(480), v => Assert.Equal(20f, v));
    }

    [Fact]
    public void TransformsMelValuesAndEmbedsTheLast76Frames()
    {
        var models = new FakeModels();
        var pipeline = new OpenWakeWordPipeline(models, Noise);

        pipeline.Process(Frame(30));

        Assert.Equal(30f / 10f + 2f, pipeline.MelFrames[^1][0]);
        Assert.Equal(1f, pipeline.MelFrames[^6][0]);
        var call = models.EmbedCalls[^1];
        Assert.Equal(1, call.Count);
        Assert.Equal(76 * 32, call.Windows.Length);
        // The window starts 5 frames into the initial block of ones and ends with the 5 new frames.
        Assert.Equal(1f, call.Windows[0]);
        Assert.Equal(5f, call.Windows[^1]);
        Assert.Equal(42, pipeline.Embeddings.Count);
        Assert.Equal(1f, pipeline.Embeddings[^1][0]);
    }

    [Fact]
    public void ScoresAreZeroForTheFirstFiveFramesThenUseTheLast16Embeddings()
    {
        var models = new FakeModels();
        var pipeline = new OpenWakeWordPipeline(models, Noise);

        for (var i = 0; i < OpenWakeWordPipeline.WarmupFrames; i++)
            Assert.Equal(0f, pipeline.Process(Frame(100)));
        Assert.Empty(models.ScoreInputs);

        Assert.Equal(0.75f, pipeline.Process(Frame(100)));
        var features = Assert.Single(models.ScoreInputs);
        Assert.Equal(16 * 96, features.Length);
        for (var i = 0; i < 16; i++)
            Assert.Equal(pipeline.Embeddings[pipeline.Embeddings.Count - 16 + i][0], features[i * 96]);
    }

    [Fact]
    public void KeepsAbout10SecondsOfHistory()
    {
        var pipeline = new OpenWakeWordPipeline(new FakeModels(), Noise);

        for (var i = 0; i < 200; i++)
            pipeline.Process(Frame((short)i));

        Assert.Equal(OpenWakeWordPipeline.MelBufferMaxFrames, pipeline.MelFrames.Count);
        Assert.Equal(OpenWakeWordPipeline.FeatureBufferMaxFrames, pipeline.Embeddings.Count);
        Assert.Equal(199f / 10f + 2f, pipeline.MelFrames[^1][0]);
    }

    [Fact]
    public void ResetForgetsTheAudioAndRestartsTheWarmUp()
    {
        var models = new FakeModels();
        var pipeline = new OpenWakeWordPipeline(models, Noise);
        for (var i = 0; i < 10; i++)
            pipeline.Process(Frame(50));

        pipeline.Reset();

        Assert.Equal(0, pipeline.FramesSinceReset);
        Assert.Equal(41, pipeline.Embeddings.Count);
        Assert.Equal(76, pipeline.MelFrames.Count);
        Assert.Equal(0f, pipeline.Process(Frame(50)));
        Assert.Equal(1280, models.MelInputs[^1].Length);
    }

    [Fact]
    public void RejectsFramesOfTheWrongSize()
    {
        var pipeline = new OpenWakeWordPipeline(new FakeModels(), Noise);
        Assert.Throws<ArgumentException>(() => pipeline.Process(new short[1000]));
    }

    [Fact]
    public void RandomNoiseMatchesTheReferenceRange()
    {
        var noise = OpenWakeWordPipeline.RandomNoise(new Random(1));
        Assert.Equal(64000, noise.Length);
        Assert.All(noise, v => Assert.InRange(v, (short)-1000, (short)999));
        Assert.True(noise.Distinct().Count() > 1000);
    }

    [Fact]
    public void AssemblesFramesFromBuffersOfAnySize()
    {
        var samples = Enumerable.Range(0, 3000).Select(i => (short)(i * 7 - 9000)).ToArray();
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);

        var assembler = new WakeWordFrameAssembler();
        var frames = new List<short[]>();
        var offset = 0;
        // Odd sizes split samples across buffers.
        foreach (var size in new[] { 1, 2559, 3, 1000, 2437 })
        {
            assembler.Add(bytes.AsSpan(offset, size), frame => frames.Add((short[])frame.Clone()));
            offset += size;
        }

        Assert.Equal(bytes.Length, offset);
        Assert.Equal(2, frames.Count);
        Assert.Equal(samples.Take(1280), frames[0]);
        Assert.Equal(samples.Skip(1280).Take(1280), frames[1]);
    }

    [Fact]
    public void ClearDropsAPartialFrame()
    {
        var assembler = new WakeWordFrameAssembler();
        var frames = 0;
        assembler.Add(new byte[1001], _ => frames++);
        assembler.Clear();
        assembler.Add(new byte[2559], _ => frames++);
        Assert.Equal(0, frames);
        assembler.Add(new byte[1], _ => frames++);
        Assert.Equal(1, frames);
    }

    [Fact]
    public void GateNeedsTheThresholdAndATwoSecondCooldown()
    {
        var gate = new WakeWordGate(0.5);

        Assert.False(gate.IsWake(0.49, 10));
        Assert.True(gate.IsWake(0.5, 10));
        Assert.False(gate.IsWake(0.99, 11.9));
        Assert.True(gate.IsWake(0.8, 12.0));
        Assert.False(gate.IsWake(double.NaN, 20));
    }

    [Fact]
    public void GateAsksForAResetAfterAPauseOfMoreThanASecond()
    {
        var gate = new WakeWordGate(0.5);

        Assert.False(gate.FrameArrived(5.0));
        Assert.False(gate.FrameArrived(5.08));
        Assert.False(gate.FrameArrived(6.08));
        Assert.True(gate.FrameArrived(7.2));
        Assert.False(gate.FrameArrived(7.28));
    }
}
