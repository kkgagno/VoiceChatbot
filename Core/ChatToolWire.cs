using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// JSON shapes for tool calling. Ollama /api/chat and OpenAI-compatible servers differ:
/// Ollama sends arguments as an object and matches results by tool_name, OpenAI sends
/// arguments as a JSON string and matches results by tool_call_id.
/// Only messages that carry tool data go through here, so plain requests are unchanged.
/// </summary>
public static class ChatToolWire
{
    public static List<object> ToolsPayload(IEnumerable<ToolSpec> tools) =>
        tools.Select(t => t.ToWireFormat()).ToList();

    /// <summary>
    /// Ollama message: assistant {role, content, tool_calls:[{function:{name, arguments:{...}}}]}
    /// or tool result {role:"tool", content, tool_name}.
    /// </summary>
    public static Dictionary<string, object?> OllamaMessage(
        string role, string? content, IReadOnlyList<string>? imagesBase64,
        IReadOnlyList<ToolCall>? toolCalls, string? toolName)
    {
        var message = new Dictionary<string, object?>
        {
            ["role"] = role,
            ["content"] = content ?? ""
        };
        if (imagesBase64 is { Count: > 0 })
            message["images"] = imagesBase64;
        if (toolCalls is { Count: > 0 })
        {
            message["tool_calls"] = toolCalls.Select(c => new
            {
                function = new
                {
                    name = c.Name,
                    arguments = ArgumentsObject(c.ArgumentsJson)
                }
            }).ToList();
        }
        if (!string.IsNullOrWhiteSpace(toolName))
            message["tool_name"] = toolName;
        return message;
    }

    /// <summary>
    /// OpenAI message: assistant {role, content|null, tool_calls:[{id, type:"function", function:{name, arguments:"..."}}]}
    /// or tool result {role:"tool", content, tool_call_id}. Content may be a string or a parts list.
    /// </summary>
    public static Dictionary<string, object?> OpenAiMessage(
        string role, object? content, IReadOnlyList<ToolCall>? toolCalls, string? toolCallId)
    {
        var hasCalls = toolCalls is { Count: > 0 };
        var message = new Dictionary<string, object?>
        {
            ["role"] = role,
            // Assistant turns that only call tools carry null content, as the OpenAI spec describes.
            ["content"] = hasCalls && content is string s && s.Length == 0 ? null : content ?? ""
        };
        if (hasCalls)
        {
            message["tool_calls"] = toolCalls!.Select(c => new
            {
                id = string.IsNullOrWhiteSpace(c.Id) ? ToolCall.NewId() : c.Id,
                type = "function",
                function = new
                {
                    name = c.Name,
                    arguments = ToolArguments.NormalizeObjectJson(c.ArgumentsJson)
                }
            }).ToList();
        }
        if (!string.IsNullOrWhiteSpace(toolCallId))
            message["tool_call_id"] = toolCallId;
        return message;
    }

    /// <summary>Reads message.tool_calls from an Ollama chat chunk.</summary>
    public static List<ToolCall> ParseOllamaToolCalls(JsonElement message)
    {
        var calls = new List<ToolCall>();
        if (message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("tool_calls", out var array) ||
            array.ValueKind != JsonValueKind.Array)
        {
            return calls;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var function = item.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object ? f : item;
            var name = GetString(function, "name");
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var id = GetString(item, "id");
            calls.Add(new ToolCall
            {
                Id = string.IsNullOrWhiteSpace(id) ? ToolCall.NewId() : id,
                Name = name.Trim(),
                ArgumentsJson = function.TryGetProperty("arguments", out var args) ? ArgumentsText(args) : "{}"
            });
        }

        return calls;
    }

    /// <summary>
    /// True when an error response means "this model/server cannot do tool calling", e.g. Ollama's
    /// "model does not support tools" (HTTP 400) or llama.cpp's "tools param requires --jinja flag".
    /// </summary>
    public static bool LooksLikeToolsUnsupported(int statusCode, string? body)
    {
        if (statusCode < 400 || string.IsNullOrWhiteSpace(body))
            return false;

        var text = body.ToLowerInvariant();
        if (Regex.IsMatch(text, @"(does not|doesn't|do not|don't|cannot|can't) support (function calling|tool calling|tools|tool use|tool calls)") ||
            Regex.IsMatch(text, @"(tools?|tool calling|tool use|function calling) (is |are )?(not supported|unsupported)") ||
            text.Contains("tools param requires", StringComparison.Ordinal) ||
            text.Contains("--jinja", StringComparison.Ordinal))
        {
            return true;
        }

        return statusCode == 400 && Regex.IsMatch(text, @"\btools?\b|tool_choice|tool_calls");
    }

    private static JsonElement ArgumentsObject(string? argumentsJson)
    {
        using var doc = JsonDocument.Parse(ToolArguments.NormalizeObjectJson(argumentsJson));
        return doc.RootElement.Clone();
    }

    internal static string ArgumentsText(JsonElement args) => args.ValueKind switch
    {
        JsonValueKind.String => args.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "{}",
        _ => args.GetRawText()
    };

    internal static string GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

/// <summary>
/// Rebuilds OpenAI streaming tool calls. Each chunk's delta.tool_calls carries fragments keyed by
/// index: the id and name usually arrive first, the arguments string arrives in pieces.
/// </summary>
public sealed class OpenAiToolCallAccumulator
{
    private sealed class Partial
    {
        public string Id = "";
        public readonly StringBuilder Name = new();
        public readonly StringBuilder Arguments = new();
    }

    private readonly SortedDictionary<int, Partial> _calls = new();

    public bool HasCalls => _calls.Values.Any(c => c.Name.Length > 0);

    /// <summary>Adds a tool_calls array from a stream delta (or from a complete message).</summary>
    public void Add(JsonElement toolCalls)
    {
        if (toolCalls.ValueKind != JsonValueKind.Array)
            return;

        var position = 0;
        foreach (var item in toolCalls.EnumerateArray())
        {
            var index = item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("index", out var indexElement) &&
                        indexElement.ValueKind == JsonValueKind.Number &&
                        indexElement.TryGetInt32(out var parsedIndex)
                ? parsedIndex
                : position;
            position++;
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            if (!_calls.TryGetValue(index, out var call))
                _calls[index] = call = new Partial();

            var id = ChatToolWire.GetString(item, "id");
            if (!string.IsNullOrWhiteSpace(id))
                call.Id = id;

            if (!item.TryGetProperty("function", out var function) || function.ValueKind != JsonValueKind.Object)
                continue;

            var name = ChatToolWire.GetString(function, "name");
            // Most servers send the name once; some repeat it in every chunk.
            if (name.Length > 0 && !string.Equals(call.Name.ToString(), name, StringComparison.Ordinal))
                call.Name.Append(name);

            if (function.TryGetProperty("arguments", out var args))
            {
                if (args.ValueKind == JsonValueKind.String)
                    call.Arguments.Append(args.GetString());
                else if (args.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    call.Arguments.Clear().Append(args.GetRawText());
            }
        }
    }

    public List<ToolCall> Build() =>
        _calls.Values
            .Where(c => c.Name.Length > 0)
            .Select(c => new ToolCall
            {
                Id = string.IsNullOrWhiteSpace(c.Id) ? ToolCall.NewId() : c.Id,
                Name = c.Name.ToString().Trim(),
                ArgumentsJson = c.Arguments.Length == 0 ? "{}" : c.Arguments.ToString()
            })
            .ToList();
}
