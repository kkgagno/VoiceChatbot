using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace VoiceChatbot;

public sealed class CapturedPhoto
{
    public string Path { get; init; } = "";
    public string Base64 { get; init; } = "";
}

public sealed class CameraService : IDisposable
{
    private MediaCapture? _mediaCapture;
    private bool _initialized;
    private readonly SemaphoreSlim _captureLock = new(1, 1);

    public async Task<CapturedPhoto> CapturePhotoAsync(CancellationToken ct = default)
    {
        await _captureLock.WaitAsync(ct);
        try
        {
            await EnsureInitializedAsync();

            using var stream = new InMemoryRandomAccessStream();
            await _mediaCapture!.CapturePhotoToStreamAsync(
                ImageEncodingProperties.CreateJpeg(),
                stream);

            stream.Seek(0);
            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }

            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VoiceChatbot",
                "camera");
            Directory.CreateDirectory(dir);

            var path = System.IO.Path.Combine(dir, $"photo_{DateTime.Now:yyyyMMdd_HHmmss}.jpg");
            await File.WriteAllBytesAsync(path, bytes, ct);

            return new CapturedPhoto
            {
                Path = path,
                Base64 = Convert.ToBase64String(bytes)
            };
        }
        finally
        {
            ReleaseCamera();
            _captureLock.Release();
        }
    }

    private async Task EnsureInitializedAsync()
    {
        if (_initialized)
            return;

        _mediaCapture = new MediaCapture();
        await _mediaCapture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = StreamingCaptureMode.Video
        });
        _initialized = true;
    }

    public void Dispose()
    {
        ReleaseCamera();
        _captureLock.Dispose();
    }

    private void ReleaseCamera()
    {
        _mediaCapture?.Dispose();
        _mediaCapture = null;
        _initialized = false;
    }
}
