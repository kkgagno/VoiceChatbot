using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

public sealed class StockQuoteService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "A", "AI", "AM", "AN", "AND", "ARE", "ASK", "AT", "BE", "BUY", "CAN", "DO", "FOR", "GET",
        "GO", "HAS", "HOW", "I", "IN", "IS", "IT", "ME", "MY", "NOW", "OF", "ON", "OR", "PRICE",
        "QUOTE", "SELL", "STOCK", "THE", "TO", "UP", "US", "USD", "WHAT", "WHATS", "WHEN", "WHY"
    };

    public static IReadOnlyList<string> ExtractLikelyTickers(string text)
    {
        text = StripAttachedDocumentContext(text);
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var lower = text.ToLowerInvariant();
        lower = Regex.Replace(lower, @"\bn\s*v\s*d\s*a\b", "nvda");
        lower = Regex.Replace(lower, @"\ba\s*m\s*d\b", "amd");
        var looksFinancial = lower.Contains("stock") ||
                             lower.Contains("share price") ||
                             lower.Contains("shares") ||
                             lower.Contains("quote") ||
                             lower.Contains("ticker") ||
                             lower.Contains("market cap") ||
                             lower.Contains("price of") ||
                             lower.Contains("trading at") ||
                             lower.Contains("trading around") ||
                             lower.Contains("amd") ||
                             lower.Contains("advanced micro devices") ||
                             lower.Contains("nvda") ||
                             lower.Contains("nvidia");
        if (!looksFinancial)
            return Array.Empty<string>();

        var tickers = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(text.ToUpperInvariant(), @"(?<![A-Z])\$?([A-Z]{1,5})(?![A-Z])"))
        {
            var ticker = match.Groups[1].Value;
            if (!CommonWords.Contains(ticker))
                tickers.Add(ticker);
        }

        if (lower.Contains("amd")) tickers.Add("AMD");
        if (lower.Contains("advanced micro devices")) tickers.Add("AMD");
        if (lower.Contains("nvidia") || lower.Contains("nvda")) tickers.Add("NVDA");

        return tickers.Take(6).ToList();
    }

    public async Task<string> BuildQuoteContextAsync(string userText, CancellationToken ct = default)
    {
        var quotes = await GetQuotesForTextAsync(userText, ct).ConfigureAwait(false);
        if (quotes.Count == 0)
            return "";

        return "Current stock quote context. Use this instead of model memory for prices. These quotes may be delayed and are not financial advice.\n" +
               string.Join("\n", quotes.Select(FormatQuoteLine));
    }

    public async Task<string> BuildDirectQuoteAnswerAsync(string userText, CancellationToken ct = default)
    {
        var quotes = await GetQuotesForTextAsync(userText, ct).ConfigureAwait(false);
        if (quotes.Count == 0)
            return "";

        var lines = quotes.Select(q =>
        {
            var change = q.Change.HasValue && q.ChangePercent.HasValue
                ? $"{q.Change.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)}, {q.ChangePercent.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)}%"
                : "change unavailable";
            return $"{q.Symbol} is at ${q.Price.ToString("0.00", CultureInfo.InvariantCulture)}, {change}, as of {q.TimestampLocal:t}.";
        });

        return string.Join("\n", lines) + "\nQuotes may be delayed and are not financial advice.";
    }

    public static bool IsDirectQuoteRequest(string text)
    {
        text = StripAttachedDocumentContext(text);
        if (ExtractLikelyTickers(text).Count == 0)
            return false;

        var lower = text.ToLowerInvariant();
        return lower.Contains("right now") ||
               lower.Contains("currently") ||
               lower.Contains("current") ||
               lower.Contains("trading at") ||
               lower.Contains("stock price") ||
               lower.Contains("share price") ||
               lower.Contains("quote") ||
               Regex.IsMatch(lower, @"\bwhat(?:'s| is|s)?\b.*\b(at|price|trading)\b") ||
               Regex.IsMatch(lower, @"\bhow much\b.*\b(amd|nvda|nvidia|stock|shares?)\b");
    }

    private async Task<List<StockQuote>> GetQuotesForTextAsync(string userText, CancellationToken ct)
    {
        userText = StripAttachedDocumentContext(userText);
        var tickers = ExtractLikelyTickers(userText);
        if (tickers.Count == 0)
            return new List<StockQuote>();

        var quotes = new List<StockQuote>();
        foreach (var ticker in tickers)
        {
            var quote = await GetQuoteAsync(ticker, ct).ConfigureAwait(false);
            if (quote != null)
                quotes.Add(quote);
        }

        return quotes;
    }

    private static string StripAttachedDocumentContext(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var markerIndex = text.IndexOf("Attached document:", StringComparison.OrdinalIgnoreCase);
        return markerIndex < 0
            ? text
            : text[..markerIndex].Trim();
    }

    private static string FormatQuoteLine(StockQuote q)
    {
        var change = q.Change.HasValue && q.ChangePercent.HasValue
            ? $"{q.Change.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)} ({q.ChangePercent.Value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture)}%)"
            : "change unavailable";
        return $"{q.Symbol}: ${q.Price.ToString("0.00", CultureInfo.InvariantCulture)} USD, {change}, as of {q.TimestampLocal:g}. Source: Yahoo Finance chart endpoint.";
    }

    private async Task<StockQuote?> GetQuoteAsync(string ticker, CancellationToken ct)
    {
        try
        {
            var url = $"https://query1.finance.yahoo.com/v8/finance/chart/{Uri.EscapeDataString(ticker)}?range=1d&interval=1m";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 VoiceChatbot/1.0");
            var json = await (await _http.SendAsync(request, ct).ConfigureAwait(false)).Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.GetProperty("chart").GetProperty("result")[0];
            var meta = result.GetProperty("meta");
            var price = meta.TryGetProperty("regularMarketPrice", out var priceEl) ? priceEl.GetDouble() : 0;
            if (price <= 0)
                return null;

            var previousClose = meta.TryGetProperty("chartPreviousClose", out var prevEl) ? prevEl.GetDouble() : 0;
            double? change = previousClose > 0 ? price - previousClose : null;
            double? changePercent = previousClose > 0 ? (price - previousClose) / previousClose * 100.0 : null;
            var exchangeTz = meta.TryGetProperty("exchangeTimezoneName", out var tzEl) ? tzEl.GetString() : null;
            var timestamp = DateTimeOffset.Now;
            if (meta.TryGetProperty("regularMarketTime", out var timeEl) && timeEl.TryGetInt64(out var unix))
                timestamp = DateTimeOffset.FromUnixTimeSeconds(unix).ToLocalTime();

            return new StockQuote(ticker.ToUpperInvariant(), price, change, changePercent, timestamp, exchangeTz ?? "");
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

public sealed record StockQuote(
    string Symbol,
    double Price,
    double? Change,
    double? ChangePercent,
    DateTimeOffset TimestampLocal,
    string ExchangeTimeZone);
