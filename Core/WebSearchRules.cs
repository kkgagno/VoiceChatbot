using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Query and error handling for the Tavily web search. A search is only limited to particular
/// sites when the user names them: a "site:example.com" operator, or "on/at/from example.com".
/// Mentioning a product (gpt-oss, GPTQ, ChatGPT) never limits the search to a vendor's site.
/// </summary>
public static class WebSearchRules
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private const string Domain = @"(?:www\.)?(?<d>(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,})";

    // "site:openai.com", but not the exclusion "-site:reddit.com".
    private static readonly Regex SiteOperator = new(@"(?<![\w-])site:\s*(?:https?://)?" + Domain + @"\b", Options);

    // "on reddit.com", "from bbc.co.uk". Only common web endings, so "from node.js" or
    // "migrating from asp.net" is not read as a site.
    private static readonly Regex NamedSite = new(
        @"\b(?:on|at|from)\s+(?:the\s+)?(?:https?://)?" + Domain + @"(?![\w-]|\.\w)",
        Options);

    private static readonly HashSet<string> NamedSiteEndings = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "org", "edu", "gov", "uk", "ca", "au", "de", "fr", "jp", "ai", "dev", "info", "news", "tv", "wiki"
    };

    /// <summary>The domains the user explicitly asked to search, without "www.", in order of appearance.</summary>
    public static IReadOnlyList<string> GetExplicitDomains(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        var domains = new List<string>();
        foreach (Match match in SiteOperator.Matches(query))
            Add(domains, match.Groups["d"].Value);

        foreach (Match match in NamedSite.Matches(query))
        {
            var domain = match.Groups["d"].Value;
            var ending = domain[(domain.LastIndexOf('.') + 1)..];
            if (NamedSiteEndings.Contains(ending))
                Add(domains, domain);
        }

        return domains;
    }

    /// <summary>A message for a failed Tavily call that says what went wrong (bad key, quota, rate limit).</summary>
    public static string DescribeHttpError(int statusCode, string? responseBody)
    {
        var detail = ExtractErrorDetail(responseBody);
        var reason = statusCode switch
        {
            400 => "Tavily rejected the search request",
            401 => "Tavily rejected the API key. Check the Tavily API key in Settings",
            403 => "Tavily refused the request. Check the Tavily API key and plan",
            429 => "Tavily rate limit reached. Wait a moment and try again",
            432 => "Tavily plan usage limit reached. Upgrade the plan or wait for the credits to reset",
            433 => "Tavily pay-as-you-go spending limit reached. Raise the limit in the Tavily dashboard",
            >= 500 => "Tavily had a server error. Try again later",
            _ => "Tavily returned an error"
        };

        var message = $"Web search failed: {reason} (HTTP {statusCode})";
        return string.IsNullOrWhiteSpace(detail) ? message + "." : $"{message}: {detail}";
    }

    private static string ExtractErrorDetail(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        try
        {
            using var doc = JsonDocument.Parse(body);
            var text = FindErrorText(doc.RootElement);
            if (!string.IsNullOrWhiteSpace(text))
                return Shorten(text);
        }
        catch (JsonException)
        {
            // Not JSON: fall back to the raw body.
        }

        return Shorten(Regex.Replace(body, @"\s+", " ").Trim());
    }

    // Tavily answers {"detail": {"error": "..."}}; other gateways use "detail", "error" or "message".
    private static string FindErrorText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString() ?? "";
        if (element.ValueKind != JsonValueKind.Object)
            return "";

        foreach (var name in new[] { "detail", "error", "message" })
        {
            if (element.TryGetProperty(name, out var value))
            {
                var text = FindErrorText(value);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }

        return "";
    }

    private static string Shorten(string text) =>
        text.Length <= 240 ? text : text[..240].TrimEnd() + "...";

    private static void Add(List<string> domains, string domain)
    {
        domain = domain.Trim('.').ToLowerInvariant();
        if (domain.StartsWith("www.", StringComparison.Ordinal))
            domain = domain[4..];
        if (domain.Length > 0 && !domains.Contains(domain))
            domains.Add(domain);
    }
}
