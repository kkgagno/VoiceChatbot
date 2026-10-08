using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace VoiceChatbot;

public sealed class PhoneRemoteServer : IAsyncDisposable
{
    public const string PinHeader = "X-Phone-Remote-Pin";
    private const string AuthorizedItem = "PhoneRemoteAuthorized";
    private const long MegaByte = 1024 * 1024;
    // Request body limits. Unauthenticated requests are refused before any body is read, so only the
    // small default applies to them; uploads get more once the PIN was checked.
    private const long DefaultMaxBodyBytes = 1 * MegaByte;
    private const long TextMaxBodyBytes = 4 * MegaByte;
    private const long SpeechMaxBodyBytes = 64 * MegaByte;   // a 20-minute Long Talk clip is about 38 MB
    private const long UploadMaxBodyBytes = 256 * MegaByte;  // files and recorded meetings

    private readonly Func<Stream, CancellationToken, Task<string>> _transcribeAsync;
    private readonly Func<PhoneRemoteUserInput, CancellationToken, Task<PhoneRemoteAssistantResult>> _chatAsync;
    private readonly Func<string, CancellationToken, Task<DocumentTextResult>> _extractDocumentAsync;
    private readonly Func<PhoneRemoteModelState> _modelStateProvider;
    private readonly ConcurrentDictionary<string, string> _audioFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _imageFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _videoFiles = new(StringComparer.OrdinalIgnoreCase);
    // Kept across restarts so stopping and starting the remote does not lift a lockout.
    private readonly PhoneRemoteAuthenticator _auth = new();
    private WebApplication? _app;
    private PhoneRemoteSettings _settings = new();
    private PhoneRemoteCertificateInfo? _certificateInfo;

    public PhoneRemoteServer(
        Func<Stream, CancellationToken, Task<string>> transcribeAsync,
        Func<PhoneRemoteUserInput, CancellationToken, Task<PhoneRemoteAssistantResult>> chatAsync,
        Func<string, CancellationToken, Task<DocumentTextResult>> extractDocumentAsync,
        Func<PhoneRemoteModelState>? modelStateProvider = null)
    {
        _transcribeAsync = transcribeAsync;
        _chatAsync = chatAsync;
        _extractDocumentAsync = extractDocumentAsync;
        _modelStateProvider = modelStateProvider ?? (() => new PhoneRemoteModelState("", "", ""));
    }

    public bool IsRunning => _app != null;
    public string LocalIpAddress { get; private set; } = "127.0.0.1";
    public string Url => $"https://{LocalIpAddress}:{_settings.Port}/";
    public string CertificateExportPath => _certificateInfo?.CerPath ?? "";

    /// <summary>The PIN the running remote accepts.</summary>
    public string Pin => _auth.Pin;

    /// <summary>
    /// Raised on a Kestrel thread when a device is locked out after too many wrong PINs.
    /// </summary>
    public event Action<PhoneRemoteLockout>? LockoutStarted;

    /// <summary>
    /// Changes the PIN of the running remote right away. A new PIN also lifts all lockouts.
    /// Returns false when the PIN is not valid (see <see cref="PhoneRemotePin.IsValid"/>) or unchanged.
    /// </summary>
    public bool ChangePin(string pin)
    {
        return PhoneRemotePin.IsValid(pin) && _auth.SetPin(pin);
    }

    public async Task StartAsync(PhoneRemoteSettings settings, CancellationToken ct = default)
    {
        if (IsRunning)
            return;

        // The PIN is required. MainWindow creates one before starting; refuse to run without it.
        if (!PhoneRemotePin.IsValid(settings.Pin))
            throw new InvalidOperationException("The phone remote needs a PIN of 4 to 64 characters without spaces.");

        _settings = settings;
        _auth.SetPin(settings.Pin);
        LocalIpAddress = GetLocalIpAddress();
        _certificateInfo = PhoneRemoteCertificateManager.EnsureCertificate(LocalIpAddress);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(PhoneRemoteServer).Assembly.FullName
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            // Raised per request in AuthorizeRequestAsync after the PIN was checked.
            options.Limits.MaxRequestBodySize = DefaultMaxBodyBytes;
            options.Listen(IPAddress.Any, _settings.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1;
                listen.UseHttps(_certificateInfo.Certificate);
            });
        });
        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = UploadMaxBodyBytes;
        });

        var app = builder.Build();
        MapRoutes(app);
        try
        {
            await app.StartAsync(ct);
        }
        catch
        {
            // A half-started host (for example the port is in use) would keep its resources otherwise.
            try { await app.DisposeAsync(); } catch { }
            throw;
        }
        _app = app;
    }

    public async Task StopAsync()
    {
        if (_app == null)
            return;

        var app = _app;
        _app = null;
        // ConfigureAwait(false): callers on the UI thread may block on this during shutdown.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await app.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        await app.DisposeAsync().ConfigureAwait(false);
    }

    private void MapRoutes(WebApplication app)
    {
        app.Use(AuthorizeRequestAsync);
        app.MapGet("/", () => Results.Content(BuildPhonePage(), "text/html; charset=utf-8"));
        app.MapGet("/api/status", (HttpRequest request) =>
        {
            // Without a PIN the page only learns that one is needed; with the right PIN it also gets the model.
            if (!IsAuthorized(request))
                return Results.Json(new { ok = true, requiresPin = true, authorized = false });

            var modelState = _modelStateProvider();
            return Results.Json(new
            {
                ok = true,
                requiresPin = true,
                authorized = true,
                activeProvider = modelState.Provider,
                activeModel = modelState.Model,
                activeEndpoint = modelState.Endpoint
            });
        });

        app.MapPost("/api/chat", async (HttpRequest request, CancellationToken ct) =>
        {
            if (!IsAuthorized(request))
                return Results.Unauthorized();

            var transcript = await TranscribeRequestAsync(request, ct);
            if (string.IsNullOrWhiteSpace(transcript))
                return Results.Json(new { transcript = "", response = "I did not catch that.", audioUrl = "" });

            var response = await BuildResponseAsync(new PhoneRemoteUserInput(transcript), ct);
            return Results.Json(response);
        });

        app.MapPost("/api/transcribe", async (HttpRequest request, CancellationToken ct) =>
        {
            if (!IsAuthorized(request))
                return Results.Unauthorized();

            var transcript = await TranscribeRequestAsync(request, ct);
            return Results.Json(new
            {
                transcript = transcript.Trim()
            });
        });

        app.MapPost("/api/respond", async (PhoneRemoteTextRequest request, HttpRequest httpRequest, CancellationToken ct) =>
        {
            if (!IsAuthorized(httpRequest))
                return Results.Unauthorized();

            if (string.IsNullOrWhiteSpace(request.Text))
                return Results.BadRequest(new { error = "No text was provided." });

            var response = await BuildResponseAsync(new PhoneRemoteUserInput(
                request.Text.Trim(),
                keepDocumentsActive: request.KeepDocumentsActive), ct);
            return Results.Json(response);
        });

        app.MapPost("/api/message", async (HttpRequest request, CancellationToken ct) =>
        {
            if (!IsAuthorized(request))
                return Results.Unauthorized();

            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "Expected form data." });

            var input = await BuildUserInputFromFormAsync(request, ct);
            if (string.IsNullOrWhiteSpace(input.Text) && input.ImagesBase64.Count == 0)
                return Results.BadRequest(new { error = "Type a message or attach a supported file." });

            var response = await BuildResponseAsync(input, ct);
            return Results.Json(response);
        });

        app.MapGet("/audio/{id}", (string id) =>
        {
            if (!_audioFiles.TryGetValue(id, out var path) || !File.Exists(path))
                return Results.NotFound();

            return Results.File(path, "audio/wav", enableRangeProcessing: true);
        });

        app.MapGet("/image/{id}", (string id) =>
        {
            if (!_imageFiles.TryGetValue(id, out var path) || !File.Exists(path))
                return Results.NotFound();

            return Results.File(path, GetImageContentType(path), Path.GetFileName(path), enableRangeProcessing: true);
        });

        app.MapGet("/video/{id}", (string id) =>
        {
            if (!_videoFiles.TryGetValue(id, out var path) || !File.Exists(path))
                return Results.NotFound();

            return Results.File(path, GetVideoContentType(path), Path.GetFileName(path), enableRangeProcessing: true);
        });
    }

    private async Task<string> TranscribeRequestAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType)
            return "";

        var form = await request.ReadFormAsync(ct);
        var audio = form.Files.GetFile("audio");
        if (audio == null || audio.Length == 0)
            return "";

        await using var stream = audio.OpenReadStream();
        return (await _transcribeAsync(stream, ct)).Trim();
    }

    private async Task<PhoneRemoteUserInput> BuildUserInputFromFormAsync(HttpRequest request, CancellationToken ct)
    {
        var form = await request.ReadFormAsync(ct);
        var text = form["text"].FirstOrDefault()?.Trim() ?? "";
        var images = new List<string>();
        var imagePaths = new List<string>();
        var audioPaths = new List<string>();
        var documents = new List<PhoneRemoteDocument>();
        var notes = new List<string>();
        var keepDocumentsActive = IsTrue(form["keepDocumentsActive"].FirstOrDefault());

        foreach (var file in form.Files)
        {
            if (file.Length == 0)
                continue;

            var contentType = file.ContentType ?? "";
            // Only ever shown as text; files are saved under a random name (see PhoneUploadFiles).
            var fileName = PhoneUploadFiles.SafeDisplayName(file.FileName);
            if (contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                var tempPath = PhoneUploadFiles.CreateTempPath(Path.Combine(Path.GetTempPath(), "VoiceChatbot", "phone-images"), fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
                await using (var source = file.OpenReadStream())
                await using (var target = File.Create(tempPath))
                {
                    await source.CopyToAsync(target, ct);
                }

                var bytes = await File.ReadAllBytesAsync(tempPath, ct);
                images.Add(Convert.ToBase64String(bytes));
                imagePaths.Add(tempPath);
                notes.Add($"Attached image: {fileName}");
                continue;
            }

            if (IsAudioFile(contentType, fileName))
            {
                var tempPath = PhoneUploadFiles.CreateTempPath(Path.Combine(Path.GetTempPath(), "VoiceChatbot", "phone-audio"), fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
                await using (var source = file.OpenReadStream())
                await using (var target = File.Create(tempPath))
                {
                    await source.CopyToAsync(target, ct);
                }

                audioPaths.Add(tempPath);
                notes.Add($"Attached audio: {fileName}");
                string? convertedPath = null;
                try
                {
                    var transcriptionPath = await PrepareAudioForTranscriptionAsync(tempPath, ct);
                    if (!string.Equals(transcriptionPath, tempPath, StringComparison.OrdinalIgnoreCase))
                        convertedPath = transcriptionPath;

                    await using var audioStream = File.OpenRead(transcriptionPath);
                    var transcript = (await _transcribeAsync(audioStream, ct)).Trim();
                    if (!string.IsNullOrWhiteSpace(transcript))
                    {
                        var title = $"{fileName} transcript";
                        documents.Add(new PhoneRemoteDocument(
                            title,
                            new DocumentTextResult(title, transcript, "") { FullText = transcript }));
                        notes.Add($"Audio transcript added: {fileName}");
                    }
                    else
                    {
                        notes.Add($"Audio attached but no speech transcript was detected: {fileName}");
                    }
                }
                catch (Exception ex)
                {
                    notes.Add($"Audio attached but transcription failed for {fileName}: {ex.Message}");
                }
                finally
                {
                    if (!string.IsNullOrWhiteSpace(convertedPath))
                    {
                        try { File.Delete(convertedPath); }
                        catch { }
                    }
                }
                continue;
            }

            if (IsReadableDocumentFile(contentType, fileName))
            {
                var tempPath = PhoneUploadFiles.CreateTempPath(Path.Combine(Path.GetTempPath(), "VoiceChatbot", "phone-documents"), fileName);
                Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
                try
                {
                    await using (var source = file.OpenReadStream())
                    await using (var target = File.Create(tempPath))
                    {
                        await source.CopyToAsync(target, ct);
                    }

                    var result = await _extractDocumentAsync(tempPath, ct);
                    if (!string.IsNullOrWhiteSpace(result.Text))
                    {
                        documents.Add(new PhoneRemoteDocument(fileName, result with { Path = fileName, Error = "" }));
                        notes.Add($"Attached document: {fileName}");
                    }
                    else
                        notes.Add($"Attached document: {fileName}\nCould not read document text: {result.Error}");
                }
                finally
                {
                    try { File.Delete(tempPath); }
                    catch { }
                }
                continue;
            }

            notes.Add($"Attached file: {fileName} ({contentType}). This file type is not text-readable yet from the phone remote.");
        }

        var combined = text;
        if (notes.Count > 0)
        {
            combined = string.IsNullOrWhiteSpace(combined)
                ? string.Join("\n\n", notes)
                : combined + "\n\n" + string.Join("\n\n", notes);
        }

        return new PhoneRemoteUserInput(combined, images, imagePaths, audioPaths, documents, keepDocumentsActive);
    }

    private static bool IsTrue(string? value)
    {
        return value != null &&
            (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
             value.Equals("on", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsReadableDocumentFile(string contentType, string fileName)
    {
        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".pdf" or ".docx" || DocumentFileTypes.IsTextExtension(ext);
    }

    private async Task<object> BuildResponseAsync(PhoneRemoteUserInput input, CancellationToken ct)
    {
        var result = await _chatAsync(input, ct);
        var modelState = _modelStateProvider();
        var audioUrl = "";
        if (!string.IsNullOrWhiteSpace(result.AudioPath) && File.Exists(result.AudioPath))
        {
            var id = Guid.NewGuid().ToString("N");
            _audioFiles[id] = result.AudioPath;
            audioUrl = $"/audio/{id}";
        }

        var imageUrl = "";
        if (!string.IsNullOrWhiteSpace(result.ImagePath) && File.Exists(result.ImagePath))
        {
            var id = Guid.NewGuid().ToString("N");
            _imageFiles[id] = result.ImagePath;
            imageUrl = $"/image/{id}";
        }

        var videoUrl = "";
        if (!string.IsNullOrWhiteSpace(result.VideoPath) && File.Exists(result.VideoPath))
        {
            var id = Guid.NewGuid().ToString("N");
            _videoFiles[id] = result.VideoPath;
            videoUrl = $"/video/{id}";
        }

        return new
        {
            transcript = input.Text,
            response = result.Response,
            audioUrl,
            imageUrl,
            videoUrl,
            activeDocumentCount = result.ActiveDocumentCount,
            activeProvider = modelState.Provider,
            activeModel = string.IsNullOrWhiteSpace(result.ActiveModel) ? modelState.Model : result.ActiveModel,
            activeEndpoint = string.IsNullOrWhiteSpace(result.ActiveEndpoint) ? modelState.Endpoint : result.ActiveEndpoint
        };
    }

    private static string GetImageContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            _ => "image/png"
        };
    }

    private static bool IsAudioFile(string contentType, string fileName)
    {
        if (contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
            return true;

        return Path.GetExtension(fileName).ToLowerInvariant() is ".wav" or ".mp3" or ".m4a" or ".flac" or ".ogg";
    }

    private static async Task<string> PrepareAudioForTranscriptionAsync(string inputPath, CancellationToken ct)
    {
        if (Path.GetExtension(inputPath).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return inputPath;

        var ffmpegPath = GetFfmpegPath();
        if (string.IsNullOrWhiteSpace(ffmpegPath))
            throw new InvalidOperationException("ffmpeg was not found, so this audio format cannot be converted to WAV.");

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "VoiceChatbot",
            "phone-audio",
            Guid.NewGuid().ToString("N") + ".wav");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var start = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-y");
        start.ArgumentList.Add("-i");
        start.ArgumentList.Add(inputPath);
        start.ArgumentList.Add("-ar");
        start.ArgumentList.Add("16000");
        start.ArgumentList.Add("-ac");
        start.ArgumentList.Add("1");
        start.ArgumentList.Add(outputPath);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stderr = await stderrTask;

        if (process.ExitCode != 0 || !File.Exists(outputPath))
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? "ffmpeg conversion failed." : stderr.Trim());

        return outputPath;
    }

    private static string GetFfmpegPath()
    {
        if (!OperatingSystem.IsWindows())
            return "ffmpeg";

        var bundled = Path.Combine(AppContext.BaseDirectory, "Tools", "Media", "ffmpeg.exe");
        if (File.Exists(bundled))
            return bundled;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packageRoot = Path.Combine(
            localAppData,
            "Microsoft",
            "WinGet",
            "Packages",
            "yt-dlp.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe");
        var installed = FindFirstFile(packageRoot, "ffmpeg.exe");
        if (!string.IsNullOrWhiteSpace(installed))
            return installed;

        return "ffmpeg.exe";
    }

    private static string FindFirstFile(string root, string pattern)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.GetFiles(root, pattern, SearchOption.AllDirectories).FirstOrDefault() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static string GetVideoContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            _ => "video/mp4"
        };
    }

    /// <summary>
    /// Every request except the page itself and the media links needs the PIN, so a route added later is
    /// protected by default. This runs before the endpoint reads or binds the request body, so a client
    /// without the PIN cannot make the server read an upload. Wrong PINs count towards a per-IP lockout
    /// (HTTP 429), see Core/PhoneRemoteAuth.cs.
    /// </summary>
    private async Task AuthorizeRequestAsync(HttpContext context, RequestDelegate next)
    {
        var path = context.Request.Path;
        if (IsPublicPath(path))
        {
            await next(context);
            return;
        }

        var pin = context.Request.Headers[PinHeader].FirstOrDefault();
        // The page asks for the status before a PIN was entered; that answer says a PIN is needed.
        if (string.IsNullOrWhiteSpace(pin) && path.Equals("/api/status", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var client = GetClientKey(context);
        var auth = _auth.Authenticate(client, pin);
        if (!auth.IsAuthorized)
        {
            if (auth.Status == PhoneRemoteAuthStatus.WrongPin)
                AppLog.Warn($"Phone remote: wrong PIN from {client}, {auth.AttemptsLeft} attempt(s) left.");
            if (auth.LockoutStarted)
                ReportLockout(client);
            await WriteErrorAsync(context, auth.HttpStatus, auth.Message, auth.RetryAfterSeconds);
            return;
        }

        context.Items[AuthorizedItem] = true;
        var maxBytes = GetMaxBodyBytes(path);
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
            sizeFeature.MaxRequestBodySize = maxBytes;
        if (context.Request.ContentLength > maxBytes)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge,
                $"That is too large for the phone remote (limit {maxBytes / MegaByte} MB).", 0);
            return;
        }

        await next(context);
    }

    /// <summary>
    /// The page, and the reply audio, images and videos it shows. Media links carry a random 128-bit id
    /// that is only handed out in an authenticated response; audio and img tags cannot send the PIN header.
    /// </summary>
    private static bool IsPublicPath(PathString path)
    {
        return path == "/" ||
               path.StartsWithSegments("/audio", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/image", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWithSegments("/video", StringComparison.OrdinalIgnoreCase);
    }

    private static long GetMaxBodyBytes(PathString path)
    {
        if (path.StartsWithSegments("/api/message", StringComparison.OrdinalIgnoreCase))
            return UploadMaxBodyBytes;
        if (path.StartsWithSegments("/api/chat", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api/transcribe", StringComparison.OrdinalIgnoreCase))
            return SpeechMaxBodyBytes;
        if (path.StartsWithSegments("/api/respond", StringComparison.OrdinalIgnoreCase))
            return TextMaxBodyBytes;
        return DefaultMaxBodyBytes;
    }

    /// <summary>Set by <see cref="AuthorizeRequestAsync"/>; checked again by every API handler.</summary>
    private static bool IsAuthorized(HttpRequest request)
    {
        return request.HttpContext.Items.ContainsKey(AuthorizedItem);
    }

    private static string GetClientKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address == null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return address.ToString();
    }

    private void ReportLockout(string client)
    {
        var lockout = new PhoneRemoteLockout(client, _auth.Limiter.MaxFailures, _auth.Limiter.LockoutDuration);
        try
        {
            LockoutStarted?.Invoke(lockout);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Phone remote: locked out {client}, but reporting it failed.", ex);
        }
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string message, int retryAfterSeconds)
    {
        context.Response.StatusCode = statusCode;
        if (retryAfterSeconds > 0)
            context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        await context.Response.WriteAsJsonAsync(new { error = message, retryAfterSeconds });
    }

    private static string GetLocalIpAddress()
    {
        try
        {
            foreach (var address in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            {
                if (address.AddressFamily != AddressFamily.InterNetwork)
                    continue;

                var text = address.ToString();
                if (!text.StartsWith("127.", StringComparison.Ordinal))
                    return text;
            }
        }
        catch
        {
            // Fall back below.
        }

        return "127.0.0.1";
    }

    private static string BuildPhonePage()
    {
        return """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover">
  <title>Voice Chatbot Remote</title>
  <style>
    :root { color-scheme: dark; font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
    html { width: 100%; overflow-x: hidden; -webkit-text-size-adjust: 100%; text-size-adjust: 100%; touch-action: pan-y; }
    body { width: 100%; overflow-x: hidden; margin: 0; background: #101114; color: #f5f5f5; overscroll-behavior-x: none; }
    main { width: 100%; max-width: 720px; margin: 0 auto; min-height: 100vh; display: flex; flex-direction: column; padding: 10px; box-sizing: border-box; }
    header { display: flex; justify-content: space-between; align-items: center; gap: 8px; margin-bottom: 8px; }
    h1 { font-size: 16px; margin: 0; flex: 1; }
    #status, #modelState { color: #aeb3bd; font-size: 12px; }
    #modelState { max-width: 42vw; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    #chat { flex: 1; overflow: auto; display: flex; flex-direction: column; gap: 8px; padding: 4px 0 10px; }
    .msg { border-radius: 7px; padding: 8px 10px; line-height: 1.3; white-space: pre-wrap; font-size: 14px; }
    .me { align-self: flex-end; background: #246bfe; max-width: 86%; }
    .bot { align-self: flex-start; background: #24262c; max-width: 86%; }
    .sys { align-self: center; color: #aeb3bd; font-size: 12px; }
    .generated { display: block; max-width: 100%; border-radius: 7px; margin-top: 8px; }
    .saveImage { display: inline-block; margin-top: 6px; color: white; background: #00a884; border-radius: 7px; padding: 7px 9px; text-decoration: none; font-weight: 700; }
    .bar { display: grid; grid-template-columns: 1fr auto; gap: 6px; align-items: center; background: #101114; padding: 5px 0; }
    .filebar { grid-template-columns: auto 1fr auto; }
    .fileLabel { color: #d7dae0; font-size: 12px; font-weight: 700; white-space: nowrap; }
    .textbar { display: grid; grid-template-columns: 1fr auto; gap: 6px; align-items: end; position: sticky; bottom: 0; background: #101114; padding: 5px 0 env(safe-area-inset-bottom); }
    input, textarea { min-width: 0; border: 1px solid #373b45; background: #181a20; color: white; border-radius: 7px; padding: 8px 9px; font-size: 16px; }
    input[type="file"] { font-size: 16px; padding: 7px; }
    #pin.needed { border-color: #d63031; }
    input[type="checkbox"] { width: 18px; height: 18px; padding: 0; accent-color: #00a884; }
    textarea { resize: vertical; min-height: 38px; max-height: 130px; }
    button { border: 0; border-radius: 7px; color: white; background: #00a884; padding: 8px 10px; font-weight: 700; font-size: 13px; line-height: 1.1; }
    button:disabled { opacity: .5; }
    #modelHelp { background: #2d3436; padding: 7px 9px; white-space: nowrap; }
    .commandHelp { align-self: stretch; background: #181a20; border: 1px solid #373b45; }
    .commandTitle { color: #d7dae0; font-size: 12px; font-weight: 700; margin-bottom: 7px; }
    .commandList { display: grid; gap: 6px; }
    .commandChoice { width: 100%; background: #2d3436; color: white; text-align: left; padding: 9px 10px; }
    .commandChoice:active { background: #00a884; }
    .controls { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 6px; margin-bottom: 5px; }
    #talk { width: 100%; background: #6c5ce7; }
    #live { width: 100%; background: #2d3436; }
    #live.on { background: #d63031; }
    #longTalk { width: 100%; background: #0984e3; }
    #longTalk.on { background: #00a884; }
    #stopAudio { width: 100%; background: #d63031; }
    #meetingRecord { width: 100%; background: #6c5ce7; }
    #meetingRecord.on { background: #d63031; }
    #clearMeeting { width: 100%; background: #636e72; }
    #meetingStatus { color: #aeb3bd; font-size: 12px; align-self: center; }
    .keepdoc { display: flex; align-items: center; gap: 8px; color: #d7dae0; font-size: 13px; font-weight: 700; }
    #activeDocStatus { color: #aeb3bd; font-size: 12px; text-align: right; }
    audio { width: 100%; margin-top: 8px; }
  </style>
</head>
<body>
<main>
  <header><h1>Voice Chatbot</h1><button id="modelHelp" type="button">Model Help</button><span id="modelState"></span><span id="status">Ready</span></header>
  <div id="chat"></div>
  <div class="controls">
    <button id="talk">Hold to Talk</button>
    <button id="live">Live Mode Off</button>
    <button id="longTalk">Long Talk Off</button>
  </div>
  <div class="bar">
    <button id="stopAudio">Stop Audio</button>
  </div>
  <div class="bar">
    <button id="meetingRecord">Record Meeting</button>
    <button id="clearMeeting">Clear Meeting</button>
    <span id="meetingStatus"></span>
  </div>
  <div class="bar">
    <input id="pin" inputmode="numeric" autocomplete="off" placeholder="PIN from the desktop app">
    <button id="test">Test Mic</button>
  </div>
  <div class="bar filebar">
    <label class="fileLabel" for="files">Files</label>
    <input id="files" type="file" multiple accept="image/*,audio/*,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document,.pdf,.docx,.txt,.md,.csv,.json,.xml,.log,.wav,.mp3,.m4a,.flac,.ogg">
    <button id="clearFiles">Clear</button>
  </div>
  <div class="bar">
    <label class="keepdoc"><input id="keepDoc" type="checkbox"> Keep doc active</label>
    <span id="activeDocStatus"></span>
  </div>
  <div class="bar filebar">
    <label class="fileLabel" for="videoAudio">Video Audio</label>
    <input id="videoAudio" type="file" accept="audio/*,.wav,.mp3,.m4a,.flac,.ogg">
    <button id="clearVideoAudio">Clear Audio</button>
  </div>
  <div class="bar">
    <button id="createImage">Create Image</button>
    <button id="editImage">Edit Image</button>
    <button id="createVideo">Make Video</button>
  </div>
  <div class="textbar">
    <textarea id="textMessage" placeholder="Type, paste a URL, or attach a file"></textarea>
    <button id="sendText">Send</button>
  </div>
</main>
<script>
const chat = document.getElementById('chat');
const statusEl = document.getElementById('status');
const modelStateEl = document.getElementById('modelState');
const modelHelp = document.getElementById('modelHelp');
const talk = document.getElementById('talk');
const live = document.getElementById('live');
const longTalk = document.getElementById('longTalk');
const stopAudio = document.getElementById('stopAudio');
const pin = document.getElementById('pin');
const textMessage = document.getElementById('textMessage');
const files = document.getElementById('files');
const keepDoc = document.getElementById('keepDoc');
const activeDocStatus = document.getElementById('activeDocStatus');
const videoAudio = document.getElementById('videoAudio');
const sendText = document.getElementById('sendText');
const clearFiles = document.getElementById('clearFiles');
const clearVideoAudio = document.getElementById('clearVideoAudio');
const createImage = document.getElementById('createImage');
const editImage = document.getElementById('editImage');
const createVideo = document.getElementById('createVideo');
const meetingRecord = document.getElementById('meetingRecord');
const clearMeeting = document.getElementById('clearMeeting');
const meetingStatus = document.getElementById('meetingStatus');
let audioContext, source, processor, stream, chunks = [], recordedSamples = 0, recording = false;
let meetingRecording = false, meetingChunks = [], meetingBlob = null, meetingStartedAt = 0;
let livePlaybackSource = null;
let livePlayer, liveAudioUnlocked = false;
let liveMode = false, liveSending = false, liveArmed = false, liveSpeechStarted = false;
let longTalkMode = false;
let liveSilenceMs = 0, liveVoiceMs = 0, liveLastTick = 0;
const liveStartThreshold = 0.012;
const liveStopThreshold = 0.007;
const liveMinVoiceMs = 100;
const shortUtteranceMaxVoiceMs = 800;
const shortSilenceToSendMs = 500;
const noiseResetSilenceMs = 900;
const normalSilenceToSendMs = 2600;
const longSilenceToSendMs = 5500;
const normalMaxClipMs = 300000;
const longMaxClipMs = 1200000;
const silentWav = 'data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEAQB8AAEAfAAABAAgAZGF0YQAAAAA=';
const pinStorageKey = 'voicechatbot-remote-pin';
const pinHelp = 'Enter the PIN shown in the desktop app under Settings > Phone Remote.';

// The PIN is required and sent with every request. This phone remembers it until the desktop app rejects it.
function loadSavedPin() {
  try { pin.value = localStorage.getItem(pinStorageKey) || ''; } catch {}
}

function savePin() {
  try {
    const value = pin.value.trim();
    if (value) localStorage.setItem(pinStorageKey, value);
    else localStorage.removeItem(pinStorageKey);
  } catch {}
}

function forgetPin() {
  pin.value = '';
  savePin();
  pin.classList.add('needed');
}

function pinHeaders(extra) {
  return Object.assign({ 'X-Phone-Remote-Pin': pin.value.trim() }, extra || {});
}

function requirePin() {
  if (pin.value.trim()) return true;
  pin.classList.add('needed');
  statusEl.textContent = 'PIN needed';
  add('sys', pinHelp);
  return false;
}

// Turns a failed response into an Error with a friendly message.
// 401: wrong or missing PIN (forgotten so it is not sent again), 429: locked out after too many wrong PINs.
async function responseError(r, label) {
  let message = '';
  try { message = ((await r.json()) || {}).error || ''; } catch {}
  if (r.status === 401 || r.status === 429) {
    if (r.status === 401) forgetPin();
    else pin.classList.add('needed');
    if (liveMode) setLiveMode(false);
    statusEl.textContent = r.status === 429 ? 'Locked out' : 'PIN needed';
    if (!message) {
      message = r.status === 429
        ? 'Too many wrong PIN attempts. Wait 10 minutes, then try again.'
        : 'Wrong PIN. ' + pinHelp;
    }
    return new Error(message);
  }
  if (r.status === 413) return new Error(message || 'That is too large to send to the desktop app.');
  return new Error(message || (label + ' error ' + r.status));
}

// Checks the PIN with the desktop app and shows the active model when it is right.
async function checkPin(announce) {
  if (!pin.value.trim()) return false;
  try {
    const r = await fetch('/api/status', { headers: pinHeaders() });
    if (!r.ok) {
      add('sys', (await responseError(r, 'Status')).message);
      return false;
    }
    const data = await r.json();
    if (!data.authorized) return false;
    updateModelState(data);
    pin.classList.remove('needed');
    if (announce) add('sys', 'PIN accepted.');
    return true;
  } catch {
    // Connection problems show up when something is sent.
    return false;
  }
}

function add(cls, text) {
  const el = document.createElement('div');
  el.className = 'msg ' + cls;
  el.textContent = text;
  chat.appendChild(el);
  chat.scrollTop = chat.scrollHeight;
  return el;
}

function clearSpeechChunks() {
  chunks = [];
  recordedSamples = 0;
}

function addBotResult(data) {
  updateModelState(data);
  const el = add('bot', data.response || 'No response.');
  if (data.imageUrl) {
    const img = document.createElement('img');
    img.className = 'generated';
    img.src = data.imageUrl;
    img.alt = 'Generated image';
    el.appendChild(img);

    const save = document.createElement('button');
    save.className = 'saveImage';
    save.type = 'button';
    save.textContent = 'Share Image';
    save.addEventListener('click', () => shareImage(data.imageUrl));
    el.appendChild(save);
  }
  if (data.videoUrl) {
    const video = document.createElement('video');
    video.className = 'generated';
    video.src = data.videoUrl;
    video.controls = true;
    video.playsInline = true;
    el.appendChild(video);

    const save = document.createElement('button');
    save.className = 'saveImage';
    save.type = 'button';
    save.textContent = 'Share Video';
    save.addEventListener('click', () => shareVideo(data.videoUrl));
    el.appendChild(save);
  }
  return el;
}

function updateModelState(data) {
  if (!data) return;
  const model = data.activeModel || '';
  const endpoint = data.activeEndpoint || '';
  const provider = data.activeProvider || '';
  if (!model && !endpoint && !provider) return;
  const parts = [];
  if (model) parts.push(model);
  if (endpoint) parts.push(endpoint.replace(/^https?:\/\//, ''));
  modelStateEl.textContent = parts.join(' @ ');
  modelStateEl.title = [provider, model, endpoint].filter(Boolean).join(' | ');
}

function showModelHelp() {
  const commands = [
    'Hermes stop current running llama.cpp model',
    'Hermes start gpt-oss:120b',
    'Hermes start gemma',
    'Hermes start gemma 4b',
    'Hermes start gemma 12b',
    'Hermes start gemma speculative',
    'Hermes start gemma 26b a4b',
    'Hermes start mistral',
    'Hermes start qwen',
    'Hermes start lfm',
    'Hermes start comfyui',
    'Hermes stop comfyui'
  ];
  const help = document.createElement('div');
  help.className = 'msg commandHelp';
  const title = document.createElement('div');
  title.className = 'commandTitle';
  title.textContent = 'Tap a command to put it in the message box:';
  help.appendChild(title);
  const list = document.createElement('div');
  list.className = 'commandList';
  for (const command of commands) {
    const choice = document.createElement('button');
    choice.className = 'commandChoice';
    choice.type = 'button';
    choice.textContent = command;
    choice.addEventListener('click', () => {
      textMessage.value = command;
      textMessage.focus();
      textMessage.scrollIntoView({ behavior: 'smooth', block: 'center' });
    });
    list.appendChild(choice);
  }
  help.appendChild(list);
  chat.appendChild(help);
  chat.scrollTop = chat.scrollHeight;
}

function appendAttachedFiles(form) {
  for (const file of files.files) form.append('files', file, file.name);
  for (const file of videoAudio.files) form.append('files', file, file.name);
  if (meetingBlob) form.append('files', meetingBlob, 'iphone-meeting.wav');
}

function appendRequestFlags(form) {
  form.append('keepDocumentsActive', keepDoc.checked ? 'true' : 'false');
}

function updateActiveDocStatus(count) {
  const activeCount = Number(count || 0);
  activeDocStatus.textContent = activeCount > 0 ? `Active doc: ${activeCount}` : '';
}

function selectedFileNames() {
  return [...Array.from(files.files), ...Array.from(videoAudio.files)].map(f => f.name);
}

async function shareImage(url) {
  try {
    const response = await fetch(url);
    if (!response.ok) throw new Error('Image fetch failed');
    const blob = await response.blob();
    const extension = blob.type === 'image/jpeg' ? 'jpg' : 'png';
    const file = new File([blob], `voicechatbot-image.${extension}`, { type: blob.type || 'image/png' });

    if (navigator.canShare && navigator.canShare({ files: [file] }) && navigator.share) {
      await navigator.share({ files: [file], title: 'Voice Chatbot image' });
      return;
    }

    add('sys', 'Share is unavailable here. Long-press the image and choose Save to Photos.');
  } catch (e) {
    add('sys', 'Could not open share sheet. Long-press the image to save it.');
  }
}

async function shareVideo(url) {
  try {
    const response = await fetch(url);
    if (!response.ok) throw new Error('Video fetch failed');
    const blob = await response.blob();
    const extension = blob.type === 'video/webm' ? 'webm' : 'mp4';
    const file = new File([blob], `voicechatbot-video.${extension}`, { type: blob.type || 'video/mp4' });

    if (navigator.canShare && navigator.canShare({ files: [file] }) && navigator.share) {
      await navigator.share({ files: [file], title: 'Voice Chatbot video' });
      return;
    }

    window.open(url, '_blank');
  } catch (e) {
    add('sys', 'Could not open share sheet. Open the video and use the browser share menu.');
  }
}

function unlockLiveAudio() {
  if (liveAudioUnlocked) return true;
  if (!livePlayer) {
    livePlayer = new Audio();
    livePlayer.preload = 'auto';
    livePlayer.playsInline = true;
  }

  livePlayer.src = silentWav;
  livePlayer.muted = true;
  const attempt = livePlayer.play();
  if (attempt && attempt.then) {
    attempt.then(() => {
      livePlayer.pause();
      livePlayer.currentTime = 0;
      livePlayer.muted = false;
      liveAudioUnlocked = true;
    }).catch(() => {
      livePlayer.muted = false;
      liveAudioUnlocked = false;
    });
  } else {
    livePlayer.muted = false;
    liveAudioUnlocked = true;
  }

  return true;
}

async function ensureMic() {
  if (stream) return;
  stream = await navigator.mediaDevices.getUserMedia({ audio: { echoCancellation: true, noiseSuppression: true }, video: false });
  audioContext = new (window.AudioContext || window.webkitAudioContext)();
  source = audioContext.createMediaStreamSource(stream);
  processor = audioContext.createScriptProcessor(4096, 1, 1);
  processor.onaudioprocess = e => {
    const input = e.inputBuffer.getChannelData(0);
    if (recording) {
      chunks.push(new Float32Array(input));
      recordedSamples += input.length;
    }
    if (meetingRecording) meetingChunks.push(new Float32Array(input));
    if (liveMode && liveArmed && !liveSending) handleLiveAudio(input);
  };
  source.connect(processor);
  processor.connect(audioContext.destination);
}

function releaseMicForPlayback() {
  recording = false;
  liveArmed = false;
  liveSpeechStarted = false;
  liveLastTick = 0;
  clearSpeechChunks();
  if (liveMode) {
    statusEl.textContent = 'Speaking';
    return;
  }
  try { if (processor) processor.disconnect(); } catch {}
  try { if (source) source.disconnect(); } catch {}
  try { if (stream) stream.getTracks().forEach(t => t.stop()); } catch {}
  try { if (audioContext && audioContext.state !== 'closed') audioContext.close(); } catch {}
  processor = null;
  source = null;
  stream = null;
  audioContext = null;
}

async function resumeMicAfterPlayback() {
  if (!liveMode) return;
  try {
    await ensureMic();
    if (audioContext && audioContext.state !== 'running') await audioContext.resume();
    clearSpeechChunks();
    recording = false;
    liveSending = false;
    liveSpeechStarted = false;
    liveLastTick = 0;
    liveArmed = true;
    statusEl.textContent = 'Live: listening';
  } catch (e) {
    add('sys', 'Mic resume failed: ' + e.message);
    statusEl.textContent = 'Ready';
  }
}

function guardPlaybackMic(audio) {
  audio.addEventListener('play', () => releaseMicForPlayback());
  audio.addEventListener('ended', () => { resumeMicAfterPlayback(); });
  audio.addEventListener('pause', () => {
    if (audio.currentTime > 0 && audio.currentTime < audio.duration) resumeMicAfterPlayback();
  });
}

function encodeWav(buffers, sampleRate) {
  let length = buffers.reduce((n, b) => n + b.length, 0);
  let merged = new Float32Array(length);
  let offset = 0;
  for (const b of buffers) { merged.set(b, offset); offset += b.length; }
  const targetRate = 16000;
  const ratio = sampleRate / targetRate;
  const outLength = Math.floor(merged.length / ratio);
  const pcm = new Int16Array(outLength);
  for (let i = 0; i < outLength; i++) {
    const sample = Math.max(-1, Math.min(1, merged[Math.floor(i * ratio)]));
    pcm[i] = sample < 0 ? sample * 0x8000 : sample * 0x7fff;
  }
  const buffer = new ArrayBuffer(44 + pcm.length * 2);
  const view = new DataView(buffer);
  const write = (o, s) => { for (let i = 0; i < s.length; i++) view.setUint8(o + i, s.charCodeAt(i)); };
  write(0, 'RIFF'); view.setUint32(4, 36 + pcm.length * 2, true); write(8, 'WAVE');
  write(12, 'fmt '); view.setUint32(16, 16, true); view.setUint16(20, 1, true); view.setUint16(22, 1, true);
  view.setUint32(24, targetRate, true); view.setUint32(28, targetRate * 2, true); view.setUint16(32, 2, true); view.setUint16(34, 16, true);
  write(36, 'data'); view.setUint32(40, pcm.length * 2, true);
  for (let i = 0; i < pcm.length; i++) view.setInt16(44 + i * 2, pcm[i], true);
  return new Blob([buffer], { type: 'audio/wav' });
}

async function start() {
  try {
    if (liveMode) return;
    await ensureMic();
    await audioContext.resume();
    clearSpeechChunks();
    recording = true;
    statusEl.textContent = 'Listening';
    talk.textContent = 'Release to Send';
  } catch (e) {
    add('sys', 'Mic failed: ' + e.message);
  }
}

async function stop() {
  if (!recording) return;
  recording = false;
  talk.textContent = 'Hold to Talk';
  await sendChunks(false);
}

async function sendChunks(autoPlay) {
  if (!chunks.length) return;
  if (!requirePin()) return;
  statusEl.textContent = 'Transcribing';
  const blob = encodeWav(chunks, audioContext.sampleRate);
  const form = new FormData();
  form.append('audio', blob, 'phone.wav');
  try {
    talk.disabled = true;
    const transcribe = await fetch('/api/transcribe', { method: 'POST', headers: pinHeaders(), body: form });
    if (!transcribe.ok) throw await responseError(transcribe, 'Transcribe');
    const first = await transcribe.json();
    const transcript = (first.transcript || '').trim();
    if (!transcript) {
      add('sys', 'I did not catch that.');
      return;
    }

    add('me', transcript);
    statusEl.textContent = 'Thinking';
    let r;
    if (files.files.length > 0 || videoAudio.files.length > 0) {
      const messageForm = new FormData();
      messageForm.append('text', transcript);
      appendRequestFlags(messageForm);
      appendAttachedFiles(messageForm);
      r = await fetch('/api/message', { method: 'POST', headers: pinHeaders(), body: messageForm });
    } else {
      r = await fetch('/api/respond', {
        method: 'POST',
        headers: pinHeaders({ 'Content-Type': 'application/json' }),
        body: JSON.stringify({ text: transcript, keepDocumentsActive: keepDoc.checked })
      });
    }
    if (!r.ok) throw await responseError(r, 'Response');
    const data = await r.json();
    updateActiveDocStatus(data.activeDocumentCount);
    const botEl = addBotResult(data);
    if (data.audioUrl) {
      const audio = document.createElement('audio');
      audio.controls = true;
      audio.src = data.audioUrl;
      guardPlaybackMic(audio);
      botEl.appendChild(audio);
      if (autoPlay) {
        statusEl.textContent = 'Speaking';
        const played = await playLiveAudio(data.audioUrl);
        if (played) {
          // playLiveAudio resolves when playback finishes.
        } else {
          add('sys', 'Tap the audio play button once. Safari still blocked live playback.');
        }
      }
    }
    if (files.files.length > 0) files.value = '';
    if (videoAudio.files.length > 0) videoAudio.value = '';
  } catch (e) {
    add('sys', e.message);
  } finally {
    talk.disabled = false;
    statusEl.textContent = 'Ready';
  }
}

async function sendTypedMessage() {
  if (meetingRecording) {
    add('sys', 'Stop the meeting recording before sending.');
    return;
  }

  const text = textMessage.value.trim();
  if (!text && files.files.length === 0 && videoAudio.files.length === 0 && !meetingBlob) return;
  if (!requirePin()) return;

  const form = new FormData();
  form.append('text', text);
  appendRequestFlags(form);
  appendAttachedFiles(form);

  try {
    sendText.disabled = true;
    talk.disabled = true;
    statusEl.textContent = 'Sending';
    const display = text || (meetingBlob ? 'Recorded meeting audio' : selectedFileNames().join(', '));
    add('me', display);

    const r = await fetch('/api/message', { method: 'POST', headers: pinHeaders(), body: form });
    if (!r.ok) throw await responseError(r, 'Message');
    const data = await r.json();
    updateActiveDocStatus(data.activeDocumentCount);
    const botEl = addBotResult(data);
    if (data.audioUrl) {
      const audio = document.createElement('audio');
      audio.controls = true;
      audio.src = data.audioUrl;
      guardPlaybackMic(audio);
      botEl.appendChild(audio);
    }

    textMessage.value = '';
    files.value = '';
    videoAudio.value = '';
    meetingBlob = null;
    meetingStatus.textContent = '';
  } catch (e) {
    add('sys', e.message);
  } finally {
    sendText.disabled = false;
    talk.disabled = liveMode;
    statusEl.textContent = liveMode ? 'Live: listening' : 'Ready';
  }
}

async function toggleMeetingRecording() {
  if (meetingRecording) {
    meetingRecording = false;
    meetingRecord.classList.remove('on');
    meetingRecord.textContent = 'Record Meeting';

    if (!meetingChunks.length) {
      meetingStatus.textContent = '';
      add('sys', 'No meeting audio was recorded.');
      return;
    }

    meetingBlob = encodeWav(meetingChunks, audioContext.sampleRate);
    const seconds = Math.round((performance.now() - meetingStartedAt) / 1000);
    meetingStatus.textContent = `Ready: ${seconds}s`;
    meetingChunks = [];

    if (!textMessage.value.trim()) {
      textMessage.value = 'Transcribe and summarize this meeting audio. Include key points, decisions, action items, questions, names, dates, and numbers. Keep the transcript available for follow-up questions.';
    }
    keepDoc.checked = true;
    add('sys', 'Meeting audio ready. Tap Send to transcribe it and keep the transcript active.');
    return;
  }

  try {
    if (liveMode) await setLiveMode(false);
    await ensureMic();
    await audioContext.resume();
    meetingBlob = null;
    meetingChunks = [];
    meetingStartedAt = performance.now();
    meetingRecording = true;
    meetingRecord.classList.add('on');
    meetingRecord.textContent = 'Stop Meeting';
    meetingStatus.textContent = 'Recording';
    statusEl.textContent = 'Meeting recording';
  } catch (e) {
    add('sys', 'Meeting recording failed: ' + e.message);
  }
}

function clearMeetingRecording() {
  meetingRecording = false;
  meetingBlob = null;
  meetingChunks = [];
  meetingRecord.classList.remove('on');
  meetingRecord.textContent = 'Record Meeting';
  meetingStatus.textContent = '';
  statusEl.textContent = liveMode ? 'Live: listening' : 'Ready';
}

function stopAllAudio() {
  if (livePlaybackSource) {
    try { livePlaybackSource.stop(); } catch {}
    livePlaybackSource = null;
  }
  if (livePlayer) {
    try { livePlayer.pause(); livePlayer.currentTime = 0; } catch {}
  }
  document.querySelectorAll('audio').forEach(a => {
    try { a.pause(); a.currentTime = 0; } catch {}
  });
  resumeMicAfterPlayback();
  statusEl.textContent = liveMode ? 'Live: listening' : 'Ready';
}

async function playLiveAudio(url) {
  try {
    releaseMicForPlayback();
    if (livePlaybackSource) {
      try { livePlaybackSource.stop(); } catch {}
      livePlaybackSource = null;
    }

    if (!livePlayer) {
      livePlayer = new Audio();
      livePlayer.preload = 'auto';
      livePlayer.playsInline = true;
    }

    return await new Promise(resolve => {
      let settled = false;
      const finish = async ok => {
        if (settled) return;
        settled = true;
        livePlayer.removeEventListener('ended', onEnded);
        livePlayer.removeEventListener('error', onError);
        await resumeMicAfterPlayback();
        resolve(ok);
      };
      const onEnded = () => finish(true);
      const onError = () => finish(false);
      livePlayer.addEventListener('ended', onEnded, { once: true });
      livePlayer.addEventListener('error', onError, { once: true });
      livePlayer.src = url;
      livePlayer.play().then(() => {}).catch(() => finish(false));
    });
  } catch {
    await resumeMicAfterPlayback();
    return false;
  }
}

function handleLiveAudio(input) {
  let sum = 0;
  for (let i = 0; i < input.length; i++) sum += input[i] * input[i];
  const rms = Math.sqrt(sum / input.length);
  const now = performance.now();
  const dt = liveLastTick ? now - liveLastTick : 0;
  liveLastTick = now;

  if (!liveSpeechStarted) {
    statusEl.textContent = 'Live: listening';
    if (rms > liveStartThreshold) {
      clearSpeechChunks();
      recording = true;
      liveSpeechStarted = true;
      liveSilenceMs = 0;
      liveVoiceMs = 0;
      statusEl.textContent = 'Live: heard you';
    }
    return;
  }

  if (rms > liveStopThreshold) {
    liveVoiceMs += dt;
    liveSilenceMs = 0;
  } else {
    liveSilenceMs += dt;
  }

  const clipMs = recordedSamples / audioContext.sampleRate * 1000;
  if (liveVoiceMs < liveMinVoiceMs && liveSilenceMs >= noiseResetSilenceMs) {
    clearSpeechChunks();
    recording = false;
    liveSpeechStarted = false;
    liveSilenceMs = 0;
    liveVoiceMs = 0;
    liveLastTick = 0;
    statusEl.textContent = 'Live: listening';
    return;
  }

  const silenceLimit = longTalkMode
    ? longSilenceToSendMs
    : (liveVoiceMs <= shortUtteranceMaxVoiceMs ? shortSilenceToSendMs : normalSilenceToSendMs);
  const maxClip = longTalkMode ? longMaxClipMs : normalMaxClipMs;
  if (clipMs >= maxClip) {
    add('sys', longTalkMode
      ? 'Long Talk reached its 20-minute safety limit. Sending what was recorded.'
      : 'Live speech reached its 5-minute safety limit. Sending what was recorded.');
    finishLiveUtterance();
  } else if (liveVoiceMs >= liveMinVoiceMs && liveSilenceMs >= silenceLimit) {
    finishLiveUtterance();
  }
}

async function finishLiveUtterance() {
  if (liveSending) return;
  recording = false;
  liveArmed = false;
  liveSending = true;
  liveSpeechStarted = false;
  statusEl.textContent = 'Live: thinking';
  await sendChunks(true);
  clearSpeechChunks();
  liveSending = false;
  if (liveMode) {
    liveLastTick = 0;
    liveArmed = true;
    statusEl.textContent = 'Live: listening';
  }
}

async function setLiveMode(on) {
  if (on) unlockLiveAudio();
  liveMode = on;
  live.classList.toggle('on', liveMode);
  live.textContent = liveMode ? 'Live Mode On' : 'Live Mode Off';
  talk.disabled = liveMode;
  if (liveMode) {
    await ensureMic();
    await audioContext.resume();
    clearSpeechChunks();
    recording = false;
    liveSending = false;
    liveSpeechStarted = false;
    liveLastTick = 0;
    liveArmed = true;
    add('sys', 'Live mode on.');
    statusEl.textContent = 'Live: listening';
  } else {
    liveArmed = false;
    liveSpeechStarted = false;
    recording = false;
    clearSpeechChunks();
    statusEl.textContent = 'Ready';
    add('sys', 'Live mode off.');
  }
}

function setLongTalkMode(on) {
  longTalkMode = on;
  longTalk.classList.toggle('on', longTalkMode);
  longTalk.textContent = longTalkMode ? 'Long Talk On' : 'Long Talk Off';
  add('sys', longTalkMode ? 'Long Talk on. Longer pauses are allowed.' : 'Long Talk off.');
}

talk.addEventListener('touchstart', e => { e.preventDefault(); start(); }, { passive: false });
talk.addEventListener('touchend', e => { e.preventDefault(); stop(); }, { passive: false });
talk.addEventListener('mousedown', start);
talk.addEventListener('mouseup', stop);
live.addEventListener('click', () => {
  if (!liveMode && !requirePin()) return;
  setLiveMode(!liveMode);
});
longTalk.addEventListener('click', () => setLongTalkMode(!longTalkMode));
stopAudio.addEventListener('click', stopAllAudio);
modelHelp.addEventListener('click', showModelHelp);
sendText.addEventListener('click', sendTypedMessage);
meetingRecord.addEventListener('click', toggleMeetingRecording);
clearMeeting.addEventListener('click', clearMeetingRecording);
createImage.addEventListener('click', () => {
  const text = textMessage.value.trim();
  if (!text) {
    add('sys', 'Type an image prompt first.');
    return;
  }
  textMessage.value = 'create an image of ' + text;
  sendTypedMessage();
});
editImage.addEventListener('click', () => {
  const text = textMessage.value.trim();
  if (!text) {
    add('sys', 'Type an edit instruction first.');
    return;
  }
  textMessage.value = 'edit this image ' + text;
  sendTypedMessage();
});
createVideo.addEventListener('click', () => {
  const text = textMessage.value.trim();
  if (!text) {
    add('sys', 'Type a video prompt first.');
    return;
  }
  textMessage.value = 'create a video ' + text;
  sendTypedMessage();
});
textMessage.addEventListener('keydown', e => {
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault();
    sendTypedMessage();
  }
});
clearFiles.addEventListener('click', () => { files.value = ''; });
clearVideoAudio.addEventListener('click', () => { videoAudio.value = ''; });
keepDoc.addEventListener('change', () => {
  if (!keepDoc.checked) updateActiveDocStatus(0);
});
document.getElementById('test').addEventListener('click', async () => { await ensureMic(); add('sys', 'Mic permission is working.'); });
pin.addEventListener('input', () => pin.classList.remove('needed'));
pin.addEventListener('change', () => {
  savePin();
  checkPin(true);
});
loadSavedPin();
if (pin.value.trim()) {
  checkPin(false);
} else {
  pin.classList.add('needed');
  add('sys', pinHelp);
}
if (!navigator.mediaDevices) add('sys', 'This browser requires HTTPS before microphone access works.');
</script>
</body>
</html>
""";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _certificateInfo?.Certificate.Dispose();
    }
}

public sealed record PhoneRemoteAssistantResult(
    string Response,
    string? AudioPath,
    string? ImagePath = null,
    string? VideoPath = null,
    int ActiveDocumentCount = 0,
    string? ActiveModel = null,
    string? ActiveEndpoint = null);

public sealed record PhoneRemoteModelState(string Provider, string Model, string Endpoint);

public sealed record PhoneRemoteTextRequest(string Text, bool KeepDocumentsActive = false);

public sealed record PhoneRemoteDocument(string FileName, DocumentTextResult Document);

public sealed record PhoneRemoteUserInput
{
    public PhoneRemoteUserInput(
        string text,
        List<string>? imagesBase64 = null,
        List<string>? imagePaths = null,
        List<string>? audioPaths = null,
        List<PhoneRemoteDocument>? documents = null,
        bool keepDocumentsActive = false)
    {
        Text = text;
        ImagesBase64 = imagesBase64 ?? new List<string>();
        ImagePaths = imagePaths ?? new List<string>();
        AudioPaths = audioPaths ?? new List<string>();
        Documents = documents ?? new List<PhoneRemoteDocument>();
        KeepDocumentsActive = keepDocumentsActive;
    }

    public string Text { get; init; }
    public List<string> ImagesBase64 { get; init; }
    public List<string> ImagePaths { get; init; }
    public List<string> AudioPaths { get; init; }
    public List<PhoneRemoteDocument> Documents { get; init; }
    public bool KeepDocumentsActive { get; init; }
}

public sealed record PhoneRemoteCertificateInfo(X509Certificate2 Certificate, string PfxPath, string CerPath);

public static class PhoneRemoteCertificateManager
{
    private const string Password = "VoiceChatbotLocalOnly";
    private static readonly string[] VpnAddresses = ["10.8.0.1"];

    public static PhoneRemoteCertificateInfo EnsureCertificate(string localIpAddress)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceChatbot", "phone-remote-cert");
        Directory.CreateDirectory(dir);

        var pfxPath = Path.Combine(dir, "voicechatbot-phone-remote.pfx");
        var cerPath = Path.Combine(dir, "voicechatbot-phone-remote.cer");
        var ipPath = Path.Combine(dir, "voicechatbot-phone-remote-ip.txt");
        var desiredNames = GetDesiredCertificateNames(localIpAddress);

        if (File.Exists(pfxPath) && File.Exists(cerPath) && File.Exists(ipPath))
        {
            var existing = new X509Certificate2(pfxPath, Password, X509KeyStorageFlags.Exportable);
            var certificateNames = File.ReadAllLines(ipPath)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (existing.NotAfter > DateTimeOffset.Now.AddDays(30) &&
                desiredNames.SetEquals(certificateNames))
            {
                return new PhoneRemoteCertificateInfo(existing, pfxPath, cerPath);
            }

            existing.Dispose();
        }

        using var rsa = RSA.Create(2048);
        var subject = new X500DistinguishedName("CN=VoiceChatbot Local Remote");
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        [
            new Oid("1.3.6.1.5.5.7.3.1")
        ], false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var name in desiredNames)
        {
            if (IPAddress.TryParse(name, out var ip))
                san.AddIpAddress(ip);
        }
        request.CertificateExtensions.Add(san.Build());

        var certificate = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(3));
        var exportable = new X509Certificate2(certificate.Export(X509ContentType.Pfx, Password), Password, X509KeyStorageFlags.Exportable);

        File.WriteAllBytes(pfxPath, exportable.Export(X509ContentType.Pfx, Password));
        File.WriteAllBytes(cerPath, exportable.Export(X509ContentType.Cert));
        File.WriteAllLines(ipPath, desiredNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        return new PhoneRemoteCertificateInfo(exportable, pfxPath, cerPath);
    }

    private static HashSet<string> GetDesiredCertificateNames(string localIpAddress)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(localIpAddress))
            names.Add(localIpAddress);

        foreach (var address in VpnAddresses)
            names.Add(address);

        return names;
    }
}
