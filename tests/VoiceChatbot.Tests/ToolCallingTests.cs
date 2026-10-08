using System.Net;
using System.Text;
using System.Text.Json;
using VoiceChatbot;
using Xunit;

public class ToolSpecTests
{
    [Fact]
    public void CreateBuildsFunctionWireFormat()
    {
        var spec = ToolSpec.Create("web_search", "Search the web.", new ToolParameter("query", "What to search for."));
        var json = JsonSerializer.Serialize(spec.ToWireFormat());

        Assert.Equal(
            "{\"type\":\"function\",\"function\":{\"name\":\"web_search\",\"description\":\"Search the web.\"," +
            "\"parameters\":{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"description\":\"What to search for.\"}}," +
            "\"required\":[\"query\"]}}}",
            json);
    }

    [Fact]
    public void FromJsonSchemaKeepsTheSchema()
    {
        var spec = ToolSpec.FromJsonSchema("kb", "Search notes.",
            "{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\"}}}");
        Assert.Equal("integer", spec.Parameters.GetProperty("properties").GetProperty("limit").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("{\"query\":\"amd news\"}", "amd news")]
    [InlineData("{\"Query\":\"amd news\"}", "amd news")]
    [InlineData("\"{\\\"query\\\":\\\"double encoded\\\"}\"", "double encoded")]
    [InlineData("{\"q\":\"renamed\"}", "renamed")]
    [InlineData("plain text", "plain text")]
    [InlineData("", "")]
    public void GetStringIsLenient(string arguments, string expected) =>
        Assert.Equal(expected, ToolArguments.GetString(arguments, "query", rawTextFallback: true));

    [Fact]
    public void GetStringWithoutFallbackOnlyReadsTheNamedValue()
    {
        Assert.Equal("", ToolArguments.GetString("{\"q\":\"x\"}", "query"));
        Assert.Equal("", ToolArguments.GetString("not json", "query"));
        Assert.Equal("5", ToolArguments.GetString("{\"query\":5}", "query"));
    }

    [Theory]
    [InlineData(null, "{}")]
    [InlineData("", "{}")]
    [InlineData("not json", "{}")]
    [InlineData("[1,2]", "{}")]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("\"{\\\"a\\\":1}\"", "{\"a\":1}")]
    public void NormalizeObjectJson(string? input, string expected) =>
        Assert.Equal(expected, ToolArguments.NormalizeObjectJson(input));

    [Theory]
    [InlineData("aapl", "AAPL")]
    [InlineData(" $msft ", "MSFT")]
    [InlineData("BRK-B", "BRK-B")]
    [InlineData("^GSPC", "^GSPC")]
    [InlineData("Advanced Micro Devices", "")]
    [InlineData("", "")]
    public void NormalizeTickerSymbol(string input, string expected) =>
        Assert.Equal(expected, BuiltInTools.NormalizeTickerSymbol(input));

    [Fact]
    public void DescribeCallNotes()
    {
        Assert.Equal("Searching the web for \"latest AMD driver\"...",
            BuiltInTools.DescribeCall(new ToolCall { Name = BuiltInTools.WebSearch, ArgumentsJson = "{\"query\":\"latest AMD driver\"}" }));
        Assert.Equal("Getting a stock quote for NVDA...",
            BuiltInTools.DescribeCall(new ToolCall { Name = BuiltInTools.StockQuote, ArgumentsJson = "{\"symbol\":\"nvda\"}" }));
        Assert.Equal("Using tool kb_search...", BuiltInTools.DescribeCall(new ToolCall { Name = "kb_search" }));
    }

    [Fact]
    public void FormatDateTimeIncludesDayZoneAndIso()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test/Zone", TimeSpan.FromHours(-5), "Test Zone", "Test Standard Time");
        var text = BuiltInTools.FormatDateTime(new DateTimeOffset(2026, 10, 8, 19, 30, 0, TimeSpan.Zero), zone);

        Assert.Contains("Local date: Thursday, October 8, 2026", text);
        Assert.Contains("Local time: 2:30 PM (14:30)", text);
        Assert.Contains("Day of week: Thursday", text);
        Assert.Contains("UTC-05:00", text);
        Assert.Contains("ISO 8601: 2026-10-08T14:30:00-05:00", text);
    }
}

public class ChatToolWireTests
{
    private static readonly List<ToolCall> Calls = new()
    {
        new ToolCall { Id = "call_abc", Name = "web_search", ArgumentsJson = "{\"query\":\"weather\"}" }
    };

    [Fact]
    public void OllamaAssistantToolCallUsesArgumentsObject()
    {
        var json = JsonSerializer.Serialize(ChatToolWire.OllamaMessage("assistant", "", new List<string>(), Calls, null));
        Assert.Equal(
            "{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"web_search\",\"arguments\":{\"query\":\"weather\"}}}]}",
            json);
    }

    [Fact]
    public void OllamaToolResultUsesToolRoleAndName()
    {
        var json = JsonSerializer.Serialize(ChatToolWire.OllamaMessage("tool", "Sunny", null, null, "web_search"));
        Assert.Equal("{\"role\":\"tool\",\"content\":\"Sunny\",\"tool_name\":\"web_search\"}", json);
    }

    [Fact]
    public void OpenAiAssistantToolCallUsesArgumentsStringAndNullContent()
    {
        var json = JsonSerializer.Serialize(ChatToolWire.OpenAiMessage("assistant", "", Calls, null));
        Assert.Equal(
            "{\"role\":\"assistant\",\"content\":null,\"tool_calls\":[{\"id\":\"call_abc\",\"type\":\"function\"," +
            "\"function\":{\"name\":\"web_search\",\"arguments\":\"{\\u0022query\\u0022:\\u0022weather\\u0022}\"}}]}",
            json);
    }

    [Fact]
    public void OpenAiToolResultCarriesToolCallId()
    {
        var json = JsonSerializer.Serialize(ChatToolWire.OpenAiMessage("tool", "Sunny", null, "call_abc"));
        Assert.Equal("{\"role\":\"tool\",\"content\":\"Sunny\",\"tool_call_id\":\"call_abc\"}", json);
    }

    [Fact]
    public void ParsesOllamaToolCalls()
    {
        using var doc = JsonDocument.Parse(
            "{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[" +
            "{\"function\":{\"name\":\"get_stock_quote\",\"arguments\":{\"symbol\":\"AMD\"}}}," +
            "{\"id\":\"x1\",\"function\":{\"name\":\"get_current_datetime\",\"arguments\":{}}}]}");

        var calls = ChatToolWire.ParseOllamaToolCalls(doc.RootElement);

        Assert.Equal(2, calls.Count);
        Assert.Equal("get_stock_quote", calls[0].Name);
        Assert.Equal("{\"symbol\":\"AMD\"}", calls[0].ArgumentsJson);
        Assert.Equal(9, calls[0].Id.Length);
        Assert.Equal("x1", calls[1].Id);
    }

    [Fact]
    public void AccumulatesOpenAiStreamingDeltasByIndex()
    {
        var acc = new OpenAiToolCallAccumulator();
        foreach (var delta in new[]
                 {
                     "[{\"index\":0,\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"web_search\",\"arguments\":\"\"}}]",
                     "[{\"index\":0,\"function\":{\"arguments\":\"{\\\"query\\\":\"}}]",
                     "[{\"index\":1,\"id\":\"call_2\",\"function\":{\"name\":\"get_current_datetime\",\"arguments\":\"{}\"}}]",
                     "[{\"index\":0,\"function\":{\"arguments\":\"\\\"amd\\\"}\"}}]"
                 })
        {
            using var doc = JsonDocument.Parse(delta);
            acc.Add(doc.RootElement);
        }

        var calls = acc.Build();
        Assert.Equal(2, calls.Count);
        Assert.Equal("call_1", calls[0].Id);
        Assert.Equal("web_search", calls[0].Name);
        Assert.Equal("{\"query\":\"amd\"}", calls[0].ArgumentsJson);
        Assert.Equal("get_current_datetime", calls[1].Name);
    }

    [Fact]
    public void AccumulatorIgnoresRepeatedNames()
    {
        var acc = new OpenAiToolCallAccumulator();
        foreach (var delta in new[]
                 {
                     "[{\"index\":0,\"function\":{\"name\":\"web_search\",\"arguments\":\"{\\\"query\\\":\"}}]",
                     "[{\"index\":0,\"function\":{\"name\":\"web_search\",\"arguments\":\"\\\"x\\\"}\"}}]"
                 })
        {
            using var doc = JsonDocument.Parse(delta);
            acc.Add(doc.RootElement);
        }

        var call = Assert.Single(acc.Build());
        Assert.Equal("web_search", call.Name);
        Assert.Equal("{\"query\":\"x\"}", call.ArgumentsJson);
        Assert.False(string.IsNullOrEmpty(call.Id));
    }

    [Theory]
    [InlineData(400, "{\"error\":\"registry.ollama.ai/library/gemma2:9b does not support tools\"}", true)]
    [InlineData(400, "{\"error\":{\"code\":400,\"message\":\"tools param requires --jinja flag\",\"type\":\"invalid_request_error\"}}", true)]
    [InlineData(500, "{\"error\":\"this model doesn't support tool calling\"}", true)]
    [InlineData(400, "{\"error\":\"invalid tool_choice\"}", true)]
    [InlineData(400, "{\"error\":\"the request exceeds the available context size\"}", false)]
    [InlineData(500, "{\"error\":\"model runner has unexpectedly stopped\"}", false)]
    [InlineData(200, "does not support tools", false)]
    public void DetectsToolsUnsupportedErrors(int status, string body, bool expected) =>
        Assert.Equal(expected, ChatToolWire.LooksLikeToolsUnsupported(status, body));
}

public class ToolRegistryTests
{
    private static readonly ToolSpec Echo = ToolSpec.Create("echo", "Echo text.", new ToolParameter("text", "Text."));

    [Fact]
    public async Task ExecutesRegisteredTool()
    {
        var registry = new ToolRegistry();
        registry.Register(Echo, (args, _) => Task.FromResult("echo: " + ToolArguments.GetString(args, "text")));

        var result = await registry.ExecuteAsync(new ToolCall { Name = "ECHO", ArgumentsJson = "{\"text\":\"hi\"}" });
        Assert.Equal("echo: hi", result);
    }

    [Fact]
    public async Task UnknownToolAndFailuresComeBackAsText()
    {
        var registry = new ToolRegistry();
        registry.Register(Echo, (_, _) => throw new InvalidOperationException("boom"));

        Assert.StartsWith("Error: there is no tool named \"nope\"", await registry.ExecuteAsync(new ToolCall { Name = "nope" }));
        Assert.Equal("Error: echo failed: boom", await registry.ExecuteAsync(new ToolCall { Name = "echo" }));
    }

    [Fact]
    public async Task AsyncFailureComesBackAsText()
    {
        var registry = new ToolRegistry();
        registry.Register(Echo, async (_, ct) =>
        {
            await Task.Yield();
            throw new HttpRequestException("offline");
        });

        Assert.Equal("Error: echo failed: offline", await registry.ExecuteAsync(new ToolCall { Name = "echo" }));
    }

    [Fact]
    public async Task TimesOutEvenWhenTheToolIgnoresTheToken()
    {
        var registry = new ToolRegistry();
        registry.Register(Echo, async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            return "late";
        }, timeout: TimeSpan.FromMilliseconds(100));

        var result = await registry.ExecuteAsync(new ToolCall { Name = "echo" });
        Assert.StartsWith("Error: echo timed out", result);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var registry = new ToolRegistry();
        registry.Register(Echo, async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return "late";
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.ExecuteAsync(new ToolCall { Name = "echo" }, cts.Token));
    }

    [Fact]
    public async Task AvailabilityFiltersToolsAndBlocksExecution()
    {
        var available = false;
        var registry = new ToolRegistry();
        registry.Register(Echo, (_, _) => Task.FromResult("ok"), isAvailable: () => available);
        registry.Register(BuiltInTools.CurrentDateTimeSpec, (_, _) => Task.FromResult("now"));

        Assert.Equal(new[] { BuiltInTools.CurrentDateTime }, registry.GetAvailableTools().Select(t => t.Name));
        Assert.StartsWith("Error: the echo tool is not available", await registry.ExecuteAsync(new ToolCall { Name = "echo" }));

        available = true;
        Assert.Equal(new[] { "echo", BuiltInTools.CurrentDateTime }, registry.GetAvailableTools().Select(t => t.Name));
        Assert.Equal(new[] { "echo" }, registry.GetAvailableTools(t => t.Name == "echo").Select(t => t.Name));
    }

    [Fact]
    public async Task RegisterReplacesAndCustomNotesWin()
    {
        var registry = new ToolRegistry();
        registry.Register(Echo, (_, _) => Task.FromResult("first"));
        registry.Register(Echo, (_, _) => Task.FromResult("second"), describeCall: _ => "Echoing...");

        Assert.Single(registry.GetAvailableTools());
        Assert.Equal("second", await registry.ExecuteAsync(new ToolCall { Name = "echo" }));
        Assert.Equal("Echoing...", registry.DescribeCall(new ToolCall { Name = "echo" }));
        Assert.True(registry.Unregister("echo"));
        Assert.False(registry.IsRegistered("echo"));
    }

    [Fact]
    public async Task LongResultsAreCapped()
    {
        var registry = new ToolRegistry();
        registry.Register(Echo, (_, _) => Task.FromResult(new string('x', ToolRegistry.MaxResultChars + 500)));

        var result = await registry.ExecuteAsync(new ToolCall { Name = "echo" });
        Assert.EndsWith("[Tool result truncated.]", result);
        Assert.True(result.Length < ToolRegistry.MaxResultChars + 100);
    }
}

public class WebSearchPhrasesTests
{
    [Theory]
    [InlineData("search the web for the latest AMD driver", true)]
    [InlineData("Search online: who won the game last night", true)]
    [InlineData("can you look it up online?", true)]
    [InlineData("look up the NVDA price online", true)]
    [InlineData("check the internet for storms", true)]
    [InlineData("do a web search on Kokoro TTS", true)]
    [InlineData("web search best pizza near me", true)]
    [InlineData("is web search turned on?", false)]
    [InlineData("what is a web search engine", false)]
    [InlineData("I look forward to catching up online", false)]
    [InlineData("what's the weather today", false)]
    public void ExplicitCommands(string text, bool expected) =>
        Assert.Equal(expected, WebSearchPhrases.IsExplicitCommand(text));

    [Theory]
    [InlineData("is web search turned on?", true)]
    [InlineData("check online for updates", true)]
    [InlineData("search the web for news", false)]
    [InlineData("hello", false)]
    public void LegacyTriggersAreUnchanged(string text, bool expected) =>
        Assert.Equal(expected, WebSearchPhrases.IsLegacyTrigger(text));

    [Theory]
    [InlineData("search the web for the latest AMD driver", "the latest AMD driver")]
    [InlineData("Search online: who won last night", "who won last night")]
    [InlineData("What's the newest Ollama release? search online", "What's the newest Ollama release?")]
    [InlineData("look up the NVDA price online", "the NVDA price")]
    [InlineData("web search", "web search")]
    public void StripsCommandWords(string text, string expected) =>
        Assert.Equal(expected, WebSearchPhrases.StripCommandPhrases(text));
}

public class WebPageReaderTests
{
    [Theory]
    [InlineData("example.com/news", "https://example.com/news")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("<https://example.com/a?b=1>", "https://example.com/a?b=1")]
    public void NormalizesPublicUrls(string input, string expected)
    {
        Assert.True(WebPageReader.TryNormalizeUrl(input, out var uri, out _));
        Assert.Equal(expected, uri.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("http://localhost:11434/api/tags")]
    [InlineData("http://127.0.0.1:8080")]
    [InlineData("http://192.168.1.10/admin")]
    [InlineData("http://10.0.0.5")]
    [InlineData("http://172.20.1.1")]
    [InlineData("http://100.101.102.103")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://router/")]
    [InlineData("http://nas.local/")]
    [InlineData("https://user:pass@example.com/")]
    public void RejectsNonHttpAndLocalUrls(string input) =>
        Assert.False(WebPageReader.TryNormalizeUrl(input, out _, out _));

    [Fact]
    public void HtmlBecomesReadableText()
    {
        const string html = "<html><head><title>My &amp; Page</title><style>p{color:red}</style></head>" +
                            "<body><nav><a href='/'>Home</a></nav><script>alert('x')</script>" +
                            "<h1>Heading</h1><p>First&nbsp;paragraph &lt;ok&gt;.</p><ul><li>One</li><li>Two</li></ul>" +
                            "<!-- hidden --><footer>Footer links</footer></body></html>";

        Assert.Equal("My & Page", HtmlText.ExtractTitle(html));
        var text = HtmlText.ToPlainText(html);
        Assert.Equal("Heading\nFirst paragraph <ok>.\n\n- One\n- Two", text);
    }

    [Fact]
    public void FormatPageTruncatesLongText()
    {
        var text = string.Join(" ", Enumerable.Repeat("word", 5000));
        var page = HtmlText.FormatPage(new Uri("https://example.com/"), "Title", text, 1000);

        Assert.StartsWith("Title: Title\nURL: https://example.com/\n", page.Replace("\r\n", "\n"));
        Assert.Contains("[Page text truncated: showing", page);
        Assert.True(page.Length < 1200);
    }

    [Fact]
    public async Task FetchesAndCleansAPage()
    {
        using var reader = new WebPageReader(
            new StubHandler(_ => Html("<html><head><title>Hi</title></head><body><p>Hello world</p></body></html>")),
            PublicResolver);

        var result = await reader.FetchReadableTextAsync("example.com");
        Assert.Contains("Title: Hi", result);
        Assert.Contains("URL: https://example.com/", result);
        Assert.Contains("Hello world", result);
    }

    [Fact]
    public async Task BlocksRedirectsAndHostsThatPointAtLocalAddresses()
    {
        using var redirecting = new WebPageReader(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("http://192.168.1.1/");
            return response;
        }), PublicResolver);
        Assert.StartsWith("Error: redirected to a blocked address", await redirecting.FetchReadableTextAsync("https://example.com/"));

        using var rebinding = new WebPageReader(new StubHandler(_ => Html("<p>secret</p>")),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("10.0.0.2") }));
        Assert.Contains("local network address", await rebinding.FetchReadableTextAsync("https://evil.example/"));
    }

    [Fact]
    public async Task RejectsBinaryContentAndHttpErrors()
    {
        using var binary = new WebPageReader(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3 })
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf") }
                }
            }), PublicResolver);
        Assert.Contains("not a readable web page", await binary.FetchReadableTextAsync("https://example.com/a.pdf"));

        using var missing = new WebPageReader(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), PublicResolver);
        Assert.Contains("HTTP 404", await missing.FetchReadableTextAsync("https://example.com/missing"));
    }

    private static Task<IPAddress[]> PublicResolver(string host, CancellationToken ct) =>
        Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") });

    private static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = _respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
