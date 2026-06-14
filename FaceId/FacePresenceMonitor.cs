using System;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace VoiceChatbot;

public sealed class FacePresenceMonitor : IDisposable
{
    private readonly ICameraService _cameraService;
    private readonly IFaceDetectionService _faceDetectionService;
    private readonly TimeSpan _faceLostGracePeriod = TimeSpan.FromMilliseconds(1500);
    private int _isProcessingFrame;
    private FacePresenceState _state = FacePresenceState.CameraUnavailable;
    private DateTime _lastFaceSeenUtc = DateTime.MinValue;

    public FacePresenceMonitor(ICameraService cameraService, IFaceDetectionService faceDetectionService)
    {
        _cameraService = cameraService;
        _faceDetectionService = faceDetectionService;
        _cameraService.FrameReady += OnFrameReady;
        _cameraService.StateChanged += (_, state) => UpdateState(state);
    }

    public FacePresenceState State => _state;
    public event EventHandler<FacePresenceState>? StateChanged;
    public event EventHandler<FaceDetectionSnapshot>? DetectionUpdated;

    public Task StartAsync(CancellationToken cancellationToken = default) => _cameraService.StartAsync(cancellationToken);
    public Task StopAsync() => _cameraService.StopAsync();

    private void OnFrameReady(object? sender, CameraFrame frame)
    {
        using (frame)
        {
            if (Interlocked.Exchange(ref _isProcessingFrame, 1) == 1)
                return;

            var result = _faceDetectionService.DetectFaces(frame.Image);
            try
            {
                DetectionUpdated?.Invoke(this, new FaceDetectionSnapshot(frame.Image.Clone(), result, frame.CapturedUtc));
                UpdateState(GetStableState(result.State, frame.CapturedUtc));
            }
            finally
            {
                Interlocked.Exchange(ref _isProcessingFrame, 0);
            }
        }
    }

    private FacePresenceState GetStableState(FacePresenceState detectedState, DateTime capturedUtc)
    {
        if (detectedState is FacePresenceState.FaceDetected or FacePresenceState.MultipleFacesDetected)
        {
            _lastFaceSeenUtc = capturedUtc;
            return detectedState;
        }

        if (detectedState == FacePresenceState.NoFaceDetected &&
            _lastFaceSeenUtc != DateTime.MinValue &&
            capturedUtc - _lastFaceSeenUtc < _faceLostGracePeriod)
        {
            return _state is FacePresenceState.FaceDetected or FacePresenceState.MultipleFacesDetected
                ? _state
                : FacePresenceState.FaceDetected;
        }

        return detectedState;
    }

    private void UpdateState(FacePresenceState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        _cameraService.FrameReady -= OnFrameReady;
        _cameraService.Dispose();
    }
}

public sealed class FaceDetectionSnapshot : IDisposable
{
    public FaceDetectionSnapshot(Mat frame, FaceDetectionResult result, DateTime capturedUtc)
    {
        Frame = frame;
        Result = result;
        CapturedUtc = capturedUtc;
    }

    public Mat Frame { get; }
    public FaceDetectionResult Result { get; }
    public DateTime CapturedUtc { get; }

    public void Dispose() => Frame.Dispose();
}
