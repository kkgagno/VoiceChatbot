using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

/// <summary>Knowledge folder status for the sidebar. Details explains unreadable and skipped files (tooltip).</summary>
public sealed record KnowledgeStatus(bool IsIndexing, string Message, string Details = "");

/// <summary>
/// Keeps the knowledge folder index (Core/KnowledgeIndex) up to date and searchable. A reindex runs on
/// the thread pool, reads only new or changed files with DocumentTextService, skips files over 25 MB
/// and saves the index to %APPDATA%\VoiceChatbotMini\knowledge-index.json. Every file the scan sees is
/// accounted for (indexed, unreadable, no text, unsupported type, too large) for the status line and
/// the Files list. BuildContext can be called from any thread, also while a reindex runs; it uses the
/// last published index, which is never changed again.
/// </summary>
public sealed class KnowledgeService
{
    public const long MaxFileBytes = KnowledgeReport.MaxFileBytes;
    // A huge CSV or log is indexed on its first ~3 million characters only.
    private const int MaxIndexedCharsPerFile = 3_000_000;
    // Stop an accidental pick of a whole drive from scanning, reading and holding in memory forever.
    private const int MaxFiles = KnowledgeReport.MaxFiles;
    private const int MaxTotalChunks = KnowledgeReport.MaxTotalChunks;
    private const int MaxUnreadableDetails = 10;
    // While a long first index runs, what is read so far becomes searchable this often.
    private static readonly TimeSpan PublishInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>The formats the knowledge folder indexes (DocumentFileTypes.KnowledgeExtensions).</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions = DocumentFileTypes.KnowledgeExtensions;

    public static string DefaultIndexPath => AppPaths.DataPath("knowledge-index.json");

    // The Files list keeps at most this many unsupported files; the counts cover all of them.
    private const int MaxListedSkippedFiles = 5_000;

    private readonly DocumentTextService _extractor;
    private readonly string _indexPath;
    // One reindex at a time; a cancelled run finishes (and saves its progress) before the next starts.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile KnowledgeIndex? _index;
    private volatile KnowledgeStatus _status = new(false, "");
    // The last scan's files that are not in the index (unsupported, too large, not read yet), for the Files list.
    private volatile ScanReport? _lastScan;
    private bool _savedIndexLoaded; // only used under _gate

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

    // Office lock files and Explorer/Finder bookkeeping are not documents, so they are not even "skipped".
    private static bool IsHousekeepingFile(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ||
               name.StartsWith("~$", StringComparison.Ordinal) ||
               name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when <paramref name="folder"/> is the indexed folder and has files (with text or not).</summary>
    public bool HasFiles(string? folder)
    {
        var index = _index;
        return index != null && KnowledgeIndex.SameFolder(index.Folder, folder) && index.FileCount > 0;
    }

    /// <summary>
    /// What the model gets from <paramref name="folder"/> for a message (see <see cref="KnowledgeContext.Build"/>);
    /// <see cref="KnowledgeContextResult.None"/> when that folder is not indexed (yet).
    /// </summary>
    public KnowledgeContextResult BuildContext(string? folder, string? query, string? followUp, int minExcerpts, int budgetTokens)
    {
        var index = _index;
        if (index == null || !KnowledgeIndex.SameFolder(index.Folder, folder))
            return KnowledgeContextResult.None;

        return KnowledgeContext.Build(index, query, followUp, KnowledgeIndex.ClampMaxChunks(minExcerpts), budgetTokens);
    }

    /// <summary>Every file the last scan of <paramref name="folder"/> saw, with what happened to it (the Files list).</summary>
    public KnowledgeFileList GetFileList(string? folder)
    {
        var root = KnowledgeIndex.NormalizeFolder(folder);
        var index = _index;
        var scan = _lastScan;
        if (index != null && !KnowledgeIndex.SameFolder(index.Folder, root))
            index = null;
        if (scan != null && !KnowledgeIndex.SameFolder(scan.Folder, root))
            scan = null;

        var others = scan == null
            ? Enumerable.Empty<KnowledgeFileEntry>()
            : scan.Skipped.Concat(scan.TooLarge).Concat(scan.Pending);
        return new KnowledgeFileList(
            root,
            KnowledgeReport.BuildFileList(index, others),
            UnlistedSkipped: scan == null ? 0 : Math.Max(0, scan.SkippedTotal - scan.Skipped.Count),
            Scanned: scan != null);
    }

    /// <summary>
    /// The text indexed for <paramref name="path"/> in <paramref name="folder"/>, as the assistant reads it:
    /// its chunks in order with the overlap between them left out. Null when the file has no indexed text.
    /// </summary>
    public string? GetIndexedText(string? folder, string? path)
    {
        var index = _index;
        if (index == null || string.IsNullOrEmpty(path) || !KnowledgeIndex.SameFolder(index.Folder, folder) ||
            !index.TryGetFile(path, out var record) || record.Chunks.Count == 0)
            return null;

        return KnowledgeContext.JoinChunks(record.Chunks);
    }

    /// <summary>
    /// Brings the index up to date with <paramref name="folder"/> on the thread pool. A different folder
    /// starts a new index. With <paramref name="retryFailed"/>, files that gave no text are read again.
    /// Cancelling keeps (and saves) the files read so far and gives a Stopped result. Errors are
    /// reported through the status and the result.
    /// </summary>
    public Task<KnowledgeReindexResult> ReindexAsync(string? folder, bool retryFailed, CancellationToken ct) =>
        Task.Run(() => ReindexCoreAsync(folder, retryFailed, ct), ct);

    private async Task<KnowledgeReindexResult> ReindexCoreAsync(string? folder, bool retryFailed, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var root = KnowledgeIndex.NormalizeFolder(folder);
        var run = new RunCounts();
        KnowledgeIndex? working = null;
        ScanReport? scan = null;
        var changed = false;
        try
        {
            if (root.Length == 0)
            {
                Report(false, "Choose a folder with your documents.");
                return new KnowledgeReindexResult { Outcome = KnowledgeReindexOutcome.NoFolder };
            }

            if (!_savedIndexLoaded)
            {
                Report(true, "Loading the saved index...");
                await LoadSavedIndexAsync(ct).ConfigureAwait(false);
            }
            if (!Directory.Exists(root))
            {
                Report(false, $"Folder not found: {root}");
                return new KnowledgeReindexResult { Outcome = KnowledgeReindexOutcome.FolderNotFound, Folder = root };
            }

            Report(true, "Scanning the folder...");
            scan = ScanFolder(root, ct);
            _lastScan = scan;

            var current = _index;
            var sameFolder = current != null && KnowledgeIndex.SameFolder(current.Folder, root);
            working = sameFolder ? current!.Clone() : new KnowledgeIndex(root);
            changed = !sameFolder;

            var plan = working.Plan(scan.Files, retryFailed);
            run.ToRead = plan.ToRead.Count;
            foreach (var path in plan.Removed)
            {
                if (working.Remove(path))
                {
                    changed = true;
                    run.Removed++;
                }
            }

            var sincePublish = Stopwatch.StartNew();
            var totalChunks = working.ChunkCount;
            var activeFile = 0;
            for (var i = 0; i < plan.ToRead.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (totalChunks >= MaxTotalChunks)
                {
                    run.IndexFull = true;
                    break;
                }

                var file = plan.ToRead[i];
                var number = i + 1;
                var name = Path.GetFileName(file.Path);
                activeFile = number;
                Report(true, KnowledgeReport.FormatReadingProgress(number, plan.ToRead.Count, name));
                // OCR and other slow readers say how far they are ("OCR page 2 of 8").
                var progress = new InlineProgress(detail =>
                {
                    if (activeFile == number)
                        Report(true, KnowledgeReport.FormatReadingProgress(number, plan.ToRead.Count, name, detail));
                });

                var record = await ReadFileAsync(file, progress, ct).ConfigureAwait(false);
                activeFile = 0;
                totalChunks += record.Chunks.Count - (working.TryGetFile(file.Path, out var previous) ? previous.Chunks.Count : 0);
                working.Upsert(record);
                changed = true;
                run.Read++;
                // A photo without words was read fine; only real failures make a background run speak up.
                if (record.Chunks.Count == 0 && !record.HasNoText)
                    run.Failed++;

                if (sincePublish.Elapsed >= PublishInterval)
                {
                    _index = working.Clone();
                    sincePublish.Restart();
                }
            }

            if (run.IndexFull)
                _lastScan = scan = scan with { Pending = PendingEntries(plan.ToRead.Skip(run.Read), "the index is full") };

            working.LastIndexedUtc = DateTime.UtcNow;
            Publish(working, save: changed);
            var result = BuildResult(KnowledgeReindexOutcome.Completed, root, _index, scan, run);
            Report(false, KnowledgeReport.FormatStatus(result, DateTime.Now), KnowledgeReport.FormatDetails(result, MaxUnreadableDetails));
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Keep what was read so far; the next reindex carries on from there.
            if (working != null && changed)
            {
                var partial = working.Clone();
                _index = partial;
                SaveIndex(partial);
            }

            var index = _index;
            if (index != null && !KnowledgeIndex.SameFolder(index.Folder, root))
                index = null;
            if (scan != null && working != null)
            {
                var unread = working.Plan(scan.Files).ToRead;
                _lastScan = scan with { Pending = PendingEntries(unread, "indexing stopped") };
            }

            var result = BuildResult(KnowledgeReindexOutcome.Stopped, root, index, scan, run);
            Report(false, index == null ? "Indexing stopped." : "Indexing stopped. " + KnowledgeReport.FormatStatus(result, DateTime.Now),
                KnowledgeReport.FormatDetails(result, MaxUnreadableDetails));
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Knowledge indexing failed: {ex}");
            Report(false, $"Indexing failed: {ex.Message}");
            return new KnowledgeReindexResult { Outcome = KnowledgeReindexOutcome.Failed, Folder = root, Error = ex.Message };
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed class RunCounts
    {
        public int ToRead;
        public int Read;
        public int Failed;
        public int Removed;
        public bool IndexFull;
    }

    private static KnowledgeReindexResult BuildResult(KnowledgeReindexOutcome outcome, string root, KnowledgeIndex? index,
        ScanReport? scan, RunCounts run)
    {
        var unreadable = new List<KnowledgeFileEntry>();
        var noText = new List<KnowledgeFileEntry>();
        foreach (var f in index?.Files.Where(f => f.Chunks.Count == 0) ?? Enumerable.Empty<KnowledgeFileRecord>())
        {
            if (f.HasNoText)
                noText.Add(new KnowledgeFileEntry(f.Path, KnowledgeFileState.NoText, 0, f.Size, f.Error ?? ""));
            else
                unreadable.Add(new KnowledgeFileEntry(f.Path, KnowledgeFileState.Unreadable, 0, f.Size, f.Error ?? ""));
        }

        return new KnowledgeReindexResult
        {
            Outcome = outcome,
            Folder = root,
            IndexedFiles = index?.IndexedFileCount ?? 0,
            Chunks = index?.ChunkCount ?? 0,
            FilesToRead = run.ToRead,
            FilesRead = run.Read,
            FailedThisRun = run.Failed,
            Removed = run.Removed,
            Unreadable = unreadable,
            NoText = noText,
            Skipped = scan?.SkippedCounts ?? Array.Empty<KnowledgeTypeCount>(),
            TooLarge = scan?.TooLarge ?? Array.Empty<KnowledgeFileEntry>(),
            ScanTruncated = scan?.Truncated ?? false,
            IndexFull = run.IndexFull,
            CheckedUtc = index?.LastIndexedUtc
        };
    }

    private static IReadOnlyList<KnowledgeFileEntry> PendingEntries(IEnumerable<KnowledgeFileStamp> files, string reason) =>
        files.Select(f => new KnowledgeFileEntry(f.Path, KnowledgeFileState.Pending, 0, f.Size, reason)).ToList();

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

    /// <summary>What a folder scan found: the files to index, and the rest, which the Files list shows.</summary>
    private sealed record ScanReport(
        string Folder,
        IReadOnlyList<KnowledgeFileStamp> Files,
        IReadOnlyList<KnowledgeFileEntry> Skipped,
        int SkippedTotal,
        IReadOnlyList<KnowledgeTypeCount> SkippedCounts,
        IReadOnlyList<KnowledgeFileEntry> TooLarge,
        bool Truncated)
    {
        public IReadOnlyList<KnowledgeFileEntry> Pending { get; init; } = Array.Empty<KnowledgeFileEntry>();
    }

    private ScanReport ScanFolder(string root, CancellationToken ct)
    {
        var files = new List<KnowledgeFileStamp>();
        var skipped = new List<KnowledgeFileEntry>();
        var skippedCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var skippedTotal = 0;
        var tooLarge = new List<KnowledgeFileEntry>();
        var truncated = false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
        };

        var seen = 0;
        var sinceProgress = Stopwatch.StartNew();
        // FileInfo from the enumeration already has the size and write time: no extra disk access per file.
        foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (++seen % 50 == 0 && sinceProgress.Elapsed >= ProgressInterval)
            {
                Report(true, $"Scanning the folder... {seen:N0} files so far");
                sinceProgress.Restart();
            }

            var path = info.FullName;
            if (string.Equals(path, _indexPath, StringComparison.OrdinalIgnoreCase) || IsHousekeepingFile(path))
                continue;

            try
            {
                if (!IsSupportedFile(path))
                {
                    skippedTotal++;
                    KnowledgeReport.AddToCounts(skippedCounts, path);
                    if (skipped.Count < MaxListedSkippedFiles)
                        skipped.Add(new KnowledgeFileEntry(path, KnowledgeFileState.Unsupported, 0, info.Length));
                    continue;
                }

                if (info.Length > MaxFileBytes)
                {
                    tooLarge.Add(new KnowledgeFileEntry(path, KnowledgeFileState.TooLarge, 0, info.Length));
                    continue;
                }

                files.Add(new KnowledgeFileStamp(path, info.Length, info.LastWriteTimeUtc));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Vanished or locked between listing and reading its size; the next reindex sees it.
            }

            if (files.Count >= MaxFiles)
            {
                truncated = true;
                break;
            }
        }

        return new ScanReport(root, files, skipped, skippedTotal, KnowledgeReport.SortCounts(skippedCounts), tooLarge, truncated);
    }

    private async Task<KnowledgeFileRecord> ReadFileAsync(KnowledgeFileStamp file, IProgress<string> progress, CancellationToken ct)
    {
        var record = new KnowledgeFileRecord
        {
            Path = file.Path,
            Size = file.Size,
            LastWriteUtc = file.LastWriteUtc,
            ExtractorVersion = KnowledgeIndex.ExtractorVersionFor(file.Path)
        };
        DocumentTextResult result;
        try
        {
            result = await _extractor.ExtractAsync(file.Path, ct, progress, DocumentFileTypes.MaxKnowledgeOcrPages).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            record.Error = ex.Message;
            record.Problem = DocumentReadProblem.Failed;
            return record;
        }

        // ExtractAsync reports a cancellation as an error result; that is not the file's fault.
        ct.ThrowIfCancellationRequested();

        var text = string.IsNullOrWhiteSpace(result.FullText) ? result.Text : result.FullText;
        if (text.Length > MaxIndexedCharsPerFile)
            text = text[..MaxIndexedCharsPerFile];

        record.Chunks = KnowledgeIndex.ChunkText(text);
        if (record.Chunks.Count == 0)
        {
            record.Error = string.IsNullOrWhiteSpace(result.Error) ? "No readable text." : result.Error;
            // The reader says why ("NoTextInImage" for a photo without words), so the status can tell a
            // file without text from one that failed. A read that "worked" but left only blanks has no text.
            record.Problem = result.Problem == DocumentReadProblem.None ? DocumentReadProblem.NoText : result.Problem;
        }
        return record;
    }

    // Calls back on the reporting thread right away (Progress<T> would post to the thread pool, out of order).
    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                report(value);
        }
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
