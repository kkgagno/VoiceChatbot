using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

public class OllamaClient : IDisposable
{
    private readonly HttpClient _http;
    private string _provider = "Ollama";
    private string _baseUrl = "http://localhost:11434";
    private string _openAiBaseUrl = "http://localhost:8080/v1";
    private string _openAiApiKey = "";
    // Base URLs whose server rejected the skip-thinking fields with HTTP 400 in this session.
    private readonly HashSet<string> _thinkingFieldsRejected = new(StringComparer.OrdinalIgnoreCase);

    public string Provider
    {
        get => _provider;
        set => SetConnectionField(ref _provider, value ?? "");
    }

    /// <summary>
    /// "Hide model thinking": ask the server to skip the model's thinking phase. OpenAI-compatible servers
    /// such as llama.cpp (not api.openai.com) get chat_template_kwargs.enable_thinking = false and
    /// reasoning_format = deepseek, so thinking that is still written arrives in reasoning_content, which
    /// is ignored; Ollama gets think = false.
    /// </summary>
    public bool DisableThinking { get; set; } = true;

    public string LastFinishReason { get; private set; } = "";
    public string LastStopReason { get; private set; } = "";
    public int? LastPromptTokens { get; private set; }
    public int? LastCompletionTokens { get; private set; }

    public string BaseUrl
    {
        get => _baseUrl;
        set => SetConnectionField(ref _baseUrl, (value ?? "").TrimEnd('/'));
    }

    /// <summary>The detected context window in tokens, or null (see <see cref="DetectContextWindowAsync"/>).</summary>
    public async Task<int?> GetModelContextTokensAsync(string model, CancellationToken ct = default) =>
        (await DetectContextWindowAsync(model, ct: ct).ConfigureAwait(false)).Tokens;

    public string OpenAiBaseUrl
    {
        get => _openAiBaseUrl;
        set => SetConnectionField(ref _openAiBaseUrl, (value ?? "").TrimEnd('/'));
    }

    public string OpenAiApiKey
    {
        get => _openAiApiKey;
        set => SetConnectionField(ref _openAiApiKey, value ?? "");
    }

    // A different provider, endpoint or key is another server: detect its context window again.
    private void SetConnectionField(ref string field, string value)
    {
        if (string.Equals(field, value, StringComparison.Ordinal))
            return;

        field = value;
        InvalidateContextWindowCache();
    }

    public OllamaClient(string baseUrl = "http://localhost:11434")
    {
        BaseUrl = baseUrl;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
    }

    // ---- Models ----
    public async Task<List<string>> ListModelsAsync()
    {
        if (IsOpenAiCompatible)
            return await ListOpenAiCompatibleModelsAsync();

        try
        {
            var resp = await _http.GetAsync($"{_baseUrl}/api/tags");
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var arr))
            {
                foreach (var m in arr.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var name))
                        models.Add(name.GetString() ?? "");
                }
            }
            return models;
        }
        catch
        {
            return new List<string>();
        }
    }

    // ---- Chat (non-streaming) ----
    public async Task<string> ChatAsync(string model, List<ChatMessage> messages, string systemPrompt,
        double temperature, int maxTokens = 2048, CancellationToken ct = default, int contextTokens = 0)
    {
        ResetLastResponseMetadata();

        if (IsOpenAiCompatible)
            return await ChatOpenAiCompatibleAsync(model, messages, systemPrompt, temperature, maxTokens, ct);

        using var resp = await SendChatRequestAsync(_baseUrl, skipThinking => CreateOllamaChatRequest(
            BuildChatBody(model, messages, systemPrompt, temperature, maxTokens, contextTokens, stream: false, skipThinking: skipThinking)),
            HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccessWithBodyAsync(resp, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        CaptureOllamaResponseMetadata(doc.RootElement);
        return doc.RootElement.TryGetProperty("message", out var msg) &&
               msg.TryGetProperty("content", out var c)
            ? c.GetString() ?? ""
            : "";
    }

    // ---- Chat (streaming) ----
    /// <summary>
    /// Streams a reply: tokens go to onToken, the full text to onComplete and backend errors (also
    /// {"error": ...} objects inside the stream) to onError. Throws OperationCanceledException when
    /// cancelled, without calling onComplete.
    /// </summary>
    public async Task ChatStreamAsync(string model, List<ChatMessage> messages, string systemPrompt,
        double temperature, int maxTokens, int contextTokens, Action<string> onToken, Action<string> onComplete, Action<Exception> onError,
        CancellationToken ct = default)
    {
        ResetLastResponseMetadata();

        if (IsOpenAiCompatible)
        {
            await ChatOpenAiCompatibleStreamAsync(model, messages, systemPrompt, temperature, maxTokens, onToken, onComplete, onError, ct);
            return;
        }

        try
        {
            using var resp = await SendChatRequestAsync(_baseUrl, skipThinking => CreateOllamaChatRequest(
                BuildChatBody(model, messages, systemPrompt, temperature, maxTokens, contextTokens, stream: true, skipThinking: skipThinking)),
                HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccessWithBodyAsync(resp, ct);

            var fullResponse = new StringBuilder();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonDocument chunk;
                try { chunk = JsonDocument.Parse(line); }
                catch (JsonException) { continue; /* skip malformed chunks */ }

                using (chunk)
                {
                    // {"error": ...} after a 200 response (model crashed, out of memory, ...).
                    if (chunk.RootElement.ValueKind == JsonValueKind.Object)
                        ThrowIfStreamError(chunk.RootElement, toolsSent: false);

                    try
                    {
                        if (chunk.RootElement.TryGetProperty("message", out var msg) &&
                            msg.TryGetProperty("content", out var c))
                        {
                            var token = c.GetString() ?? "";
                            fullResponse.Append(token);
                            onToken(token);
                        }
                        if (chunk.RootElement.TryGetProperty("done", out var done) && done.GetBoolean())
                        {
                            CaptureOllamaResponseMetadata(chunk.RootElement);
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { /* skip chunks with an unexpected shape */ }
                }
            }

            // Stop/Esc/Clear Chat: no partial reply is completed (saved or spoken).
            ct.ThrowIfCancellationRequested();
            onComplete(fullResponse.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Stop can surface as an aborted stream (IOException); report it as the cancel it is.
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex) { onError(ex); }
    }

    // ---- Chat with tools (streaming) ----
    /// <summary>
    /// Streams one turn with tool definitions. Content tokens go to onToken as they arrive; tool calls
    /// are collected and returned. Throws ToolsNotSupportedException when the backend rejects tools,
    /// HttpRequestException for other backend errors and OperationCanceledException on cancel.
    /// </summary>
    public async Task<ChatTurnResult> ChatStreamWithToolsAsync(string model, List<ChatMessage> messages, string systemPrompt,
        double temperature, int maxTokens, int contextTokens, IReadOnlyList<ToolSpec>? tools, Action<string>? onToken,
        CancellationToken ct = default)
    {
        ResetLastResponseMetadata();
        var toolsToSend = tools is { Count: > 0 } ? tools : null;

        try
        {
            return IsOpenAiCompatible
                ? await StreamOpenAiTurnAsync(model, messages, systemPrompt, temperature, maxTokens, toolsToSend, onToken, ct).ConfigureAwait(false)
                : await StreamOllamaTurnAsync(model, messages, systemPrompt, temperature, maxTokens, contextTokens, toolsToSend, onToken, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is not OperationCanceledException)
        {
            // Stop can surface as an aborted stream (IOException); report it as the cancel it is.
            throw new OperationCanceledException(ct);
        }
    }

    private async Task<ChatTurnResult> StreamOllamaTurnAsync(string model, List<ChatMessage> messages, string systemPrompt,
        double temperature, int maxTokens, int contextTokens, IReadOnlyList<ToolSpec>? tools, Action<string>? onToken,
        CancellationToken ct)
    {
        using var resp = await SendChatRequestAsync(_baseUrl, skipThinking => CreateOllamaChatRequest(
            BuildChatBody(model, messages, systemPrompt, temperature, maxTokens, contextTokens, stream: true, tools, skipThinking)),
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessWithBodyAsync(resp, ct, toolsSent: tools is not null).ConfigureAwait(false);

        var content = new StringBuilder();
        var toolCalls = new List<ToolCall>();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument chunk;
            try { chunk = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }

            using (chunk)
            {
                var root = chunk.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                ThrowIfStreamError(root, tools is not null);

                if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
                {
                    if (msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var token = c.GetString() ?? "";
                        if (token.Length > 0)
                        {
                            content.Append(token);
                            onToken?.Invoke(token);
                        }
                    }
                    toolCalls.AddRange(ChatToolWire.ParseOllamaToolCalls(msg));
                }

                if (root.TryGetProperty("done", out var done) && done.ValueKind == JsonValueKind.True)
                {
                    try { CaptureOllamaResponseMetadata(root); }
                    catch (InvalidOperationException) { /* Unexpected metadata shape; the answer is still fine. */ }
                    break;
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        return new ChatTurnResult(content.ToString(), toolCalls, LastFinishReason);
    }

    private async Task<ChatTurnResult> StreamOpenAiTurnAsync(string model, List<ChatMessage> messages, string systemPrompt,
        double temperature, int maxTokens, IReadOnlyList<ToolSpec>? tools, Action<string>? onToken, CancellationToken ct)
    {
        using var resp = await SendChatRequestAsync(_openAiBaseUrl, skipThinking => CreateOpenAiChatRequest(
            BuildOpenAiChatBody(model, messages, systemPrompt, temperature, maxTokens, stream: true, tools, skipThinking)),
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await EnsureSuccessWithBodyAsync(resp, ct, toolsSent: tools is not null).ConfigureAwait(false);

        var content = new StringBuilder();
        var toolCalls = new OpenAiToolCallAccumulator();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

            var data = line[5..].Trim();
            if (data == "[DONE]") break;

            JsonDocument chunk;
            try { chunk = JsonDocument.Parse(data); }
            catch (JsonException) { continue; }

            using (chunk)
            {
                var root = chunk.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                ThrowIfStreamError(root, tools is not null);
                CaptureOpenAiUsage(root);

                if (!root.TryGetProperty("choices", out var choices) ||
                    choices.ValueKind != JsonValueKind.Array ||
                    choices.GetArrayLength() == 0)
                {
                    continue;
                }

                string token;
                try
                {
                    var choice = choices[0];
                    CaptureOpenAiChoiceMetadata(choice);
                    token = ExtractOpenAiChoiceText(choice);

                    if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object &&
                        delta.TryGetProperty("tool_calls", out var deltaCalls))
                    {
                        toolCalls.Add(deltaCalls);
                    }
                    else if (choice.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object &&
                             message.TryGetProperty("tool_calls", out var messageCalls))
                    {
                        toolCalls.Add(messageCalls);
                    }
                }
                catch (InvalidOperationException)
                {
                    continue; // Skip chunks with an unexpected shape.
                }

                if (!string.IsNullOrEmpty(token))
                {
                    content.Append(token);
                    onToken?.Invoke(token);
                }
            }
        }

        ct.ThrowIfCancellationRequested();
        return new ChatTurnResult(content.ToString(), toolCalls.Build(), LastFinishReason);
    }

    // Some servers report errors inside the stream ({"error": ...}) after a 200 response.
    private void ThrowIfStreamError(JsonElement root, bool toolsSent)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return;

        if (TryCreateContextOverflow(root.GetRawText()) is { } overflow)
            throw overflow;

        var message = error.ValueKind == JsonValueKind.String
            ? error.GetString() ?? ""
            : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var m)
                ? m.ToString()
                : error.GetRawText();
        if (toolsSent && ChatToolWire.LooksLikeToolsUnsupported(400, message))
            throw new ToolsNotSupportedException($"The model or server does not support tool calling: {message}");

        throw new HttpRequestException($"Backend error: {message}");
    }

    private void CaptureOpenAiUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return;

        if (usage.TryGetProperty("prompt_tokens", out var promptTokens) && promptTokens.TryGetInt32(out var p))
            LastPromptTokens = p;
        if (usage.TryGetProperty("completion_tokens", out var completionTokens) && completionTokens.TryGetInt32(out var c))
            LastCompletionTokens = c;
    }

    // ---- Ping ----
    public async Task<bool> PingAsync()
    {
        if (IsOpenAiCompatible)
            return await PingOpenAiCompatibleAsync();

        try
        {
            var resp = await _http.GetAsync($"{_baseUrl}/api/tags", new CancellationTokenSource(5000).Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ---- Helpers ----
    /// <summary>True for llama.cpp and other OpenAI-compatible servers (false for Ollama).</summary>
    public bool IsOpenAiCompatibleBackend => IsOpenAiCompatible;

    private bool IsOpenAiCompatible => _provider.Equals("OpenAI-compatible", StringComparison.OrdinalIgnoreCase) ||
                                       _provider.Equals("llama.cpp", StringComparison.OrdinalIgnoreCase);

    private async Task<List<string>> ListOpenAiCompatibleModelsAsync()
    {
        try
        {
            using var request = CreateOpenAiRequest(HttpMethod.Get, $"{_openAiBaseUrl}/models");
            var resp = await _http.SendAsync(request);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var arr))
            {
                foreach (var m in arr.EnumerateArray())
                {
                    if (m.TryGetProperty("id", out var id))
                        models.Add(id.GetString() ?? "");
                }
            }
            if (models.Count == 0 && doc.RootElement.TryGetProperty("models", out var modelsArr))
            {
                foreach (var m in modelsArr.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var name))
                        models.Add(name.GetString() ?? "");
                    else if (m.TryGetProperty("model", out var model))
                        models.Add(model.GetString() ?? "");
                }
            }
            return models;
        }
        catch
        {
            return new List<string>();
        }
    }

    private async Task<string> ChatOpenAiCompatibleAsync(string model, List<ChatMessage> messages, string systemPrompt,
        double temperature, int maxTokens, CancellationToken ct)
    {
        using var resp = await SendChatRequestAsync(_openAiBaseUrl, skipThinking => CreateOpenAiChatRequest(
            BuildOpenAiChatBody(model, messages, systemPrompt, temperature, maxTokens, stream: false, skipThinking: skipThinking)),
            HttpCompletionOption.ResponseContentRead, ct);
        await EnsureSuccessWithBodyAsync(resp, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        CaptureOpenAiResponseMetadata(doc.RootElement);
        return doc.RootElement.TryGetProperty("choices", out var choices) &&
               choices.GetArrayLength() > 0
            ? ExtractOpenAiChoiceText(choices[0])
            : "";
    }

    private async Task ChatOpenAiCompatibleStreamAsync(string model, List<ChatMessage> messages, string systemPrompt,
        double temperature, int maxTokens, Action<string> onToken, Action<string> onComplete, Action<Exception> onError,
        CancellationToken ct)
    {
        try
        {
            using var resp = await SendChatRequestAsync(_openAiBaseUrl, skipThinking => CreateOpenAiChatRequest(
                BuildOpenAiChatBody(model, messages, systemPrompt, temperature, maxTokens, stream: true, skipThinking: skipThinking)),
                HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccessWithBodyAsync(resp, ct);

            var fullResponse = new StringBuilder();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) continue;

                var data = line[5..].Trim();
                if (data == "[DONE]") break;

                JsonDocument chunk;
                try { chunk = JsonDocument.Parse(data); }
                catch (JsonException) { continue; /* skip malformed chunks */ }

                using (chunk)
                {
                    // {"error": ...} after a 200 response.
                    if (chunk.RootElement.ValueKind == JsonValueKind.Object)
                        ThrowIfStreamError(chunk.RootElement, toolsSent: false);

                    try
                    {
                        // With stream_options.include_usage the last chunk carries usage and no choices.
                        CaptureOpenAiUsage(chunk.RootElement);
                        if (!chunk.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                            continue;

                        var choice = choices[0];
                        CaptureOpenAiChoiceMetadata(choice);
                        var token = ExtractOpenAiChoiceText(choice);
                        if (!string.IsNullOrEmpty(token))
                        {
                            fullResponse.Append(token);
                            onToken(token);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Skip chunks with an unexpected shape.
                    }
                }
            }

            // Stop/Esc/Clear Chat: no partial reply is completed (saved or spoken).
            ct.ThrowIfCancellationRequested();
            onComplete(fullResponse.ToString());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Stop can surface as an aborted stream (IOException); report it as the cancel it is.
            throw new OperationCanceledException(ct);
        }
        catch (Exception ex) { onError(ex); }
    }

    private async Task<bool> PingOpenAiCompatibleAsync()
    {
        try
        {
            using var request = CreateOpenAiRequest(HttpMethod.Get, $"{_openAiBaseUrl}/models");
            using var resp = await _http.SendAsync(request, new CancellationTokenSource(5000).Token);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ---- Context window detection ----

    // Detected windows are kept this long, so a llama-server restarted with another -c is noticed.
    private static readonly TimeSpan ContextCacheLifetime = TimeSpan.FromMinutes(5);
    // Nothing found (server down, or it does not report a window): ask again sooner.
    private static readonly TimeSpan ContextMissLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ContextProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ContextDetectTimeout = TimeSpan.FromSeconds(20);

    private sealed record ContextProbeTarget(string Provider, bool OpenAiCompatible, string OllamaUrl, string OpenAiUrl,
        string ApiKey, string Model, string EndpointKey)
    {
        public string CacheKey => $"{EndpointKey}|{Model}";
    }

    private sealed record ContextCacheEntry(Task<ServerContextWindow> Detection, DateTime StartedUtc);

    private sealed record ReportedContext(int Tokens, bool IsLlamaCpp, DateTime ReportedUtc);

    private readonly object _contextLock = new();
    // Keyed by provider, endpoints and model. An entry holds the detection task, so concurrent requests
    // share one round of probes.
    private readonly Dictionary<string, ContextCacheEntry> _contextCache = new(StringComparer.OrdinalIgnoreCase);
    // n_ctx a server put in a context overflow error, per endpoint, for when probing finds nothing better.
    private readonly Dictionary<string, ReportedContext> _reportedContext = new(StringComparer.OrdinalIgnoreCase);
    private string _lastLoggedContext = "";
    private string? _lastContextModel;

    private string EndpointKey => $"{_provider}|{_baseUrl}|{_openAiBaseUrl}";

    /// <summary>
    /// The context window the server reports for <paramref name="model"/>: llama.cpp's per-slot n_ctx
    /// (/props, then /slots), another server's runtime field (vLLM max_model_len, LM Studio), Ollama's
    /// model maximum (/api/show), or a known limit on a cloud API. Nothing is guessed for a local or
    /// self-hosted server: when it reports nothing, <see cref="ServerContextWindow.Tokens"/> is null and
    /// the Context window setting applies. Results are kept for five minutes (one minute when nothing was
    /// found) per provider, endpoint and model, and dropped when any of them changes;
    /// <paramref name="forceRefresh"/> asks the server again.
    /// Server problems never throw; <paramref name="ct"/> only stops waiting.
    /// </summary>
    public Task<ServerContextWindow> DetectContextWindowAsync(string model, bool forceRefresh = false, CancellationToken ct = default)
    {
        Task<ServerContextWindow> detection;
        lock (_contextLock)
        {
            var target = new ContextProbeTarget(_provider, IsOpenAiCompatible, _baseUrl, _openAiBaseUrl, _openAiApiKey,
                (model ?? "").Trim(), EndpointKey);

            if (forceRefresh)
                _reportedContext.Remove(target.EndpointKey);
            // Another model may mean a restarted server (llama-server loads one model, with its own -c).
            if (!string.Equals(_lastContextModel, target.Model, StringComparison.OrdinalIgnoreCase))
            {
                _contextCache.Clear();
                _lastContextModel = target.Model;
            }

            if (!forceRefresh && _contextCache.TryGetValue(target.CacheKey, out var entry) && !IsExpired(entry))
            {
                detection = entry.Detection;
            }
            else
            {
                // Off the caller's thread (the UI thread for the settings line), and not tied to one
                // caller's cancellation, since other requests may wait for the same detection.
                detection = Task.Run(() => DetectContextWindowCoreAsync(target));
                _contextCache[target.CacheKey] = new ContextCacheEntry(detection, DateTime.UtcNow);
            }
        }

        return detection.WaitAsync(ct);
    }

    /// <summary>Forgets every detected context window, so the next request asks the server again.</summary>
    public void InvalidateContextWindowCache()
    {
        lock (_contextLock)
            _contextCache.Clear();
    }

    private static bool IsExpired(ContextCacheEntry entry)
    {
        if (!entry.Detection.IsCompleted)
            return false; // Still probing: share it.
        if (!entry.Detection.IsCompletedSuccessfully)
            return true;

        var lifetime = entry.Detection.Result.Tokens is null ? ContextMissLifetime : ContextCacheLifetime;
        return DateTime.UtcNow - entry.StartedUtc > lifetime;
    }

    private async Task<ServerContextWindow> DetectContextWindowCoreAsync(ContextProbeTarget target)
    {
        ServerContextWindow result;
        using var timeout = new CancellationTokenSource(ContextDetectTimeout);
        try
        {
            result = target.OpenAiCompatible
                ? await DetectOpenAiCompatibleContextAsync(target, timeout.Token).ConfigureAwait(false)
                : await DetectOllamaContextAsync(target, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Context window detection failed", ex);
            result = ServerContextWindow.NotDetected(reachable: false);
        }

        result = ApplyReportedContext(target, result);
        LogContextWindowIfChanged(target, result);
        return result;
    }

    private async Task<ServerContextWindow> DetectOpenAiCompatibleContextAsync(ContextProbeTarget target, CancellationToken ct)
    {
        var baseUrl = target.OpenAiUrl.TrimEnd('/');
        // llama.cpp serves /props and /slots at the server root, next to /v1.
        var root = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? baseUrl[..^3].TrimEnd('/') : baseUrl;
        var roots = root.Equals(baseUrl, StringComparison.OrdinalIgnoreCase) ? new[] { root } : new[] { root, baseUrl };
        var reachable = false;

        // 1. llama.cpp /props: default_generation_settings.n_ctx, the window of one slot.
        foreach (var url in roots.Select(r => r + "/props"))
        {
            var props = await ProbeAsync(HttpMethod.Get, url, target.ApiKey, null, ct).ConfigureAwait(false);
            // No answer at all: the other URLs are on the same server, so do not make a request wait for them.
            if (props.Status == 0 && !reachable)
                return ServerContextWindow.NotDetected(reachable: false);
            reachable = true;
            var tokens = ContextWindowParser.FromLlamaCppProps(props.Body, out var totalSlots);
            if (tokens is null && target.Model.Length > 0 && (props.Body is not null || props.Status == 400))
            {
                // llama-server in router mode (several models) answers /props for one model at a time.
                var routed = await ProbeAsync(HttpMethod.Get, $"{url}?model={Uri.EscapeDataString(target.Model)}",
                    target.ApiKey, null, ct).ConfigureAwait(false);
                tokens = ContextWindowParser.FromLlamaCppProps(routed.Body, out totalSlots);
            }
            if (tokens is int slotContext)
            {
                return new ServerContextWindow(slotContext, ContextWindowSource.LlamaCppProps,
                    ContextWindowParser.LlamaCppServerName, totalSlots);
            }
        }

        // 2. llama.cpp /slots: the smallest slot n_ctx.
        foreach (var url in roots.Select(r => r + "/slots"))
        {
            var slots = await ProbeAsync(HttpMethod.Get, url, target.ApiKey, null, ct).ConfigureAwait(false);
            reachable |= slots.Status != 0;
            if (ContextWindowParser.FromLlamaCppSlots(slots.Body, out var slotCount) is int slotContext)
            {
                return new ServerContextWindow(slotContext, ContextWindowSource.LlamaCppSlots,
                    ContextWindowParser.LlamaCppServerName, slotCount);
            }
        }

        // 3. Other servers' runtime fields: vLLM max_model_len in /v1/models, LM Studio's loaded context length.
        var models = await ProbeAsync(HttpMethod.Get, $"{baseUrl}/models", target.ApiKey, null, ct).ConfigureAwait(false);
        reachable |= models.Status != 0;
        if (ContextWindowParser.FromServerMetadata(models.Body, target.Model) is int modelsContext)
        {
            var name = ContextWindowParser.ServerNameFromModels(models.Body);
            return new ServerContextWindow(modelsContext, ContextWindowSource.ServerMetadata, name.Length > 0 ? name : "Server");
        }

        var isCloud = ContextWindowParser.IsKnownCloudEndpoint(baseUrl);
        if (!isCloud && reachable)
        {
            var lmStudio = await ProbeAsync(HttpMethod.Get, $"{root}/api/v0/models", target.ApiKey, null, ct).ConfigureAwait(false);
            if (ContextWindowParser.FromServerMetadata(lmStudio.Body, target.Model) is int lmStudioContext)
                return new ServerContextWindow(lmStudioContext, ContextWindowSource.ServerMetadata, "LM Studio");
        }

        // 4. Hosted APIs only: a known limit for the model name.
        if (isCloud && ContextWindowParser.GuessCloudModelContext(baseUrl, target.Model) is int known)
            return new ServerContextWindow(known, ContextWindowSource.KnownModel, Reachable: reachable);

        return ServerContextWindow.NotDetected(reachable);
    }

    private async Task<ServerContextWindow> DetectOllamaContextAsync(ContextProbeTarget target, CancellationToken ct)
    {
        if (target.Model.Length == 0)
            return ServerContextWindow.NotDetected(reachable: true, "Ollama");

        using var content = new StringContent(JsonSerializer.Serialize(new { model = target.Model }), Encoding.UTF8, "application/json");
        var show = await ProbeAsync(HttpMethod.Post, $"{target.OllamaUrl}/api/show", "", content, ct).ConfigureAwait(false);
        return ContextWindowParser.FromOllamaShow(show.Body) is int modelMax
            ? new ServerContextWindow(modelMax, ContextWindowSource.OllamaModel, "Ollama")
            : ServerContextWindow.NotDetected(show.Status != 0, "Ollama");
    }

    /// <summary>One metadata request with a short timeout. Status 0 = no answer; Body is null unless it succeeded.</summary>
    private async Task<(int Status, string? Body)> ProbeAsync(HttpMethod method, string url, string apiKey, HttpContent? content,
        CancellationToken ct)
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probe.CancelAfter(ContextProbeTimeout);
        try
        {
            using var request = new HttpRequestMessage(method, url);
            if (content is not null)
                request.Content = content;
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

            using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, probe.Token).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
                return (status, null);

            return (status, await resp.Content.ReadAsStringAsync(probe.Token).ConfigureAwait(false));
        }
        catch (Exception)
        {
            return (0, null); // Unreachable, timed out or refused: try the next source.
        }
    }

    // An n_ctx the server reported in an overflow error caps what probing found, unless llama.cpp itself
    // reported a value (then the probe is current: a restarted server may run with another -c).
    private ServerContextWindow ApplyReportedContext(ContextProbeTarget target, ServerContextWindow result)
    {
        ReportedContext? reported;
        lock (_contextLock)
        {
            if (!_reportedContext.TryGetValue(target.EndpointKey, out reported))
                return result;
            if (DateTime.UtcNow - reported.ReportedUtc > ContextCacheLifetime)
            {
                _reportedContext.Remove(target.EndpointKey);
                return result;
            }
        }

        if (result.Source is ContextWindowSource.LlamaCppProps or ContextWindowSource.LlamaCppSlots)
            return result;
        if (result.IsServerWindow && result.Tokens <= reported.Tokens)
            return result;

        var name = reported.IsLlamaCpp
            ? ContextWindowParser.LlamaCppServerName
            : result.IsServerWindow ? result.ServerName : target.OpenAiCompatible ? "Server" : "Ollama";
        return new ServerContextWindow(reported.Tokens, ContextWindowSource.ServerError, name);
    }

    private void LogContextWindowIfChanged(ContextProbeTarget target, ServerContextWindow result)
    {
        var summary = result.Tokens is int tokens
            ? $"{tokens:N0} tokens from {DescribeSource(result)}"
            : result.Reachable
                ? "not reported, so the Context window setting is used"
                : "server not reachable, so the Context window setting is used";
        var logKey = $"{target.CacheKey}|{summary}";
        lock (_contextLock)
        {
            if (logKey == _lastLoggedContext)
                return;
            _lastLoggedContext = logKey;
        }

        var endpoint = target.OpenAiCompatible ? target.OpenAiUrl : target.OllamaUrl;
        var model = target.Model.Length > 0 ? $", model {target.Model}" : "";
        AppLog.Info($"Context window for {target.Provider} at {endpoint}{model}: {summary}.");
    }

    private static string DescribeSource(ServerContextWindow window) => window.Source switch
    {
        ContextWindowSource.LlamaCppProps => window.Slots > 1 ? $"llama.cpp /props ({window.Slots} slots)" : "llama.cpp /props",
        ContextWindowSource.LlamaCppSlots => $"llama.cpp /slots ({window.Slots} slot(s))",
        ContextWindowSource.ServerMetadata => $"{window.ServerName} metadata",
        ContextWindowSource.ServerError => "the server's context overflow error",
        ContextWindowSource.OllamaModel => "the Ollama model (its maximum, capped by the setting)",
        ContextWindowSource.KnownModel => "the known limit for this model (capped by the setting)",
        _ => "nowhere",
    };

    /// <summary>
    /// Turns an error body that says the request did not fit the context window into a
    /// <see cref="ContextOverflowException"/>, and drops the cached windows so the next request (and the
    /// caller's retry) detects again; an n_ctx in the error caps what the server reports otherwise.
    /// </summary>
    private ContextOverflowException? TryCreateContextOverflow(string? body)
    {
        if (!ContextOverflow.TryParse(body, out var info))
            return null;

        lock (_contextLock)
        {
            _contextCache.Clear();
            if (info.ServerContextTokens is int serverWindow)
                _reportedContext[EndpointKey] = new ReportedContext(serverWindow, info.IsLlamaCpp, DateTime.UtcNow);
        }

        var window = info.ServerContextTokens is int n ? $"{n:N0}-token window" : "context window";
        var prompt = info.PromptTokens is int p ? $" ({p:N0} prompt tokens)" : "";
        AppLog.Info($"The chat server rejected a request as too long for its {window}{prompt}: {info.Message} " +
                    "The context window is detected again.");
        return new ContextOverflowException(info);
    }

    private HttpRequestMessage CreateOllamaChatRequest(object body) =>
        new(HttpMethod.Post, $"{_baseUrl}/api/chat")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

    private HttpRequestMessage CreateOpenAiChatRequest(object body)
    {
        var request = CreateOpenAiRequest(HttpMethod.Post, $"{_openAiBaseUrl}/chat/completions");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>
    /// Sends a chat request made by <paramref name="createRequest"/> (true = with the fields that skip
    /// the model's thinking, see <see cref="DisableThinking"/>). When the server answers HTTP 400 to a
    /// request with those fields, it is sent once more without them; when that works, they are left out
    /// for that server for the rest of the session.
    /// </summary>
    private async Task<HttpResponseMessage> SendChatRequestAsync(string baseUrl, Func<bool, HttpRequestMessage> createRequest,
        HttpCompletionOption completion, CancellationToken ct)
    {
        var skipThinking = ShouldSendSkipThinkingFields(baseUrl);
        var response = await SendAsync(createRequest(skipThinking)).ConfigureAwait(false);
        if (!skipThinking || response.StatusCode != HttpStatusCode.BadRequest)
            return response;

        string problem;
        try { problem = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { problem = ""; }
        finally { response.Dispose(); }
        // Too long for the context window: sending it again without the thinking fields would not help.
        if (TryCreateContextOverflow(problem) is { } overflow)
            throw overflow;
        if (problem.Length > 300)
            problem = problem[..300] + "...";

        var retry = await SendAsync(createRequest(false)).ConfigureAwait(false);
        if (retry.IsSuccessStatusCode)
        {
            lock (_thinkingFieldsRejected)
                _thinkingFieldsRejected.Add(baseUrl);
            AppLog.Info($"The chat server at {baseUrl} rejected the request to skip model thinking (HTTP 400: {problem}). " +
                        "Requests to it are sent without it for the rest of this session.");
        }
        return retry;

        async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            using (request)
                return await _http.SendAsync(request, completion, ct).ConfigureAwait(false);
        }
    }

    private bool ShouldSendSkipThinkingFields(string baseUrl)
    {
        if (!DisableThinking)
            return false;
        // OpenAI's own API rejects fields it does not know.
        if (IsOpenAiCompatible && Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
            uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase))
            return false;

        lock (_thinkingFieldsRejected)
            return !_thinkingFieldsRejected.Contains(baseUrl);
    }

    private HttpRequestMessage CreateOpenAiRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(_openAiApiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _openAiApiKey);
        return request;
    }

    private async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response, CancellationToken ct, bool toolsSent = false)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode is >= 400 and < 500 && TryCreateContextOverflow(body) is { } overflow)
            throw overflow;
        var detail = string.IsNullOrWhiteSpace(body)
            ? response.ReasonPhrase ?? ""
            : body.Trim();
        if (detail.Length > 1000)
            detail = detail[..1000] + "...";

        if (toolsSent && ChatToolWire.LooksLikeToolsUnsupported((int)response.StatusCode, body))
            throw new ToolsNotSupportedException($"The model or server does not support tool calling: {detail}");

        throw new HttpRequestException(
            $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). {detail}");
    }

    private static object BuildChatBody(string model, List<ChatMessage> messages, string systemPrompt,
        double temperature, int maxTokens, int contextTokens, bool stream, IReadOnlyList<ToolSpec>? tools = null,
        bool skipThinking = false)
    {
        var msgList = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            msgList.Add(new { role = "system", content = systemPrompt });
        foreach (var m in messages)
        {
            if (m.HasToolData())
                msgList.Add(ChatToolWire.OllamaMessage(m.Role, m.Content, m.ImagesBase64, m.ToolCalls, m.ToolName));
            else if (m.ImagesBase64.Count > 0)
                msgList.Add(new { role = m.Role, content = m.Content, images = m.ImagesBase64 });
            else
                msgList.Add(new { role = m.Role, content = m.Content });
        }

        var optionsDict = new System.Collections.Generic.Dictionary<string, object>() { { "temperature", temperature } };
        if (IsGemma412BModel(model))
        {
            optionsDict["top_p"] = 0.9;
            optionsDict["repeat_penalty"] = 1.05;
        }
        if (maxTokens != 0)
            optionsDict["num_predict"] = maxTokens;
        if (contextTokens != 0)
            optionsDict["num_ctx"] = contextTokens;

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = msgList,
            ["stream"] = stream,
            ["options"] = optionsDict
        };
        if (tools is { Count: > 0 })
            body["tools"] = ChatToolWire.ToolsPayload(tools);
        // Thinking models (Qwen3, DeepSeek R1, gpt-oss...) answer without their thinking phase.
        if (skipThinking)
            body["think"] = false;

        return body;
    }

    private static object BuildOpenAiChatBody(string model, List<ChatMessage> messages, string systemPrompt,
        double temperature, int maxTokens, bool stream, IReadOnlyList<ToolSpec>? tools = null, bool skipThinking = false)
    {
        var msgList = new List<object>();
        var isGemma412B = IsGemma412BModel(model);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            msgList.Add(new { role = "system", content = systemPrompt });

        foreach (var m in messages)
        {
            if (m.HasToolData())
            {
                msgList.Add(ChatToolWire.OpenAiMessage(m.Role, m.Content, m.ToolCalls, m.ToolCallId));
            }
            else if (m.ImagesBase64.Count > 0)
            {
                var parts = new List<object>();
                foreach (var image in m.ImagesBase64)
                    parts.Add(new { type = "image_url", image_url = new { url = $"data:image/jpeg;base64,{image}" } });
                parts.Add(new { type = "text", text = m.Content });

                msgList.Add(new { role = m.Role, content = parts });
            }
            else
            {
                msgList.Add(new { role = m.Role, content = m.Content });
            }
        }

        var body = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = msgList,
            ["stream"] = stream,
            ["temperature"] = temperature
        };
        if (stream)
            body["stream_options"] = new { include_usage = true };
        if (maxTokens != 0)
            body["max_tokens"] = maxTokens;
        if (isGemma412B)
        {
            body["top_p"] = 0.9;
            body["repeat_penalty"] = 1.05;
        }
        if (tools is { Count: > 0 })
            body["tools"] = ChatToolWire.ToolsPayload(tools);
        if (skipThinking)
        {
            // llama.cpp: the chat template skips the thinking phase, and thinking the model still writes
            // is moved to reasoning_content, which ExtractOpenAiChoiceText ignores.
            body["chat_template_kwargs"] = new Dictionary<string, object> { ["enable_thinking"] = false };
            body["reasoning_format"] = "deepseek";
        }

        return body;
    }

    private static string ExtractOpenAiChoiceText(JsonElement choice)
    {
        if (choice.TryGetProperty("delta", out var delta))
        {
            var text = ExtractOpenAiContentText(delta);
            if (!string.IsNullOrEmpty(text))
                return text;
        }

        if (choice.TryGetProperty("message", out var message))
        {
            var text = ExtractOpenAiContentText(message);
            if (!string.IsNullOrEmpty(text))
                return text;
        }

        if (choice.TryGetProperty("text", out var textElement))
            return ExtractJsonText(textElement);

        return ExtractOpenAiContentText(choice);
    }

    private static string ExtractOpenAiContentText(JsonElement element)
    {
        if (element.TryGetProperty("content", out var content))
        {
            var text = ExtractJsonText(content);
            if (!string.IsNullOrEmpty(text))
                return text;
        }

        return "";
    }

    private static string ExtractJsonText(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Array => string.Concat(element.EnumerateArray().Select(ExtractJsonText)),
            JsonValueKind.Object when element.TryGetProperty("text", out var text) => ExtractJsonText(text),
            JsonValueKind.Object when element.TryGetProperty("content", out var content) => ExtractJsonText(content),
            _ => ""
        };
    }

    private static bool IsGemma412BModel(string model)
    {
        var normalized = (model ?? "").ToLowerInvariant();
        return normalized.Contains("gemma", StringComparison.Ordinal) &&
               (normalized.Contains("12b", StringComparison.Ordinal) ||
                System.Text.RegularExpressions.Regex.IsMatch(normalized, @"\b12\s*b\b"));
    }

    private void ResetLastResponseMetadata()
    {
        LastFinishReason = "";
        LastStopReason = "";
        LastPromptTokens = null;
        LastCompletionTokens = null;
    }

    private void CaptureOllamaResponseMetadata(JsonElement root)
    {
        if (root.TryGetProperty("done_reason", out var doneReason))
            LastFinishReason = doneReason.GetString() ?? "";
        if (root.TryGetProperty("prompt_eval_count", out var promptEval) && promptEval.TryGetInt32(out var promptTokens))
            LastPromptTokens = promptTokens;
        if (root.TryGetProperty("eval_count", out var evalCount) && evalCount.TryGetInt32(out var completionTokens))
            LastCompletionTokens = completionTokens;
    }

    private void CaptureOpenAiResponseMetadata(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            CaptureOpenAiChoiceMetadata(choices[0]);

        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var promptTokens) && promptTokens.TryGetInt32(out var p))
                LastPromptTokens = p;
            if (usage.TryGetProperty("completion_tokens", out var completionTokens) && completionTokens.TryGetInt32(out var c))
                LastCompletionTokens = c;
        }
    }

    private void CaptureOpenAiChoiceMetadata(JsonElement choice)
    {
        if (choice.TryGetProperty("finish_reason", out var finishReason) &&
            finishReason.ValueKind != JsonValueKind.Null)
        {
            LastFinishReason = finishReason.GetString() ?? "";
        }

        if (choice.TryGetProperty("stop_reason", out var stopReason) &&
            stopReason.ValueKind != JsonValueKind.Null)
        {
            LastStopReason = stopReason.ToString();
        }
    }

    public void Dispose() => _http.Dispose();
}
