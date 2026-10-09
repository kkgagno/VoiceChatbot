using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace VoiceChatbot;

/// <summary>Where a detected context window came from.</summary>
public enum ContextWindowSource
{
    /// <summary>Nothing was detected; the Context window setting applies.</summary>
    None,
    /// <summary>llama.cpp GET /props: default_generation_settings.n_ctx, the window of one slot (one request).</summary>
    LlamaCppProps,
    /// <summary>llama.cpp GET /slots: the smallest slot n_ctx.</summary>
    LlamaCppSlots,
    /// <summary>A runtime field in another server's metadata, such as vLLM's max_model_len in /v1/models.</summary>
    ServerMetadata,
    /// <summary>The n_ctx a server put in a "request exceeds the available context size" error.</summary>
    ServerError,
    /// <summary>Ollama /api/show: the model's trained maximum. The Context window setting caps it (sent as num_ctx).</summary>
    OllamaModel,
    /// <summary>A known limit for a model on a cloud API (api.openai.com, ...). The Context window setting caps it.</summary>
    KnownModel,
}

/// <summary>
/// One context window detection: the tokens (null when nothing was found), where they came from, the
/// kind of server ("llama.cpp server", "vLLM", ...), the llama.cpp slot count and whether the server
/// answered at all.
/// </summary>
public sealed record ServerContextWindow(
    int? Tokens,
    ContextWindowSource Source,
    string ServerName = "",
    int Slots = 0,
    bool Reachable = true)
{
    public static ServerContextWindow NotDetected(bool reachable, string serverName = "") =>
        new(null, ContextWindowSource.None, serverName, 0, reachable);

    /// <summary>
    /// True when <see cref="Tokens"/> is the window the server runs with (llama.cpp, vLLM, LM Studio, or
    /// an overflow error), which wins over the setting; false when it is a model maximum that the
    /// Context window setting caps (Ollama, known cloud models) or nothing was found.
    /// </summary>
    public bool IsServerWindow => Tokens is > 0 && Source is ContextWindowSource.LlamaCppProps
        or ContextWindowSource.LlamaCppSlots or ContextWindowSource.ServerMetadata or ContextWindowSource.ServerError;

    public bool IsLlamaCpp => Source is ContextWindowSource.LlamaCppProps or ContextWindowSource.LlamaCppSlots ||
                              ServerName.StartsWith("llama.cpp", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads the runtime context window from server metadata. llama.cpp reports the window of one slot
/// (what a single request can use) as n_ctx in /props and /slots; n_ctx_train and other training
/// fields are the model's maximum, not what the server was started with, and are never used.
/// </summary>
public static class ContextWindowParser
{
    public const int MinContextTokens = 512;
    public const int MaxContextTokens = 16_777_216;

    public const string LlamaCppServerName = "llama.cpp server";

    // Fields that hold the window a server runs with, best first. Training-context fields
    // (n_ctx_train, max_position_embeddings, LM Studio's max_context_length, Ollama's
    // <arch>.context_length) are deliberately not listed.
    private static readonly string[] RuntimeKeys =
    {
        "n_ctx", "max_model_len", "loaded_context_length", "context_length", "ctx_size", "context_size", "num_ctx",
        "context_window",
    };

    private static readonly string[] CloudHosts =
    {
        "api.openai.com", "openai.azure.com", "api.anthropic.com", "generativelanguage.googleapis.com", "api.mistral.ai",
        "api.groq.com", "api.deepseek.com", "api.together.xyz", "openrouter.ai", "api.x.ai", "api.fireworks.ai",
        "api.perplexity.ai",
    };

    public static bool IsReasonable(long value) => value is >= MinContextTokens and <= MaxContextTokens;

    /// <summary>llama.cpp GET /props: default_generation_settings.n_ctx (the per-slot window).</summary>
    public static int? FromLlamaCppProps(string? json) => FromLlamaCppProps(json, out _);

    /// <summary>llama.cpp GET /props: default_generation_settings.n_ctx, and total_slots.</summary>
    public static int? FromLlamaCppProps(string? json, out int totalSlots)
    {
        totalSlots = 0;
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        var root = doc.RootElement;
        if (TryGetInt(root, "total_slots", out var slots) && slots > 0)
            totalSlots = slots;

        if (root.TryGetProperty("default_generation_settings", out var settings) &&
            settings.ValueKind == JsonValueKind.Object &&
            TryGetInt(settings, "n_ctx", out var slotContext) && IsReasonable(slotContext))
        {
            return slotContext;
        }

        // Some forks put it at the top level.
        return TryGetInt(root, "n_ctx", out var topLevel) && IsReasonable(topLevel) ? topLevel : null;
    }

    /// <summary>llama.cpp GET /slots: the smallest n_ctx of all slots, and the number of slots.</summary>
    public static int? FromLlamaCppSlots(string? json, out int slotCount)
    {
        slotCount = 0;
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        int? smallest = null;
        foreach (var slot in doc.RootElement.EnumerateArray())
        {
            if (slot.ValueKind != JsonValueKind.Object)
                continue;

            slotCount++;
            if (TryGetInt(slot, "n_ctx", out var nCtx) && IsReasonable(nCtx))
                smallest = smallest is int s ? Math.Min(s, nCtx) : nCtx;
        }

        return smallest;
    }

    /// <summary>
    /// A runtime window from other servers' metadata, for example vLLM's max_model_len or LM Studio's
    /// loaded_context_length in a model list. In a list ("data": [...]) only the entry for
    /// <paramref name="model"/> counts (or the only entry); with several entries and no match the
    /// result is null. Fields are tried in <see cref="RuntimeKeys"/> order; several values of the same
    /// field give the smallest.
    /// </summary>
    public static int? FromServerMetadata(string? json, string? model)
    {
        using var doc = TryParse(json);
        if (doc is null)
            return null;

        var scope = SelectModelEntry(doc.RootElement, model);
        if (scope is not JsonElement element)
            return null;

        foreach (var key in RuntimeKeys)
        {
            int? smallest = null;
            CollectValues(element, key, value => smallest = smallest is int s ? Math.Min(s, value) : value);
            if (smallest is int found)
                return found;
        }

        return null;
    }

    /// <summary>"vLLM" when a /v1/models list says owned_by vllm, otherwise "".</summary>
    public static string ServerNameFromModels(string? json)
    {
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return "";
        }

        foreach (var entry in data.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object &&
                entry.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String &&
                string.Equals(owner.GetString(), "vllm", StringComparison.OrdinalIgnoreCase))
            {
                return "vLLM";
            }
        }

        return "";
    }

    /// <summary>
    /// Ollama /api/show: the model's maximum window (model_info "&lt;arch&gt;.context_length"), or the
    /// Modelfile's num_ctx when model_info is missing. It is a cap for num_ctx, not a fixed window.
    /// </summary>
    public static int? FromOllamaShow(string? json)
    {
        using var doc = TryParse(json);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        var root = doc.RootElement;
        int? modelMax = null;
        if (root.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in info.EnumerateObject())
            {
                if (property.Name.EndsWith(".context_length", StringComparison.OrdinalIgnoreCase) &&
                    TryGetInt(property.Value, out var length) && IsReasonable(length))
                {
                    modelMax = modelMax is int m ? Math.Max(m, length) : length;
                }
            }
        }

        if (modelMax is not null)
            return modelMax;

        if (root.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.String)
        {
            foreach (var line in (parameters.GetString() ?? "").Split('\n'))
            {
                var parts = line.Trim().Split([' ', '\t', '='], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].Equals("num_ctx", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(parts[^1], out var numCtx) && IsReasonable(numCtx))
                {
                    return numCtx;
                }
            }
        }

        return null;
    }

    /// <summary>True for hosted APIs (api.openai.com, api.anthropic.com, ...), never for a local or self-hosted server.</summary>
    public static bool IsKnownCloudEndpoint(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return false;

        var host = uri.Host;
        return CloudHosts.Any(h => host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
                                   host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A known limit for a model on a cloud API, or null. Only used for <see cref="IsKnownCloudEndpoint"/>
    /// endpoints: a local server runs a model with whatever window it was started with.
    /// </summary>
    public static int? GuessCloudModelContext(string? baseUrl, string? model)
    {
        if (!IsKnownCloudEndpoint(baseUrl))
            return null;

        var name = (model ?? "").ToLowerInvariant();
        if (name.Contains("gpt-4.1", StringComparison.Ordinal))
            return 1_047_576;
        if (name.Contains("gpt-5", StringComparison.Ordinal))
            return 272_000;
        if (name.Contains("gpt-4o", StringComparison.Ordinal))
            return 128_000;
        if (name.Contains("claude", StringComparison.Ordinal))
            return 200_000;
        if (name.Contains("gemini", StringComparison.Ordinal))
            return 1_048_576;
        return null;
    }

    private static JsonElement? SelectModelEntry(JsonElement root, string? model)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return root;
        }

        var entries = data.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
        if (entries.Count == 0)
            return null;

        var wanted = (model ?? "").Trim();
        if (wanted.Length > 0)
        {
            foreach (var entry in entries)
            {
                foreach (var idKey in new[] { "id", "name", "model" })
                {
                    if (entry.TryGetProperty(idKey, out var id) && id.ValueKind == JsonValueKind.String &&
                        string.Equals(id.GetString()?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry;
                    }
                }
            }
        }

        return entries.Count == 1 ? entries[0] : null;
    }

    private static void CollectValues(JsonElement element, string key, Action<int> found)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                        TryGetInt(property.Value, out var value) && IsReasonable(value))
                    {
                        found(value);
                    }
                    else
                    {
                        CollectValues(property.Value, key, found);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectValues(item, key, found);
                break;
        }
    }

    private static bool TryGetInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var element) &&
               TryGetInt(element, out value);
    }

    private static bool TryGetInt(JsonElement element, out int value)
    {
        value = 0;
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when element.TryGetInt64(out var number) && number is > 0 and <= int.MaxValue:
                value = (int)number;
                return true;
            case JsonValueKind.String when int.TryParse(element.GetString(), out var parsed) && parsed > 0:
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    private static JsonDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>The line under the Context window box: the window in use and where it came from.</summary>
public static class ContextWindowText
{
    /// <param name="window">What detection found.</param>
    /// <param name="configured">The Context window setting (0 = use what the server or model reports).</param>
    /// <param name="effective">The window requests are planned with (TokenBudget.ResolveContextWindow).</param>
    /// <param name="isOllama">The provider is Ollama (the window is sent as num_ctx).</param>
    /// <param name="hasModel">A model is selected.</param>
    public static string Describe(ServerContextWindow window, int configured, int effective, bool isOllama, bool hasModel)
    {
        if (isOllama)
        {
            if (!hasModel)
                return "Ollama: pick a model to see its context window.";
            if (window.Tokens is int modelMax)
                return $"Ollama: {effective:N0} tokens, sent as num_ctx (model max {modelMax:N0}).";
            return window.Reachable
                ? $"Ollama: {effective:N0} tokens {FromBox(configured)}, sent as num_ctx (model max not reported)."
                : $"Ollama not reachable: using {effective:N0} {FromBox(configured)}.";
        }

        var tokens = window.Tokens ?? 0;
        switch (window.Source)
        {
            case ContextWindowSource.LlamaCppProps or ContextWindowSource.LlamaCppSlots:
                var slots = window.Slots > 1 ? $", {window.Slots} parallel slots" : "";
                return $"llama.cpp server: {effective:N0} tokens per request (detected{slots}). Set it with -c on llama-server.";
            case ContextWindowSource.ServerError:
                return $"{ServerLabel(window)}: {effective:N0} tokens per request (reported by the server).{SetItHint(window)}";
            case ContextWindowSource.ServerMetadata:
                return $"{ServerLabel(window)}: {effective:N0} tokens per request (detected).{SetItHint(window)}";
            case ContextWindowSource.KnownModel when tokens > 0:
                return effective < tokens
                    ? $"{effective:N0} tokens {FromBox(configured)} (this model allows {tokens:N0})."
                    : $"{effective:N0} tokens (known limit for this model).";
        }

        if (!window.Reachable)
            return $"Server not reachable: using {effective:N0} {FromBox(configured)}.";
        return $"Not detected: using {effective:N0} {FromBox(configured)}.";
    }

    private static string FromBox(int configured) => configured > 0 ? "from this box" : "(this box is 0)";

    private static string ServerLabel(ServerContextWindow window) =>
        string.IsNullOrWhiteSpace(window.ServerName) ? "Server" : window.ServerName;

    private static string SetItHint(ServerContextWindow window)
    {
        if (window.IsLlamaCpp)
            return " Set it with -c on llama-server.";
        if (window.ServerName.Equals("vLLM", StringComparison.OrdinalIgnoreCase))
            return " Set it with --max-model-len on vLLM.";
        if (window.ServerName.Equals("LM Studio", StringComparison.OrdinalIgnoreCase))
            return " Set it with Context Length when loading the model in LM Studio.";
        return "";
    }
}
