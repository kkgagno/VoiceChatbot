using VoiceChatbot;
using Xunit;

public class BargeInDetectorTests
{
    private const double Buffer = 50; // ms, like the monitoring microphone
    private const double Quiet = 0.002;
    private const double Speech = 0.15;

    private static BargeInDetector Calibrated(double floor = Quiet, int sensitivity = 50)
    {
        var detector = new BargeInDetector(sensitivity);
        Feed(detector, floor, 6); // 300 ms of calibration
        Assert.False(detector.IsCalibrating);
        return detector;
    }

    // Feeds count buffers of one level and returns the index of the buffer that fired, or -1.
    private static int Feed(BargeInDetector detector, double rms, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if (detector.Process(rms, Buffer))
                return i;
        }

        return -1;
    }

    [Fact]
    public void NeverFiresWhileCalibrating()
    {
        var detector = new BargeInDetector();

        Assert.Equal(-1, Feed(detector, 0.9, 6));
        Assert.False(detector.IsCalibrating);
        Assert.Equal(0.9, detector.NoiseFloor, 6);
    }

    [Fact]
    public void FiresAfterThreeHundredMillisecondsOfSpeech()
    {
        var detector = Calibrated();

        Assert.Equal(5, Feed(detector, Speech, 10)); // the 6th buffer completes 300 ms
        Assert.True(detector.Triggered);
    }

    [Fact]
    public void ShortBurstDoesNotFire()
    {
        var detector = Calibrated();

        Assert.Equal(-1, Feed(detector, Speech, 5)); // 250 ms
        Assert.Equal(-1, Feed(detector, Quiet, 10));
        Assert.Equal(-1, Feed(detector, Speech, 5)); // count started over
        Assert.False(detector.Triggered);
    }

    [Fact]
    public void OneShortDipBetweenSyllablesIsTolerated()
    {
        var detector = Calibrated();

        Assert.Equal(-1, Feed(detector, Speech, 3));
        Assert.Equal(-1, Feed(detector, Quiet, 1));
        Assert.Equal(2, Feed(detector, Speech, 5)); // 150 ms + 150 ms; the dip does not count
    }

    [Fact]
    public void LongerPauseStartsTheCountOver()
    {
        var detector = Calibrated();

        Assert.Equal(-1, Feed(detector, Speech, 5));
        Assert.Equal(-1, Feed(detector, Quiet, 2)); // 100 ms
        Assert.Equal(-1, Feed(detector, Speech, 5));
        Assert.Equal(0, Feed(detector, Speech, 1));
    }

    [Fact]
    public void LoudFloorRaisesTheThreshold()
    {
        // Speaker echo measured during calibration: 0.05 * margin 3 = 0.15 at sensitivity 50.
        var detector = Calibrated(floor: 0.05);
        Assert.Equal(3.0, detector.FloorMargin, 6);
        Assert.Equal(0.15, detector.Threshold, 6);

        Assert.Equal(-1, Feed(detector, 0.12, 20));
        Assert.Equal(5, Feed(detector, 0.2, 10));
    }

    [Fact]
    public void QuietRoomUsesTheSensitivityThreshold()
    {
        var detector = Calibrated(floor: 0);

        Assert.Equal(0, detector.NoiseFloor);
        Assert.Equal(BargeInDetector.ThresholdForSensitivity(50), detector.Threshold, 9);
    }

    [Fact]
    public void SensitivityDecidesWhetherModerateSpeechCounts()
    {
        Assert.Equal(5, Feed(Calibrated(sensitivity: 50), 0.05, 10));
        Assert.Equal(-1, Feed(Calibrated(sensitivity: 0), 0.05, 20));
        Assert.Equal(5, Feed(Calibrated(sensitivity: 100), 0.015, 10));
    }

    [Fact]
    public void FloorIsTheRmsOfTheCalibrationPeriod()
    {
        var detector = new BargeInDetector();
        detector.Process(0.01, 150);
        detector.Process(0.03, 150);

        Assert.Equal(Math.Sqrt((0.01 * 0.01 + 0.03 * 0.03) / 2), detector.NoiseFloor, 9);
    }

    [Fact]
    public void FiresOnlyOnceUntilReset()
    {
        var detector = Calibrated();
        Assert.Equal(5, Feed(detector, Speech, 6));
        Assert.Equal(-1, Feed(detector, Speech, 20));

        detector.Reset();
        Assert.True(detector.IsCalibrating);
        Assert.False(detector.Triggered);
        Assert.Equal(-1, Feed(detector, Speech, 6)); // calibrating again
        Assert.Equal(-1, Feed(detector, Speech, 10)); // floor is now the loud level
        Assert.Equal(5, Feed(detector, 0.6, 10));
    }

    [Fact]
    public void IgnoresEmptyAndInvalidBuffers()
    {
        var detector = Calibrated();

        Assert.False(detector.Process(Speech, 0));
        Assert.False(detector.Process(Speech, -50));
        Assert.False(detector.Process(double.NaN, Buffer));
        Assert.False(detector.Process(Speech, double.NaN));
        Assert.Equal(5, Feed(detector, Speech, 10));
    }

    [Fact]
    public void UnevenBufferSizesAreMeasuredInTime()
    {
        var detector = new BargeInDetector();
        detector.Process(Quiet, 100);
        detector.Process(Quiet, 200);
        Assert.False(detector.IsCalibrating);

        Assert.False(detector.Process(Speech, 100));
        Assert.False(detector.Process(Speech, 150));
        Assert.True(detector.Process(Speech, 60));
    }

    [Theory]
    [InlineData(-20, 0.12)]
    [InlineData(0, 0.12)]
    [InlineData(100, 0.01)]
    [InlineData(250, 0.01)]
    public void SensitivityThresholdRange(int sensitivity, double expected) =>
        Assert.Equal(expected, BargeInDetector.ThresholdForSensitivity(sensitivity), 9);

    [Fact]
    public void HigherSensitivityMeansLowerThresholdAndMargin()
    {
        for (var s = 0; s < 100; s++)
        {
            Assert.True(BargeInDetector.ThresholdForSensitivity(s + 1) < BargeInDetector.ThresholdForSensitivity(s));
            Assert.True(BargeInDetector.MarginForSensitivity(s + 1) < BargeInDetector.MarginForSensitivity(s));
        }

        Assert.Equal(4.0, BargeInDetector.MarginForSensitivity(0), 9);
        Assert.Equal(2.0, BargeInDetector.MarginForSensitivity(100), 9);
    }

    [Fact]
    public void RmsOfSilenceAndNullIsZero()
    {
        Assert.Equal(0, BargeInDetector.ComputeRms(new byte[1600], 1600));
        Assert.Equal(0, BargeInDetector.ComputeRms(null, 100));
        Assert.Equal(0, BargeInDetector.ComputeRms(new byte[] { 0xFF }, 1));
    }

    [Fact]
    public void RmsOfConstantAndAlternatingSamples()
    {
        Assert.Equal(0.5, BargeInDetector.ComputeRms(Pcm(16384, 16384, 16384, 16384), 8), 9);
        Assert.Equal(0.5, BargeInDetector.ComputeRms(Pcm(16384, -16384, 16384, -16384), 8), 9);
        Assert.Equal(1.0, BargeInDetector.ComputeRms(Pcm(-32768, -32768), 4), 9);
    }

    [Fact]
    public void RmsOnlyReadsRecordedBytes()
    {
        var pcm = Pcm(16384, 0, 0, 0);
        Assert.Equal(0.5, BargeInDetector.ComputeRms(pcm, 2), 9);
        Assert.Equal(0.5, BargeInDetector.ComputeRms(pcm, 3), 9); // trailing odd byte ignored
        Assert.Equal(0.25, BargeInDetector.ComputeRms(pcm, 8), 9);
        Assert.Equal(0.25, BargeInDetector.ComputeRms(pcm, 1000), 9); // count larger than the buffer
    }

    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[2 * i] = (byte)(samples[i] & 0xFF);
            bytes[2 * i + 1] = (byte)((samples[i] >> 8) & 0xFF);
        }

        return bytes;
    }
}
