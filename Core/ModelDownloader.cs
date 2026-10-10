using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

public enum DownloadPhase
{
    Downloading,
    Checking
}

/// <summary>Progress of <see cref="ModelDownloader.DownloadAsync"/>: bytes done of the total, and the speed while downloading.</summary>
public readonly record struct DownloadProgress(DownloadPhase Phase, long Done, long Total, double BytesPerSecond)
{
    public double Fraction => Total > 0 ? Math.Clamp(Done / (double)Total, 0, 1) : 0;
}

/// <summary>
/// Downloads a large file (a model) to "&lt;target&gt;.part" and renames it when it is complete and its
/// SHA-256 matches. A cancelled or broken download keeps the part file and continues from there the next
/// time (HTTP Range); short network drops are retried on the spot.
/// </summary>
public static class ModelDownloader
{
    private const int BufferSize = 1 << 20;
    private const int Attempts = 4;
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>No data for this long (Wi-Fi dropped, PC slept): give up on the connection and resume on a new one.</summary>
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Spare disk space to leave on top of the file.</summary>
    public const long DiskHeadroomBytes = 512L * 1024 * 1024;

    public static string PartPath(string targetPath) => targetPath + ".part";

    public static async Task DownloadAsync(HttpClient http, string url, string targetPath, long expectedSize, string? sha256,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(targetPath));
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        var part = PartPath(targetPath);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadPartAsync(http, url, part, expectedSize, progress, ct).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (attempt < Attempts && !ct.IsCancellationRequested && IsTransient(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }

        var length = new FileInfo(part).Length;
        if (expectedSize > 0 && length != expectedSize)
            throw new IOException($"The download stopped early ({FormatBytes(length)} of {FormatBytes(expectedSize)}). Try again to continue it.");

        if (!string.IsNullOrWhiteSpace(sha256))
        {
            var actual = await HashFileAsync(part, progress, ct).ConfigureAwait(false);
            if (!string.Equals(actual, sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(part);
                throw new InvalidDataException("The downloaded file is damaged (its checksum does not match). It was deleted; try again.");
            }
        }

        File.Move(part, targetPath, overwrite: true);
    }

    private static async Task DownloadPartAsync(HttpClient http, string url, string part, long expectedSize,
        IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var existing = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (expectedSize > 0 && existing > expectedSize)
        {
            TryDelete(part);
            existing = 0;
        }
        if (expectedSize > 0 && existing == expectedSize)
            return;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);

        // Restarted before every wait: a connection that stops sending is dropped (and resumed by the caller).
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        HttpResponseMessage sent;
        try
        {
            sent = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("The download server did not answer.");
        }
        using var response = sent;
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            // The server has nothing after what we already have: complete when the size is unknown,
            // otherwise the part file is not what the server holds now, so start over.
            if (expectedSize <= 0)
                return;
            TryDelete(part);
            throw new IOException("The partly downloaded file did not match; starting over.");
        }
        response.EnsureSuccessStatusCode();

        var resuming = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resuming)
            existing = 0;

        var total = expectedSize > 0 ? expectedSize : existing + (response.Content.Headers.ContentLength ?? 0);
        EnsureDiskSpace(part, total - existing);

        await using var source = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
        await using var target = new FileStream(part, resuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read,
            BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            var done = existing;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            long receivedThisRun = 0;
            progress?.Report(new DownloadProgress(DownloadPhase.Downloading, done, total, 0));
            while (true)
            {
                int read;
                stall.CancelAfter(StallTimeout);
                try
                {
                    read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), stall.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException("The download stalled.");
                }
                if (read == 0)
                    break;
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                receivedThisRun += read;
                if (clock.Elapsed - lastReport >= ReportEvery)
                {
                    lastReport = clock.Elapsed;
                    progress?.Report(new DownloadProgress(DownloadPhase.Downloading, done, total,
                        receivedThisRun / Math.Max(0.001, clock.Elapsed.TotalSeconds)));
                }
            }

            progress?.Report(new DownloadProgress(DownloadPhase.Downloading, done, total, 0));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<string> HashFileAsync(string path, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var total = stream.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize * 4);
        try
        {
            long done = 0;
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;
            progress?.Report(new DownloadProgress(DownloadPhase.Checking, 0, total, 0));
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, BufferSize * 4), ct).ConfigureAwait(false);
                if (read == 0)
                    break;
                hash.AppendData(buffer, 0, read);
                done += read;
                if (clock.Elapsed - lastReport >= ReportEvery)
                {
                    lastReport = clock.Elapsed;
                    progress?.Report(new DownloadProgress(DownloadPhase.Checking, done, total, 0));
                }
            }
            progress?.Report(new DownloadProgress(DownloadPhase.Checking, total, total, 0));
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void EnsureDiskSpace(string path, long needed)
    {
        if (needed <= 0)
            return;

        long free;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return;
            free = new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return; // Network paths and the like: let the write fail if it must.
        }

        if (free < needed + DiskHeadroomBytes)
            throw new IOException($"Not enough disk space: the download needs {FormatBytes(needed + DiskHeadroomBytes)} free and the drive has {FormatBytes(free)}.");
    }

    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError } ||
        (ex is IOException && ex is not FileNotFoundException && !ex.Message.StartsWith("Not enough disk space", StringComparison.Ordinal)) ||
        ex is TaskCanceledException; // HttpClient timeouts; a real cancel is excluded by the caller's filter.

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>"5.3 GB", "740 MB".</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes >= LocalModelCatalog.GiB)
            return $"{bytes / (double)LocalModelCatalog.GiB:0.0} GB";
        return $"{Math.Max(0, bytes) / (1024.0 * 1024):0} MB";
    }
}
