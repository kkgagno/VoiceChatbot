using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

/// <summary>
/// How a model download ended. <see cref="ProjectorOnly"/>: the model was already on this PC and only its
/// picture support was downloaded. <see cref="ProjectorPath"/> is the picture support file on this PC
/// afterwards ("" when there is none).
/// </summary>
public sealed record ModelDownloadResult(LocalModelInfo Model, bool Success, bool Cancelled, string Error, string FilePath,
    string ProjectorPath = "", bool ProjectorOnly = false);

/// <summary>
/// The model chooser's downloads: one model at a time, looked up on Hugging Face and saved to the models
/// folder, with progress the chooser window and the sidebar both show. A download fetches what is missing:
/// the model file and its picture support file (vision projector, from the same repository), or only the
/// picture support for a model already on this PC. It keeps running when the chooser is closed; a cancelled
/// download continues where it stopped next time.
/// </summary>
public sealed class ModelDownloads : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _appFolder;
    private readonly ConcurrentDictionary<string, HfModelFiles> _lookups = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task<ModelDownloadResult>? _task;

    /// <param name="http">The client to download with; null creates one.</param>
    /// <param name="appFolder">The app folder, whose models folder holds the included model; null for this app's folder.</param>
    public ModelDownloads(HttpClient? http = null, string? appFolder = null)
    {
        _ownsHttp = http == null;
        _http = http ?? CreateHttpClient();
        _appFolder = appFolder ?? AppContext.BaseDirectory;
    }

    /// <summary>The model being downloaded, or null.</summary>
    public LocalModelInfo? Current { get; private set; }

    /// <summary>Progress over everything this download fetches (the model and its picture support together).</summary>
    public DownloadProgress Progress { get; private set; }

    /// <summary>One line for the UI: "Downloading Gemma 4 12B: 34% (2.5 of 7.4 GB, 45 MB/s, about 2 min left)".</summary>
    public string Status { get; private set; } = "";

    /// <summary>Progress or state changed. Raised on a background thread.</summary>
    public event Action? Changed;

    /// <summary>A download ended (finished, failed or cancelled). Raised once per download, on a background thread.</summary>
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

    /// <summary>
    /// The model's file and its picture support file on Hugging Face (cached), for their exact download sizes.
    /// When the model is already on this PC (in <paramref name="folder"/> or the app folder), the repository it
    /// came from is preferred, so its picture support matches it. Null when not found.
    /// </summary>
    public async Task<HfModelFiles?> LookupAsync(LocalModelInfo model, string folder, CancellationToken ct)
    {
        var installedSize = FileLength(LocalModelCatalog.FindInstalledFile(model, folder, _appFolder));
        var key = $"{model.Id}|{installedSize}";
        if (_lookups.TryGetValue(key, out var known))
            return known;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var files = await HuggingFaceFiles.ResolveAsync(_http, model, LocalModelCatalog.Quant, timeout.Token, installedSize).ConfigureAwait(false);
        if (files != null)
            _lookups[key] = files;
        return files;
    }

    /// <summary>
    /// Downloads what <paramref name="model"/> is missing into <paramref name="folder"/>: the model file and
    /// its picture support, or only the picture support when the model is already on this PC. While another
    /// download runs, that one's task is returned when it is the same model; another model is refused (the
    /// task fails).
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
        var installedModel = LocalModelCatalog.FindInstalledFile(model, folder, _appFolder);
        var projectorOnly = installedModel != null;
        var modelPath = installedModel ?? LocalModelCatalog.DownloadPath(model, folder);
        var picturesName = $"picture support for {model.Name}";
        // The file being fetched when the download ends, for the status line.
        var currentName = projectorOnly ? picturesName : model.Name;
        ModelDownloadResult result;
        try
        {
            SetStatus($"Looking up {model.Name} on Hugging Face...");
            var files = await LookupAsync(model, folder, ct).ConfigureAwait(false)
                        ?? throw new FileNotFoundException($"No {LocalModelCatalog.Quant} file of {model.Name} was found on Hugging Face.");

            var parts = new List<(HfFile File, string Target, string Name)>();
            if (installedModel == null)
                parts.Add((files.Model, LocalModelCatalog.DownloadPath(model, folder), model.Name));
            if (files.Projector != null && LocalModelCatalog.FindInstalledProjector(model, folder, _appFolder) == null)
                parts.Add((files.Projector, LocalModelCatalog.ProjectorDownloadPath(model, folder), picturesName));
            if (files.Projector == null)
                AppLog.Info($"Model download: {files.Model.Repository} has no picture support file for {model.Name}; it reads text only.");

            // One bar for both files: each file's progress is counted after the ones before it.
            var sizesKnown = parts.All(p => p.File.Size > 0);
            var total = parts.Sum(p => Math.Max(0, p.File.Size));
            long before = 0;
            foreach (var part in parts)
            {
                currentName = part.Name;
                AppLog.Info($"Model download: {part.Name} from {part.File.Url} ({ModelDownloader.FormatBytes(part.File.Size)}) to {part.Target}.");
                var offset = before;
                var progress = new ActionProgress(p =>
                {
                    var overall = sizesKnown ? p with { Done = offset + p.Done, Total = total } : p;
                    Progress = overall;
                    SetStatus($"{(p.Phase == DownloadPhase.Checking ? "Checking" : "Downloading")} {part.Name}: {Describe(overall)}");
                });
                await ModelDownloader.DownloadAsync(_http, part.File.Url, part.Target, part.File.Size, part.File.Sha256, progress, ct).ConfigureAwait(false);
                before += Math.Max(0, part.File.Size);
            }

            var projector = LocalModelCatalog.FindInstalledProjector(model, folder, _appFolder) ?? "";
            result = new ModelDownloadResult(model, true, false, "", modelPath, projector, projectorOnly);
            SetStatus(parts.Count == 0 ? $"{model.Name} is already on this PC."
                : projectorOnly ? $"Picture support for {model.Name} downloaded."
                : $"{model.Name} downloaded.");
            AppLog.Info($"Model download: {model.Name} finished{(projector.Length > 0 ? ", with picture support" : ", text only")}.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new ModelDownloadResult(model, false, true, "", modelPath, ProjectorOnly: projectorOnly);
            SetStatus($"Download of {currentName} paused. Start it again to continue.");
            AppLog.Info($"Model download: {currentName} cancelled.");
        }
        catch (Exception ex)
        {
            result = new ModelDownloadResult(model, false, false, ex.Message, modelPath, ProjectorOnly: projectorOnly);
            SetStatus($"Download of {currentName} failed: {ex.Message}");
            AppLog.Warn($"Model download: {currentName} failed.", ex);
        }

        lock (_gate)
            Current = null;
        try { Finished?.Invoke(result); } catch (Exception ex) { AppLog.Warn("Model download: a listener failed.", ex); }
        RaiseChanged();
        return result;
    }

    private static long FileLength(string? path)
    {
        if (path == null)
            return 0;
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
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
