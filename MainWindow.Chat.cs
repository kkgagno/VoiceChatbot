using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Cv2 = OpenCvSharp.Cv2;
using Mat = OpenCvSharp.Mat;

namespace VoiceChatbot;

public partial class MainWindow
{
    // ==================== Chat ====================

    /// <summary>
    /// Builds the effective system prompt with conversation memories injected.
    /// </summary>
    private string GetEffectiveSystemPrompt(string? currentUserText = null)
    {
        var basePrompt = SystemPromptBox.Text;
        if (IsCodeOrScriptRequest(currentUserText))
        {
            basePrompt += "\n\n" + GetCodeArtifactSystemInstruction(currentUserText);
        }

        var identityContext = GetLastIdentifiedUserSystemContext();
        if (!string.IsNullOrWhiteSpace(identityContext))
            basePrompt += "\n\n" + identityContext;

        if (_settings.FaceFeatures.FaceGatingEnabled)
            basePrompt += "\n\n" + GetFaceIdentitySystemContext();

        var memoryBlock = MemoryManager.FormatForSystemPrompt(_loadedMemories);
        if (!string.IsNullOrWhiteSpace(memoryBlock))
            basePrompt += "\n\n" + memoryBlock;

        var transcriptionContext = GetLiveTranscriptionSystemContext();
        if (!string.IsNullOrWhiteSpace(transcriptionContext))
            basePrompt += "\n\n" + transcriptionContext;

        var recentWebContext = GetRecentWebSearchSystemContext();
        if (!string.IsNullOrWhiteSpace(recentWebContext))
            basePrompt += "\n\n" + recentWebContext;

        return basePrompt;
    }

    private static bool IsCodeOrScriptRequest(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = Regex.Replace(text.ToLowerInvariant(), "\\s+", " ").Trim();
        if (Regex.IsMatch(normalized, "\\b(svg|vector)\\b"))
            return true;

        var asksForArtifact = Regex.IsMatch(normalized, "\\b(write|create|make|build|generate|give|show|provide|need|fix|convert|update|draw|design|render|illustrate)\\b");
        var mentionsCode = Regex.IsMatch(normalized, "\\b(code|script|program|function|class|method|snippet|markup|svg|vector|xml|html|css|powershell|python|bash|batch|cmd|javascript|typescript|sql|json|yaml|c#|csharp|dotnet|regex)\\b");

        return mentionsCode && (asksForArtifact || normalized.Contains("code block") || normalized.Contains("```"));
    }

    private static bool IsManualContinuationRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = Regex.Replace(text.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}\s']", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized is "continue" or "continue please" or "please continue" or
               "go on" or "keep going" or "carry on" or "more" or
               "continue from there" or "continue where you left off" or
               "finish it" or "finish that" or "finish the answer" or
               "keep writing";
    }

    private static bool IsGemma412BModel(string? model)
    {
        var normalized = (model ?? "").ToLowerInvariant();
        return normalized.Contains("gemma", StringComparison.Ordinal) &&
               (normalized.Contains("12b", StringComparison.Ordinal) ||
                Regex.IsMatch(normalized, @"\b12\s*b\b"));
    }

    private int GetMaxTokensForRequest(string? text, string? model = null)
    {
        return IsGemma412BModel(model) ? 512 : CodeResponseMaxTokens;
    }

    private async Task<int> GetContextTokensForRequestAsync(string model, CancellationToken ct)
    {
        return await _ollama.GetModelContextTokensAsync(model, ct) ?? CodeContextTokens;
    }

    private List<ChatMessage> BuildMessagesForModel(string currentUserText, IEnumerable<string> currentImagesBase64)
    {
        var currentContent = LimitCurrentModelInput(currentUserText);
        if (IsLargePaste(currentUserText))
        {
            return new List<ChatMessage>
            {
                new()
                {
                    Role = "system",
                    Content = "The next user message is a large pasted document. Treat the entire message as available context, not only the first section. If the user asks what you can see, scan for and report all obvious section/chapter headings present in the pasted text."
                },
                new()
                {
                    Role = "user",
                    Content = currentContent,
                    ImagesBase64 = currentImagesBase64.Where(i => !string.IsNullOrWhiteSpace(i)).ToList()
                }
            };
        }

        var messages = _history.GetAll()
            .Select(m => new ChatMessage
            {
                Role = m.Role,
                Content = m.Content,
                ImagesBase64 = new List<string>(),
                Timestamp = m.Timestamp
            })
            .ToList();
        if (messages.Count > 0 && messages[^1].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
        {
            messages[^1] = new ChatMessage
            {
                Role = "user",
                Content = currentContent,
                ImagesBase64 = currentImagesBase64.Where(i => !string.IsNullOrWhiteSpace(i)).ToList()
            };
        }

        return messages;
    }

    private static bool IsLargePaste(string text) => text.Length > LargePasteChars;

    private static string LimitCurrentModelInput(string text)
    {
        if (text.Length <= MaxCurrentModelInputChars)
            return text;

        var headLength = MaxCurrentModelInputChars / 2;
        var tailLength = MaxCurrentModelInputChars - headLength;
        return text[..headLength] +
               $"\n\n[Input trimmed before model request: original length {text.Length:N0} characters. Paste less text or use an attachment/chunking workflow for exact full-document work.]\n\n" +
               text[^tailLength..];
    }

    private int TrimMessagesToContextBudget(List<ChatMessage> messages, string systemPrompt, int contextTokens, int maxOutputTokens)
    {
        if (messages.Count <= 1 || contextTokens <= 0)
            return 0;

        var promptBudget = Math.Max(4096, contextTokens - maxOutputTokens - ContextSafetyTokens);
        var dropped = 0;
        while (messages.Count > 1 && EstimatePromptTokens(messages, systemPrompt) > promptBudget)
        {
            messages.RemoveAt(0);
            dropped++;
        }

        return dropped;
    }

    private void AddTokenEstimateDiagnostic(string userText, List<ChatMessage> messages, string systemPrompt, int contextTokens, int maxOutputTokens, int droppedMessages)
    {
        var sentUserText = GetLastUserMessageContent(messages);
        var typedTokens = EstimateTextTokens(userText);
        var currentTurnTokens = EstimateTextTokens(sentUserText);
        var promptTokens = EstimatePromptTokens(messages, systemPrompt);
        var contextPercent = contextTokens > 0 ? promptTokens / (double)contextTokens : 0;
        var addedContextTokens = Math.Max(0, currentTurnTokens - typedTokens);
        var carriedTokens = Math.Max(0, promptTokens - currentTurnTokens);

        var message = new StringBuilder();
        message.AppendLine("Tokens:");
        if (contextTokens > 0)
        {
            message.AppendLine($"Window: {contextTokens:N0}");
            message.AppendLine($"Context used: ~{promptTokens:N0} / {contextTokens:N0} ({FormatPercent(contextPercent)})");
        }
        else
        {
            message.AppendLine($"Context used: ~{promptTokens:N0}");
        }

        message.AppendLine($"This turn: ~{typedTokens:N0} typed");
        if (addedContextTokens > 0)
            message.AppendLine($"Added context: ~{addedContextTokens:N0}");
        if (carriedTokens > 0)
            message.AppendLine($"Carried chat/system: ~{carriedTokens:N0}");
        if (maxOutputTokens > 0)
            message.AppendLine($"Reply limit: {maxOutputTokens:N0}");
        if (droppedMessages > 0)
            message.Append($"Old messages dropped: {droppedMessages}");
        else
            message.Length--;

        AddSystemMessage(message.ToString());
    }

    private static string GetLastUserMessageContent(List<ChatMessage> messages)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                return messages[i].Content;
        }

        return "";
    }

    private static List<string> ExtractLikelySectionMarkers(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string>();

        var markers = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(
                     text,
                     @"(?m)^\s*((?:(?:chapter|section)\s+)?(?:[IVXLCDM]+|\d+)[\.\)]?(?:\s+[-:.]?\s*[A-Z][^\r\n]{0,80})?)\s*$",
                     RegexOptions.IgnoreCase))
        {
            var marker = Regex.Replace(match.Groups[1].Value.Trim(), "\\s+", " ");
            if (marker.Length == 0 || marker.Length > 100 || !seen.Add(marker))
                continue;

            markers.Add(marker);
            if (markers.Count >= 20)
                break;
        }

        return markers;
    }

    private static string FormatPercent(double value) => $"{value:P1}";

    private static int EstimatePromptTokens(IEnumerable<ChatMessage> messages, string systemPrompt)
    {
        var total = EstimateTextTokens(systemPrompt);
        foreach (var message in messages)
            total += EstimateTextTokens(message.Content) + 6;

        return total;
    }

    private static int EstimateTextTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        return Math.Max(1, (int)Math.Ceiling(text.Length / (double)EstimatedCharsPerToken));
    }

    private static string GetCodeArtifactSystemInstruction(string? currentUserText)
    {
        var isSvgRequest = !string.IsNullOrWhiteSpace(currentUserText) &&
            currentUserText.Contains("svg", StringComparison.OrdinalIgnoreCase);

        var instruction = "For the current desktop chat response, the user is asking for code, markup, an SVG, or a script. In this response only, ignore any instruction that forbids markdown or code blocks. Put complete code, markup, SVG, or scripts in fenced markdown code blocks with an appropriate language tag, such as ```svg, ```html, ```csharp, ```python, ```powershell, ```bash, or ```json. Do not discuss message length limits, do not offer a smaller version, and do not offer a generator script unless the user explicitly asks for one. Produce the complete requested artifact as directly as possible. Keep any setup notes short and outside the code block.";

        if (isSvgRequest)
        {
            instruction += " For SVG requests, create one complete standalone SVG in a single ```svg code block. Do not replace requested detail with a summary. Close the <svg> element.";
        }

        return instruction;
    }

    private async Task<string> CompleteCodeArtifactIfNeededAsync(
        string response,
        string userText,
        List<ChatMessage> messagesForModel,
        string systemPrompt,
        string model,
        double temperature,
        int maxTokens,
        int contextTokens,
        CancellationToken ct)
    {
        var shouldHandleArtifact =
            IsCodeOrScriptRequest(userText) ||
            ContainsFencedCodeBlock(response) ||
            LooksLikeSvgRequestOrOutput(userText, response);

        if (!shouldHandleArtifact)
            return response;

        maxTokens = Math.Max(maxTokens, CodeResponseMaxTokens);
        if (contextTokens <= 0)
            contextTokens = CodeContextTokens;

        var completed = response;
        var needsContinuation = IsIncompleteCodeArtifact(completed, userText) || WasLastResponseTokenLimited();
        for (var attempt = 1; attempt <= CodeContinuationMaxAttempts && needsContinuation; attempt++)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                SetUIState("processing", "Continuing code/SVG...");
                AddSystemMessage($"Code/SVG looked incomplete or hit a token limit, continuing automatically ({attempt}/{CodeContinuationMaxAttempts}).");
            });

            var continuationMessages = messagesForModel.Select(m => new ChatMessage
            {
                Role = m.Role,
                Content = m.Content,
                ImagesBase64 = m.ImagesBase64.ToList(),
                Timestamp = m.Timestamp,
                ToolCalls = m.ToolCalls,
                ToolCallId = m.ToolCallId,
                ToolName = m.ToolName
            }).ToList();

            continuationMessages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = completed
            });
            continuationMessages.Add(new ChatMessage
            {
                Role = "user",
                Content = "Continue exactly from the previous character. Do not restart, do not summarize, do not explain, and do not wrap in a new code fence unless the original code fence was already closed. Finish the artifact completely."
            });

            var continuation = await _ollama.ChatAsync(
                model,
                continuationMessages,
                systemPrompt + "\n\nContinuation repair mode: output only the missing continuation text needed to complete the artifact. Do not repeat earlier content.",
                temperature,
                maxTokens,
                ct,
                contextTokens);
            var continuationHitLimit = WasLastResponseTokenLimited();
            AddBackendFinishDiagnostic($"Continuation {attempt}", continuation.Length, maxTokens, contextTokens);

            if (string.IsNullOrWhiteSpace(continuation))
                break;

            completed += NormalizeArtifactContinuation(completed, continuation);
            needsContinuation = IsIncompleteCodeArtifact(completed, userText) || continuationHitLimit;
        }

        return completed;
    }

    private bool WasLastResponseTokenLimited()
    {
        return IsTokenLimitFinishReason(_ollama.LastFinishReason) ||
               IsTokenLimitFinishReason(_ollama.LastStopReason);
    }

    private static bool IsTokenLimitFinishReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return false;

        var normalized = reason.Trim().ToLowerInvariant();
        return normalized.Contains("length", StringComparison.Ordinal) ||
               normalized.Contains("limit", StringComparison.Ordinal) ||
               normalized.Contains("max_tokens", StringComparison.Ordinal) ||
               normalized.Contains("num_predict", StringComparison.Ordinal);
    }

    private void AddBackendFinishDiagnostic(string label, int? responseChars = null, int? requestedMaxTokens = null, int? requestedContextTokens = null)
    {
        var message = new StringBuilder();
        message.AppendLine(label.StartsWith("Continuation", StringComparison.OrdinalIgnoreCase)
            ? $"Tokens ({label}):"
            : "Tokens:");
        var hasUsage = _ollama.LastPromptTokens is int || _ollama.LastCompletionTokens is int;
        var contextTokens = requestedContextTokens is int ctx && ctx > 0 ? ctx : 0;
        var promptTokens = _ollama.LastPromptTokens;
        var completionTokens = _ollama.LastCompletionTokens;

        if (contextTokens > 0)
            message.AppendLine($"Window: {contextTokens:N0}");
        if (promptTokens is int prompt)
        {
            var contextLine = contextTokens > 0
                ? $" / {contextTokens:N0} ({FormatPercent(prompt / (double)contextTokens)})"
                : "";
            message.AppendLine($"Context used: {prompt:N0}{contextLine}");
        }
        if (promptTokens is int p && completionTokens is int c)
            message.AppendLine($"Last exchange: {p:N0} in + {c:N0} out = {(p + c):N0}");
        else if (completionTokens is int completion)
            message.AppendLine($"Last reply: {completion:N0}");

        if (!string.IsNullOrWhiteSpace(_ollama.LastFinishReason))
            message.AppendLine($"Finish reason: {_ollama.LastFinishReason}");
        if (!string.IsNullOrWhiteSpace(_ollama.LastStopReason))
            message.AppendLine($"Stop reason: {_ollama.LastStopReason}");

        var text = message.ToString().Trim();
        AddSystemMessage(!hasUsage
            ? "Tokens: backend did not return usage metadata."
            : text);
    }

    private static bool IsIncompleteCodeArtifact(string text, string userText)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var isSvgRequest = LooksLikeSvgRequestOrOutput(userText, text);
        if (isSvgRequest &&
            text.Contains("<svg", StringComparison.OrdinalIgnoreCase) &&
            !text.Contains("</svg>", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return CountCodeFences(text) % 2 != 0;
    }

    private static bool LooksLikeSvgRequestOrOutput(string userText, string text) =>
        userText.Contains("svg", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("<svg", StringComparison.OrdinalIgnoreCase);

    private static int CountCodeFences(string text) =>
        Regex.Matches(text, "```").Count;

    private static string NormalizeArtifactContinuation(string previous, string continuation)
    {
        var next = continuation.TrimStart();

        if (CountCodeFences(previous) % 2 != 0)
            next = Regex.Replace(next, "^```[A-Za-z0-9_+.#-]*\\s*", "", RegexOptions.Singleline);

        if (previous.Contains("<svg", StringComparison.OrdinalIgnoreCase) &&
            !previous.Contains("</svg>", StringComparison.OrdinalIgnoreCase))
        {
            var duplicateSvgStart = Regex.Match(next, "<svg\\b[^>]*>", RegexOptions.IgnoreCase);
            if (duplicateSvgStart.Success)
                next = next[(duplicateSvgStart.Index + duplicateSvgStart.Length)..].TrimStart();
        }

        return next;
    }

    private string GetLiveTranscriptionSystemContext()
    {
        var hasTranscript = !string.IsNullOrWhiteSpace(_latestLiveTranscript);
        var hasSummary = !string.IsNullOrWhiteSpace(_latestLiveTranscriptSummary);
        if (!hasTranscript && !hasSummary)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("Live transcription context from the separate transcriber window is available. Treat it as user-visible session context and use it when relevant.");

        if (hasSummary)
        {
            sb.AppendLine();
            sb.AppendLine("Latest transcription summary:");
            sb.AppendLine(_latestLiveTranscriptSummary.Trim());
        }

        if (hasTranscript)
        {
            var transcript = _latestLiveTranscript.Trim();
            if (transcript.Length > 8000)
                transcript = transcript[^8000..];

            sb.AppendLine();
            sb.AppendLine("Latest live transcript:");
            sb.AppendLine(transcript);
        }

        return sb.ToString().Trim();
    }

    private sealed record RecentWebSearchContext(DateTime RetrievedAt, string Query, string Context);

    private void RememberWebSearchContext(string query, string context)
    {
        if (string.IsNullOrWhiteSpace(context))
            return;

        var trimmedContext = context.Trim();
        if (trimmedContext.Length > 12000)
            trimmedContext = trimmedContext[..12000] + "\n[Older source text trimmed for context budget.]";

        _recentWebSearchContexts.Insert(0, new RecentWebSearchContext(DateTime.Now, query.Trim(), trimmedContext));
        if (_recentWebSearchContexts.Count > 3)
            _recentWebSearchContexts.RemoveRange(3, _recentWebSearchContexts.Count - 3);
    }

    private string GetRecentWebSearchSystemContext()
    {
        if (_recentWebSearchContexts.Count == 0)
            return "";

        var sb = new StringBuilder();
        sb.AppendLine("Recent live web search context from this app session is available for follow-up questions.");
        sb.AppendLine("Treat these retrieved source snippets as current evidence from live web search, not as the model's own prior memory. If these sources conflict with your training data, prefer the retrieved web context and explain that it is newer. Do not call it fake or a mistake solely because it is newer than your built-in knowledge.");

        foreach (var item in _recentWebSearchContexts)
        {
            sb.AppendLine();
            sb.AppendLine($"Retrieved: {item.RetrievedAt:yyyy-MM-dd HH:mm} local time");
            sb.AppendLine($"Query: {item.Query}");
            sb.AppendLine(item.Context);
        }

        return sb.ToString().Trim();
    }

    private string GetFaceIdentitySystemContext()
    {
        return _recognizedFaceIdentity switch
        {
            FaceIdentity.Keith => "Local face identity says Keith is present. Use full assistant mode.",
            FaceIdentity.Child1 or FaceIdentity.Child2 => "Local face identity says a child profile is present. Use kid-safe mode: keep content age-appropriate, avoid adult topics, avoid dangerous instructions, and ask for an adult for sensitive actions.",
            _ when _facePresenceState == FacePresenceState.MultipleFacesDetected => "Local face presence sees multiple people. Use guest/private mode: avoid exposing personal memory or private details unless Keith is recognized.",
            _ => "Local face identity is unknown or no face is present. Use guest/private mode: avoid exposing personal memory or private details unless Keith is recognized."
        };
    }

    private string GetLastIdentifiedUserSystemContext()
    {
        if (string.IsNullOrWhiteSpace(_lastIdentifiedFaceName))
            return "";

        var identifiedWhen = _lastIdentifiedFaceUtc == DateTime.MinValue
            ? "earlier in this app session"
            : $"at {_lastIdentifiedFaceUtc.ToLocalTime():g}";

        return _lastIdentifiedFaceIdentity switch
        {
            FaceIdentity.Keith =>
                $"Local face identity context: the last identified user in this app session is Keith, identified {identifiedWhen}. When replying directly to the user, you may address him as Keith and should treat the conversation as being with Keith unless the user says otherwise.",
            FaceIdentity.Child1 or FaceIdentity.Child2 =>
                $"Local face identity context: the last identified user in this app session is {_lastIdentifiedFaceName}, identified {identifiedWhen}. Keep replies age-appropriate and avoid adult or dangerous content unless Keith is identified again.",
            _ =>
                $"Local face identity context: the last identified user in this app session is {_lastIdentifiedFaceName}, identified {identifiedWhen}. When replying directly to the user, you may address them by that name unless the user says otherwise."
        };
    }

    private async void SendMessage(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return;
        var modelUserText = IsManualContinuationRequest(userText)
            ? "Continue the previous assistant response from where it left off. Do not restart, do not summarize, and do not ask what to continue. If the previous response was code, SVG, markup, a list, or a long answer, continue that same content directly."
            : userText;

        if (TryCreateHermesPrompt(userText, out var hermesPrompt))
        {
            await SendHermesAgentMessageAsync(userText, hermesPrompt);
            return;
        }

        if (PiAgentService.TryCreateReadOnlyPrompt(userText, out var piPrompt, out var piBlockedReason))
        {
            await SendPiAgentMessageAsync(userText, piPrompt, piBlockedReason);
            return;
        }

        if (TryGetVideoPrompt(userText, out var videoPrompt, out var videoSeconds))
        {
            await RunLtxVideoAsync(userText, videoPrompt, videoSeconds);
            return;
        }

        if (TryGetImageEditPrompt(userText, out var editPrompt))
        {
            await RunQwenImageEditAsync(userText, editPrompt);
            return;
        }

        if (TryGetImageCreatePrompt(userText, out var createPrompt))
        {
            await RunQwenImageCreateAsync(userText, createPrompt);
            return;
        }

        if (string.IsNullOrWhiteSpace(ModelCombo.Text))
        {
            AddSystemMessage("Please select a model first!");
            return;
        }

        _chatCts = new CancellationTokenSource();
        var model = ModelCombo.Text;
        AssistantMessageUi? assistantMessage = null;
        StreamingSpeech? streamingSpeech = null;

        SetUIState("thinking", "Thinking...");

        try
        {
            var imagePaths = new List<string>();
            var imagesBase64 = new List<string>();

            if (ShouldCaptureCameraForPrompt(userText))
            {
                SetUIState("processing", "Capturing camera...");
                AddSystemMessage("Taking one camera photo for this message.");
                var photo = await _camera.CapturePhotoAsync(_chatCts.Token);
                imagePaths.Add(photo.Path);
                imagesBase64.Add(photo.Base64);
            }

            if (_pendingImages.Count > 0)
            {
                imagePaths.AddRange(_pendingImages.Select(i => i.Path));
                imagesBase64.AddRange(_pendingImages.Select(i => i.Base64));
                _pendingImages.Clear();
                UpdateImageButtonLabel();
            }

            var keepDocumentsActive = KeepDocumentActiveToggle.IsChecked == true;
            if (keepDocumentsActive && _pendingDocuments.Count > 0)
            {
                _activeDocuments.Clear();
                _activeDocuments.AddRange(_pendingDocuments);
                AddSystemMessage($"{_activeDocuments.Count} document(s) set active. Uncheck Keep doc to stop including them.");
            }

            var documentsForResponse = keepDocumentsActive && _activeDocuments.Count > 0
                ? _activeDocuments
                : _pendingDocuments;
            var documentContext = DocumentTextService.BuildContext(documentsForResponse.Select(d => d.Document), modelUserText);
            var documentCount = documentsForResponse.Count;
            if (_pendingDocuments.Count > 0)
            {
                _pendingDocuments.Clear();
                UpdateDocumentButtonLabel();
            }

            // Add user message to UI and history after optional capture so the thumbnail can be shown.
            AddUserMessage(userText, imagePaths);
            _history.Add("user", userText, imagesBase64);
            if (IsLargePaste(modelUserText))
                AddSystemMessage("Large paste mode: previous chat history will not be sent with this request.");

            // Create assistant bubble for streaming - get direct TextBlock reference
            assistantMessage = AddAssistantMessage("");

            _history.RemoveWhere(m =>
                m.Role.Equals("system", StringComparison.OrdinalIgnoreCase) &&
                (m.Content.StartsWith("YouTube video transcript context for ", StringComparison.Ordinal) ||
                 m.Content.StartsWith("YouTube video local audio transcription context for ", StringComparison.Ordinal)));

            var transientContexts = new List<ChatMessage>();
            var messagesForModel = BuildMessagesForModel(modelUserText, imagesBase64);
            if (!string.IsNullOrWhiteSpace(documentContext))
            {
                AddSystemMessage(keepDocumentsActive
                    ? $"{documentCount} active document(s) included in this response."
                    : $"{documentCount} attached document(s) added to this response.");
            }

            if (DocumentTextService.TryAnswerExactSentenceQuestion(modelUserText, documentsForResponse.Select(d => d.Document), out var exactDocumentAnswer))
            {
                assistantMessage.Body.Text = exactDocumentAnswer;
                _history.Add("assistant", exactDocumentAnswer);
                SpeakLastResponse(exactDocumentAnswer, assistantMessage);
                return;
            }

            if (YouTubeTranscriptService.TryExtractYouTubeUrl(modelUserText, out var youtubeUrl))
            {
                SetUIState("processing", "Fetching YouTube transcript...");
                AddSystemMessage("Fetching YouTube transcript.");
                var transcriptResult = await _youtubeTranscripts.FetchTranscriptAsync(youtubeUrl, _chatCts.Token);
                if (!string.IsNullOrWhiteSpace(transcriptResult.Transcript))
                {
                    var titleLine = string.IsNullOrWhiteSpace(transcriptResult.Title)
                        ? ""
                        : $"Title: {transcriptResult.Title}\n";
                    var transcriptContext =
                        $"YouTube video transcript context for {youtubeUrl}\n{titleLine}Transcript:\n{transcriptResult.Transcript}";

                    transientContexts.Add(new ChatMessage
                    {
                        Role = "system",
                        Content = transcriptContext
                    });
                    messagesForModel = BuildMessagesForModel(modelUserText, imagesBase64);
                    InsertTransientContexts(messagesForModel, transientContexts);

                    AddSystemMessage("YouTube transcript added to this response.");
                }
                else
                {
                    AddSystemMessage("YouTube captions unavailable. Downloading audio for local transcription.");
                    transcriptResult = await _youtubeTranscripts.FetchAudioTranscriptAsync(
                        youtubeUrl,
                        (stream, ct) => _speech.TranscribeWavAsync(stream, ct),
                        _chatCts.Token);

                    if (!string.IsNullOrWhiteSpace(transcriptResult.Transcript))
                    {
                        var titleLine = string.IsNullOrWhiteSpace(transcriptResult.Title)
                            ? ""
                            : $"Title: {transcriptResult.Title}\n";
                        var transcriptContext =
                            $"YouTube video local audio transcription context for {youtubeUrl}\n{titleLine}Transcript:\n{transcriptResult.Transcript}";

                        transientContexts.Add(new ChatMessage
                        {
                            Role = "system",
                            Content = transcriptContext
                        });
                        messagesForModel = BuildMessagesForModel(modelUserText, imagesBase64);
                        InsertTransientContexts(messagesForModel, transientContexts);

                        AddSystemMessage("YouTube audio transcription added to this response.");
                    }
                    else
                    {
                        var error = $"YouTube transcript unavailable: {transcriptResult.Error}";
                        AddSystemMessage(error);
                        assistantMessage.Body.Text = error;
                        SpeakLastResponse(error, assistantMessage);
                        return;
                    }
                }
            }

            // With tool calling the model decides when to search; only explicit commands force a search here.
            var toolsActive = IsToolCallingActive(model);
            var shouldSearchWeb = WebSearchToggle.IsChecked == true && (toolsActive
                ? WebSearchPhrases.IsExplicitCommand(modelUserText)
                : ShouldTriggerWebSearch(modelUserText));
            var searchedWebThisTurn = false;
            if (shouldSearchWeb)
            {
                if (string.IsNullOrWhiteSpace(_tavily.ApiKey))
                {
                    AddSystemMessage("Web search is on, but no Tavily API key is set.");
                }
                else
                {
                    SetUIState("searching", "Searching web...");
                    var webSearchQuery = RemoveWebSearchTriggerPhrases(modelUserText);
                    var searchContext = await _tavily.SearchAndBuildContextAsync(webSearchQuery, maxResults: 5, ct: _chatCts.Token);
                    if (!string.IsNullOrWhiteSpace(searchContext))
                    {
                        RememberWebSearchContext(webSearchQuery, searchContext);
                        searchedWebThisTurn = true;
                        AddSystemMessage("Web search results added to this response.");
                        messagesForModel = BuildMessagesForModel(modelUserText, imagesBase64);
                        InsertTransientContexts(messagesForModel, transientContexts);
                        messagesForModel.Insert(Math.Max(0, messagesForModel.Count - 1), new ChatMessage
                        {
                            Role = "system",
                            Content = "You have current web search context for this answer. Use it as the authoritative source for current facts, releases, versions, prices, dates, schedules, and news. If your training data conflicts with the web context, say the web context is newer and answer from it. Do not dismiss retrieved sources as fake unless the source itself says so."
                        });
                        if (IsCodeOrScriptRequest(modelUserText))
                        {
                            messagesForModel.Insert(Math.Max(0, messagesForModel.Count - 1), new ChatMessage
                            {
                                Role = "system",
                                Content = GetCodeArtifactSystemInstruction(modelUserText)
                            });
                        }
                        messagesForModel[^1] = new ChatMessage
                        {
                            Role = "user",
                            Content = $"{RemoveWebSearchTriggerPhrases(modelUserText)}\n\nCurrent web search context:\n{searchContext}\n\nAnswer the user's question using the current web search context above. If the question asks for the latest or current information, prioritize dated official sources over your prior knowledge.",
                            ImagesBase64 = imagesBase64
                        };
                    }
                    else
                    {
                        AddSystemMessage("Web search did not return usable results. Answering from model knowledge.");
                    }
                }
            }

            ApplyDocumentContextToCurrentUserMessage(messagesForModel, documentContext);

            var systemPrompt = GetEffectiveSystemPrompt(modelUserText);
            var maxTokens = GetMaxTokensForRequest(modelUserText, model);
            var contextTokens = await GetContextTokensForRequestAsync(model, _chatCts.Token);
            var droppedContextMessages = TrimMessagesToContextBudget(messagesForModel, systemPrompt, contextTokens, maxTokens);
            AddTokenEstimateDiagnostic(userText, messagesForModel, systemPrompt, contextTokens, maxTokens, droppedContextMessages);

            if (toolsActive &&
                await TryAnswerWithToolsAsync(model, modelUserText, messagesForModel, systemPrompt, maxTokens, contextTokens,
                    assistantMessage, skipWebSearchTool: searchedWebThisTurn, _chatCts.Token))
            {
                return;
            }

            if (StreamToggle.IsChecked == true)
            {
                // Stream
                SetUIState("thinking", "Thinking...");
                var fullText = new StringBuilder();
                streamingSpeech = BeginStreamingSpeech(modelUserText, assistantMessage);
                await _ollama.ChatStreamAsync(
                    model,
                    messagesForModel,
                    systemPrompt,
                    TempSlider.Value,
                    maxTokens,
                    contextTokens,
                    onToken: token =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            fullText.Append(token);
                            var streamingText = fullText.ToString();
                            var shouldPreserveCode = IsCodeOrScriptRequest(modelUserText) || ContainsFencedCodeBlock(streamingText);
                            assistantMessage.Body.Text = GetStreamingDisplayText(streamingText, shouldPreserveCode);
                            ScrollChat();
                            FeedStreamingSpeech(streamingSpeech, token);
                        }, DispatcherPriority.Background);
                    },
                    onComplete: async full =>
                    {
                        try
                        {
                            await Dispatcher.InvokeAsync(() => AddBackendFinishDiagnostic("Backend usage", full.Length, maxTokens, contextTokens));

                            var completed = await CompleteCodeArtifactIfNeededAsync(
                                full,
                                modelUserText,
                                messagesForModel,
                                systemPrompt,
                                model,
                                TempSlider.Value,
                                maxTokens,
                                contextTokens,
                                _chatCts.Token);

                            Dispatcher.Invoke(() =>
                            {
                                var isCodeResponse = IsCodeOrScriptRequest(modelUserText) || ContainsFencedCodeBlock(completed);
                                var cleaned = CleanDisplayText(completed, preserveCodeBlocks: isCodeResponse);
                                if (!string.IsNullOrWhiteSpace(cleaned))
                                {
                                    SetAssistantMessageText(assistantMessage, cleaned, isCodeResponse);
                                    _history.Add("assistant", cleaned);
                                    // With live speech the reply is already being spoken; otherwise speak it now.
                                    if (!FinishStreamingSpeech(streamingSpeech, full, completed))
                                        SpeakLastResponse(cleaned, assistantMessage);
                                }
                                else
                                {
                                    CancelStreamingSpeech(streamingSpeech);
                                    SetUIState("idle", "Ready");
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            AppLog.Error("Code/SVG continuation failed", ex);
                            Dispatcher.Invoke(() =>
                            {
                                CancelStreamingSpeech(streamingSpeech);
                                assistantMessage.Body.Text = $"Error: {ex.Message}";
                                AddSystemMessage($"Code/SVG continuation error: {ex.Message}");
                                SetUIState("idle", "Ready");
                            });
                        }
                    },
                    onError: ex =>
                    {
                        AppLog.Error("Chat backend stream failed", ex);
                        Dispatcher.Invoke(() =>
                        {
                            CancelStreamingSpeech(streamingSpeech);
                            assistantMessage.Body.Text = $"Error: {ex.Message}";
                            AddSystemMessage($"API Error: {ex.Message}");
                            SetUIState("idle", "Ready");
                        });
                    },
                    ct: _chatCts.Token
                );
            }
            else
            {
                // Non-streaming
                SetUIState("processing", "Generating...");
                var response = await _ollama.ChatAsync(
                    model,
                    messagesForModel,
                    systemPrompt,
                    TempSlider.Value,
                    maxTokens,
                    _chatCts.Token,
                    contextTokens
                );
                AddBackendFinishDiagnostic("Backend usage", response.Length, maxTokens, contextTokens);

                response = await CompleteCodeArtifactIfNeededAsync(
                    response,
                    modelUserText,
                    messagesForModel,
                    systemPrompt,
                    model,
                    TempSlider.Value,
                    maxTokens,
                    contextTokens,
                    _chatCts.Token);

                var isCodeResponse = IsCodeOrScriptRequest(modelUserText) || ContainsFencedCodeBlock(response);
                var cleaned = CleanDisplayText(response, preserveCodeBlocks: isCodeResponse);
                SetAssistantMessageText(assistantMessage, cleaned, isCodeResponse);
                _history.Add("assistant", cleaned);
                SpeakLastResponse(cleaned, assistantMessage);
            }
        }
        catch (OperationCanceledException)
        {
            CancelStreamingSpeech(streamingSpeech);
            if (assistantMessage is not null)
                assistantMessage.Body.Text += " [cancelled]";
            SetUIState("idle", "Ready");
        }
        catch (Exception ex)
        {
            AppLog.Error("Chat request failed", ex);
            CancelStreamingSpeech(streamingSpeech);
            if (assistantMessage is not null)
                assistantMessage.Body.Text = $"Error: {ex.Message}";
            AddSystemMessage($"Chat error: {ex.Message}");
            SetUIState("idle", "Ready");
        }
    }

    private static void InsertTransientContexts(List<ChatMessage> messages, IEnumerable<ChatMessage> contexts)
    {
        foreach (var context in contexts.Where(c => !string.IsNullOrWhiteSpace(c.Content)))
            messages.Insert(Math.Max(0, messages.Count - 1), context);
    }

    private static void ApplyDocumentContextToCurrentUserMessage(List<ChatMessage> messages, string documentContext)
    {
        if (string.IsNullOrWhiteSpace(documentContext))
            return;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (!messages[i].Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                continue;

            if (messages[i].Content.Contains("Attached document context for this response.", StringComparison.Ordinal))
                return;

            messages[i] = new ChatMessage
            {
                Role = messages[i].Role,
                Content = $"{messages[i].Content}\n\n{documentContext}\n\nUse the attached document context above when answering this question.",
                ImagesBase64 = messages[i].ImagesBase64
            };
            return;
        }
    }

    // Used when tools are off, and by the scheduler and phone remote: the original phrases plus explicit commands.
    private static bool ShouldTriggerWebSearch(string text) =>
        WebSearchPhrases.IsLegacyTrigger(text) || WebSearchPhrases.IsExplicitCommand(text);

    private static string RemoveWebSearchTriggerPhrases(string text) =>
        WebSearchPhrases.StripCommandPhrases(text);

    private void WebSearchToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = WebSearchToggle.IsChecked == true;
        _settings.WebSearchEnabled = enabled;
    }

    private void SendText_Click(object sender, RoutedEventArgs e)
    {
        var text = MessageInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(text)) return;
        MessageInput.Clear();
        ResumeListeningAfterTextInput();
        SendMessage(text);
    }
}
