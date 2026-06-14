using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace VoiceChatbot;

public sealed class OpenCvCameraService : ICameraService
{
    private readonly int _cameraIndex;
    private readonly int _frameIntervalMs;
    private VideoCapture? _capture;
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private FacePresenceState _state = FacePresenceState.CameraUnavailable;

    public OpenCvCameraService(int cameraIndex = 0, int frameIntervalMs = 250)
    {
        _cameraIndex = cameraIndex;
        _frameIntervalMs = Math.Max(33, frameIntervalMs);
    }

    public FacePresenceState State => _state;
    public event EventHandler<CameraFrame>? FrameReady;
    public event EventHandler<FacePresenceState>? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_captureTask != null) return Task.CompletedTask;

        try
        {
            _capture = new VideoCapture(_cameraIndex);
            if (!_capture.IsOpened())
            {
                SetState(FacePresenceState.CameraUnavailable);
                return Task.CompletedTask;
            }

            _capture.Set(VideoCaptureProperties.FrameWidth, 640);
            _capture.Set(VideoCaptureProperties.FrameHeight, 480);
            _capture.Set(VideoCaptureProperties.Fps, 5);

            _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _captureTask = Task.Run(() => CaptureLoopAsync(_cts.Token), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Face camera start failed: {ex.Message}");
            SetState(FacePresenceState.CameraUnavailable);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_captureTask != null)
        {
            try { await _captureTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _captureTask = null;
        _cts?.Dispose();
        _cts = null;
        _capture?.Release();
        _capture?.Dispose();
        _capture = null;
        SetState(FacePresenceState.CameraUnavailable);
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        using var frame = new Mat();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_capture == null || !_capture.Read(frame) || frame.Empty())
                {
                    SetState(FacePresenceState.CameraUnavailable);
                    await Task.Delay(_frameIntervalMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                FrameReady?.Invoke(this, new CameraFrame(frame.Clone(), DateTime.UtcNow));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Debug.WriteLine($"Face camera frame failed: {ex.Message}");
                SetState(FacePresenceState.CameraUnavailable);
            }

            await Task.Delay(_frameIntervalMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private void SetState(FacePresenceState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}
