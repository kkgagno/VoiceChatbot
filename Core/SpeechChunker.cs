using System;
using System.Collections.Generic;

namespace VoiceChatbot;

/// <summary>Settings for <see cref="SpeechChunker"/>. The defaults suit the live transcriber (16 kHz mono).</summary>
public sealed record SpeechChunkerOptions
{
    public int SampleRate { get; init; } = 16000;

    /// <summary>Audio is judged in frames of this length (speech or silence by their RMS level).</summary>
    public TimeSpan FrameLength { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>RMS level (0..1 of full scale) at or above which a frame counts as speech.</summary>
    public double SpeechThreshold { get; init; } = 0.006;

    /// <summary>A pause only ends a chunk once the chunk is at least this long.</summary>
    public TimeSpan MinChunk { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Silence after speech that ends a chunk.</summary>
    public TimeSpan PauseToCut { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>A chunk is cut at this length even without a pause.</summary>
    public TimeSpan MaxChunk { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Chunks with less speech than this are dropped (silence, a click, a cough).</summary>
    public TimeSpan MinSpeech { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Silence kept in front of the first speech, so a soft first syllable is not clipped.</summary>
    public TimeSpan PreRoll { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>At <see cref="MaxChunk"/> the cut goes after the quietest frame within this last stretch.</summary>
    public TimeSpan CutSearch { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The speech threshold the main voice input uses for a noise suppression setting (0-100).</summary>
    public static double ThresholdForNoiseGate(int noiseGate) => Math.Max(0.004, Math.Clamp(noiseGate, 0, 100) / 5000.0);
}

/// <summary>A piece of audio to transcribe: 16-bit mono PCM, where it starts in the stream, and how much of it is speech.</summary>
public sealed record SpeechChunk(byte[] Pcm, TimeSpan Start, TimeSpan Duration, TimeSpan Speech);

/// <summary>
/// Cuts a live 16-bit mono PCM stream into chunks at natural pauses, with a simple RMS voice detector:
/// a chunk ends after <see cref="SpeechChunkerOptions.PauseToCut"/> of silence once it is at least
/// <see cref="SpeechChunkerOptions.MinChunk"/> long, or at <see cref="SpeechChunkerOptions.MaxChunk"/> at the latest.
/// Silence before speech is not kept (apart from a short pre-roll) and chunks without enough speech are dropped.
/// Not thread safe; holds no timers, so the caller decides when audio arrives and when to flush.
/// </summary>
public sealed class SpeechChunker
{
    private readonly record struct Frame(byte[] Pcm, long StartSample, bool IsSpeech, double Rms);

    private readonly SpeechChunkerOptions _options;
    private readonly int _frameBytes;
    private readonly int _preRollFrames;
    private readonly int _pauseFrames;
    private readonly int _minChunkFrames;
    private readonly int _maxChunkFrames;
    private readonly int _minSpeechFrames;
    private readonly int _cutSearchFrames;
    private readonly List<Frame> _frames = new();
    private readonly byte[] _partial;
    private int _partialBytes;
    private long _nextFrameStartSample;
    private int _speechFrames;
    private int _trailingSilenceFrames;

    public SpeechChunker(SpeechChunkerOptions? options = null)
    {
        _options = options ?? new SpeechChunkerOptions();
        if (_options.SampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "The sample rate must be positive.");

        var frameSamples = Math.Max(1, (int)Math.Round(_options.SampleRate * _options.FrameLength.TotalSeconds));
        _frameBytes = frameSamples * 2;
        _partial = new byte[_frameBytes];
        _preRollFrames = FramesFor(_options.PreRoll);
        _pauseFrames = Math.Max(1, FramesFor(_options.PauseToCut));
        _minChunkFrames = FramesFor(_options.MinChunk);
        _maxChunkFrames = Math.Max(2, FramesFor(_options.MaxChunk));
        _minSpeechFrames = Math.Max(1, FramesFor(_options.MinSpeech));
        _cutSearchFrames = Math.Clamp(FramesFor(_options.CutSearch), 1, _maxChunkFrames - 1);
    }

    public SpeechChunkerOptions Options => _options;

    /// <summary>How far into the stream the audio added (or skipped) so far reaches.</summary>
    public TimeSpan Position => SamplesToTime(_nextFrameStartSample + _partialBytes / 2);

    /// <summary>Length of the audio waiting for the current chunk to end.</summary>
    public TimeSpan Buffered => SamplesToTime((long)_frames.Count * (_frameBytes / 2) + _partialBytes / 2);

    /// <summary>True when the audio waiting for the current chunk contains speech.</summary>
    public bool HasSpeech => _speechFrames > 0;

    /// <summary>RMS level (0..1) of the last whole frame added; 0 before any.</summary>
    public double LastLevel { get; private set; }

    /// <summary>Adds captured audio (16-bit little-endian mono PCM) and returns the chunks it completed, usually none.</summary>
    public IReadOnlyList<SpeechChunk> Add(ReadOnlySpan<byte> pcm16)
    {
        List<SpeechChunk>? completed = null;
        while (!pcm16.IsEmpty)
        {
            var take = Math.Min(_frameBytes - _partialBytes, pcm16.Length);
            pcm16[..take].CopyTo(_partial.AsSpan(_partialBytes));
            _partialBytes += take;
            pcm16 = pcm16[take..];
            if (_partialBytes < _frameBytes)
                break;

            var frame = (byte[])_partial.Clone();
            _partialBytes = 0;
            var chunk = AddFrame(frame);
            if (chunk != null)
                (completed ??= new List<SpeechChunk>()).Add(chunk);
        }

        return completed ?? (IReadOnlyList<SpeechChunk>)Array.Empty<SpeechChunk>();
    }

    /// <summary>Adds digital silence, e.g. for a stretch where a loopback device delivered no packets.</summary>
    public IReadOnlyList<SpeechChunk> AddSilence(TimeSpan duration)
    {
        var bytes = (int)Math.Min(int.MaxValue / 2, Math.Max(0, Math.Round(duration.TotalSeconds * _options.SampleRate))) * 2;
        return bytes == 0 ? Array.Empty<SpeechChunk>() : Add(new byte[bytes]);
    }

    /// <summary>
    /// Ends the current chunk now (when recording stops, for example): returns everything buffered when it
    /// holds enough speech, otherwise null. The buffer is empty afterwards either way.
    /// </summary>
    public SpeechChunk? Flush()
    {
        if (_partialBytes > 0)
        {
            var tail = _partial.AsSpan(0, _partialBytes).ToArray();
            var rms = Rms(tail);
            var isSpeech = rms >= _options.SpeechThreshold;
            _frames.Add(new Frame(tail, _nextFrameStartSample, isSpeech, rms));
            if (isSpeech)
                _speechFrames++;
            _nextFrameStartSample += _partialBytes / 2;
            _partialBytes = 0;
        }

        return TakeFrames(_frames.Count);
    }

    /// <summary>
    /// Drops everything buffered and moves the position on by <paramref name="duration"/> without audio
    /// (used while the assistant is speaking). Call <see cref="Flush"/> first to keep buffered speech.
    /// </summary>
    public void Skip(TimeSpan duration)
    {
        _nextFrameStartSample += _partialBytes / 2 + Math.Max(0, (long)Math.Round(duration.TotalSeconds * _options.SampleRate));
        _partialBytes = 0;
        _frames.Clear();
        _speechFrames = 0;
        _trailingSilenceFrames = 0;
    }

    /// <summary>RMS level of 16-bit little-endian PCM as a fraction of full scale (0..1).</summary>
    public static double Rms(ReadOnlySpan<byte> pcm16)
    {
        var samples = pcm16.Length / 2;
        if (samples == 0)
            return 0;

        double sum = 0;
        for (var i = 0; i < samples * 2; i += 2)
        {
            var sample = (short)(pcm16[i] | pcm16[i + 1] << 8);
            sum += (double)sample * sample;
        }

        return Math.Sqrt(sum / samples) / 32768.0;
    }

    private SpeechChunk? AddFrame(byte[] pcm)
    {
        var rms = Rms(pcm);
        LastLevel = rms;
        var isSpeech = rms >= _options.SpeechThreshold;
        _frames.Add(new Frame(pcm, _nextFrameStartSample, isSpeech, rms));
        _nextFrameStartSample += pcm.Length / 2;

        if (isSpeech)
        {
            _speechFrames++;
            _trailingSilenceFrames = 0;
        }
        else
        {
            _trailingSilenceFrames++;
        }

        if (_speechFrames == 0)
        {
            // Nothing heard yet: keep only the pre-roll.
            var extra = _frames.Count - _preRollFrames;
            if (extra > 0)
                _frames.RemoveRange(0, extra);
            _trailingSilenceFrames = 0;
            return null;
        }

        if (_trailingSilenceFrames >= _pauseFrames && _frames.Count >= _minChunkFrames)
            return TakeFrames(_frames.Count);

        if (_frames.Count >= _maxChunkFrames)
            return TakeFrames(QuietestCutPoint());

        return null;
    }

    // Number of frames to cut off at the maximum length: up to and including the quietest frame of the last stretch.
    private int QuietestCutPoint()
    {
        var best = _frames.Count - 1;
        for (var i = _frames.Count - 1; i >= _frames.Count - _cutSearchFrames; i--)
        {
            if (_frames[i].Rms < _frames[best].Rms)
                best = i;
        }

        return best + 1;
    }

    // Removes the first `count` frames as one chunk, or null when they hold too little speech.
    private SpeechChunk? TakeFrames(int count)
    {
        if (count <= 0 || _frames.Count == 0)
            return null;

        var taken = _frames.GetRange(0, count);
        _frames.RemoveRange(0, count);
        RecountRemainder();

        var speechFrames = 0;
        var length = 0;
        foreach (var frame in taken)
        {
            length += frame.Pcm.Length;
            if (frame.IsSpeech)
                speechFrames++;
        }

        if (speechFrames < _minSpeechFrames)
            return null;

        var pcm = new byte[length];
        var offset = 0;
        foreach (var frame in taken)
        {
            frame.Pcm.CopyTo(pcm, offset);
            offset += frame.Pcm.Length;
        }

        return new SpeechChunk(
            pcm,
            SamplesToTime(taken[0].StartSample),
            SamplesToTime(length / 2),
            SamplesToTime((long)speechFrames * (_frameBytes / 2)));
    }

    private void RecountRemainder()
    {
        _speechFrames = 0;
        _trailingSilenceFrames = 0;
        foreach (var frame in _frames)
        {
            if (frame.IsSpeech)
            {
                _speechFrames++;
                _trailingSilenceFrames = 0;
            }
            else
            {
                _trailingSilenceFrames++;
            }
        }
    }

    private int FramesFor(TimeSpan duration) =>
        (int)Math.Ceiling(Math.Max(0, duration.TotalMilliseconds) / _options.FrameLength.TotalMilliseconds - 1e-9);

    private TimeSpan SamplesToTime(long samples) => TimeSpan.FromTicks(samples * TimeSpan.TicksPerSecond / _options.SampleRate);
}
