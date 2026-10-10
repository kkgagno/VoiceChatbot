using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

/// <summary>How a model download ended.</summary>
public sealed record ModelDownloadResult(LocalModelInfo Model, bool Success, bool Cancelled, string Error, string FilePath);

/// <summary>
/// The model chooser's downloads: one model at a time, looked up on Hugging Face and saved to the models
/// folder, with progress the chooser window and the sidebar both show. It keeps running when the chooser
/// is closed; a cancelled download continues where it stopped next time.
/// </summary>
public sealed class ModelDownloads : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ConcurrentDictionary<string, HfFile> _lookups = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task<ModelDownloadResult>? _task;

    public ModelDownloads(HttpClient? http = null)
    {
        _ownsHttp = http == null;
        _http = http ?? CreateHttpClient();
    }

    /// <summary>The model being downloaded, or null.</summary>
    public LocalModelInfo? Current { get; private set; }

    public DownloadProgress Progress { get; private set; }

    /// <summary>One line for the UI: "Downloading Gemma 4 12B: 34% (2.5 of 7.4 GB, 45 MB/s, about 2 min left)".</summary>
    public string Status { get; private set; } = "";

    /// <summary>Progress or state changed. Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>A download ended (finished, failed or cancelled). Raised on a background thread.</summary>
    public event Action<ModelDownloadResult>? Finished;

    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10
        })
        {
            // A 20 GB model takes a while; stalls are caught by the read loop's own retries.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppPaths.DataFolderName}/1.0");
        return http;
    }

    /// <summary>The model's file on Hugging Face (cached), for its exact download size. Null when not found.</summary>
    public async Task<HfFile?> LookupAsync(LocalModelInfo model, CancellationToken ct)
    {
        if (_lookups.TryGetValue(model.Id, out var known))
            return known;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var file = await HuggingFaceFiles.ResolveAsync(_http, model, LocalModelCatalog.Quant, timeout.Token).ConfigureAwait(false);
        if (file != null)
            _lookups[model.Id] = file;
        return file;
    }

    /// <summary>
    /// Downloads <paramref name="model"/> into <paramref name="folder"/>. While another download runs, that
    /// one's task is returned when it is the same model; another model is refused (the task fails).
    /// </summary>
    public Task<ModelDownloadResult> StartAsync(LocalModelInfo model, string folder)
    {
        lock (_gate)
        {
            if (_task is { IsCompleted: false } running)
            {
                if (Current?.Id == model.Id)
                    return running;
                return Task.FromResult(new ModelDownloadResult(model, false, false,
                    $"{Current?.Name} is still downloading. Wait for it or cancel it first.", ""));
            }

            Current = model;
            Progress = default;
            var cts = new CancellationTokenSource();
            _cts = cts;
            _task = Task.Run(() => RunAsync(model, folder, cts.Token));
            return _task;
        }
    }

    public void Cancel()
    {
        lock (_gate)
            _cts?.Cancel();
    }

    private async Task<ModelDownloadResult> RunAsync(LocalModelInfo model, string folder, CancellationToken ct)
    {
        var target = LocalModelCatalog.DownloadPath(model, folder);
        ModelDownloadResult result;
        try
        {
            SetStatus($"Looking up {model.Name} on Hugging Face...");
            var file = await LookupAsync(model, ct).ConfigureAwait(false)
                       ?? throw new FileNotFoundException($"No {LocalModelCatalog.Quant} file of {model.Name} was found on Hugging Face.");

            AppLog.Info($"Model download: {model.Name} from {file.Url} ({ModelDownloader.FormatBytes(file.Size)}) to {target}.");
            var progress = new ActionProgress(p =>
            {
                Progress = p;
                SetStatus($"{(p.Phase == DownloadPhase.Checking ? "Checking" : "Downloading")} {model.Name}: {Describe(p)}");
            });
            await ModelDownloader.DownloadAsync(_http, file.Url, target, file.Size, file.Sha256, progress, ct).ConfigureAwait(false);
            result = new ModelDownloadResult(model, true, false, "", target);
            SetStatus($"{model.Name} downloaded.");
            AppLog.Info($"Model download: {model.Name} finished.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new ModelDownloadResult(model, false, true, "", target);
            SetStatus($"{model.Name} download paused. Start it again to continue.");
            AppLog.Info($"Model download: {model.Name} cancelled.");
        }
        catch (Exception ex)
        {
            result = new ModelDownloadResult(model, false, false, ex.Message, target);
            SetStatus($"{model.Name} download failed: {ex.Message}");
            AppLog.Warn($"Model download: {model.Name} failed.", ex);
        }

        lock (_gate)
            Current = null;
        try { Finished?.Invoke(result); } catch (Exception ex) { AppLog.Warn("Model download: a listener failed.", ex); }
        RaiseChanged();
        return result;
    }

    /// <summary>"34% (2.5 of 7.4 GB, 45 MB/s, about 2 min left)" or, while checking, "60%".</summary>
    public static string Describe(DownloadProgress p)
    {
        var percent = $"{p.Fraction * 100:0}%";
        if (p.Phase == DownloadPhase.Checking || p.Total <= 0)
            return percent;

        var text = $"{percent} ({ModelDownloader.FormatBytes(p.Done)} of {ModelDownloader.FormatBytes(p.Total)}";
        if (p.BytesPerSecond > 0)
        {
            text += $", {p.BytesPerSecond / (1024 * 1024):0} MB/s";
            var seconds = (p.Total - p.Done) / p.BytesPerSecond;
            text += seconds < 90 ? ", under 2 min left" : $", about {Math.Ceiling(seconds / 60):0} min left";
        }
        return text + ")";
    }

    private void SetStatus(string status)
    {
        Status = status;
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); } catch (Exception ex) { AppLog.Warn("Model download: a progress listener failed.", ex); }
    }

    public void Dispose()
    {
        Cancel();
        if (_ownsHttp)
            _http.Dispose();
    }

    // Calls the handler on the reporting thread, in order (Progress<T> would hand each report to the thread pool).
    private sealed class ActionProgress : IProgress<DownloadProgress>
    {
        private readonly Action<DownloadProgress> _handler;

        public ActionProgress(Action<DownloadProgress> handler) => _handler = handler;

        public void Report(DownloadProgress value) => _handler(value);
    }
}
