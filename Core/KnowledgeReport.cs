using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VoiceChatbot;

/// <summary>
/// What a knowledge folder scan found out about one file. The order is the "by status" sort order:
/// problems first, then files that were skipped on purpose, then indexed files.
/// </summary>
public enum KnowledgeFileState
{
    Unreadable,
    TooLarge,
    Pending,
    Unsupported,
    Indexed
}

/// <summary>One file in the knowledge folder and what happened to it (the Files list).</summary>
public sealed record KnowledgeFileEntry(string Path, KnowledgeFileState State, int Chunks = 0, long Size = 0, string Reason = "");

/// <summary>
/// The Files list: every file the last scan saw. <paramref name="UnlistedSkipped"/> counts unsupported
/// files beyond the listed ones; <paramref name="Scanned"/> is false until a scan of this folder ran.
/// </summary>
public sealed record KnowledgeFileList(string Folder, IReadOnlyList<KnowledgeFileEntry> Entries, int UnlistedSkipped = 0, bool Scanned = true);

/// <summary>How many skipped files have one extension (".jpg"); "" for files without one.</summary>
public readonly record struct KnowledgeExtensionCount(string Extension, int Count);

public enum KnowledgeReindexOutcome
{
    Completed,
    Stopped,
    Failed,
    NoFolder,
    FolderNotFound
}

public enum KnowledgeFileSort
{
    Status,
    Name,
    Type
}

/// <summary>The result of one knowledge folder reindex, for the status line, its tooltip and the chat summary.</summary>
public sealed record KnowledgeReindexResult
{
    public KnowledgeReindexOutcome Outcome { get; init; }
    public string Folder { get; init; } = "";
    /// <summary>Files with text in the index, and their chunks, after this run.</summary>
    public int IndexedFiles { get; init; }
    public int Chunks { get; init; }
    /// <summary>New or changed files this run had to read, how many it read and how many of those gave no text.</summary>
    public int FilesToRead { get; init; }
    public int FilesRead { get; init; }
    public int FailedThisRun { get; init; }
    /// <summary>Indexed files that are no longer in the folder.</summary>
    public int Removed { get; init; }
    /// <summary>Every file in the index that gave no text (not only this run's), with its reason.</summary>
    public IReadOnlyList<KnowledgeFileEntry> Unreadable { get; init; } = Array.Empty<KnowledgeFileEntry>();
    public IReadOnlyList<KnowledgeExtensionCount> Skipped { get; init; } = Array.Empty<KnowledgeExtensionCount>();
    public IReadOnlyList<KnowledgeFileEntry> TooLarge { get; init; } = Array.Empty<KnowledgeFileEntry>();
    public bool ScanTruncated { get; init; }
    public bool IndexFull { get; init; }
    public string Error { get; init; } = "";
    public DateTime? CheckedUtc { get; init; }

    public int SkippedCount => Skipped.Sum(s => s.Count);
    public bool HasChanges => FilesRead > 0 || Removed > 0;
}

/// <summary>
/// Wording for knowledge folder indexing: the status line under the folder box, its tooltip, the
/// one-line chat summary after a reindex and the rows of the Files list. Pure text, no I/O.
/// </summary>
public static class KnowledgeReport
{
    /// <summary>Files larger than this are not read.</summary>
    public const long MaxFileBytes = 25L * 1024 * 1024;
    public const int MaxFiles = 10_000;
    public const int MaxTotalChunks = 50_000;

    /// <summary>Extensions named in the status line and chat summary; the rest are counted as "other".</summary>
    public const int MaxExtensionGroups = 3;
    /// <summary>Unreadable files named in the chat summary.</summary>
    public const int MaxNamedUnreadable = 3;

    private static string MaxFileSizeText => $"{MaxFileBytes / (1024 * 1024)} MB";

    // ==================== Skipped files ====================

    /// <summary>Skipped files counted per lowercased extension, most common first (then alphabetical).</summary>
    public static IReadOnlyList<KnowledgeExtensionCount> CountByExtension(IEnumerable<string>? paths)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in paths ?? Enumerable.Empty<string>())
        {
            var extension = ExtensionOf(path);
            counts[extension] = counts.TryGetValue(extension, out var n) ? n + 1 : 1;
        }

        return SortCounts(counts);
    }

    /// <summary>Adds one file to a running per-extension count (a scan counts without keeping every path).</summary>
    public static void AddToCounts(Dictionary<string, int> counts, string path)
    {
        ArgumentNullException.ThrowIfNull(counts);
        var extension = ExtensionOf(path);
        counts[extension] = counts.TryGetValue(extension, out var n) ? n + 1 : 1;
    }

    public static IReadOnlyList<KnowledgeExtensionCount> SortCounts(IReadOnlyDictionary<string, int> counts) =>
        counts
            .Where(c => c.Value > 0)
            .Select(c => new KnowledgeExtensionCount(c.Key, c.Value))
            .OrderByDescending(c => c.Count)
            .ThenBy(c => c.Extension.Length == 0 ? 1 : 0)
            .ThenBy(c => c.Extension, StringComparer.Ordinal)
            .ToList();

    /// <summary>The lowercased extension (".pdf"), or "" when the name has none.</summary>
    public static string ExtensionOf(string? path)
    {
        var extension = System.IO.Path.GetExtension(path ?? "");
        return string.IsNullOrEmpty(extension) || extension == "." ? "" : extension.ToLowerInvariant();
    }

    /// <summary>"12 .jpg, 6 .xlsx, 5 .doc", with the rest as "4 other" past <paramref name="maxGroups"/> extensions.</summary>
    public static string FormatExtensionCounts(IReadOnlyList<KnowledgeExtensionCount>? counts, int maxGroups = MaxExtensionGroups)
    {
        if (counts == null || counts.Count == 0)
            return "";

        maxGroups = Math.Max(1, maxGroups);
        // "4 other" is only worth it for two or more groups; otherwise name the last one too.
        var shown = counts.Count <= maxGroups + 1 ? counts.Count : maxGroups;
        var parts = counts.Take(shown).Select(c => $"{c.Count:N0} {ExtensionLabel(c.Extension)}").ToList();
        var other = counts.Skip(shown).Sum(c => c.Count);
        if (other > 0)
            parts.Add($"{other:N0} other");
        return string.Join(", ", parts);
    }

    /// <summary>The status line part for skipped files: "23 skipped: 12 .jpg, 6 .xlsx, 5 .doc".</summary>
    public static string FormatSkippedStatus(IReadOnlyList<KnowledgeExtensionCount>? counts)
    {
        var total = counts?.Sum(c => c.Count) ?? 0;
        return total == 0 ? "" : $"{total:N0} skipped: {FormatExtensionCounts(counts)}";
    }

    private static string ExtensionLabel(string extension) => extension.Length == 0 ? "without extension" : extension;

    // ==================== Status line and tooltip ====================

    /// <summary>
    /// The status line after a reindex (or for a saved index): "18 files, 412 chunks · checked today
    /// 11:52 PM · 1 could not be read · 23 skipped: 12 .jpg, 6 .xlsx, 5 .doc".
    /// </summary>
    public static string FormatStatus(KnowledgeReindexResult result, DateTime nowLocal)
    {
        ArgumentNullException.ThrowIfNull(result);
        var text = $"{Plural(result.IndexedFiles, "file")}, {Plural(result.Chunks, "chunk")}";
        if (result.CheckedUtc is DateTime checkedUtc)
            text += $" · checked {FormatWhen(checkedUtc, nowLocal)}";

        if (result.Unreadable.Count > 0)
            text += $" · {result.Unreadable.Count:N0} could not be read";
        var skipped = FormatSkippedStatus(result.Skipped);
        if (skipped.Length > 0)
            text += " · " + skipped;
        if (result.TooLarge.Count > 0)
            text += $" · {result.TooLarge.Count:N0} over {MaxFileSizeText} skipped";
        if (result.ScanTruncated)
            text += $" · stopped at {MaxFiles:N0} files";
        if (result.IndexFull)
            text += $" · index full ({MaxTotalChunks:N0} chunks), some files left out";
        return text;
    }

    /// <summary>
    /// The status line's tooltip: which files could not be read and why, what was skipped, and a
    /// pointer to the Files list. "" when there is nothing to explain.
    /// </summary>
    public static string FormatDetails(KnowledgeReindexResult result, int maxListed = 10)
    {
        ArgumentNullException.ThrowIfNull(result);
        maxListed = Math.Max(1, maxListed);
        var sections = new List<string>();

        if (result.Unreadable.Count > 0)
        {
            var lines = result.Unreadable
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Take(maxListed)
                .Select(f => $"{KnowledgeIndex.DisplayName(f.Path, result.Folder)}: {ReasonOrDefault(f.Reason)}")
                .ToList();
            if (result.Unreadable.Count > maxListed)
                lines.Add($"...and {result.Unreadable.Count - maxListed:N0} more");
            sections.Add("Could not read (Reindex tries these again):\n" + string.Join("\n", lines));
        }

        if (result.SkippedCount > 0)
            sections.Add("Skipped, type not supported: " + FormatExtensionCounts(result.Skipped, maxGroups: 8));

        if (result.TooLarge.Count > 0)
        {
            var names = result.TooLarge.Take(maxListed).Select(f => KnowledgeIndex.DisplayName(f.Path, result.Folder)).ToList();
            if (result.TooLarge.Count > maxListed)
                names.Add($"...and {result.TooLarge.Count - maxListed:N0} more");
            sections.Add($"Skipped, over {MaxFileSizeText}: " + string.Join(", ", names));
        }

        if (sections.Count == 0)
            return "";

        sections.Add("Click Files for the full list.");
        return string.Join("\n\n", sections);
    }

    /// <summary>"Reading 3 of 25: HOA Bylaws.pdf", plus the reader's own progress ("OCR page 2 of 8") when it has some.</summary>
    public static string FormatReadingProgress(int number, int total, string? fileName, string? detail = null)
    {
        var text = $"Reading {number:N0} of {Math.Max(number, total):N0}: {fileName}";
        var extra = (detail ?? "").Trim().TrimEnd('.', '…').Trim();
        // "reading PDF text" only repeats the line itself.
        if (extra.Length > 0 && !extra.StartsWith("reading", StringComparison.OrdinalIgnoreCase))
            text += " · " + extra;
        return text;
    }

    // ==================== Chat summary ====================

    /// <summary>
    /// The one-line chat message after a reindex the user asked for, e.g. "Knowledge folder: 18 files
    /// indexed (412 chunks), 2 new or changed. Skipped 23 (12 .jpg, 6 .xlsx, 5 .doc: not supported).
    /// Could not read 1: scan.pdf (no text)." or "Knowledge folder is up to date: 18 files (412 chunks),
    /// checked 11:52 PM.". "" for a reindex without a folder.
    /// </summary>
    public static string FormatChatSummary(KnowledgeReindexResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        switch (result.Outcome)
        {
            case KnowledgeReindexOutcome.NoFolder:
                return "";
            case KnowledgeReindexOutcome.FolderNotFound:
                return $"Knowledge folder not found: {result.Folder}. Choose the folder again with Browse.";
            case KnowledgeReindexOutcome.Failed:
                return $"Knowledge folder: indexing failed: {EndSentence(string.IsNullOrWhiteSpace(result.Error) ? "unknown error" : result.Error.Trim())}";
        }

        var sb = new StringBuilder();
        var files = $"{Plural(result.IndexedFiles, "file")} ({Plural(result.Chunks, "chunk")})";
        if (result.Outcome == KnowledgeReindexOutcome.Stopped)
        {
            sb.Append($"Knowledge folder: indexing stopped after {result.FilesRead:N0} of {Plural(result.FilesToRead, "new or changed file")}. ");
            sb.Append($"{files} can be searched; Reindex carries on from there.");
        }
        else if (!result.HasChanges)
        {
            sb.Append($"Knowledge folder is up to date: {files}");
            if (result.CheckedUtc is DateTime checkedUtc)
                sb.Append($", checked {ToLocal(checkedUtc).ToString("t", CultureInfo.CurrentCulture)}");
            sb.Append('.');
        }
        else
        {
            sb.Append($"Knowledge folder: {Plural(result.IndexedFiles, "file")} indexed ({Plural(result.Chunks, "chunk")})");
            var changes = new List<string>();
            if (result.FilesRead > 0)
                changes.Add($"{result.FilesRead:N0} new or changed");
            if (result.Removed > 0)
                changes.Add($"{result.Removed:N0} removed");
            if (changes.Count > 0)
                sb.Append(", ").Append(string.Join(", ", changes));
            sb.Append('.');
        }

        var skippedParts = new List<string>();
        if (result.SkippedCount > 0)
            skippedParts.Add($"{result.SkippedCount:N0} ({FormatExtensionCounts(result.Skipped)}: not supported)");
        if (result.TooLarge.Count > 0)
            skippedParts.Add($"{result.TooLarge.Count:N0} over {MaxFileSizeText}");
        if (skippedParts.Count > 0)
            sb.Append(" Skipped ").Append(string.Join(" and ", skippedParts)).Append('.');

        if (result.Unreadable.Count > 0)
        {
            var named = result.Unreadable
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxNamedUnreadable)
                .Select(f => $"{System.IO.Path.GetFileName(f.Path)} ({ShortReason(f.Reason)})")
                .ToList();
            var more = result.Unreadable.Count - named.Count;
            sb.Append($" Could not read {result.Unreadable.Count:N0}: {string.Join(", ", named)}");
            if (more > 0)
                sb.Append($" and {more:N0} more");
            sb.Append('.');
        }

        if (result.ScanTruncated)
            sb.Append($" Stopped at {MaxFiles:N0} files: pick a smaller folder to include the rest.");
        if (result.IndexFull)
            sb.Append($" The index is full ({MaxTotalChunks:N0} chunks), so some files were left out.");
        if (result.IndexedFiles == 0 && result.Outcome == KnowledgeReindexOutcome.Completed)
            sb.Append(" Nothing in this folder can be searched yet.");

        return sb.ToString();
    }

    /// <summary>A reader error cut down for a one-line summary: "No readable text." becomes "no text".</summary>
    public static string ShortReason(string? error, int maxChars = 70)
    {
        var text = (error ?? "").Trim();
        if (text.Length == 0 || text.StartsWith("No readable text", StringComparison.OrdinalIgnoreCase))
            return "no text";

        // The first sentence says what went wrong; the rest is advice.
        var end = text.IndexOf(". ", StringComparison.Ordinal);
        if (end > 0)
            text = text[..end];
        text = text.TrimEnd('.', ' ');

        if (text.Length > maxChars)
            text = text[..Math.Max(1, maxChars - 3)].TrimEnd() + "...";
        // "File not found" -> "file not found", but keep "OCR unavailable".
        if (text.Length > 1 && char.IsUpper(text[0]) && char.IsLower(text[1]))
            text = char.ToLowerInvariant(text[0]) + text[1..];
        return text;
    }

    // ==================== Files list ====================

    /// <summary>The status column of the Files list: "Indexed - 12 chunks", "Could not read - no text", ...</summary>
    public static string DescribeEntry(KnowledgeFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return entry.State switch
        {
            KnowledgeFileState.Indexed => $"Indexed - {Plural(entry.Chunks, "chunk")}",
            KnowledgeFileState.Unreadable => $"Could not read - {ShortReason(entry.Reason)}",
            KnowledgeFileState.TooLarge => $"Too large - {FormatSize(entry.Size)} (limit {MaxFileSizeText})",
            KnowledgeFileState.Pending => string.IsNullOrWhiteSpace(entry.Reason) ? "Not read yet" : $"Not read yet - {entry.Reason}",
            _ => "Skipped - type not supported"
        };
    }

    /// <summary>
    /// Sorts the Files list. By status: problems first (could not read, too large, not read yet), then
    /// skipped, then indexed, each by name. By type: by extension, then name. Descending reverses it.
    /// </summary>
    public static IReadOnlyList<KnowledgeFileEntry> Sort(IEnumerable<KnowledgeFileEntry>? entries, KnowledgeFileSort sort,
        bool descending = false, string? folder = null)
    {
        var list = (entries ?? Enumerable.Empty<KnowledgeFileEntry>()).Where(e => e != null).ToList();
        string Name(KnowledgeFileEntry e) => KnowledgeIndex.DisplayName(e.Path, folder);

        IOrderedEnumerable<KnowledgeFileEntry> ordered = sort switch
        {
            KnowledgeFileSort.Name => list.OrderBy(Name, StringComparer.OrdinalIgnoreCase),
            KnowledgeFileSort.Type => list.OrderBy(e => ExtensionOf(e.Path), StringComparer.Ordinal)
                .ThenBy(Name, StringComparer.OrdinalIgnoreCase),
            _ => list.OrderBy(e => (int)e.State).ThenBy(Name, StringComparer.OrdinalIgnoreCase)
        };

        var sorted = ordered.ToList();
        if (descending)
            sorted.Reverse();
        return sorted;
    }

    /// <summary>
    /// The Files list entries: each file in the index (indexed, or could not be read with its reason),
    /// then the scan's other files (<paramref name="scanEntries"/>: unsupported, too large, not read
    /// yet) that are not in the index.
    /// </summary>
    public static IReadOnlyList<KnowledgeFileEntry> BuildFileList(KnowledgeIndex? index, IEnumerable<KnowledgeFileEntry>? scanEntries)
    {
        var entries = new List<KnowledgeFileEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (index != null)
        {
            foreach (var file in index.Files)
            {
                if (!seen.Add(file.Path))
                    continue;
                entries.Add(file.Chunks.Count > 0
                    ? new KnowledgeFileEntry(file.Path, KnowledgeFileState.Indexed, file.Chunks.Count, file.Size)
                    : new KnowledgeFileEntry(file.Path, KnowledgeFileState.Unreadable, 0, file.Size, ReasonOrDefault(file.Error)));
            }
        }

        foreach (var entry in scanEntries ?? Enumerable.Empty<KnowledgeFileEntry>())
        {
            if (entry != null && !string.IsNullOrEmpty(entry.Path) && seen.Add(entry.Path))
                entries.Add(entry);
        }

        return entries;
    }

    /// <summary>The counts line at the top of the Files list.</summary>
    public static string FormatFileCounts(IReadOnlyCollection<KnowledgeFileEntry>? entries, int unlistedSkipped = 0)
    {
        var list = entries ?? Array.Empty<KnowledgeFileEntry>();
        int Count(KnowledgeFileState state) => list.Count(e => e.State == state);

        var parts = new List<string> { $"{Count(KnowledgeFileState.Indexed):N0} indexed" };
        var unreadable = Count(KnowledgeFileState.Unreadable);
        if (unreadable > 0)
            parts.Add($"{unreadable:N0} could not be read");
        var skipped = Count(KnowledgeFileState.Unsupported) + Math.Max(0, unlistedSkipped);
        if (skipped > 0)
            parts.Add($"{skipped:N0} skipped (type not supported)");
        var tooLarge = Count(KnowledgeFileState.TooLarge);
        if (tooLarge > 0)
            parts.Add($"{tooLarge:N0} too large");
        var pending = Count(KnowledgeFileState.Pending);
        if (pending > 0)
            parts.Add($"{pending:N0} not read yet");
        return string.Join(" · ", parts);
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{Math.Max(0, bytes)} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:0} KB";
        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):0.#} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.#} GB";
    }

    // ==================== Helpers ====================

    private static string ReasonOrDefault(string? reason) => string.IsNullOrWhiteSpace(reason) ? "No readable text." : reason.Trim();

    private static string EndSentence(string text) => text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";

    private static string Plural(int count, string noun) => $"{count:N0} {(count == 1 ? noun : noun + "s")}";

    /// <summary>"today 11:52 PM", or the date and time for another day.</summary>
    public static string FormatWhen(DateTime utc, DateTime nowLocal)
    {
        var local = ToLocal(utc);
        return local.Date == nowLocal.Date
            ? $"today {local.ToString("t", CultureInfo.CurrentCulture)}"
            : local.ToString("g", CultureInfo.CurrentCulture);
    }

    private static DateTime ToLocal(DateTime utc) =>
        utc.Kind == DateTimeKind.Local ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
}
