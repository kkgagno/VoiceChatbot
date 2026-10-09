using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>How much of the knowledge folder went to the model with a message.</summary>
public enum KnowledgeContextMode
{
    /// <summary>Nothing: no indexed files, or no room at all.</summary>
    None,
    /// <summary>Only the list of files: the message is not about them.</summary>
    Catalog,
    /// <summary>The list of files and the best-matching excerpts.</summary>
    Excerpts,
    /// <summary>The list of files and all of their text (it fits the budget).</summary>
    WholeFolder,
    /// <summary>The list of files and the start of each one: the message asks about the documents but no passage matches its words.</summary>
    Overview
}

/// <summary>The knowledge text appended to one message, and the short chat note about it ("" for none).</summary>
public sealed record KnowledgeContextResult(
    KnowledgeContextMode Mode,
    string Text,
    string Note,
    IReadOnlyList<KnowledgeHit> Excerpts,
    int DocumentsUsed)
{
    public static KnowledgeContextResult None { get; } =
        new(KnowledgeContextMode.None, "", "", Array.Empty<KnowledgeHit>(), 0);
}

/// <summary>
/// Chooses what the model gets from the knowledge folder for one message, within a token budget:
/// <list type="bullet">
/// <item>always a short catalog (the folder's name and its files) with an instruction not to invent
/// document contents, so the model knows the folder exists and what is in it;</item>
/// <item>when the message is about the documents (an excerpt matches, or it mentions the documents or
/// the folder) and all the indexed text fits the budget, all of it, file by file;</item>
/// <item>otherwise the best excerpts: at least the "Excerpts per message" setting, more while they
/// still match well and fit the budget.</item>
/// </list>
/// Sizes are estimated at <see cref="CharsPerToken"/> characters per token, like the chat's own estimate.
/// </summary>
public static class KnowledgeContext
{
    /// <summary>The knowledge folder may use about this share of the request's context window...</summary>
    public const double ContextWindowShare = 0.35;
    /// <summary>...and at most this share of what the prompt still has room for after the message itself.</summary>
    public const double PromptRoomShare = 0.6;
    public const int CharsPerToken = 4;
    public const int MaxCatalogNames = 60;
    public const int MaxCatalogUnreadable = 10;
    /// <summary>Ranked excerpts considered beyond the minimum.</summary>
    public const int MaxCandidates = 200;
    /// <summary>Beyond the minimum, an excerpt must score at least this share of the best one.</summary>
    public const double ExtraScoreFloor = 0.35;
    /// <summary>The catalog may take at most this share of the budget; in a small window the text matters more.</summary>
    public const double MaxCatalogShare = 1.0 / 3;
    public const int MaxNoteFiles = 3;
    /// <summary>The first line of the knowledge text, so it stands apart from what the user typed.</summary>
    public const string ContextHeading = "[Knowledge folder]";

    // Consecutive chunks of a file overlap by up to KnowledgeIndex.DefaultOverlapChars; a shorter match
    // than this is a coincidence, not the overlap.
    private const int MinJoinOverlapChars = 12;
    private const int MaxJoinOverlapChars = KnowledgeIndex.DefaultChunkChars / 3;
    // "[12] Folder\File.pdf\n" plus the blank line before it, and a "\n[...]\n" gap.
    private const int HeaderOverheadChars = 16;
    private const string WholeFolderHeading = "Full text of all {0}:";
    private const string ExcerptsHeading = "Excerpts found by keyword search for the latest message, most relevant file first:";
    private const string BeginningsHeading = "The beginning of each document:";

    private static readonly Regex DocumentWords = new(
        @"\b(?:documents?|docs?|files?|folders?|pdfs?|paperwork|knowledge)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // ==================== Budget ====================

    /// <summary>
    /// Tokens the knowledge folder may add to a request: <see cref="ContextWindowShare"/> of the context
    /// window, but no more than <see cref="PromptRoomShare"/> of the prompt room left after the reply
    /// budget, the safety margin and the message itself, so the chat history keeps some room too.
    /// </summary>
    public static int BudgetTokens(int contextWindow, int maxOutputTokens, int currentMessageTokens)
    {
        if (contextWindow <= 0)
            contextWindow = TokenBudget.DefaultContextWindow;

        var share = (long)(contextWindow * ContextWindowShare);
        var room = (long)TokenBudget.PromptBudget(contextWindow, maxOutputTokens) - Math.Max(0, currentMessageTokens);
        return (int)Math.Clamp(Math.Min(share, (long)(room * PromptRoomShare)), 0, int.MaxValue);
    }

    public static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / (double)CharsPerToken);

    // ==================== Building ====================

    /// <summary>
    /// The knowledge text for a message (see the class summary). <paramref name="followUp"/> is the
    /// previous question when this message is a short follow-up, <paramref name="minExcerpts"/> the
    /// "Excerpts per message" setting.
    /// </summary>
    public static KnowledgeContextResult Build(KnowledgeIndex index, string? query, string? followUp, int minExcerpts, int budgetTokens)
    {
        ArgumentNullException.ThrowIfNull(index);
        var files = index.IndexedFilesInOrder();
        var folder = index.Folder;
        var unreadable = index.UnreadableFilesInOrder().Select(f => KnowledgeIndex.DisplayName(f.Path, folder)).ToList();
        if ((files.Count == 0 && unreadable.Count == 0) || budgetTokens <= 0)
            return KnowledgeContextResult.None;

        var folderName = FolderName(folder);
        var names = files.Select(f => KnowledgeIndex.DisplayName(f.Path, folder)).ToList();
        var budgetChars = (long)budgetTokens * CharsPerToken;
        var allNames = names.Concat(unreadable).ToList();
        bool Mentions(string? text) => MentionsDocuments(text, folderName) || MentionsFileNames(text, allNames);
        var mentioned = Mentions(query) || (KnowledgeIndex.IsShortFollowUp(query) && Mentions(followUp));
        if (files.Count == 0)
        {
            // A message about something else gets nothing: even a bare file list pulls the model towards the documents.
            if (!mentioned)
                return KnowledgeContextResult.None;

            // Only files without text (photos without words, scans OCR could not read): the model can at least say they exist.
            var unreadableOnly = FitCatalog(folderName, names, unreadable, KnowledgeContextMode.Catalog, budgetChars);
            return unreadableOnly.Length == 0
                ? KnowledgeContextResult.None
                : new KnowledgeContextResult(KnowledgeContextMode.Catalog, unreadableOnly, "", Array.Empty<KnowledgeHit>(), 0);
        }

        var hits = index.Search(query, MaxCandidates, followUpContext: followUp);
        // A message about something else gets nothing: even a bare file list pulls the model towards the documents.
        if (hits.Count == 0 && !mentioned)
            return KnowledgeContextResult.None;

        // The catalogs differ only in their instruction; the text gets what the longest one leaves.
        var room = budgetChars - 2 - new[] { KnowledgeContextMode.WholeFolder, KnowledgeContextMode.Excerpts, KnowledgeContextMode.Overview }
            .Max(mode => FitCatalog(folderName, names, unreadable, mode, budgetChars).Length);

        var whole = TryFormatWholeFolder(files, folder, room);
        if (whole != null)
        {
            var catalog = FitCatalog(folderName, names, unreadable, KnowledgeContextMode.WholeFolder, budgetChars);
            return new KnowledgeContextResult(KnowledgeContextMode.WholeFolder, Combine(catalog, whole),
                $"Using all {Plural(files.Count, "document")} in '{folderName}'", Array.Empty<KnowledgeHit>(), files.Count);
        }

        if (hits.Count > 0)
        {
            var selected = SelectExcerpts(hits, minExcerpts, room - ExcerptsHeading.Length, folder).ToList();
            var text = FormatExcerpts(selected, folder);
            // The size estimate per excerpt is generous, but make sure.
            while (selected.Count > 0 && text.Length > room)
            {
                selected.RemoveAt(selected.Count - 1);
                text = FormatExcerpts(selected, folder);
            }

            if (selected.Count > 0)
            {
                var catalog = FitCatalog(folderName, names, unreadable, KnowledgeContextMode.Excerpts, budgetChars);
                var documents = selected.Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                return new KnowledgeContextResult(KnowledgeContextMode.Excerpts, Combine(catalog, text),
                    FormatNote(selected), selected, documents);
            }
        }
        else
        {
            var beginnings = SelectBeginnings(files, room - BeginningsHeading.Length, folder);
            var text = FormatBeginnings(beginnings, folder);
            while (beginnings.Count > 0 && text.Length > room)
            {
                beginnings.RemoveAt(beginnings.Count - 1);
                text = FormatBeginnings(beginnings, folder);
            }

            if (beginnings.Count > 0)
            {
                var catalog = FitCatalog(folderName, names, unreadable, KnowledgeContextMode.Overview, budgetChars);
                return new KnowledgeContextResult(KnowledgeContextMode.Overview, Combine(catalog, text),
                    $"Using the beginning of {Plural(beginnings.Count, "document")} in '{folderName}'", Array.Empty<KnowledgeHit>(), beginnings.Count);
            }
        }

        var fallback = FitCatalog(folderName, names, unreadable, KnowledgeContextMode.Catalog, budgetChars);
        return fallback.Length == 0
            ? KnowledgeContextResult.None
            : new KnowledgeContextResult(KnowledgeContextMode.Catalog, fallback, "", Array.Empty<KnowledgeHit>(), 0);
    }

    /// <summary>
    /// The best hits that fit <paramref name="roomChars"/>: the first <paramref name="minExcerpts"/>
    /// that fit, then more while they score at least <see cref="ExtraScoreFloor"/> of the best one.
    /// </summary>
    public static IReadOnlyList<KnowledgeHit> SelectExcerpts(IReadOnlyList<KnowledgeHit> hits, int minExcerpts, long roomChars, string? folder = null)
    {
        var selected = new List<KnowledgeHit>();
        if (hits == null || hits.Count == 0 || roomChars <= 0)
            return selected;

        minExcerpts = KnowledgeIndex.ClampMaxChunks(minExcerpts);
        var best = hits[0].Score;
        long used = 0;
        foreach (var hit in hits)
        {
            var extra = selected.Count >= minExcerpts;
            if (extra && hit.Score < best * ExtraScoreFloor)
                break;

            var cost = hit.Text.Length + KnowledgeIndex.DisplayName(hit.Path, folder).Length + HeaderOverheadChars;
            if (used + cost > roomChars)
            {
                // A long chunk that does not fit: a shorter one may still make up the minimum.
                if (extra)
                    break;
                continue;
            }

            selected.Add(hit);
            used += cost;
        }

        return selected;
    }

    // ==================== Catalog ====================

    /// <summary>
    /// The catalog: the folder's name, the relative paths of its indexed files (at most
    /// <paramref name="maxNames"/>, then "...and N more"), files that could not be read, and what the
    /// model should do with the text that follows (which depends on <paramref name="mode"/>).
    /// </summary>
    public static string FormatCatalog(string folderName, IReadOnlyList<string> files, IReadOnlyList<string>? unreadable,
        KnowledgeContextMode mode, int maxNames = MaxCatalogNames)
    {
        files ??= Array.Empty<string>();
        var sb = new StringBuilder();
        sb.AppendLine(ContextHeading);
        if (files.Count == 0)
        {
            sb.Append($"The owner's knowledge folder '{folderName}' has documents, but no text could be read from any of them yet, so their contents are unknown. ");
            sb.Append("If asked about them, say that they could not be read (the Files button in the Knowledge Folder panel shows why for each file), and do not invent contents.");
            if (unreadable is { Count: > 0 })
            {
                var listed = unreadable.Take(Math.Max(1, maxNames)).ToList();
                sb.AppendLine().Append("Unreadable: ").Append(string.Join(", ", listed));
                if (unreadable.Count > listed.Count)
                    sb.Append($" and {unreadable.Count - listed.Count:N0} more");
                sb.Append('.');
            }

            return sb.ToString().TrimEnd();
        }

        sb.Append($"These are the owner's documents in the knowledge folder '{folderName}' ({Plural(files.Count, "file")}).");
        sb.AppendLine(" " + Instruction(mode));

        maxNames = Math.Max(0, maxNames);
        foreach (var name in files.Take(maxNames))
            sb.Append("- ").AppendLine(name);
        if (files.Count > maxNames)
            sb.AppendLine(maxNames == 0 ? "(file list left out to save room)" : $"...and {files.Count - maxNames:N0} more");

        if (unreadable is { Count: > 0 })
        {
            var listed = unreadable.Take(MaxCatalogUnreadable).ToList();
            var line = "Also in the folder, but no text could be read from: " + string.Join(", ", listed);
            if (unreadable.Count > listed.Count)
                line += $" and {unreadable.Count - listed.Count:N0} more";
            sb.AppendLine(line + ".");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Instruction(KnowledgeContextMode mode) => (mode switch
    {
        KnowledgeContextMode.WholeFolder =>
            "The full text of every document is below. Use it when relevant and say which file you used; if it does not contain the answer, say so, and do not invent contents.",
        KnowledgeContextMode.Excerpts =>
            "Use the excerpts below when relevant; if they do not contain the answer, say which documents might, and do not invent contents.",
        KnowledgeContextMode.Overview =>
            "No passage matched the message's words, so the beginning of each document is below. If they do not contain the answer, say which documents might, and do not invent contents.",
        _ =>
            "No passage matched this message, so only the file names are listed. If the question is about these documents, say which ones might have the answer, and do not invent their contents."
    }) + " " + QuoteTheSource + " " + IgnoreWhenUnrelated;

    /// <summary>
    /// Added to every knowledge text with documents: a small model pairs a form's labels and amounts
    /// wrongly when it guesses, so it has to show the line it read and admit when the text is unclear.
    /// </summary>
    public const string QuoteTheSource =
        "When the answer uses a number, amount, date or name from a document, quote the exact line it came from and name the file. " +
        "Text from forms and tables can come out of order (in PDFs each line is one row of the page, with \" | \" between columns), " +
        "so if a label and its value are not clearly on the same line, say it is unclear instead of guessing. " +
        "If the owner says an answer is wrong, re-read the text and quote it rather than offering another guess.";

    /// <summary>Added to every knowledge text: the documents ride along on a guess, so the model must be free to ignore them.</summary>
    public const string IgnoreWhenUnrelated =
        "If the message is actually about something else, ignore these documents and do not mention them.";

    /// <summary>
    /// Added to the current message when the owner turned the knowledge folder off after this chat used it,
    /// so earlier answers about the documents do not keep steering the conversation back to them.
    /// </summary>
    public const string DocumentsOffNote =
        "[Note from the app: the owner has turned off access to their documents. Do not bring up the documents, " +
        "or anything taken from them earlier in this chat, unless the owner asks about them. Answer this message on its own.]";

    // Words in file names that say nothing about the contents ("Deed scan final.pdf" is about the deed).
    private static readonly HashSet<string> GenericNameWords = new(
        TextRanker.Tokenize("scan scanned copy final draft doc docs document file page pages img image photo pic picture new old version signed misc untitled"),
        StringComparer.Ordinal);

    /// <summary>
    /// True when the message uses a telling word from a file or folder name in the knowledge folder
    /// ("what does my deed say" with Deed.pdf, "insurance" with Insurance policy.pdf). Generic words such
    /// as "scan" or "final" and numbers do not count.
    /// </summary>
    public static bool MentionsFileNames(string? text, IEnumerable<string>? displayNames)
    {
        if (string.IsNullOrWhiteSpace(text) || displayNames == null)
            return false;

        var nameWords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in displayNames)
        {
            var withoutExtension = Path.ChangeExtension(name ?? "", null) ?? "";
            foreach (var word in TextRanker.Tokenize(withoutExtension))
            {
                if (word.Length >= 3 && !word.All(char.IsDigit) && !GenericNameWords.Contains(word))
                    nameWords.Add(word);
            }
        }

        return nameWords.Count > 0 && TextRanker.Tokenize(text).Any(nameWords.Contains);
    }

    // Fewer names until the catalog takes at most MaxCatalogShare of the budget; "" when not even that fits.
    private static string FitCatalog(string folderName, IReadOnlyList<string> files, IReadOnlyList<string> unreadable,
        KnowledgeContextMode mode, long budgetChars)
    {
        var limit = (long)(budgetChars * MaxCatalogShare);
        foreach (var maxNames in new[] { MaxCatalogNames, 30, 15, 5, 0 })
        {
            var catalog = FormatCatalog(folderName, files, maxNames == 0 ? Array.Empty<string>() : unreadable, mode, maxNames);
            if (catalog.Length <= limit)
                return catalog;
        }

        return "";
    }

    // ==================== Content ====================

    // All indexed text, file by file in folder order, or null when it does not fit roomChars.
    private static string? TryFormatWholeFolder(IReadOnlyList<KnowledgeFileRecord> files, string folder, long roomChars)
    {
        if (roomChars <= 0)
            return null;

        // Cheap check first: even with every overlap removed, does it fit?
        long raw = 0, overlaps = 0;
        foreach (var file in files)
        {
            raw += file.Chunks.Sum(c => (long)c.Length + 1) + KnowledgeIndex.DisplayName(file.Path, folder).Length + HeaderOverheadChars;
            overlaps += Math.Max(0, file.Chunks.Count - 1);
        }
        if (raw - overlaps * KnowledgeIndex.DefaultOverlapChars > roomChars)
            return null;

        var sb = new StringBuilder();
        sb.Append(string.Format(WholeFolderHeading, Plural(files.Count, "document")));
        for (var i = 0; i < files.Count; i++)
        {
            sb.AppendLine().AppendLine();
            sb.Append('[').Append(i + 1).Append("] ").AppendLine(KnowledgeIndex.DisplayName(files[i].Path, folder));
            sb.Append(JoinChunks(files[i].Chunks));
            if (sb.Length > roomChars)
                return null;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Excerpts grouped by file, best file first; within a file in document order, with neighbouring
    /// chunks joined into one passage and "[...]" between passages that are apart.
    /// </summary>
    public static string FormatExcerpts(IReadOnlyList<KnowledgeHit> hits, string? folder = null)
    {
        if (hits == null || hits.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.Append(ExcerptsHeading);
        var groups = hits.GroupBy(h => h.Path, StringComparer.OrdinalIgnoreCase).ToList();
        for (var g = 0; g < groups.Count; g++)
        {
            sb.AppendLine().AppendLine();
            sb.Append('[').Append(g + 1).Append("] ").AppendLine(KnowledgeIndex.DisplayName(groups[g].Key, folder));

            var chunks = groups[g].GroupBy(h => h.ChunkIndex).Select(c => c.First()).OrderBy(h => h.ChunkIndex).ToList();
            var run = new List<string>();
            for (var i = 0; i < chunks.Count; i++)
            {
                if (i > 0 && chunks[i].ChunkIndex != chunks[i - 1].ChunkIndex + 1)
                {
                    sb.Append(JoinChunks(run)).Append("\n[...]\n");
                    run.Clear();
                }
                run.Add(chunks[i].Text);
            }
            sb.Append(JoinChunks(run));
        }

        return sb.ToString();
    }

    // The first chunk of each file, in folder order, while they fit.
    private static List<KnowledgeFileRecord> SelectBeginnings(IReadOnlyList<KnowledgeFileRecord> files, long roomChars, string folder)
    {
        var selected = new List<KnowledgeFileRecord>();
        long used = 0;
        foreach (var file in files)
        {
            var cost = file.Chunks[0].Length + KnowledgeIndex.DisplayName(file.Path, folder).Length + HeaderOverheadChars;
            if (used + cost > roomChars)
                continue;
            selected.Add(file);
            used += cost;
        }

        return selected;
    }

    private static string FormatBeginnings(IReadOnlyList<KnowledgeFileRecord> files, string folder)
    {
        var sb = new StringBuilder();
        sb.Append(BeginningsHeading);
        for (var i = 0; i < files.Count; i++)
        {
            sb.AppendLine().AppendLine();
            sb.Append('[').Append(i + 1).Append("] ").AppendLine(KnowledgeIndex.DisplayName(files[i].Path, folder));
            sb.Append(files[i].Chunks[0].Trim());
            if (files[i].Chunks.Count > 1)
                sb.Append("\n[...]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Consecutive chunks of one file as one text: the overlap each chunk shares with the one before
    /// (see <see cref="KnowledgeIndex.ChunkText"/>) is left out, so no passage appears twice.
    /// </summary>
    public static string JoinChunks(IEnumerable<string>? chunks)
    {
        var sb = new StringBuilder();
        string? previous = null;
        foreach (var raw in chunks ?? Enumerable.Empty<string>())
        {
            var chunk = (raw ?? "").Trim();
            if (chunk.Length == 0)
                continue;

            if (previous == null)
            {
                sb.Append(chunk);
            }
            else
            {
                var overlap = OverlapLength(previous, chunk);
                if (overlap > 0)
                    sb.Append(chunk, overlap, chunk.Length - overlap);
                else
                    sb.Append('\n').Append(chunk);
            }

            previous = chunk;
        }

        return sb.ToString();
    }

    // The longest start of next that is also the end of previous, or 0 when that is too short to be the overlap.
    private static int OverlapLength(string previous, string next)
    {
        var max = Math.Min(Math.Min(previous.Length, next.Length), MaxJoinOverlapChars);
        for (var length = max; length >= MinJoinOverlapChars; length--)
        {
            if (previous.AsSpan(previous.Length - length).SequenceEqual(next.AsSpan(0, length)))
                return length;
        }

        return 0;
    }

    // ==================== Notes and names ====================

    /// <summary>The chat note for excerpts: "Using 3 excerpts from: a.pdf, b.md", naming at most three files.</summary>
    public static string FormatNote(IReadOnlyList<KnowledgeHit> hits)
    {
        if (hits == null || hits.Count == 0)
            return "";

        var files = hits.Select(h => h.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var named = string.Join(", ", files.Take(MaxNoteFiles));
        if (files.Count > MaxNoteFiles)
            named += $" and {files.Count - MaxNoteFiles} more";
        return $"Using {Plural(hits.Count, "excerpt")} from: {named}";
    }

    /// <summary>The folder's own name ("townhouse"), or the path itself for a drive root.</summary>
    public static string FolderName(string? folder)
    {
        var root = KnowledgeIndex.NormalizeFolder(folder);
        var name = Path.GetFileName(root);
        return string.IsNullOrEmpty(name) ? root : name;
    }

    /// <summary>True when the message talks about the documents ("my files", "the PDF") or names the folder.</summary>
    public static bool MentionsDocuments(string? text, string? folderName)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        if (DocumentWords.IsMatch(text))
            return true;

        var folderWords = TextRanker.Tokenize(folderName);
        if (folderWords.Count == 0)
            return false;

        var words = new HashSet<string>(TextRanker.Tokenize(text), StringComparer.Ordinal);
        return folderWords.All(words.Contains);
    }

    private static string Combine(string catalog, string content) =>
        catalog.Length == 0 ? content : content.Length == 0 ? catalog : catalog + "\n\n" + content;

    private static string Plural(int count, string noun) => $"{count:N0} {(count == 1 ? noun : noun + "s")}";
}
