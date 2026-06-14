using System;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace VoiceChatbot;

public sealed class CameraFrame : IDisposable
{
    public CameraFrame(Mat image, DateTime capturedUtc)
    {
        Image = image;
        CapturedUtc = capturedUtc;
    }

    public Mat Image { get; }
    public DateTime CapturedUtc { get; }

    public void Dispose() => Image.Dispose();
}

public interface ICameraService : IDisposable
{
    FacePresenceState State { get; }
    event EventHandler<CameraFrame>? FrameReady;
    event EventHandler<FacePresenceState>? StateChanged;
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
}
