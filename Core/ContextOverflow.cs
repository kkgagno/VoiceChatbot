using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// What a server said when a request was too long for its context window. llama.cpp answers HTTP 400
/// with error type "exceed_context_size_error", "the request exceeds the available context size", and
/// (newer builds) n_ctx and n_prompt_tokens; vLLM and OpenAI say "maximum context length is N tokens".
/// </summary>
public sealed record ContextOverflowInfo(int? ServerContextTokens, int? PromptTokens, string Message, bool IsLlamaCpp);

public static class ContextOverflow
{
    private static readonly Regex[] WindowPatterns =
    {
        new(@"maximum context length is (\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"context length of only (\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"available context size \((\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"n_ctx\s*[=:]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"tokens? > (\d+) maximum", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    };

    private static readonly Regex[] PromptPatterns =
    {
        new(@"n_prompt_tokens\s*[=:]\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"\((\d+) in the messages", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"(?:messages )?resulted in (\d+) tokens", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"you requested (\d+) tokens", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"prompt is too long: (\d+) tokens", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
    };

    /// <summary>
    /// True when <paramref name="body"/> (an HTTP error body or an {"error": ...} stream chunk) says the
    /// request did not fit the context window.
    /// </summary>
    public static bool TryParse(string? body, out ContextOverflowInfo info)
    {
        info = new ContextOverflowInfo(null, null, "", false);
        if (string.IsNullOrWhiteSpace(body))
            return false;

        var message = "";
        var type = "";
        var code = "";
        int? nCtx = null;
        int? nPrompt = null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e) ? e : root;
            if (error.ValueKind == JsonValueKind.String)
            {
                message = error.GetString() ?? "";
            }
            else if (error.ValueKind == JsonValueKind.Object)
            {
                message = ReadText(error, "message");
                type = ReadText(error, "type");
                code = ReadText(error, "code");
                nCtx = ReadInt(error, "n_ctx");
                nPrompt = ReadInt(error, "n_prompt_tokens");
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (message.Length == 0)
                    message = ReadText(root, "message");
                nCtx ??= ReadInt(root, "n_ctx");
                nPrompt ??= ReadInt(root, "n_prompt_tokens");
            }
        }
        catch (JsonException)
        {
            message = body.Trim();
        }

        var isLlamaCpp = type.Equals("exceed_context_size_error", StringComparison.OrdinalIgnoreCase) ||
                         message.Contains("available context size", StringComparison.OrdinalIgnoreCase);
        if (!isLlamaCpp &&
            !code.Equals("context_length_exceeded", StringComparison.OrdinalIgnoreCase) &&
            !type.Equals("context_length_exceeded", StringComparison.OrdinalIgnoreCase) &&
            !LooksLikeOverflowMessage(message))
        {
            return false;
        }

        nCtx ??= FirstNumber(message, WindowPatterns);
        nPrompt ??= FirstNumber(message, PromptPatterns);
        if (nCtx is int window && !ContextWindowParser.IsReasonable(window))
            nCtx = null;

        info = new ContextOverflowInfo(nCtx, nPrompt is > 0 ? nPrompt : null, message.Trim(), isLlamaCpp);
        return true;
    }

    /// <summary>
    /// The short message shown when a request still does not fit. <paramref name="knownWindow"/> is the
    /// detected server window, used when the error did not say; <paramref name="serverIsLlamaCpp"/> is
    /// what detection found, used when the error does not say either.
    /// </summary>
    public static string Describe(ContextOverflowInfo info, int? knownWindow = null, bool serverIsLlamaCpp = false)
    {
        var window = info.ServerContextTokens ?? (knownWindow is > 0 ? knownWindow : null);
        var needed = info.PromptTokens is int p ? $"; this request needed {p:N0}" : "";
        if (info.IsLlamaCpp || serverIsLlamaCpp)
        {
            return window is int n
                ? $"This chat is too long for the llama.cpp server, which takes {n:N0} tokens per request{needed}. " +
                  "Start llama-server with a larger -c (--ctx-size), or clear the chat."
                : $"This chat is too long for the llama.cpp server's context window{needed}. " +
                  "Start llama-server with a larger -c (--ctx-size), or clear the chat.";
        }

        return window is int w
            ? $"This chat is too long for the server, which takes {w:N0} tokens per request{needed}. " +
              "Raise the server's context size, or clear the chat."
            : $"This chat is too long for the server's context window{needed}. Raise the server's context size, or clear the chat.";
    }

    private static bool LooksLikeOverflowMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        var text = message.ToLowerInvariant();
        return text.Contains("exceeds the available context size", StringComparison.Ordinal) ||
               text.Contains("exceed_context_size", StringComparison.Ordinal) ||
               text.Contains("maximum context length", StringComparison.Ordinal) ||
               text.Contains("context_length_exceeded", StringComparison.Ordinal) ||
               text.Contains("context length exceeded", StringComparison.Ordinal) ||
               text.Contains("context length of only", StringComparison.Ordinal) ||
               text.Contains("exceeds the context length", StringComparison.Ordinal) ||
               text.Contains("exceeds the context window", StringComparison.Ordinal) ||
               text.Contains("exceeds the maximum context", StringComparison.Ordinal) ||
               text.Contains("prompt is too long", StringComparison.Ordinal);
    }

    private static int? FirstNumber(string text, Regex[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = pattern.Match(text);
            if (!match.Success)
                continue;

            for (var g = 1; g < match.Groups.Count; g++)
            {
                if (match.Groups[g].Success && int.TryParse(match.Groups[g].Value, out var number) && number > 0)
                    return number;
            }
        }

        return null;
    }

    private static string ReadText(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0)
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed) && parsed > 0)
            return parsed;
        return null;
    }
}

/// <summary>
/// A chat request the server rejected as too long for its context window. The message is the short
/// explanation for the chat; <see cref="Info"/> carries the server's n_ctx and prompt size when it said.
/// </summary>
public sealed class ContextOverflowException : HttpRequestException
{
    public ContextOverflowException(ContextOverflowInfo info)
        : base(ContextOverflow.Describe(info), null, HttpStatusCode.BadRequest)
    {
        Info = info;
    }

    public ContextOverflowInfo Info { get; }
}
