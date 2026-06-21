using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private readonly Dictionary<string, int> _contextTokenCache = new(StringComparer.OrdinalIgnoreCase);
    private string _baseUrl = "http://localhost:11434";
    private string _openAiBaseUrl = "http://localhost:8080/v1";
    private string _openAiApiKey = "";

    public string Provider { get; set; } = "Ollama";
    public string LastFinishReason { get; private set; } = "";
    public string LastStopReason { get; private set; } = "";
    public int? LastPromptTokens { get; private set; }
    public int? LastCompletionTokens { get; private set; }

    public string BaseUrl
    {
        get => _baseUrl;
        set => _baseUrl = value.TrimEnd('/');
    }

    public async Task<int?> GetModelContextTokensAsync(string model, CancellationToken ct = default)
    {
        var cacheKey = $"{Provider}|{_baseUrl}|{_openAiBaseUrl}|{model}";
        if (_contextTokenCache.TryGetValue(cacheKey, out var cached))
            return cached;

        int? detected = IsOpenAiCompatible
            ? await TryGetOpenAiCompatibleContextTokensAsync(ct)
            : await TryGetOllamaContextTokensAsync(model, ct);
        detected ??= GuessKnownContextTokens(model);

        if (detected is int tokens && tokens > 0)
            _contextTokenCache[cacheKey] = tokens;

        return detected;
    }

    public string OpenAiBaseUrl
    {
        get => _openAiBaseUrl;
        set => _openAiBaseUrl = value.TrimEnd('/');
    }

    public string OpenAiApiKey
    {
        get => _openAiApiKey;
        set => _openAiApiKey = value;
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

        var body = BuildChatBody(model, messages, systemPrompt, temperature, maxTokens, contextTokens, stream: false);
        var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{_baseUrl}/api/chat", content, ct);
        await EnsureSuccessWithBodyAsync(resp, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        CaptureOllamaResponseMetadata(doc.RootElement);
        return doc.RootElement.TryGetProperty("message", out var msg) &&
               msg.TryGetProperty("content", out var c)
            ? c.GetString() ?? ""
            : "";
    }

    public async Task<StructuredTextMessageResult> PrepareTextMessageAsync(
        string model,
        string userRequest,
        CancellationToken ct = default)
    {
        if (!IsOpenAiCompatible)
            throw new InvalidOperationException(
                "Structured text-message preparation requires the OpenAI-compatible provider.");

        var body = new
        {
            model,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content =
                        "Determine whether the user wants to compose an SMS/iMessage. " +
                        "If true, copy only the recipient wording the user actually spoke into recipient; " +
                        "never invent, expand, or list alternate contact spellings because the iPhone resolves " +
                        "the recipient against the user's real contacts after this step. Draft the message body. " +
                        "When the user asks for detailed information, write the complete useful " +
                        "message using your knowledge. Never use placeholders such as 'insert details here'. " +
                        "Include only facts you are confident are accurate; omit uncertain details and never " +
                        "invent names, dates, credits, statistics, quotations, or current status. " +
                        "Resolve obvious speech-recognition or phonetic misspellings of well-known subjects when " +
                        "the intended subject is reasonably clear. Treat 'McAvelli', 'Machiavelli', and similar " +
                        "phonetic forms as Niccolo Machiavelli unless the user explicitly identifies a different " +
                        "person, company, or product. Never invent a company, product, person, or biography to " +
                        "explain an unfamiliar term. Do not ask the user to narrow a broad topic; " +
                        "write a useful concise overview instead. Set needsClarification true only when the user " +
                        "genuinely omitted the recipient or omitted what the message should say. Never combine " +
                        "recipient clarification with content clarification. Do not add a signature or claim " +
                        "anything was sent. Examples: 'say hello to Jane Cook send a message' means recipient " +
                        "'Jane Cook' and body 'Hello!'; 'send a message to Keith Gagman with details on the " +
                        "world's deadliest spiders' means recipient 'Keith Gagman' and a complete informative " +
                        "body about that topic; 'text Jane' is missing content and may ask what to say."
                },
                new { role = "user", content = userRequest }
            },
            temperature = 0.1,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "text_message_request",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            isTextMessage = new { type = "boolean" },
                            recipient = new { type = "string" },
                            body = new { type = "string" },
                            needsClarification = new { type = "boolean" },
                            clarificationQuestion = new { type = "string" }
                        },
                        required = new[]
                        {
                            "isTextMessage",
                            "recipient",
                            "body",
                            "needsClarification",
                            "clarificationQuestion"
                        },
                        additionalProperties = false
                    }
                }
            }
        };

        using var request = CreateOpenAiRequest(
            HttpMethod.Post,
            $"{_openAiBaseUrl}/chat/completions");
        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessWithBodyAsync(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(json);
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
        var result = JsonSerializer.Deserialize<StructuredTextMessageResult>(
            content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (result == null)
            throw new InvalidOperationException(
                "The model returned an empty structured text-message result.");
        if (result.IsTextMessage &&
            !string.IsNullOrWhiteSpace(result.Recipient) &&
            !string.IsNullOrWhiteSpace(result.Body))
        {
            return result with
            {
                NeedsClarification = false,
                ClarificationQuestion = ""
            };
        }
        return result;
    }

    public async Task<StructuredCalendarEventResult> PrepareCalendarEventAsync(
        string model,
        string userRequest,
        string currentDateTime,
        string timeZone,
        CancellationToken ct = default)
    {
        if (!IsOpenAiCompatible)
            throw new InvalidOperationException(
                "Structured calendar preparation requires the OpenAI-compatible provider.");

        var prompt =
            $"Current local date and time: {currentDateTime}\n" +
            $"Time zone: {timeZone}\n" +
            $"User request: {userRequest}";
        var body = new
        {
            model,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content =
                        "Convert the user's request into exactly one calendar event. " +
                        "Resolve relative dates from the supplied current local date, time, and time zone. " +
                        "When the year is omitted, choose the next future occurrence. " +
                        "Infer a concise title and use a one-hour duration when no duration or end is given. " +
                        "Use ISO-8601 timestamps with an explicit UTC offset. " +
                        "Put only useful supplied details in notes; never invent people, places, or facts."
                },
                new { role = "user", content = prompt }
            },
            temperature = 0.1,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "calendar_event",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            title = new { type = "string" },
                            start = new { type = "string" },
                            end = new { type = "string" },
                            notes = new { type = "string" }
                        },
                        required = new[] { "title", "start", "end", "notes" },
                        additionalProperties = false
                    }
                }
            }
        };

        var result = await SendStructuredOpenAiRequestAsync<StructuredCalendarEventResult>(
            body,
            "calendar event",
            ct);
        if (string.IsNullOrWhiteSpace(result.Title) ||
            !DateTimeOffset.TryParse(result.Start, out var start) ||
            !DateTimeOffset.TryParse(result.End, out var end) ||
            end <= start)
        {
            throw new InvalidOperationException(
                "The model returned an invalid calendar title or date range.");
        }
        return result;
    }

    public async Task<StructuredGroundedAnswerResult> AnswerGroundedQuestionAsync(
        string model,
        string prompt,
        CancellationToken ct = default)
    {
        if (!IsOpenAiCompatible)
            throw new InvalidOperationException(
                "Grounded iPhone answers require the OpenAI-compatible provider.");

        var body = new
        {
            model,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content =
                        "Answer from only the private live data and instructions in the user prompt. " +
                        "Never invent missing values. Return a clear, natural spoken answer."
                },
                new { role = "user", content = prompt }
            },
            temperature = 0.2,
            response_format = new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "grounded_answer",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            answer = new { type = "string" }
                        },
                        required = new[] { "answer" },
                        additionalProperties = false
                    }
                }
            }
        };

        var result = await SendStructuredOpenAiRequestAsync<StructuredGroundedAnswerResult>(
            body,
            "grounded answer",
            ct);
        if (string.IsNullOrWhiteSpace(result.Answer))
            throw new InvalidOperationException("The model returned an empty grounded answer.");
        return result;
    }

    private async Task<T> SendStructuredOpenAiRequestAsync<T>(
        object body,
        string resultName,
        CancellationToken ct)
    {
        using var request = CreateOpenAiRequest(
            HttpMethod.Post,
            $"{_openAiBaseUrl}/chat/completions");
        request.Content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessWithBodyAsync(response, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        using var document = JsonDocument.Parse(json);
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
        var result = JsonSerializer.Deserialize<T>(
            content,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return result ?? throw new InvalidOperationException(
            $"The model returned an empty structured {resultName} result.");
    }

    // ---- Chat (streaming) ----
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
            var body = BuildChatBody(model, messages, systemPrompt, temperature, maxTokens, contextTokens, stream: true);
            var reqContent = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat") { Content = reqContent };

            using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            await EnsureSuccessWithBodyAsync(resp, ct);

            var fullResponse = new StringBuilder();
            using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    using var chunk = JsonDocument.Parse(line);
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
                catch { /* skip malformed chunks */ }
            }

            onComplete(fullResponse.ToString());
        }
        catch (OperationCanceledException) { onComplete(""); }
        catch (Exception ex) { onError(ex); }
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
    private bool IsOpenAiCompatible => Provider.Equals("OpenAI-compatible", StringComparison.OrdinalIgnoreCase) ||
                                       Provider.Equals("llama.cpp", StringComparison.OrdinalIgnoreCase);

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
        var body = BuildOpenAiChatBody(model, messages, systemPrompt, temperature, maxTokens, stream: false);
        using var request = CreateOpenAiRequest(HttpMethod.Post, $"{_openAiBaseUrl}/chat/completions");
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(request, ct);
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
            var body = BuildOpenAiChatBody(model, messages, systemPrompt, temperature, maxTokens, stream: true);
            using var request = CreateOpenAiRequest(HttpMethod.Post, $"{_openAiBaseUrl}/chat/completions");
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
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

                try
                {
                    using var chunk = JsonDocument.Parse(data);
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
                catch
                {
                    // Skip malformed chunks.
                }
            }

            onComplete(fullResponse.ToString());
        }
        catch (OperationCanceledException) { onComplete(""); }
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

    private async Task<int?> TryGetOllamaContextTokensAsync(string model, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;

        try
        {
            var body = new { model };
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync($"{_baseUrl}/api/show", content, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return ExtractContextTokens(doc.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private async Task<int?> TryGetOpenAiCompatibleContextTokensAsync(CancellationToken ct)
    {
        foreach (var url in GetOpenAiCompatibleMetadataUrls())
        {
            try
            {
                using var request = CreateOpenAiRequest(HttpMethod.Get, url);
                using var resp = await _http.SendAsync(request, ct);
                if (!resp.IsSuccessStatusCode)
                    continue;

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var tokens = ExtractContextTokens(doc.RootElement);
                if (tokens is > 0)
                    return tokens;
            }
            catch
            {
                // Try the next metadata endpoint.
            }
        }

        return null;
    }

    private IEnumerable<string> GetOpenAiCompatibleMetadataUrls()
    {
        var baseUrl = _openAiBaseUrl.TrimEnd('/');
        yield return $"{baseUrl}/props";
        yield return $"{baseUrl}/slots";

        if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            var root = baseUrl[..^3].TrimEnd('/');
            yield return $"{root}/props";
            yield return $"{root}/slots";
        }
    }

    private static int? ExtractContextTokens(JsonElement element)
    {
        var candidates = new List<int>();
        Visit(element, "");
        return candidates.Count == 0 ? null : candidates.Max();

        void Visit(JsonElement current, string name)
        {
            switch (current.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in current.EnumerateObject())
                        Visit(property.Value, property.Name);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in current.EnumerateArray())
                        Visit(item, name);
                    break;
                case JsonValueKind.Number:
                    if (current.TryGetInt32(out var number) && LooksLikeContextKey(name) && IsReasonableContextLength(number))
                        candidates.Add(number);
                    break;
                case JsonValueKind.String:
                    var value = current.GetString() ?? "";
                    if (LooksLikeContextKey(name) && int.TryParse(value, out var stringNumber) && IsReasonableContextLength(stringNumber))
                        candidates.Add(stringNumber);
                    ExtractContextFromParameterText(value, candidates);
                    break;
            }
        }
    }

    private static bool LooksLikeContextKey(string name)
    {
        var key = name.Replace(".", "_", StringComparison.Ordinal).Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
        return key is "n_ctx" or "num_ctx" or "ctx_size" or "context_size" or "context_length" or "max_context_length" or "max_position_embeddings" ||
               (key.Contains("context", StringComparison.Ordinal) && key.Contains("length", StringComparison.Ordinal));
    }

    private static void ExtractContextFromParameterText(string text, List<int> candidates)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            var parts = trimmed.Split([' ', '\t', '='], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !LooksLikeContextKey(parts[0]))
                continue;

            if (int.TryParse(parts[^1], out var number) && IsReasonableContextLength(number))
                candidates.Add(number);
        }
    }

    private static bool IsReasonableContextLength(int value) => value is >= 4096 and <= 2_000_000;

    private static int? GuessKnownContextTokens(string model)
    {
        var normalized = (model ?? "").ToLowerInvariant();
        if (normalized.Contains("gemma") && (normalized.Contains("4") || normalized.Contains("3")))
            return 262144;
        if (normalized.Contains("gpt-4.1") || normalized.Contains("gpt-4o") || normalized.Contains("gpt-5"))
            return 131072;
        if (normalized.Contains("claude"))
            return 200000;
        return null;
    }

    private HttpRequestMessage CreateOpenAiRequest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(_openAiApiKey))
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _openAiApiKey);
        return request;
    }

    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = string.IsNullOrWhiteSpace(body)
            ? response.ReasonPhrase ?? ""
            : body.Trim();
        if (detail.Length > 1000)
            detail = detail[..1000] + "...";

        throw new HttpRequestException(
            $"Response status code does not indicate success: {(int)response.StatusCode} ({response.ReasonPhrase}). {detail}");
    }

    private static object BuildChatBody(string model, List<ChatMessage> messages, string systemPrompt,
        double temperature, int maxTokens, int contextTokens, bool stream)
    {
        var msgList = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            msgList.Add(new { role = "system", content = systemPrompt });
        foreach (var m in messages)
        {
            if (m.ImagesBase64.Count > 0)
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

        return new
        {
            model,
            messages = msgList,
            stream,
            options = optionsDict
        };
    }

    private static object BuildOpenAiChatBody(string model, List<ChatMessage> messages, string systemPrompt,
        double temperature, int maxTokens, bool stream)
    {
        var msgList = new List<object>();
        var isGemma412B = IsGemma412BModel(model);
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            msgList.Add(new { role = "system", content = systemPrompt });

        foreach (var m in messages)
        {
            if (m.ImagesBase64.Count > 0)
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
        if (maxTokens != 0)
            body["max_tokens"] = maxTokens;
        if (isGemma412B)
        {
            body["top_p"] = 0.9;
            body["repeat_penalty"] = 1.05;
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

public sealed record StructuredTextMessageResult(
    bool IsTextMessage,
    string Recipient,
    string Body,
    bool NeedsClarification,
    string ClarificationQuestion);

public sealed record StructuredCalendarEventResult(
    string Title,
    string Start,
    string End,
    string Notes);

public sealed record StructuredGroundedAnswerResult(string Answer);
