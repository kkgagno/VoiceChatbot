using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace VoiceChatbot;

public partial class MainWindow
{
    // ==================== Tool calling ====================

    private const int MaxToolRounds = 4;

    private const string ToolUseSystemInstruction =
        "Tools: you can call the provided tools. Call one only when it really helps: current or live information " +
        "(news, recent events, releases, prices, weather, sports), today's date or time, stock quotes, reading a web page or link, " +
        "or saving something the user wants remembered. Otherwise answer directly without calling a tool. " +
        "When tool results arrive, answer the user's question naturally and do not describe the tool mechanics.";

    private ToolRegistry? _modelTools;
    private StockQuoteService? _stockQuotes;
    private WebPageReader? _webPageReader;
    // Models whose backend rejected tool definitions in this session, by model name.
    private readonly HashSet<string> _modelsWithoutTools = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The tools offered to the model. Other features can add their own with ModelTools.Register(...),
    /// for example a knowledge-search tool.
    /// </summary>
    private ToolRegistry ModelTools => _modelTools ??= CreateModelTools();

    private void ApplyToolSettings() => UseToolsToggle.IsChecked = _settings.UseTools;

    private void SaveToolSettings() => _settings.UseTools = UseToolsToggle.IsChecked == true;

    private void UseToolsToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.UseTools = UseToolsToggle.IsChecked == true;
    }

    /// <summary>True when tools are switched on and this model has not rejected them this session.</summary>
    private bool IsToolCallingActive(string? model) =>
        UseToolsToggle.IsChecked == true &&
        !string.IsNullOrWhiteSpace(model) &&
        !_modelsWithoutTools.Contains(model.Trim());

    private ToolRegistry CreateModelTools()
    {
        var registry = new ToolRegistry();
        registry.Register(BuiltInTools.WebSearchSpec, RunWebSearchToolAsync,
            isAvailable: () => _settings.WebSearchEnabled && !string.IsNullOrWhiteSpace(_tavily.ApiKey),
            timeout: TimeSpan.FromSeconds(40));
        registry.Register(BuiltInTools.CurrentDateTimeSpec,
            (_, _) => Task.FromResult(BuiltInTools.FormatDateTime(DateTimeOffset.Now, TimeZoneInfo.Local)),
            timeout: TimeSpan.FromSeconds(5));
        registry.Register(BuiltInTools.StockQuoteSpec, RunStockQuoteToolAsync, timeout: TimeSpan.FromSeconds(15));
        registry.Register(BuiltInTools.SaveMemorySpec, RunSaveMemoryToolAsync, timeout: TimeSpan.FromSeconds(10));
        registry.Register(BuiltInTools.FetchWebPageSpec, RunFetchWebPageToolAsync, timeout: TimeSpan.FromSeconds(30));
        return registry;
    }

    private void DisposeToolServices()
    {
        _stockQuotes?.Dispose();
        _webPageReader?.Dispose();
    }

    // ---- Tool executors (started on the UI thread; UI state is touched through the Dispatcher) ----

    private async Task<string> RunWebSearchToolAsync(string argumentsJson, CancellationToken ct)
    {
        var query = ToolArguments.GetString(argumentsJson, "query", rawTextFallback: true).Trim();
        if (query.Length == 0)
            return "Error: the search query was empty.";

        var context = await _tavily.SearchAndBuildContextAsync(query, maxResults: 5, ct: ct);
        if (string.IsNullOrWhiteSpace(context))
            return "The web search returned no usable results.";

        // Keep it for follow-up questions, the same as an explicit web search.
        await Dispatcher.InvokeAsync(() => RememberWebSearchContext(query, context));
        return context;
    }

    private async Task<string> RunStockQuoteToolAsync(string argumentsJson, CancellationToken ct)
    {
        var symbol = BuiltInTools.NormalizeTickerSymbol(ToolArguments.GetString(argumentsJson, "symbol", rawTextFallback: true));
        if (symbol.Length == 0)
            return "Error: give an exchange ticker symbol such as AAPL or MSFT.";

        _stockQuotes ??= new StockQuoteService();
        var quote = await _stockQuotes.GetQuoteAsync(symbol, ct);
        return quote is null
            ? $"No quote found for {symbol}. Use the exchange ticker symbol, not the company name."
            : StockQuoteService.FormatQuoteLine(quote) + " Quotes may be delayed and are not financial advice.";
    }

    private async Task<string> RunSaveMemoryToolAsync(string argumentsJson, CancellationToken ct)
    {
        var text = ToolArguments.GetString(argumentsJson, "text", rawTextFallback: true).Trim();
        if (text.Length == 0)
            return "Error: there was nothing to save.";
        if (text.Length > 2000)
            text = text[..2000];

        var count = await Dispatcher.InvokeAsync(() =>
        {
            MemoryManager.Save(new ConversationMemory
            {
                Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                Summary = text,
                Model = string.IsNullOrWhiteSpace(ModelCombo.Text) ? "Model" : ModelCombo.Text,
                MessageCount = 0,
                DurationMinutes = 0
            });
            _loadedMemories = MemoryManager.LoadAll();
            if (MemoryPanel.Visibility == Visibility.Visible)
                RefreshMemoryPanel();
            return _loadedMemories.Count;
        }, DispatcherPriority.Normal, ct);

        return $"Saved to long-term memory ({count} memories stored).";
    }

    private Task<string> RunFetchWebPageToolAsync(string argumentsJson, CancellationToken ct)
    {
        var url = ToolArguments.GetString(argumentsJson, "url", rawTextFallback: true);
        _webPageReader ??= new WebPageReader();
        return _webPageReader.FetchReadableTextAsync(url, ct);
    }

    // ---- Tool loop ----

    /// <summary>
    /// Answers with native tool calling: stream a turn with the tool list; while the model asks for
    /// tools, run them (with a status note each), feed the results back and ask again, at most
    /// MaxToolRounds times. The final answer then takes the normal completion path (code/SVG
    /// continuation, history, speech). Returns false when the backend says the model cannot use
    /// tools (remembered for the session) so the caller answers without them.
    /// </summary>
    private async Task<bool> TryAnswerWithToolsAsync(
        string model,
        string modelUserText,
        List<ChatMessage> messagesForModel,
        string systemPrompt,
        int maxTokens,
        int contextTokens,
        AssistantMessageUi assistantMessage,
        bool skipWebSearchTool,
        CancellationToken ct)
    {
        var tools = ModelTools.GetAvailableTools(spec =>
            !(skipWebSearchTool && spec.Name == BuiltInTools.WebSearch));
        if (tools.Count == 0)
            return false;

        var toolSystemPrompt = systemPrompt + "\n\n" + ToolUseSystemInstruction;
        var temperature = TempSlider.Value;
        var liveStream = StreamToggle.IsChecked == true;
        var working = new List<ChatMessage>(messagesForModel);
        // Speak the answer sentence by sentence while it streams, like the plain chat path.
        var speech = liveStream ? BeginStreamingSpeech(modelUserText, assistantMessage) : null;
        try
        {
            return await RunToolTurnAsync();
        }
        catch
        {
            CancelStreamingSpeech(speech);
            throw;
        }

        async Task<bool> RunToolTurnAsync()
        {
            // Web text in this turn (the forced search or a web tool result) can carry instructions aimed
            // at the model; it must not be able to plant memories that every later conversation loads.
            var webContentInTurn = skipWebSearchTool;
            ChatTurnResult turn;

            for (var round = 0; ; round++)
            {
                // The last request goes without tools so the model has to answer.
                var offerTools = round < MaxToolRounds;
                SetUIState(liveStream ? "thinking" : "processing", round == 0 ? "Thinking..." : "Reading tool results...");
                if (round > 0)
                    TrimToolTurnToContextBudget(working, toolSystemPrompt, contextTokens, maxTokens);
                var roundTools = webContentInTurn
                    ? tools.Where(t => !IsTool(t.Name, BuiltInTools.SaveMemory)).ToList()
                    : tools;

                var streamed = new StringBuilder();
                Action<string>? onToken = null;
                if (liveStream)
                {
                    onToken = token => Dispatcher.Invoke(() =>
                    {
                        streamed.Append(token);
                        var text = streamed.ToString();
                        var preserveCode = IsCodeOrScriptRequest(modelUserText) || ContainsFencedCodeBlock(text);
                        assistantMessage.Body.Text = GetStreamingDisplayText(text, preserveCode);
                        ScrollChat(onlyIfFollowing: true);
                        FeedStreamingSpeech(speech, token);
                    }, DispatcherPriority.Background);
                }

                try
                {
                    turn = await _ollama.ChatStreamWithToolsAsync(model, working, toolSystemPrompt, temperature,
                        maxTokens, contextTokens, offerTools ? roundTools : null, onToken, ct);
                }
                catch (ToolsNotSupportedException ex)
                {
                    Debug.WriteLine(ex.Message);
                    // The plain chat path that runs next starts its own speech.
                    speech?.Session.Abandon();
                    assistantMessage.Body.Text = "";
                    if (round == 0)
                    {
                        _modelsWithoutTools.Add(model.Trim());
                        AddSystemMessage($"{model} does not support tool calling, so tools are off for this model until the app restarts. Answering without tools.");
                    }
                    else
                    {
                        // The tool definitions were accepted in round 0, so the backend rejected the tool
                        // results just sent; keep tools on for the next message.
                        AddSystemMessage("The backend could not take the tool results. Answering without tools.");
                    }
                    return false;
                }

                if (!offerTools || !turn.HasToolCalls)
                    break;

                working.Add(new ChatMessage
                {
                    Role = "assistant",
                    Content = turn.Content,
                    ToolCalls = turn.ToolCalls.ToList()
                });

                var webContentThisRound = false;
                foreach (var call in turn.ToolCalls)
                {
                    var note = ModelTools.DescribeCall(call);
                    AddSystemMessage(note);
                    SetUIState(call.Name == BuiltInTools.WebSearch ? "searching" : "processing", note);

                    var result = webContentInTurn && IsTool(call.Name, BuiltInTools.SaveMemory)
                        ? "Error: memories cannot be saved after reading web content in the same answer. " +
                          "Tell the user they can ask you to remember it in their next message."
                        : await ModelTools.ExecuteAsync(call, ct);
                    if (IsTool(call.Name, BuiltInTools.WebSearch) || IsTool(call.Name, BuiltInTools.FetchWebPage))
                        webContentThisRound = true;

                    working.Add(new ChatMessage
                    {
                        Role = "tool",
                        Content = result,
                        ToolCallId = call.Id,
                        ToolName = call.Name
                    });
                }

                // Calls in one round are chosen before any of their results are seen.
                webContentInTurn |= webContentThisRound;

                // Any text streamed alongside the calls was a preamble; the answer comes next, below the notes.
                EndStreamingSpeechRound(speech);
                assistantMessage.Body.Text = "";
                MoveAssistantBubbleToEnd(assistantMessage);
            }

            var answer = turn.Content;
            AddBackendFinishDiagnostic("Backend usage", answer.Length, maxTokens, contextTokens);

            try
            {
                answer = await CompleteCodeArtifactIfNeededAsync(answer, modelUserText, working, toolSystemPrompt,
                    model, temperature, maxTokens, contextTokens, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep what we have rather than replacing the answer with the error.
                AddSystemMessage($"Code/SVG continuation error: {ex.Message}");
            }

            var isCodeResponse = IsCodeOrScriptRequest(modelUserText) || ContainsFencedCodeBlock(answer);
            var cleaned = CleanDisplayText(answer, preserveCodeBlocks: isCodeResponse);
            if (IsPlanningNotesOnlyNotice(cleaned))
            {
                // Only planning notes: show the note, but do not save or speak it.
                assistantMessage.Body.Text = cleaned;
                CancelStreamingSpeechAndFinishTurn(speech);
                return true;
            }

            if (string.IsNullOrWhiteSpace(cleaned))
            {
                assistantMessage.Body.Text = "";
                AddSystemMessage("The model returned an empty answer.");
                CancelStreamingSpeechAndFinishTurn(speech);
                return true;
            }

            SetAssistantMessageText(assistantMessage, cleaned, isCodeResponse);
            _history.Add("assistant", cleaned);
            if (!FinishStreamingSpeech(speech, turn.Content, answer))
                SpeakLastResponse(cleaned, assistantMessage);
            return true;
        }
    }

    private static bool IsTool(string? name, string toolName) =>
        string.Equals(name?.Trim(), toolName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Tool results can be long. Before sending them back, drop the oldest chat history until the
    /// request fits the context budget again; the current question and this turn's tool calls and
    /// results always stay together (a tool result without its call breaks the request).
    /// </summary>
    private static void TrimToolTurnToContextBudget(List<ChatMessage> working, string systemPrompt, int contextTokens, int maxTokens)
    {
        if (contextTokens <= 0)
            return;

        var budget = Math.Max(4096, contextTokens - maxTokens - ContextSafetyTokens);
        var currentQuestion = working.FindLastIndex(m =>
            m.Role.Equals("user", StringComparison.OrdinalIgnoreCase) && !m.HasToolData());
        while (currentQuestion > 0 && EstimatePromptTokens(working, systemPrompt) > budget)
        {
            working.RemoveAt(0);
            currentQuestion--;
        }
    }

    private void MoveAssistantBubbleToEnd(AssistantMessageUi assistantMessage)
    {
        DependencyObject node = assistantMessage.Content;
        while (node is FrameworkElement element && element.Parent is not null && !ReferenceEquals(element.Parent, ChatPanel))
            node = element.Parent;

        if (node is not UIElement bubble || !ChatPanel.Children.Contains(bubble))
            return;

        if (!ReferenceEquals(ChatPanel.Children[ChatPanel.Children.Count - 1], bubble))
        {
            ChatPanel.Children.Remove(bubble);
            ChatPanel.Children.Add(bubble);
        }
        ScrollChat();
    }
}
