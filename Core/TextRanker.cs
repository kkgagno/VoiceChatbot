using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VoiceChatbot;

/// <summary>
/// One scored document from <see cref="TextRanker.Rank"/>; Index is its position in the input list,
/// Coverage is <see cref="TextRanker.Coverage"/> for the same query and MatchedTerms the number of
/// distinct query words it contains.
/// </summary>
public readonly record struct RankedDocument(int Index, double Score, double Coverage = 0, int MatchedTerms = 0);

/// <summary>
/// Small in-memory BM25 keyword ranker for document lists (memories, notes, knowledge-folder chunks).
/// Text is lowercased, split on anything that is not a letter or digit, English stopwords are
/// dropped and words get a light suffix stemming so "batteries"/"battery" or "coding"/"code" match.
/// Build it once per document list; ranking is cheap and thread-safe after construction.
/// </summary>
public sealed class TextRanker
{
    // Standard BM25 constants: k1 controls term-frequency saturation, b how much long documents are penalised.
    public const double DefaultK1 = 1.2;
    public const double DefaultB = 0.75;

    private readonly List<Dictionary<string, int>> _termCounts = new();
    private readonly List<int> _lengths = new();
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly double _averageLength;
    private readonly double _k1;
    private readonly double _b;

    public TextRanker(IEnumerable<string?> documents, double k1 = DefaultK1, double b = DefaultB)
    {
        _k1 = k1;
        _b = b;

        // One string instance per distinct word, shared by all documents: a large corpus (thousands of
        // knowledge-folder chunks) repeats the same words over and over.
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (var document in documents ?? Enumerable.Empty<string?>())
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var tokens = Tokenize(document);
            foreach (var token in tokens)
            {
                if (!words.TryGetValue(token, out var word))
                {
                    words.Add(token);
                    word = token;
                }

                counts[word] = counts.TryGetValue(word, out var n) ? n + 1 : 1;
            }

            counts.TrimExcess();

            foreach (var term in counts.Keys)
                _documentFrequency[term] = _documentFrequency.TryGetValue(term, out var df) ? df + 1 : 1;

            _termCounts.Add(counts);
            _lengths.Add(tokens.Count);
        }

        _averageLength = _lengths.Count == 0 ? 0 : _lengths.Average();
    }

    public int Count => _termCounts.Count;

    /// <summary>BM25 score of one document for the query (0 when nothing matches).</summary>
    public double Score(string? query, int index)
    {
        if (index < 0 || index >= Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        return ScoreTerms(QueryTerms(query), index);
    }

    /// <summary>
    /// Share (0..1) of the query's idf weight that the document contains, ignoring how often each
    /// word occurs. A query word the corpus never uses weighs as much as the rarest word in it, so
    /// "a joke about solar panels" only partly matches a document about solar panels. Unlike a raw
    /// BM25 score this is comparable across queries and corpus sizes, so it works as a relevance cut-off.
    /// </summary>
    public double Coverage(string? query, int index)
    {
        if (index < 0 || index >= Count)
            throw new ArgumentOutOfRangeException(nameof(index));

        var terms = QueryTerms(query);
        return CoverageOfTerms(terms, QueryWeight(terms), index).Coverage;
    }

    /// <summary>
    /// Documents that match at least one query term, best first. Equal scores keep input order.
    /// </summary>
    public IReadOnlyList<RankedDocument> Rank(string? query, int top = int.MaxValue)
    {
        var terms = QueryTerms(query);
        if (terms.Count == 0 || Count == 0 || top <= 0)
            return Array.Empty<RankedDocument>();

        var queryWeight = QueryWeight(terms);
        var results = new List<RankedDocument>();
        for (var i = 0; i < Count; i++)
        {
            var score = ScoreTerms(terms, i);
            if (score <= 0)
                continue;

            var (coverage, matchedTerms) = CoverageOfTerms(terms, queryWeight, i);
            results.Add(new RankedDocument(i, score, coverage, matchedTerms));
        }

        return results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Index)
            .Take(top)
            .ToList();
    }

    /// <summary>The distinct search words of a query (as <see cref="Rank"/> uses them).</summary>
    public static IReadOnlyCollection<string> QueryTermsOf(string? query) => QueryTerms(query);

    /// <summary>
    /// Share (0..1) of the query words' coverage weight that <paramref name="words"/> contains, for
    /// example the words of a file's name; weighted like <see cref="Coverage"/>, so a rare query word
    /// in the name counts for more than a common one.
    /// </summary>
    public double WeightShare(IReadOnlyCollection<string>? queryTerms, IReadOnlySet<string>? words)
    {
        if (queryTerms == null || queryTerms.Count == 0 || words == null || words.Count == 0)
            return 0;

        double total = 0, matched = 0;
        foreach (var term in queryTerms)
        {
            var weight = CoverageWeight(term);
            total += weight;
            if (words.Contains(term))
                matched += weight;
        }

        return total > 0 ? Math.Min(1, matched / total) : 0;
    }

    private double ScoreTerms(IReadOnlyCollection<string> terms, int index)
    {
        var counts = _termCounts[index];
        if (counts.Count == 0)
            return 0;

        // Length normalisation; guard the all-empty corpus where the average is 0.
        var lengthRatio = _averageLength > 0 ? _lengths[index] / _averageLength : 1;
        var norm = _k1 * (1 - _b + _b * lengthRatio);
        double score = 0;
        foreach (var term in terms)
        {
            if (!counts.TryGetValue(term, out var tf))
                continue;

            score += Idf(term) * (tf * (_k1 + 1)) / (tf + norm);
        }

        return score;
    }

    private double QueryWeight(IEnumerable<string> terms) => terms.Sum(CoverageWeight);

    private (double Coverage, int MatchedTerms) CoverageOfTerms(IEnumerable<string> terms, double queryWeight, int index)
    {
        var counts = _termCounts[index];
        double matched = 0;
        var matchedTerms = 0;
        foreach (var term in terms)
        {
            if (!counts.ContainsKey(term))
                continue;

            matched += CoverageWeight(term);
            matchedTerms++;
        }

        return (queryWeight > 0 ? Math.Min(1, matched / queryWeight) : 0, matchedTerms);
    }

    // Idf, except that a word no document contains counts as if one did: missing a word the corpus
    // never uses should not weigh much more than missing its rarest word, or small corpora match nothing.
    private double CoverageWeight(string term)
    {
        var df = _documentFrequency.TryGetValue(term, out var n) ? n : 0;
        return Math.Log(1 + (Count - Math.Max(df, 1) + 0.5) / (Math.Max(df, 1) + 0.5));
    }

    // BM25+ style idf that never goes negative, so a word found in every document still counts a little.
    private double Idf(string term)
    {
        var df = _documentFrequency.TryGetValue(term, out var n) ? n : 0;
        return Math.Log(1 + (Count - df + 0.5) / (df + 0.5));
    }

    // Repeating a word in the query should not count it twice.
    private static HashSet<string> QueryTerms(string? query) => new(Tokenize(query), StringComparer.Ordinal);

    /// <summary>
    /// Lowercased, stemmed content words of the text in order (stopwords and single letters removed).
    /// </summary>
    public static List<string> Tokenize(string? text)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return tokens;

        var word = new StringBuilder();
        foreach (var raw in text)
        {
            // Apostrophes are dropped inside words so "don't" -> "dont" and "user's" -> "users".
            if (raw is '\'' or '’')
                continue;

            if (char.IsLetterOrDigit(raw))
            {
                word.Append(char.ToLowerInvariant(raw));
                continue;
            }

            AddToken(tokens, word);
        }

        AddToken(tokens, word);
        return tokens;
    }

    private static void AddToken(List<string> tokens, StringBuilder word)
    {
        if (word.Length == 0)
            return;

        var token = word.ToString();
        word.Clear();
        if (token.Length == 1 && !char.IsDigit(token[0]))
            return;
        if (Stopwords.Contains(token))
            return;

        tokens.Add(Stem(token));
    }

    public static bool IsStopword(string? word) =>
        !string.IsNullOrEmpty(word) && Stopwords.Contains(word.ToLowerInvariant().Replace("'", "").Replace("’", ""));

    /// <summary>
    /// Light English suffix stemmer (plural -s/-es/-ies, -ing, -ed, final -e and -y). It is not a full
    /// Porter stemmer: it only has to map common variants of a word onto the same key.
    /// </summary>
    public static string Stem(string word)
    {
        if (string.IsNullOrEmpty(word))
            return "";

        var w = word.ToLowerInvariant();
        if (w.Length <= 3 || w.Any(char.IsDigit))
            return w;

        // Plurals.
        if (w.EndsWith("ies", StringComparison.Ordinal) && w.Length > 4)
            w = w[..^3] + "y";
        else if (w.EndsWith("sses", StringComparison.Ordinal))
            w = w[..^2];
        else if (w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal) && !w.EndsWith("is", StringComparison.Ordinal) &&
                 // "status"/"virus" keep their s, acronyms such as "gpus"/"cpus" lose it.
                 !(w.EndsWith("us", StringComparison.Ordinal) && w[..^2].Any(IsVowel)))
            w = w[..^1];

        // Verb endings.
        if (w.EndsWith("ied", StringComparison.Ordinal) && w.Length > 4)
            w = w[..^3] + "y";
        else if (w.EndsWith("ing", StringComparison.Ordinal) && w.Length > 5)
            w = Undouble(w[..^3]);
        else if (w.EndsWith("ed", StringComparison.Ordinal) && w.Length > 4)
            w = Undouble(w[..^2]);

        // "make"/"making"/"makes" -> "mak"; "battery"/"batteries" -> "batteri".
        if (w.Length > 3 && w.EndsWith('e'))
            w = w[..^1];
        else if (w.Length > 3 && w.EndsWith('y') && !IsVowel(w[^2]))
            w = w[..^1] + "i";

        return w;
    }

    // "running" -> "runn" -> "run", "stopped" -> "stopp" -> "stop"; keeps "fall", "miss", "buzz", "add".
    private static string Undouble(string stem)
    {
        if (stem.Length >= 4 && stem[^1] == stem[^2] && !IsVowel(stem[^1]) && stem[^1] is not ('l' or 's' or 'z'))
            return stem[..^1];
        return stem;
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "a", "about", "above", "after", "again", "against", "all", "also", "am", "an", "and", "any", "are",
        "arent", "as", "at", "be", "because", "been", "before", "being", "below", "between", "both", "but",
        "by", "can", "cant", "could", "couldnt", "did", "didnt", "do", "does", "doesnt", "doing", "dont",
        "down", "during", "each", "else", "even", "ever", "few", "for", "from", "further", "get", "gets",
        "got", "had", "hadnt", "has", "hasnt", "have", "havent", "having", "he", "hed", "hell", "her", "here",
        "heres", "hers", "herself", "hes", "him", "himself", "his", "how", "hows", "i", "id", "if", "ill",
        "im", "in", "into", "is", "isnt", "it", "its", "itself", "ive", "just", "lets", "like", "me", "might",
        "more", "most", "much", "must", "mustnt", "my", "myself", "no", "nor", "not", "now", "of", "off", "ok",
        "okay", "on", "once", "only", "or", "other", "ought", "our", "ours", "ourselves", "out", "over", "own",
        "please", "same", "shall", "shant", "she", "shed", "shell", "shes", "should", "shouldnt", "so", "some",
        "such", "than", "that", "thats", "the", "their", "theirs", "them", "themselves", "then", "there",
        "theres", "these", "they", "theyd", "theyll", "theyre", "theyve", "this", "those", "through", "to",
        "too", "under", "until", "up", "us", "very", "was", "wasnt", "we", "wed", "well", "were", "werent",
        "weve", "what", "whats", "when", "whens", "where", "wheres", "which", "while", "who", "whom", "whos",
        "why", "whys", "will", "with", "wont", "would", "wouldnt", "yes", "yet", "you", "youd", "youll",
        "your", "youre", "yours", "yourself", "yourselves", "youve"
    };
}
