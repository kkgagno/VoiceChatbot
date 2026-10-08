using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Picks which saved conversation memories go into the system prompt.
/// "Relevant": the best BM25 matches for the current user text (up to maxItems) plus the most
/// recent memory; with no usable text, the most recent maxItems. "All": every memory (old behaviour).
/// </summary>
public static class MemorySelector
{
    public const string ModeRelevant = "Relevant";
    public const string ModeAll = "All";
    public static readonly string[] Modes = { ModeRelevant, ModeAll };

    public const int DefaultMaxItems = 4;
    public const int MinItems = 1;
    public const int MaxItems = 20;

    // Long pastes are ranked on their first part only; that is plenty to find matching memories.
    private const int MaxQueryChars = 4000;

    // "What do you remember about me?" has no topic words worth matching, so it also gets recent memories.
    private static readonly Regex RecallPattern = new(
        @"\b(remember|recall|memory|memories|last (time|conversation|chat|session)|previous (conversation|chat|session)s?|we (talked|spoke|chatted|discussed)|know about me)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string NormalizeMode(string? mode) =>
        Modes.FirstOrDefault(m => string.Equals(m, mode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? ModeRelevant;

    public static int ClampMaxItems(int maxItems) => Math.Clamp(maxItems, MinItems, MaxItems);

    public static bool IsRecallQuery(string? text) =>
        !string.IsNullOrWhiteSpace(text) && RecallPattern.IsMatch(text);

    /// <summary>
    /// Indices of the memories to include, in ascending (input) order. <paramref name="memoryTexts"/>
    /// must be oldest first, so the last item is the most recent memory.
    /// </summary>
    public static List<int> SelectIndices(IReadOnlyList<string?> memoryTexts, string? currentText, string? mode, int maxItems)
    {
        if (memoryTexts == null || memoryTexts.Count == 0)
            return new List<int>();

        var count = memoryTexts.Count;

        if (NormalizeMode(mode) == ModeAll)
            return Enumerable.Range(0, count).ToList();

        var limit = ClampMaxItems(maxItems);
        var newest = count - 1;
        var query = currentText ?? "";
        if (query.Length > MaxQueryChars)
            query = query[..MaxQueryChars];

        // No text (or only stopwords such as "ok then"): fall back to the most recent memories.
        if (TextRanker.Tokenize(query).Count == 0)
            return Enumerable.Range(Math.Max(0, count - limit), Math.Min(limit, count)).ToList();

        var ranker = new TextRanker(memoryTexts);
        var selected = new HashSet<int>(ranker.Rank(query)
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Index) // newer memory wins a tie
            .Take(limit)
            .Select(r => r.Index));

        if (IsRecallQuery(query))
        {
            for (var i = newest; i >= 0 && selected.Count < limit; i--)
                selected.Add(i);
        }

        // The newest memory is always included so the assistant knows what happened last time.
        selected.Add(newest);
        return selected.OrderBy(i => i).ToList();
    }

    /// <summary>The selected items themselves, oldest first.</summary>
    public static List<T> Select<T>(IReadOnlyList<T> memories, Func<T, string?> text, string? currentText, string? mode, int maxItems)
    {
        if (memories == null || memories.Count == 0)
            return new List<T>();

        var texts = memories.Select(text).ToList();
        return SelectIndices(texts, currentText, mode, maxItems).Select(i => memories[i]).ToList();
    }
}
