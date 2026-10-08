using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// One indexed file: the size and write time it had when it was read, and its text chunks.
/// Treated as read-only once it has been added to a <see cref="KnowledgeIndex"/>.
/// </summary>
public sealed class KnowledgeFileRecord
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public DateTime LastWriteUtc { get; set; }
    // Why no text was indexed (unreadable, scanned PDF without OCR, ...). Kept so an unchanged file is
    // not re-read on every automatic reindex; a manual reindex retries it.
    public string? Error { get; set; }
    public List<string> Chunks { get; set; } = new();
}

/// <summary>A supported file found by a folder scan.</summary>
public readonly record struct KnowledgeFileStamp(string Path, long Size, DateTime LastWriteUtc);

/// <summary>What a reindex has to do: files to (re)read, indexed files that are gone, and how many are up to date.</summary>
public sealed record KnowledgeIndexPlan(IReadOnlyList<KnowledgeFileStamp> ToRead, IReadOnlyList<string> Removed, int Unchanged)
{
    public bool HasChanges => ToRead.Count > 0 || Removed.Count > 0;
}

/// <summary>One matching excerpt from <see cref="KnowledgeIndex.Search"/>.</summary>
public sealed record KnowledgeHit(string Path, int ChunkIndex, string Text, double Score, double Coverage)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Searchable index of the text in the user's knowledge folder. Each file is split into ~900 character
/// chunks that overlap by ~150 characters and end on paragraph or sentence boundaries, and chunks are
/// ranked with BM25 (<see cref="TextRanker"/>). Per-file size and write time let a reindex re-read only
/// new or changed files. Reading files is the app's job (KnowledgeService); this class is data + logic.
/// Search may run on any thread. Upsert/Remove/Plan are meant for one owner thread: the app changes a
/// private copy and publishes a <see cref="Clone"/> that it never changes again.
/// </summary>
public sealed class KnowledgeIndex
{
    public const int FormatVersion = 1;
    public const int DefaultChunkChars = 900;
    public const int DefaultOverlapChars = 150;
    public const int DefaultMaxChunks = 4;
    public const int MinChunks = 1;
    public const int MaxChunks = 10;
    // A chunk has to contain at least this share of the message's (idf-weighted) words to be used,
    // so a chat message that only shares a word or two with the documents adds nothing.
    public const double DefaultMinCoverage = 0.4;
    // A chunk that shares just one word with a longer message needs that word to carry most of the
    // message's weight: "name my cat" should not pull in "WiFi name: ...".
    public const double SingleWordMinCoverage = 0.6;
    public const string ContextHeading = "Relevant excerpts from the user's documents";

    // Long pastes are searched on their first part only.
    private const int MaxQueryChars = 4000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // Chat filler that says nothing about the topic: "what does the document say about X" should match
    // on X only, and small talk ("thanks", "good morning", "what time is it?") should not pull in excerpts.
    private static readonly Regex FillerWords = new(
        @"\b(?:tell|say|says|said|know|think|show|give|explain|describe|mean|means|look|help|according|mention|mentions|mentioned|remember|document|documents|doc|docs|file|files|folder|folders|thing|things|something|anything|stuff|thanks|thank|hello|hi|hey|good|morning|afternoon|evening|night|tonight|today|tomorrow|yesterday|day|time|weather|great|cool|nice|sure|sorry|okay|yeah|yep|nope|bye|goodbye)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HorizontalSpace = new(@"[\t\v\f \u00A0\x00-\x08\x0E-\x1F]+", RegexOptions.Compiled);
    private static readonly Regex SpaceAroundNewline = new(@" ?\n ?", RegexOptions.Compiled);
    private static readonly Regex ExtraBlankLines = new(@"\n{3,}", RegexOptions.Compiled);

    private readonly Dictionary<string, KnowledgeFileRecord> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private SearchCache? _searchCache;

    public KnowledgeIndex(string? folder = null)
    {
        Folder = NormalizeFolder(folder);
    }

    /// <summary>The indexed folder (full path, no trailing separator), or "" when unknown.</summary>
    public string Folder { get; }

    public DateTime? LastIndexedUtc { get; set; }

    public IReadOnlyCollection<KnowledgeFileRecord> Files => _files.Values;
    public int FileCount => _files.Count;
    public int IndexedFileCount => _files.Values.Count(f => f.Chunks.Count > 0);
    public int UnreadableFileCount => _files.Values.Count(f => f.Chunks.Count == 0);
    public int ChunkCount => _files.Values.Sum(f => f.Chunks.Count);

    public static int ClampMaxChunks(int maxChunks) => Math.Clamp(maxChunks, MinChunks, MaxChunks);

    public static string NormalizeFolder(string? folder)
    {
        var text = (folder ?? "").Trim().Trim('"').Trim();
        if (text.Length == 0)
            return "";

        try
        {
            text = Path.GetFullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            // Keep what the user typed; the scan will report that the folder cannot be found.
        }

        return Path.TrimEndingDirectorySeparator(text);
    }

    public static bool SameFolder(string? a, string? b)
    {
        var left = NormalizeFolder(a);
        return left.Length > 0 && string.Equals(left, NormalizeFolder(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A copy with the same records (records are shared, they are never changed).</summary>
    public KnowledgeIndex Clone()
    {
        var copy = new KnowledgeIndex(Folder) { LastIndexedUtc = LastIndexedUtc };
        lock (_gate)
        {
            foreach (var (path, record) in _files)
                copy._files[path] = record;
        }

        return copy;
    }

    /// <summary>
    /// Compares a folder scan with the index: new files and files whose size or write time changed must
    /// be read, indexed files missing from the scan are gone. With <paramref name="retryFailed"/>, files
    /// that produced no text last time are read again too (e.g. after installing OCR tools).
    /// </summary>
    public KnowledgeIndexPlan Plan(IEnumerable<KnowledgeFileStamp> scannedFiles, bool retryFailed = false)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toRead = new List<KnowledgeFileStamp>();
        var unchanged = 0;
        lock (_gate)
        {
            foreach (var file in scannedFiles ?? Enumerable.Empty<KnowledgeFileStamp>())
            {
                if (string.IsNullOrWhiteSpace(file.Path) || !seen.Add(file.Path))
                    continue;

                if (_files.TryGetValue(file.Path, out var record) && IsCurrent(record, file) &&
                    !(retryFailed && record.Chunks.Count == 0))
                    unchanged++;
                else
                    toRead.Add(file);
            }

            var removed = _files.Keys.Where(path => !seen.Contains(path)).ToList();
            return new KnowledgeIndexPlan(toRead, removed, unchanged);
        }
    }

    public void Upsert(KnowledgeFileRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (string.IsNullOrWhiteSpace(record.Path))
            throw new ArgumentException("The record needs a path.", nameof(record));

        record.Chunks ??= new List<string>();
        lock (_gate)
        {
            _files[record.Path] = record;
            _searchCache = null;
        }
    }

    public bool TryGetFile(string path, [MaybeNullWhen(false)] out KnowledgeFileRecord record)
    {
        lock (_gate)
            return _files.TryGetValue(path ?? "", out record);
    }

    public bool Remove(string path)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(path) || !_files.Remove(path))
                return false;

            _searchCache = null;
            return true;
        }
    }

    /// <summary>
    /// The best chunks for a chat message, best first: BM25 order, keeping only chunks that contain at
    /// least <paramref name="minCoverage"/> of the message's weighted words (more when they share only
    /// one word with it). Empty when nothing is relevant.
    /// </summary>
    public IReadOnlyList<KnowledgeHit> Search(string? query, int maxChunks = DefaultMaxChunks, double minCoverage = DefaultMinCoverage)
    {
        var text = CleanQuery(query);
        if (maxChunks <= 0 || TextRanker.Tokenize(text).Count == 0)
            return Array.Empty<KnowledgeHit>();

        var cache = GetSearchCache();
        var hits = new List<KnowledgeHit>();
        foreach (var ranked in cache.Ranker.Rank(text))
        {
            if (ranked.Coverage < minCoverage || (ranked.MatchedTerms < 2 && ranked.Coverage < Math.Max(minCoverage, SingleWordMinCoverage)))
                continue;

            var (record, chunk) = cache.Chunks[ranked.Index];
            hits.Add(new KnowledgeHit(record.Path, chunk, record.Chunks[chunk], ranked.Score, ranked.Coverage));
            if (hits.Count >= maxChunks)
                break;
        }

        return hits;
    }

    /// <summary>Builds the search structures now (on a background thread) instead of on the first search.</summary>
    public void Prepare() => GetSearchCache();

    /// <summary>The words of a chat message worth searching for: capped in length, filler such as "tell me" removed.</summary>
    public static string CleanQuery(string? query)
    {
        var text = query ?? "";
        if (text.Length > MaxQueryChars)
            text = text[..MaxQueryChars];
        return FillerWords.Replace(text, " ");
    }

    // ==================== Chunking ====================

    /// <summary>
    /// Splits text into chunks of at most <paramref name="chunkChars"/> characters. A chunk ends at the last
    /// paragraph break in its second half, else the last sentence end, line break or space; the next chunk
    /// starts up to <paramref name="overlapChars"/> earlier, at a sentence or word start, so a passage cut
    /// in two is still found whole in one of them.
    /// </summary>
    public static List<string> ChunkText(string? text, int chunkChars = DefaultChunkChars, int overlapChars = DefaultOverlapChars)
    {
        var chunks = new List<string>();
        var normalized = NormalizeText(text);
        if (normalized.Length == 0)
            return chunks;

        chunkChars = Math.Max(100, chunkChars);
        var minChunk = chunkChars / 2;
        // Less than the shortest chunk, so every step moves forward.
        overlapChars = Math.Clamp(overlapChars, 0, chunkChars / 3);

        var start = 0;
        while (start < normalized.Length)
        {
            if (normalized.Length - start <= chunkChars)
            {
                AddChunk(chunks, normalized[start..]);
                break;
            }

            var end = FindChunkEnd(normalized, start + minChunk, start + chunkChars);
            AddChunk(chunks, normalized[start..end]);

            var next = overlapChars == 0 ? end : FindOverlapStart(normalized, end - overlapChars, end);
            start = next > start ? next : end;
            while (start < normalized.Length && char.IsWhiteSpace(normalized[start]))
                start++;
        }

        return chunks;
    }

    // The exclusive end of a chunk, between min and limit (limit is always inside the text).
    private static int FindChunkEnd(string text, int min, int limit)
    {
        // Paragraph break: a blank line follows.
        for (var e = limit; e >= min; e--)
        {
            if (text[e] == '\n' && e + 1 < text.Length && text[e + 1] == '\n')
                return e;
        }

        // Sentence end: ".", "!" or "?" (maybe inside a closing quote or bracket) followed by white space.
        for (var e = limit; e >= min; e--)
        {
            if (char.IsWhiteSpace(text[e]) && IsSentenceEnd(text, e - 1))
                return e;
        }

        for (var e = limit; e >= min; e--)
        {
            if (text[e] == '\n')
                return e;
        }

        for (var e = limit; e >= min; e--)
        {
            if (char.IsWhiteSpace(text[e]))
                return e;
        }

        return limit;
    }

    // Where the next chunk starts: the first sentence or line start in [from, end), else the first word
    // start, else from itself.
    private static int FindOverlapStart(string text, int from, int end)
    {
        from = Math.Max(1, from);
        for (var s = from; s < end; s++)
        {
            if (!char.IsWhiteSpace(text[s]) && (text[s - 1] == '\n' || (char.IsWhiteSpace(text[s - 1]) && IsSentenceEnd(text, s - 2))))
                return s;
        }

        for (var s = from; s < end; s++)
        {
            if (!char.IsWhiteSpace(text[s]) && char.IsWhiteSpace(text[s - 1]))
                return s;
        }

        return from;
    }

    private static bool IsSentenceEnd(string text, int i)
    {
        if (i < 0 || i >= text.Length)
            return false;

        var c = text[i];
        if (c is '.' or '!' or '?' or '…')
            return true;

        return (c is '"' or '\'' or ')' or ']' or '”' or '’') && i > 0 && (text[i - 1] is '.' or '!' or '?');
    }

    private static void AddChunk(List<string> chunks, string chunk)
    {
        chunk = chunk.Trim();
        if (chunk.Any(char.IsLetterOrDigit))
            chunks.Add(chunk);
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        normalized = HorizontalSpace.Replace(normalized, " ");
        normalized = SpaceAroundNewline.Replace(normalized, "\n");
        normalized = ExtraBlankLines.Replace(normalized, "\n\n");
        return normalized.Trim();
    }

    // ==================== Prompt text ====================

    /// <summary>The context block for the model (appended to the user message): a short instruction, then each excerpt under its file name.</summary>
    public static string FormatContext(IReadOnlyList<KnowledgeHit> hits, string? folder = null)
    {
        if (hits == null || hits.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine(ContextHeading + " (their knowledge folder), found by keyword search for the latest message.");
        sb.AppendLine("Use them when they help answer that message and mention which file you used. If they do not cover the question, ignore them and answer normally. Do not invent document content.");
        for (var i = 0; i < hits.Count; i++)
        {
            sb.AppendLine();
            sb.Append('[').Append(i + 1).Append("] ").AppendLine(DisplayName(hits[i].Path, folder));
            sb.AppendLine(hits[i].Text.Trim());
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>The chat note for a search, e.g. "Using 3 excerpts from: a.pdf, b.md".</summary>
    public static string FormatNote(IReadOnlyList<KnowledgeHit> hits)
    {
        if (hits == null || hits.Count == 0)
            return "";

        var files = hits.Select(h => h.FileName).Distinct(StringComparer.OrdinalIgnoreCase);
        return $"Using {hits.Count} {(hits.Count == 1 ? "excerpt" : "excerpts")} from: {string.Join(", ", files)}";
    }

    /// <summary>The path relative to the knowledge folder when the file is inside it, else just the file name.</summary>
    public static string DisplayName(string path, string? folder)
    {
        var root = NormalizeFolder(folder);
        if (root.Length > 0 && !string.IsNullOrEmpty(path))
        {
            try
            {
                var relative = Path.GetRelativePath(root, path);
                if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
                    return relative;
            }
            catch (ArgumentException)
            {
                // Fall back to the file name.
            }
        }

        return Path.GetFileName(path);
    }

    // ==================== JSON ====================

    public string ToJson()
    {
        List<KnowledgeFileRecord> files;
        lock (_gate)
            files = _files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();

        return JsonSerializer.Serialize(new IndexData
        {
            Version = FormatVersion,
            Folder = Folder,
            LastIndexedUtc = LastIndexedUtc,
            Files = files
        }, JsonOptions);
    }

    /// <summary>The index saved by <see cref="ToJson"/>; an empty index for blank, corrupt or other-version JSON.</summary>
    public static KnowledgeIndex FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new KnowledgeIndex();

        IndexData? data;
        try
        {
            data = JsonSerializer.Deserialize<IndexData>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return new KnowledgeIndex();
        }

        if (data == null || data.Version != FormatVersion)
            return new KnowledgeIndex();

        var index = new KnowledgeIndex(data.Folder) { LastIndexedUtc = data.LastIndexedUtc };
        foreach (var record in data.Files ?? new List<KnowledgeFileRecord>())
        {
            if (record == null || string.IsNullOrWhiteSpace(record.Path))
                continue;

            record.Chunks = (record.Chunks ?? new List<string>()).Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
            index._files[record.Path] = record;
        }

        return index;
    }

    // ==================== Internals ====================

    private static bool IsCurrent(KnowledgeFileRecord record, KnowledgeFileStamp file) =>
        record.Size == file.Size && AsUtc(record.LastWriteUtc) == AsUtc(file.LastWriteUtc);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private SearchCache GetSearchCache()
    {
        lock (_gate)
        {
            if (_searchCache != null)
                return _searchCache;

            var chunks = new List<(KnowledgeFileRecord Record, int Chunk)>();
            var documents = new List<string>();
            foreach (var record in _files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            {
                // The file name and its subfolders are searchable too, so "my car's tire pressure" also
                // finds chunks of "Car\Honda manual.md" and "my lease" those of "Lease 2025.pdf".
                var title = Path.ChangeExtension(DisplayName(record.Path, Folder), null);
                for (var i = 0; i < record.Chunks.Count; i++)
                {
                    chunks.Add((record, i));
                    documents.Add(title + "\n" + record.Chunks[i]);
                }
            }

            _searchCache = new SearchCache(new TextRanker(documents), chunks);
            return _searchCache;
        }
    }

    private sealed record SearchCache(TextRanker Ranker, List<(KnowledgeFileRecord Record, int Chunk)> Chunks);

    internal sealed class IndexData
    {
        public int Version { get; set; }
        public string Folder { get; set; } = "";
        public DateTime? LastIndexedUtc { get; set; }
        public List<KnowledgeFileRecord>? Files { get; set; }
    }
}
