using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

/// <summary>Knowledge folder status for the sidebar. Details lists unreadable files (tooltip).</summary>
public sealed record KnowledgeStatus(bool IsIndexing, string Message, string Details = "");

/// <summary>
/// Keeps the knowledge folder index (Core/KnowledgeIndex) up to date and searchable. A reindex runs on
/// the thread pool, reads only new or changed files with DocumentTextService, skips files over 25 MB
/// and saves the index to %APPDATA%\VoiceChatbot\knowledge-index.json. Search can be called from any
/// thread, also while a reindex runs; it uses the last published index, which is never changed again.
/// </summary>
public sealed class KnowledgeService
{
    public const long MaxFileBytes = 25L * 1024 * 1024;
    // A huge CSV or log is indexed on its first ~3 million characters only.
    private const int MaxIndexedCharsPerFile = 3_000_000;
    // Stop an accidental pick of a whole drive from scanning, reading and holding in memory forever.
    private const int MaxFiles = 10_000;
    private const int MaxTotalChunks = 50_000;
    private const int MaxUnreadableDetails = 10;
    // While a long first index runs, what is read so far becomes searchable this often.
    private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>The formats the knowledge folder indexes (DocumentFileTypes.KnowledgeExtensions).</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions = DocumentFileTypes.KnowledgeExtensions;

    public static string DefaultIndexPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceChatbot",
        "knowledge-index.json");

    private readonly DocumentTextService _extractor;
    private readonly string _indexPath;
    // One reindex at a time; a cancelled run finishes (and saves its progress) before the next starts.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile KnowledgeIndex? _index;
    private volatile KnowledgeStatus _status = new(false, "");
    private bool _savedIndexLoaded; // only used under _gate
    private int _tooLargeCount;     // from the last scan, only used under _gate
    private bool _scanTruncated;    // from the last scan, only used under _gate
    private bool _indexFull;        // the last reindex hit MaxTotalChunks, only used under _gate

    public KnowledgeService(DocumentTextService extractor, string indexPath)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _indexPath = string.IsNullOrWhiteSpace(indexPath) ? DefaultIndexPath : indexPath;
    }

    /// <summary>Raised on a background thread whenever <see cref="Status"/> changes.</summary>
    public event Action<KnowledgeStatus>? StatusChanged;

    public KnowledgeStatus Status => _status;

    public static bool IsSupportedFile(string path)
    {
        var name = Path.GetFileName(path);
        // "~$Report.docx" is Word's lock file, not a document.
        return !string.IsNullOrEmpty(name) &&
               !name.StartsWith("~$", StringComparison.Ordinal) &&
               SupportedExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Best excerpts for a chat message from the index of <paramref name="folder"/>; empty when that
    /// folder is not indexed (yet) or nothing in it is relevant.
    /// </summary>
    public IReadOnlyList<KnowledgeHit> Search(string? query, string? folder, int maxChunks)
    {
        var index = _index;
        if (index == null || !KnowledgeIndex.SameFolder(index.Folder, folder))
            return Array.Empty<KnowledgeHit>();

        return index.Search(query, KnowledgeIndex.ClampMaxChunks(maxChunks));
    }

    /// <summary>
    /// Brings the index up to date with <paramref name="folder"/> on the thread pool. A different folder
    /// starts a new index. With <paramref name="retryFailed"/>, files that gave no text are read again.
    /// Cancelling keeps (and saves) the files read so far. Errors are reported through the status.
    /// </summary>
    public Task ReindexAsync(string? folder, bool retryFailed, CancellationToken ct) =>
        Task.Run(() => ReindexCoreAsync(folder, retryFailed, ct), ct);

    private async Task ReindexCoreAsync(string? folder, bool retryFailed, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        KnowledgeIndex? working = null;
        var changed = false;
        try
        {
            var root = KnowledgeIndex.NormalizeFolder(folder);
            if (root.Length == 0)
            {
                Report(false, "Choose a folder with your documents.");
                return;
            }

            if (!_savedIndexLoaded)
            {
                Report(true, "Loading the saved index...");
                await LoadSavedIndexAsync(ct).ConfigureAwait(false);
            }
            if (!Directory.Exists(root))
            {
                Report(false, $"Folder not found: {root}");
                return;
            }

            Report(true, "Scanning the folder...");
            var files = ScanFolder(root, ct);

            var current = _index;
            var sameFolder = current != null && KnowledgeIndex.SameFolder(current.Folder, root);
            working = sameFolder ? current!.Clone() : new KnowledgeIndex(root);
            changed = !sameFolder;

            var plan = working.Plan(files, retryFailed);
            foreach (var path in plan.Removed)
                changed |= working.Remove(path);

            var sincePublish = Stopwatch.StartNew();
            var sinceProgress = Stopwatch.StartNew();
            var totalChunks = working.ChunkCount;
            _indexFull = false;
            for (var i = 0; i < plan.ToRead.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (totalChunks >= MaxTotalChunks)
                {
                    _indexFull = true;
                    break;
                }

                var file = plan.ToRead[i];
                if (i == 0 || sinceProgress.Elapsed >= ProgressInterval)
                {
                    Report(true, $"Indexing {i + 1:N0} of {plan.ToRead.Count:N0}: {Path.GetFileName(file.Path)}");
                    sinceProgress.Restart();
                }

                var record = await ReadFileAsync(file, ct).ConfigureAwait(false);
                totalChunks += record.Chunks.Count - (working.TryGetFile(file.Path, out var previous) ? previous.Chunks.Count : 0);
                working.Upsert(record);
                changed = true;

                if (sincePublish.Elapsed >= PublishInterval)
                {
                    _index = working.Clone();
                    sincePublish.Restart();
                }
            }

            working.LastIndexedUtc = DateTime.UtcNow;
            Publish(working, save: changed);
            Report(false, Describe(_index!), DescribeUnreadable(_index!));
        }
        catch (OperationCanceledException)
        {
            // Keep what was read so far; the next reindex carries on from there.
            if (working != null && changed)
            {
                var partial = working.Clone();
                _index = partial;
                SaveIndex(partial);
            }

            var index = _index;
            Report(false, index == null ? "Indexing stopped." : "Indexing stopped. " + Describe(index));
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Knowledge indexing failed: {ex}");
            Report(false, $"Indexing failed: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Publish(KnowledgeIndex working, bool save)
    {
        var published = working.Clone();
        // Build the search structures here, off the UI thread, rather than on the first question.
        published.Prepare();
        _index = published;
        if (save)
            SaveIndex(published);
    }

    private async Task LoadSavedIndexAsync(CancellationToken ct)
    {
        try
        {
            if (File.Exists(_indexPath))
            {
                var loaded = KnowledgeIndex.FromJson(await File.ReadAllTextAsync(_indexPath, ct).ConfigureAwait(false));
                // Searchable right away while the scan for changes runs.
                if (loaded.Folder.Length > 0 && _index == null)
                    _index = loaded;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable index file: start over, the reindex rebuilds it.
            Debug.WriteLine($"Could not read the knowledge index: {ex.Message}");
        }

        _savedIndexLoaded = true;
    }

    private void SaveIndex(KnowledgeIndex index)
    {
        var temp = _indexPath + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(_indexPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Write a temp file first so a crash mid-write never leaves a half-written index.
            File.WriteAllText(temp, index.ToJson());
            File.Move(temp, _indexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Could not save the knowledge index: {ex.Message}");
            try { File.Delete(temp); } catch (Exception) { }
        }
    }

    private List<KnowledgeFileStamp> ScanFolder(string root, CancellationToken ct)
    {
        var files = new List<KnowledgeFileStamp>();
        _tooLargeCount = 0;
        _scanTruncated = false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        foreach (var path in Directory.EnumerateFiles(root, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsSupportedFile(path) || string.Equals(path, _indexPath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                var info = new FileInfo(path);
                if (info.Length > MaxFileBytes)
                {
                    _tooLargeCount++;
                    continue;
                }

                files.Add(new KnowledgeFileStamp(info.FullName, info.Length, info.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Vanished or locked between listing and reading its size; the next reindex sees it.
            }

            if (files.Count >= MaxFiles)
            {
                _scanTruncated = true;
                break;
            }
        }

        return files;
    }

    private async Task<KnowledgeFileRecord> ReadFileAsync(KnowledgeFileStamp file, CancellationToken ct)
    {
        var record = new KnowledgeFileRecord { Path = file.Path, Size = file.Size, LastWriteUtc = file.LastWriteUtc };
        DocumentTextResult result;
        try
        {
            result = await _extractor.ExtractAsync(file.Path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            record.Error = ex.Message;
            return record;
        }

        // ExtractAsync reports a cancellation as an error result; that is not the file's fault.
        ct.ThrowIfCancellationRequested();

        var text = string.IsNullOrWhiteSpace(result.FullText) ? result.Text : result.FullText;
        if (text.Length > MaxIndexedCharsPerFile)
            text = text[..MaxIndexedCharsPerFile];

        record.Chunks = KnowledgeIndex.ChunkText(text);
        if (record.Chunks.Count == 0)
            record.Error = string.IsNullOrWhiteSpace(result.Error) ? "No readable text." : result.Error;
        return record;
    }

    private string Describe(KnowledgeIndex index)
    {
        var files = index.IndexedFileCount;
        var chunks = index.ChunkCount;
        var text = $"{files:N0} {(files == 1 ? "file" : "files")}, {chunks:N0} {(chunks == 1 ? "chunk" : "chunks")}";
        if (index.LastIndexedUtc is DateTime indexed)
            text += $" · last indexed {FormatWhen(indexed)}";

        var unreadable = index.UnreadableFileCount;
        if (unreadable > 0)
            text += $" · {unreadable:N0} could not be read";
        if (_tooLargeCount > 0)
            text += $" · {_tooLargeCount:N0} over 25 MB skipped";
        if (_scanTruncated)
            text += $" · stopped at {MaxFiles:N0} files";
        if (_indexFull)
            text += $" · index full ({MaxTotalChunks:N0} chunks), some files left out";
        return text;
    }

    private static string DescribeUnreadable(KnowledgeIndex index)
    {
        var failed = index.Files
            .Where(f => f.Chunks.Count == 0)
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (failed.Count == 0)
            return "";

        var lines = failed
            .Take(MaxUnreadableDetails)
            .Select(f => $"{KnowledgeIndex.DisplayName(f.Path, index.Folder)}: {f.Error ?? "No readable text."}")
            .ToList();
        if (failed.Count > MaxUnreadableDetails)
            lines.Add($"...and {failed.Count - MaxUnreadableDetails:N0} more");
        return "Could not read:\n" + string.Join("\n", lines) + "\n\nReindex tries these again.";
    }

    private static string FormatWhen(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? $"today {local:t}" : local.ToString("g");
    }

    private void Report(bool indexing, string message, string details = "")
    {
        var status = new KnowledgeStatus(indexing, message, details);
        _status = status;
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Knowledge status handler failed: {ex.Message}");
        }
    }
}
