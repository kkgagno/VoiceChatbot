using System;

namespace VoiceChatbot;

/// <summary>
/// Decides when the user starts talking over the assistant ("barge-in") from the RMS level of short
/// microphone buffers recorded while speech plays. The first <see cref="CalibrationMs"/> of playback
/// measure the noise floor (room noise plus any echo from the speakers); after that the detector fires
/// once the level stays above max(floor * margin, sensitivity threshold) for <see cref="TriggerMs"/>.
/// Feed buffers in order from one thread. It fires once; call <see cref="Reset"/> to use it again.
/// </summary>
public sealed class BargeInDetector
{
    public const int DefaultSensitivity = 50;
    public const double DefaultCalibrationMs = 300;
    public const double DefaultTriggerMs = 300;
    // One short buffer of quiet between syllables pauses the count instead of starting it over.
    public const double DefaultMaxDipMs = 60;

    // Normalized RMS (0..1) the voice must exceed at sensitivity 0 and 100. Normal speech into a
    // headset microphone is roughly 0.05-0.2, quiet speech 0.01-0.05.
    private const double LeastSensitiveThreshold = 0.12;
    private const double MostSensitiveThreshold = 0.01;

    // How far above the measured floor the voice must be at sensitivity 0 and 100.
    private const double LeastSensitiveMargin = 4.0;
    private const double MostSensitiveMargin = 2.0;

    private double _calibratedMs;
    private double _floorEnergy; // sum of rms^2 * duration while calibrating
    private double _loudMs;
    private double _dipMs;

    public BargeInDetector(
        int sensitivity = DefaultSensitivity,
        double calibrationMs = DefaultCalibrationMs,
        double triggerMs = DefaultTriggerMs,
        double maxDipMs = DefaultMaxDipMs)
    {
        Sensitivity = Math.Clamp(sensitivity, 0, 100);
        CalibrationMs = Math.Max(0, calibrationMs);
        TriggerMs = Math.Max(1, triggerMs);
        MaxDipMs = Math.Max(0, maxDipMs);
        MinimumThreshold = ThresholdForSensitivity(Sensitivity);
        FloorMargin = MarginForSensitivity(Sensitivity);
    }

    public int Sensitivity { get; }
    public double CalibrationMs { get; }
    public double TriggerMs { get; }
    public double MaxDipMs { get; }

    /// <summary>The level speech must exceed however quiet the room is.</summary>
    public double MinimumThreshold { get; }

    /// <summary>How many times louder than the noise floor speech must be.</summary>
    public double FloorMargin { get; }

    /// <summary>RMS level measured during calibration (0 until the first buffer).</summary>
    public double NoiseFloor { get; private set; }

    public bool IsCalibrating => _calibratedMs < CalibrationMs;

    public bool Triggered { get; private set; }

    /// <summary>The level a buffer must exceed to count as the user talking.</summary>
    public double Threshold => Math.Max(NoiseFloor * FloorMargin, MinimumThreshold);

    /// <summary>
    /// Feeds one buffer's normalized RMS level (0..1) and its length. Returns true exactly once,
    /// on the buffer that completes <see cref="TriggerMs"/> of loud audio after calibration.
    /// </summary>
    public bool Process(double rms, double durationMs)
    {
        if (Triggered || durationMs <= 0 || double.IsNaN(rms) || double.IsNaN(durationMs))
            return false;

        rms = Math.Clamp(rms, 0, 1);

        if (IsCalibrating)
        {
            // RMS over the whole calibration period, so short loud bursts of echo raise the floor.
            _floorEnergy += rms * rms * durationMs;
            _calibratedMs += durationMs;
            NoiseFloor = Math.Sqrt(_floorEnergy / _calibratedMs);
            return false;
        }

        if (rms > Threshold)
        {
            _loudMs += durationMs;
            _dipMs = 0;
            if (_loudMs >= TriggerMs)
            {
                Triggered = true;
                return true;
            }
        }
        else if (_loudMs > 0)
        {
            _dipMs += durationMs;
            if (_dipMs > MaxDipMs)
            {
                _loudMs = 0;
                _dipMs = 0;
            }
        }

        return false;
    }

    /// <summary>Starts over, including a new calibration.</summary>
    public void Reset()
    {
        _calibratedMs = 0;
        _floorEnergy = 0;
        _loudMs = 0;
        _dipMs = 0;
        NoiseFloor = 0;
        Triggered = false;
    }

    /// <summary>Minimum speech level for a sensitivity of 0 (hard to trigger) to 100 (easy).</summary>
    public static double ThresholdForSensitivity(int sensitivity)
    {
        var t = Math.Clamp(sensitivity, 0, 100) / 100.0;
        // Geometric steps, so each notch changes the level by the same ratio.
        return LeastSensitiveThreshold * Math.Pow(MostSensitiveThreshold / LeastSensitiveThreshold, t);
    }

    /// <summary>Required ratio over the noise floor for a sensitivity of 0 to 100.</summary>
    public static double MarginForSensitivity(int sensitivity)
    {
        var t = Math.Clamp(sensitivity, 0, 100) / 100.0;
        return LeastSensitiveMargin + (MostSensitiveMargin - LeastSensitiveMargin) * t;
    }

    /// <summary>Normalized RMS (0..1) of 16-bit little-endian PCM; a trailing odd byte is ignored.</summary>
    public static double ComputeRms(byte[]? pcm16, int byteCount)
    {
        if (pcm16 == null)
            return 0;

        var samples = Math.Min(byteCount, pcm16.Length) / 2;
        if (samples <= 0)
            return 0;

        double sumSquares = 0;
        for (var i = 0; i < samples; i++)
        {
            var sample = (short)(pcm16[2 * i] | pcm16[2 * i + 1] << 8);
            sumSquares += (double)sample * sample;
        }

        return Math.Sqrt(sumSquares / samples) / 32768.0;
    }
}
