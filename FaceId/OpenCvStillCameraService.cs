using System;
using System.Diagnostics;
using System.Threading;
using OpenCvSharp;

namespace VoiceChatbot;

public sealed class StillCameraCaptureResult : IDisposable
{
    public StillCameraCaptureResult(Mat frame, int framesRead, double meanBrightness, double brightnessStdDev)
    {
        Frame = frame;
        FramesRead = framesRead;
        MeanBrightness = meanBrightness;
        BrightnessStdDev = brightnessStdDev;
    }

    public Mat Frame { get; }
    public int FramesRead { get; }
    public double MeanBrightness { get; }
    public double BrightnessStdDev { get; }

    public bool LooksBlank => MeanBrightness < 8 || BrightnessStdDev < 2;

    public void Dispose() => Frame.Dispose();
}

public static class OpenCvStillCameraService
{
    public static StillCameraCaptureResult CaptureFrame(
        int cameraIndex,
        int warmupFrames = 18,
        int maxAttempts = 36,
        int frameDelayMs = 60)
    {
        using var capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
        if (!capture.IsOpened())
        {
            capture.Open(cameraIndex);
        }

        if (!capture.IsOpened())
            throw new InvalidOperationException($"Camera {cameraIndex} is unavailable.");

        capture.Set(VideoCaptureProperties.FrameWidth, 1280);
        capture.Set(VideoCaptureProperties.FrameHeight, 720);
        capture.Set(VideoCaptureProperties.Fps, 15);

        using var scratch = new Mat();
        Mat? bestFrame = null;
        var bestScore = double.MinValue;
        var bestMean = 0d;
        var bestStdDev = 0d;
        var framesRead = 0;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (!capture.Read(scratch) || scratch.Empty())
            {
                Thread.Sleep(frameDelayMs);
                continue;
            }

            framesRead++;
            var (mean, stdDev) = MeasureBrightness(scratch);
            var score = mean + (stdDev * 8);

            if (attempt >= warmupFrames && score > bestScore)
            {
                bestFrame?.Dispose();
                bestFrame = scratch.Clone();
                bestScore = score;
                bestMean = mean;
                bestStdDev = stdDev;
            }

            Thread.Sleep(frameDelayMs);
        }

        if (bestFrame == null)
            throw new InvalidOperationException("Camera opened, but no usable frame was returned.");

        Debug.WriteLine($"Still camera captured {framesRead} frames; brightness {bestMean:F1}, contrast {bestStdDev:F1}");
        return new StillCameraCaptureResult(bestFrame, framesRead, bestMean, bestStdDev);
    }

    private static (double Mean, double StdDev) MeasureBrightness(Mat frame)
    {
        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.MeanStdDev(gray, out var mean, out var stdDev);
        return (mean.Val0, stdDev.Val0);
    }
}
