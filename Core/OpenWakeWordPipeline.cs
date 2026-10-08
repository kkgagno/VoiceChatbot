using System;
using System.Collections.Generic;

namespace VoiceChatbot;

/// <summary>
/// Runs openWakeWord's three ONNX models. The app uses ONNX Runtime (WakeWordOnnxModels); tests use fakes.
/// </summary>
public interface IWakeWordModelRunner
{
    /// <summary>
    /// melspectrogram.onnx on raw sample values (input [1, n]). Returns the untransformed mel frames,
    /// 32 values per frame, row-major.
    /// </summary>
    float[] MelSpectrogram(float[] samples);

    /// <summary>
    /// embedding_model.onnx on <paramref name="count"/> windows of 76 x 32 mel values
    /// (input [count, 76, 32, 1]). Returns 96 values per window.
    /// </summary>
    float[] Embed(float[] windows, int count);

    /// <summary>The wake word model on the last 16 embeddings (input [1, 16, 96]). Returns the score.</summary>
    float Score(float[] features);
}

/// <summary>
/// openWakeWord's streaming pipeline (AudioFeatures._streaming_features and Model.predict in
/// openwakeword 0.6.0) for one wake word model, fed 1280-sample (80 ms) frames of 16 kHz int16 audio.
/// Each frame adds 8 mel frames (5 for the first frame after a reset), one embedding of the last 76
/// mel frames, and one score from the last 16 embeddings. Like the reference, the embedding history
/// starts with embeddings of 4 s of random noise and the first 5 scores after a reset are 0.
/// Not thread-safe: use it from one thread.
/// </summary>
public sealed class OpenWakeWordPipeline
{
    public const int SampleRate = 16000;
    public const int FrameSamples = 1280;
    /// <summary>Extra samples before each frame given to the mel model (160 * 3), as in the reference.</summary>
    public const int MelContextSamples = 160 * 3;
    public const int MelBins = 32;
    public const int EmbeddingWindowFrames = 76;
    public const int EmbeddingStepFrames = 8;
    public const int EmbeddingSize = 96;
    public const int WakeModelFrames = 16;
    /// <summary>~10 s of mel frames (97 per second).</summary>
    public const int MelBufferMaxFrames = 10 * 97;
    /// <summary>~10 s of embeddings.</summary>
    public const int FeatureBufferMaxFrames = 120;
    /// <summary>Scores of the first frames after a reset are reported as 0.</summary>
    public const int WarmupFrames = 5;
    public const int WarmupNoiseSamples = SampleRate * 4;

    private readonly IWakeWordModelRunner _models;
    private readonly Func<short[]> _warmupNoise;
    // The reference keeps 10 s of raw audio but only ever reads the last frame plus its context.
    private readonly float[] _raw = new float[FrameSamples + MelContextSamples];
    private int _rawCount;
    private readonly List<float[]> _mel = new();
    private readonly List<float[]> _features = new();
    private int _framesSinceReset;

    /// <param name="models">The three models.</param>
    /// <param name="warmupNoise">
    /// Audio whose embeddings fill the history after a reset. Defaults to 4 s of random integers in
    /// [-1000, 1000), like np.random.randint in the reference.
    /// </param>
    public OpenWakeWordPipeline(IWakeWordModelRunner models, Func<short[]>? warmupNoise = null)
    {
        _models = models ?? throw new ArgumentNullException(nameof(models));
        _warmupNoise = warmupNoise ?? (() => RandomNoise(Random.Shared));
        Reset();
    }

    /// <summary>Mel frames kept (after the x/10 + 2 transform), oldest first.</summary>
    public IReadOnlyList<float[]> MelFrames => _mel;

    /// <summary>Embeddings kept, oldest first; the last <see cref="WakeModelFrames"/> are scored.</summary>
    public IReadOnlyList<float[]> Embeddings => _features;

    /// <summary>Frames processed since the last reset.</summary>
    public int FramesSinceReset => _framesSinceReset;

    /// <summary>Forgets all audio, as Model.reset() does: used after a detection and after a pause.</summary>
    public void Reset()
    {
        _rawCount = 0;
        _framesSinceReset = 0;

        _mel.Clear();
        for (var i = 0; i < EmbeddingWindowFrames; i++)
        {
            var ones = new float[MelBins];
            Array.Fill(ones, 1f);
            _mel.Add(ones);
        }

        _features.Clear();
        _features.AddRange(EmbedClip(_warmupNoise()));
    }

    /// <summary>
    /// Processes one 80 ms frame and returns the wake word score (0-1). The model only runs once
    /// <see cref="WarmupFrames"/> frames have been seen since the last reset; before that it returns 0.
    /// </summary>
    public float Process(ReadOnlySpan<short> frame)
    {
        if (frame.Length != FrameSamples)
            throw new ArgumentException($"A frame must have {FrameSamples} samples.", nameof(frame));

        // Last frame plus up to 480 samples before it (fewer straight after a reset).
        var keep = Math.Min(_rawCount, MelContextSamples);
        Array.Copy(_raw, _rawCount - keep, _raw, 0, keep);
        for (var i = 0; i < FrameSamples; i++)
            _raw[keep + i] = frame[i];
        _rawCount = keep + FrameSamples;

        var input = new float[_rawCount];
        Array.Copy(_raw, input, _rawCount);
        _mel.AddRange(TransformMel(_models.MelSpectrogram(input)));
        if (_mel.Count > MelBufferMaxFrames)
            _mel.RemoveRange(0, _mel.Count - MelBufferMaxFrames);

        _features.Add(_models.Embed(Window(_mel, _mel.Count - EmbeddingWindowFrames), 1));
        if (_features.Count > FeatureBufferMaxFrames)
            _features.RemoveRange(0, _features.Count - FeatureBufferMaxFrames);

        _framesSinceReset++;
        if (_framesSinceReset <= WarmupFrames)
            return 0f;

        var features = new float[WakeModelFrames * EmbeddingSize];
        for (var i = 0; i < WakeModelFrames; i++)
            Array.Copy(_features[_features.Count - WakeModelFrames + i], 0, features, i * EmbeddingSize, EmbeddingSize);
        return _models.Score(features);
    }

    /// <summary>The reference's mel transform, applied to every value: x / 10 + 2.</summary>
    public static float TransformMelValue(float value) => value / 10f + 2f;

    /// <summary>4 s of random integers in [-1000, 1000).</summary>
    public static short[] RandomNoise(Random random)
    {
        var noise = new short[WarmupNoiseSamples];
        for (var i = 0; i < noise.Length; i++)
            noise[i] = (short)random.Next(-1000, 1000);
        return noise;
    }

    // AudioFeatures._get_embeddings: mel of the whole clip, then a 76-frame window every 8 frames
    // (short windows at the end are dropped), embedded as one batch.
    private List<float[]> EmbedClip(short[] clip)
    {
        var samples = new float[clip.Length];
        for (var i = 0; i < clip.Length; i++)
            samples[i] = clip[i];

        var mel = TransformMel(_models.MelSpectrogram(samples));
        var starts = new List<int>();
        for (var start = 0; start + EmbeddingWindowFrames <= mel.Count; start += EmbeddingStepFrames)
            starts.Add(start);

        var result = new List<float[]>(starts.Count);
        if (starts.Count == 0)
            return result;

        var windowSize = EmbeddingWindowFrames * MelBins;
        var batch = new float[starts.Count * windowSize];
        for (var w = 0; w < starts.Count; w++)
            Array.Copy(Window(mel, starts[w]), 0, batch, w * windowSize, windowSize);

        var embedded = _models.Embed(batch, starts.Count);
        if (embedded.Length != starts.Count * EmbeddingSize)
            throw new InvalidOperationException($"The embedding model returned {embedded.Length} values for {starts.Count} windows.");
        for (var w = 0; w < starts.Count; w++)
            result.Add(embedded.AsSpan(w * EmbeddingSize, EmbeddingSize).ToArray());
        return result;
    }

    private static List<float[]> TransformMel(float[] values)
    {
        if (values.Length == 0 || values.Length % MelBins != 0)
            throw new InvalidOperationException($"The mel model returned {values.Length} values, not a multiple of {MelBins}.");

        var frames = new List<float[]>(values.Length / MelBins);
        for (var f = 0; f < values.Length / MelBins; f++)
        {
            var frame = new float[MelBins];
            for (var b = 0; b < MelBins; b++)
                frame[b] = TransformMelValue(values[f * MelBins + b]);
            frames.Add(frame);
        }
        return frames;
    }

    private static float[] Window(List<float[]> mel, int start)
    {
        var window = new float[EmbeddingWindowFrames * MelBins];
        for (var i = 0; i < EmbeddingWindowFrames; i++)
            Array.Copy(mel[start + i], 0, window, i * MelBins, MelBins);
        return window;
    }
}

/// <summary>Turns 16-bit little-endian PCM buffers of any size into 1280-sample frames.</summary>
public sealed class WakeWordFrameAssembler
{
    private readonly short[] _frame = new short[OpenWakeWordPipeline.FrameSamples];
    private int _count;
    private int _pendingByte = -1;

    /// <summary>Adds captured bytes; calls <paramref name="onFrame"/> for each complete frame (the span is reused).</summary>
    public void Add(ReadOnlySpan<byte> pcm, Action<short[]> onFrame)
    {
        var i = 0;
        if (_pendingByte >= 0 && pcm.Length > 0)
        {
            Append((short)(_pendingByte | (pcm[0] << 8)), onFrame);
            _pendingByte = -1;
            i = 1;
        }

        for (; i + 1 < pcm.Length; i += 2)
            Append((short)(pcm[i] | (pcm[i + 1] << 8)), onFrame);

        if (i < pcm.Length)
            _pendingByte = pcm[i];
    }

    /// <summary>Drops a partly filled frame.</summary>
    public void Clear()
    {
        _count = 0;
        _pendingByte = -1;
    }

    private void Append(short sample, Action<short[]> onFrame)
    {
        _frame[_count++] = sample;
        if (_count < _frame.Length)
            return;
        _count = 0;
        onFrame(_frame);
    }
}

/// <summary>
/// When a score counts as the wake word: at or above the threshold, and at least 2 s after the last
/// detection. A gap of more than 1 s between frames means the audio was paused, so the pipeline
/// should forget what it heard before.
/// </summary>
public sealed class WakeWordGate
{
    public const double CooldownSeconds = 2.0;
    public const double ResumeGapSeconds = 1.0;

    private double _lastFrame = double.NegativeInfinity;
    private double _lastWake = double.NegativeInfinity;

    public WakeWordGate(double threshold) => Threshold = threshold;

    public double Threshold { get; }

    /// <summary>Call for each frame before scoring it. True when the pipeline should be reset first.</summary>
    public bool FrameArrived(double nowSeconds)
    {
        var gap = nowSeconds - _lastFrame;
        var first = double.IsNegativeInfinity(_lastFrame);
        _lastFrame = nowSeconds;
        return !first && gap > ResumeGapSeconds;
    }

    /// <summary>True when <paramref name="score"/> is a new detection; the pipeline should then be reset.</summary>
    public bool IsWake(double score, double nowSeconds)
    {
        if (!(score >= Threshold) || nowSeconds - _lastWake < CooldownSeconds)
            return false;
        _lastWake = nowSeconds;
        return true;
    }
}
