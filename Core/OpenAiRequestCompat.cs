using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VoiceChatbot;

/// <summary>
/// Adjusts OpenAI-compatible chat request bodies to what the server and model accept. OpenAI's newer
/// models want <c>max_completion_tokens</c> instead of <c>max_tokens</c>, and its reasoning models
/// (o1/o3/o4, GPT-5) only accept the default temperature. Known cases are handled up front for
/// api.openai.com; anything else is learned from the server's HTTP 400 answer ("Unsupported parameter:
/// 'max_tokens' ...", "Unsupported value: 'temperature' ...") and remembered per server and model for
/// the rest of the session. Thread-safe.
/// </summary>
public sealed class OpenAiRequestCompat
{
    /// <summary>Reasoning models spend part of the reply budget on hidden thinking; give them at least this much.</summary>
    public const int ReasoningMinCompletionTokens = 8192;

    // Parameters that only tune the reply and can be left out when a server refuses them. Never the
    // messages, the model, the tools or stream.
    private static readonly HashSet<string> Droppable = new(StringComparer.Ordinal)
    {
        "temperature", "top_p", "repeat_penalty", "presence_penalty", "frequency_penalty", "stream_options",
        "chat_template_kwargs", "reasoning_format", "parallel_tool_calls", "max_tokens", "max_completion_tokens"
    };

    private readonly ConcurrentDictionary<string, Quirks> _learned = new(StringComparer.OrdinalIgnoreCase);

    private sealed record Quirks(bool UseMaxCompletionTokens, IReadOnlySet<string> Dropped);

    /// <summary>
    /// True for OpenAI reasoning models (o1, o3, o4 families and GPT-5, except the "chat" variants), which
    /// take only the default temperature.
    /// </summary>
    public static bool IsReasoningModel(string? model)
    {
        var name = (model ?? "").Trim().ToLowerInvariant();
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
            name = name[(slash + 1)..];
        if (name.Contains("chat", StringComparison.Ordinal))
            return false;
        return name.StartsWith("o1", StringComparison.Ordinal) ||
               name.StartsWith("o3", StringComparison.Ordinal) ||
               name.StartsWith("o4", StringComparison.Ordinal) ||
               name.StartsWith("gpt-5", StringComparison.Ordinal);
    }

    public static bool IsOpenAiHost(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The request body adjusted for <paramref name="baseUrl"/>; unchanged when nothing applies or it is not a JSON object.</summary>
    public string Apply(string baseUrl, string json)
    {
        if (JsonNode.Parse(json) is not JsonObject body)
            return json;

        var model = ReadModel(body);
        var openAi = IsOpenAiHost(baseUrl);
        var reasoning = openAi && IsReasoningModel(model);
        var quirks = _learned.TryGetValue(Key(baseUrl, model), out var known) ? known : null;
        var changed = false;

        // OpenAI deprecated max_tokens for every chat model; its newer models refuse it.
        if ((openAi || quirks?.UseMaxCompletionTokens == true) && body["max_tokens"] is JsonNode maxTokens)
        {
            body.Remove("max_tokens");
            body["max_completion_tokens"] = maxTokens;
            changed = true;
        }

        if (reasoning)
        {
            changed |= body.Remove("temperature") | body.Remove("top_p");
            if (body["max_completion_tokens"] is JsonValue value && value.TryGetValue<int>(out var limit) &&
                limit < ReasoningMinCompletionTokens)
            {
                body["max_completion_tokens"] = ReasoningMinCompletionTokens;
                changed = true;
            }
        }

        if (quirks != null)
        {
            foreach (var name in quirks.Dropped)
                changed |= body.Remove(name);
        }

        return changed ? body.ToJsonString() : json;
    }

    /// <summary>
    /// Learns from a server's HTTP 400 answer to <paramref name="sentJson"/>: a refused <c>max_tokens</c>
    /// switches to <c>max_completion_tokens</c>, and another refused tuning parameter is left out from then
    /// on. False when the answer is not about such a parameter or nothing new was learned (so a retry
    /// would fail the same way). <paramref name="change"/> describes what changed, for the log.
    /// </summary>
    public bool Learn(string baseUrl, string sentJson, string? errorBody, out string change)
    {
        change = "";
        if (!TryReadError(errorBody, out var param, out var code, out var message))
            return false;

        JsonObject? sent;
        try { sent = JsonNode.Parse(sentJson) as JsonObject; }
        catch (JsonException) { return false; }
        if (sent == null)
            return false;

        var model = ReadModel(sent);
        var key = Key(baseUrl, model);
        var current = _learned.TryGetValue(key, out var known) ? known : new Quirks(false, new HashSet<string>());

        if (param.Length == 0)
            param = ParamFromMessage(message);
        if (param.Length == 0 || !Droppable.Contains(param) || !sent.ContainsKey(param))
            return false;

        var refused = code is "unsupported_parameter" or "unsupported_value" or "invalid_request_error" or "" ||
                      message.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("not supported", StringComparison.OrdinalIgnoreCase);
        if (!refused)
            return false;

        Quirks updated;
        if (param == "max_tokens" && !current.UseMaxCompletionTokens &&
            message.Contains("max_completion_tokens", StringComparison.OrdinalIgnoreCase))
        {
            updated = current with { UseMaxCompletionTokens = true };
            change = "max_tokens is sent as max_completion_tokens";
        }
        else if (!current.Dropped.Contains(param))
        {
            updated = current with { Dropped = new HashSet<string>(current.Dropped) { param } };
            change = $"{param} is left out";
        }
        else
        {
            return false;
        }

        _learned[key] = updated;
        return true;
    }

    private static string Key(string baseUrl, string model) => $"{baseUrl.TrimEnd('/')}|{model}";

    private static string ReadModel(JsonObject body) =>
        body["model"] is JsonValue value && value.TryGetValue<string>(out var model) ? model : "";

    private static bool TryReadError(string? errorBody, out string param, out string code, out string message)
    {
        param = code = message = "";
        if (string.IsNullOrWhiteSpace(errorBody))
            return false;

        try
        {
            using var doc = JsonDocument.Parse(errorBody);
            var root = doc.RootElement;
            var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e) ? e : root;
            if (error.ValueKind == JsonValueKind.String)
            {
                message = error.GetString() ?? "";
                return message.Length > 0;
            }
            if (error.ValueKind != JsonValueKind.Object)
                return false;

            param = ReadString(error, "param");
            code = ReadString(error, "code");
            message = ReadString(error, "message");
            return param.Length > 0 || message.Length > 0;
        }
        catch (JsonException)
        {
            message = errorBody.Trim();
            return true;
        }

        static string ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    // "Unsupported parameter: 'max_tokens' is not supported..." when the server sent no param field.
    private static string ParamFromMessage(string message)
    {
        var start = message.IndexOf('\'');
        if (start < 0)
            return "";
        var end = message.IndexOf('\'', start + 1);
        return end > start + 1 ? message[(start + 1)..end] : "";
    }
}
