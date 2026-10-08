using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// A function the model may call (native tool calling). Parameters is a JSON-schema object.
/// The same wire shape works for Ollama /api/chat and OpenAI-compatible /v1/chat/completions.
/// </summary>
public sealed class ToolSpec
{
    public ToolSpec(string name, string description, JsonElement parameters)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tool name is required.", nameof(name));

        Name = name.Trim();
        Description = description?.Trim() ?? "";
        Parameters = parameters.ValueKind == JsonValueKind.Object ? parameters.Clone() : EmptyObjectSchema();
    }

    public string Name { get; }
    public string Description { get; }
    public JsonElement Parameters { get; }

    /// <summary>Builds a tool whose parameters are simple named values (string by default).</summary>
    public static ToolSpec Create(string name, string description, params ToolParameter[] parameters)
    {
        var properties = new Dictionary<string, object>();
        var required = new List<string>();
        foreach (var p in parameters)
        {
            properties[p.Name] = new Dictionary<string, object>
            {
                ["type"] = p.Type,
                ["description"] = p.Description
            };
            if (p.Required)
                required.Add(p.Name);
        }

        var schema = new Dictionary<string, object>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required
        };
        return new ToolSpec(name, description, JsonSerializer.SerializeToElement(schema));
    }

    /// <summary>Builds a tool from a JSON-schema text, for features that need richer parameters.</summary>
    public static ToolSpec FromJsonSchema(string name, string description, string parametersJsonSchema)
    {
        using var doc = JsonDocument.Parse(parametersJsonSchema);
        return new ToolSpec(name, description, doc.RootElement);
    }

    /// <summary>{"type":"function","function":{name, description, parameters}}</summary>
    public object ToWireFormat() => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = Description,
            parameters = Parameters
        }
    };

    private static JsonElement EmptyObjectSchema() =>
        JsonSerializer.SerializeToElement(new { type = "object", properties = new Dictionary<string, object>() });
}

public sealed record ToolParameter(string Name, string Description, string Type = "string", bool Required = true);

/// <summary>A tool call requested by the model. ArgumentsJson is the raw JSON text the model produced.</summary>
public sealed class ToolCall
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";

    /// <summary>Nine alphanumeric characters: valid for every chat template we know of (Mistral is the strictest).</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..9];
}

/// <summary>The outcome of one model turn that may have asked for tools.</summary>
public sealed record ChatTurnResult(string Content, IReadOnlyList<ToolCall> ToolCalls, string FinishReason)
{
    public bool HasToolCalls => ToolCalls.Count > 0;
}

/// <summary>Thrown when the backend rejects a request because the model cannot use tools.</summary>
public sealed class ToolsNotSupportedException : Exception
{
    public ToolsNotSupportedException(string message) : base(message) { }
}

/// <summary>Lenient readers for tool-call arguments; local models are not always tidy with JSON.</summary>
public static class ToolArguments
{
    /// <summary>
    /// Returns the named argument as text. Tolerates double-encoded JSON, differently cased names and,
    /// with rawTextFallback, a single unnamed value or plain non-JSON text.
    /// </summary>
    public static string GetString(string? argumentsJson, string name, bool rawTextFallback = false)
    {
        var text = argumentsJson?.Trim() ?? "";
        if (text.Length == 0)
            return "";

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString() ?? "";
                if (inner.TrimStart().StartsWith('{'))
                    return GetString(inner, name, rawTextFallback);
                return rawTextFallback ? inner.Trim() : "";
            }

            if (root.ValueKind != JsonValueKind.Object)
                return rawTextFallback && root.ValueKind is JsonValueKind.Number ? root.GetRawText() : "";

            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return ValueText(property.Value);
            }

            if (rawTextFallback)
            {
                var values = root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToList();
                if (values.Count == 1)
                    return ValueText(values[0].Value);
            }

            return "";
        }
        catch (JsonException)
        {
            return rawTextFallback ? text : "";
        }
    }

    /// <summary>Returns a valid JSON object text for the arguments ("{}" when they are not an object).</summary>
    public static string NormalizeObjectJson(string? argumentsJson)
    {
        var text = argumentsJson?.Trim() ?? "";
        if (text.Length == 0)
            return "{}";

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                return doc.RootElement.GetRawText();
            if (doc.RootElement.ValueKind == JsonValueKind.String)
            {
                var inner = doc.RootElement.GetString() ?? "";
                return inner.TrimStart().StartsWith('{') ? NormalizeObjectJson(inner) : "{}";
            }
        }
        catch (JsonException)
        {
            // Fall through to an empty object.
        }

        return "{}";
    }

    private static string ValueText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        _ => value.GetRawText()
    };
}

/// <summary>Names, definitions and status notes for the tools the app ships with.</summary>
public static class BuiltInTools
{
    public const string WebSearch = "web_search";
    public const string CurrentDateTime = "get_current_datetime";
    public const string StockQuote = "get_stock_quote";
    public const string SaveMemory = "save_memory";
    public const string FetchWebPage = "fetch_web_page";

    public static readonly ToolSpec WebSearchSpec = ToolSpec.Create(
        WebSearch,
        "Search the live web for current information: news, recent events, new releases and versions, prices, sports, weather, or anything that may have changed since your training data. Returns a short answer plus source snippets with URLs.",
        new ToolParameter("query", "A concise search-engine style query."));

    public static readonly ToolSpec CurrentDateTimeSpec = ToolSpec.Create(
        CurrentDateTime,
        "Get the user's current local date, time, time zone and day of the week.");

    public static readonly ToolSpec StockQuoteSpec = ToolSpec.Create(
        StockQuote,
        "Get a current (possibly delayed) stock quote: price and today's change.",
        new ToolParameter("symbol", "The exchange ticker symbol, for example AAPL, MSFT or AMD. Not the company name."));

    public static readonly ToolSpec SaveMemorySpec = ToolSpec.Create(
        SaveMemory,
        "Save a fact to long-term memory so it is remembered in future conversations. Use it when the user asks you to remember something, or shares a lasting personal preference or detail.",
        new ToolParameter("text", "The fact to remember, written as one short self-contained sentence."));

    public static readonly ToolSpec FetchWebPageSpec = ToolSpec.Create(
        FetchWebPage,
        "Download a public web page and return its readable text (up to about 8000 characters). Use it to read a link the user gives you or a source URL from a web search.",
        new ToolParameter("url", "The full http or https URL."));

    /// <summary>A short note shown in the chat while a call runs, e.g. Searching the web for "x"...</summary>
    public static string DescribeCall(ToolCall call)
    {
        var name = call.Name ?? "";
        switch (name)
        {
            case WebSearch:
                var query = ToolArguments.GetString(call.ArgumentsJson, "query", rawTextFallback: true);
                return string.IsNullOrWhiteSpace(query) ? "Searching the web..." : $"Searching the web for \"{Shorten(query, 80)}\"...";
            case CurrentDateTime:
                return "Checking the current date and time...";
            case StockQuote:
                var symbol = NormalizeTickerSymbol(ToolArguments.GetString(call.ArgumentsJson, "symbol", rawTextFallback: true));
                return symbol.Length == 0 ? "Getting a stock quote..." : $"Getting a stock quote for {symbol}...";
            case SaveMemory:
                var text = ToolArguments.GetString(call.ArgumentsJson, "text", rawTextFallback: true);
                return string.IsNullOrWhiteSpace(text) ? "Saving a memory..." : $"Saving a memory: \"{Shorten(text, 80)}\"";
            case FetchWebPage:
                var url = ToolArguments.GetString(call.ArgumentsJson, "url", rawTextFallback: true);
                return string.IsNullOrWhiteSpace(url) ? "Reading a web page..." : $"Reading {Shorten(url.Trim(), 90)}...";
            default:
                return $"Using tool {(string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name)}...";
        }
    }

    /// <summary>Text returned by get_current_datetime.</summary>
    public static string FormatDateTime(DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var offset = local.Offset;
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var utcOffset = $"UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
        var zoneName = zone.IsDaylightSavingTime(local) ? zone.DaylightName : zone.StandardName;
        if (string.IsNullOrWhiteSpace(zoneName))
            zoneName = zone.Id;

        var culture = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine($"Local date: {local.ToString("dddd, MMMM d, yyyy", culture)}");
        sb.AppendLine($"Local time: {local.ToString("h:mm tt", culture)} ({local.ToString("HH:mm", culture)})");
        sb.AppendLine($"Day of week: {local.DayOfWeek}");
        sb.AppendLine($"Time zone: {zoneName} ({utcOffset}), id {zone.Id}");
        sb.Append($"ISO 8601: {local.ToString("yyyy-MM-dd'T'HH:mm:sszzz", culture)}");
        return sb.ToString();
    }

    /// <summary>Upper-cases and strips "$" from a ticker; returns "" for text that cannot be a symbol.</summary>
    public static string NormalizeTickerSymbol(string? symbol)
    {
        var text = (symbol ?? "").Trim().TrimStart('$').Trim().ToUpperInvariant();
        return Regex.IsMatch(text, @"^\^?[A-Z0-9][A-Z0-9.\-=]{0,14}$") ? text : "";
    }

    private static string Shorten(string text, int max)
    {
        var flat = Regex.Replace(text, @"\s+", " ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 3)].TrimEnd() + "...";
    }
}
