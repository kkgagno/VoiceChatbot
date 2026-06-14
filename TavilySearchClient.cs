using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

// TavilySearchClient - Web search via Tavily API
// https://tavily.com
public class TavilySearchClient : IDisposable
{
    private readonly HttpClient _http;
    private const string ApiUrl = "https://api.tavily.com/search";

    public string ApiKey { get; set; } = "";

    public TavilySearchClient(string apiKey = "")
    {
        ApiKey = apiKey;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task SearchAsync(
        string query,
        int maxResults,
        Action onStarted,
        Action onSuccess,
        Action onFailed,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            onFailed();
            return;
        }

        onStarted();

        try
        {
            var body = new Dictionary<string, object>
            {
                ["api_key"] = ApiKey,
                ["query"] = BuildSearchQuery(query),
                ["search_depth"] = ShouldUseAdvancedSearch(query) ? "advanced" : "basic",
                ["include_answer"] = true,
                ["max_results"] = maxResults,
                ["include_images"] = false
            };

            var preferredDomains = GetPreferredDomains(query);
            if (preferredDomains.Count > 0)
                body["include_domains"] = preferredDomains;

            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(ApiUrl, content, ct);
            resp.EnsureSuccessStatusCode();

            var respJson = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(respJson);
            var root = doc.RootElement;

            string answer = "";
            if (root.TryGetProperty("answer", out var ansProp))
                answer = ansProp.GetString() ?? "";

            var results = new List<string>();
            if (root.TryGetProperty("results", out var resArr))
            {
                foreach (var r in resArr.EnumerateArray())
                {
                    var sb = new StringBuilder();
                    if (r.TryGetProperty("title", out var t))
                        sb.AppendLine($"Title: {t.GetString()}");
                    if (r.TryGetProperty("content", out var c))
                        sb.AppendLine($"Content: {c.GetString()}");
                    if (r.TryGetProperty("url", out var u))
                        sb.Append($"URL: {u.GetString()}");
                    results.Add(sb.ToString().Trim());
                }
            }

            _lastSearchAnswer = answer;
            _lastSearchResults = results;
            onSuccess();
        }
        catch
        {
            onFailed();
        }
    }

    private string _lastSearchAnswer = "";
    private List<string> _lastSearchResults = new();

    // Formats the last search into a single string ready to send to Ollama.
    // Call only after SearchAsync onSuccess.
    public string BuildSearchContext(string query)
    {
        if (string.IsNullOrWhiteSpace(_lastSearchAnswer) && _lastSearchResults.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine($"Current web search context retrieved on {DateTime.Now:yyyy-MM-dd HH:mm} local time.");
        sb.AppendLine($"Search query: '{query}'");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(_lastSearchAnswer))
        {
            sb.AppendLine("Search answer:");
            sb.AppendLine(_lastSearchAnswer);
            sb.AppendLine();
        }

        if (_lastSearchResults.Count > 0)
        {
            sb.AppendLine("Sources:");
            for (int i = 0; i < _lastSearchResults.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {_lastSearchResults[i]}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Answer rules:");
        sb.AppendLine("Use this web context as current evidence. If it conflicts with your prior knowledge, trust the web context.");
        sb.AppendLine("Do not call the web results fake unless the sources themselves say they are fake.");
        sb.AppendLine("Mention the source names or URLs when they matter. Keep the answer concise because it will be spoken aloud.");
        return sb.ToString().Trim();
    }

    public async Task<string> SearchAndBuildContextAsync(string query, int maxResults = 5, CancellationToken ct = default)
    {
        var searchSucceeded = false;
        await SearchAsync(
            query,
            maxResults,
            onStarted: () => { },
            onSuccess: () => searchSucceeded = true,
            onFailed: () => searchSucceeded = false,
            ct: ct);

        return searchSucceeded ? BuildSearchContext(query) : "";
    }

    private static string BuildSearchQuery(string query)
    {
        var q = query.Trim();
        if (LooksLikeOpenAiQuery(q) && !q.Contains("openai.com", StringComparison.OrdinalIgnoreCase))
            return $"{q} site:openai.com OR site:help.openai.com";
        return q;
    }

    private static bool ShouldUseAdvancedSearch(string query)
    {
        return query.Contains("latest", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("current", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("newest", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("version", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("today", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("release", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> GetPreferredDomains(string query)
    {
        return LooksLikeOpenAiQuery(query)
            ? new List<string> { "openai.com", "help.openai.com" }
            : new List<string>();
    }

    private static bool LooksLikeOpenAiQuery(string query)
    {
        return query.Contains("chatgpt", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("openai", StringComparison.OrdinalIgnoreCase) ||
               query.Contains("gpt", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _http.Dispose();
}
