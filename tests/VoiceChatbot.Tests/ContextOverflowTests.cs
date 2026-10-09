using VoiceChatbot;
using Xunit;

public class ContextOverflowTests
{
    [Fact]
    public void LlamaCppErrorGivesTheServerWindowAndPromptSize()
    {
        const string body = """
            {"error":{"code":400,"message":"the request exceeds the available context size, try increasing it","type":"exceed_context_size_error","n_prompt_tokens":20512,"n_ctx":16384}}
            """;

        Assert.True(ContextOverflow.TryParse(body, out var info));
        Assert.Equal(16384, info.ServerContextTokens);
        Assert.Equal(20512, info.PromptTokens);
        Assert.True(info.IsLlamaCpp);
    }

    [Fact]
    public void OlderLlamaCppErrorWithoutNumbersIsStillAnOverflow()
    {
        const string body = """
            {"error":{"code":400,"message":"the request exceeds the available context size. try increasing the context size or enable context shift","type":"invalid_request_error"}}
            """;

        Assert.True(ContextOverflow.TryParse(body, out var info));
        Assert.Null(info.ServerContextTokens);
        Assert.Null(info.PromptTokens);
        Assert.True(info.IsLlamaCpp);
    }

    [Fact]
    public void VllmAndOpenAiErrorsGiveTheWindowFromTheMessage()
    {
        const string vllm = """
            {"object":"error","message":"This model's maximum context length is 32768 tokens. However, you requested 34000 tokens (32000 in the messages, 2000 in the completion). Please reduce the length of the messages or completion.","type":"BadRequestError","param":null,"code":400}
            """;
        Assert.True(ContextOverflow.TryParse(vllm, out var v));
        Assert.Equal(32768, v.ServerContextTokens);
        Assert.Equal(32000, v.PromptTokens);
        Assert.False(v.IsLlamaCpp);

        const string openAi = """
            {"error":{"message":"This model's maximum context length is 128000 tokens. However, your messages resulted in 130532 tokens. Please reduce the length of the messages.","type":"invalid_request_error","param":"messages","code":"context_length_exceeded"}}
            """;
        Assert.True(ContextOverflow.TryParse(openAi, out var o));
        Assert.Equal(128000, o.ServerContextTokens);
        Assert.Equal(130532, o.PromptTokens);
    }

    [Fact]
    public void OtherErrorsAreNotOverflows()
    {
        Assert.False(ContextOverflow.TryParse("""{"error":{"code":400,"message":"tools param requires --jinja flag","type":"invalid_request_error"}}""", out _));
        Assert.False(ContextOverflow.TryParse("""{"error":{"code":503,"message":"Loading model","type":"unavailable_error"}}""", out _));
        Assert.False(ContextOverflow.TryParse("""{"error":"model 'x' not found"}""", out _));
        Assert.False(ContextOverflow.TryParse("Bad Request", out _));
        Assert.False(ContextOverflow.TryParse("", out _));
    }

    [Fact]
    public void PlainTextOverflowMessagesAreRecognized()
    {
        Assert.True(ContextOverflow.TryParse("the request exceeds the available context size, try increasing it", out var info));
        Assert.True(info.IsLlamaCpp);
    }

    [Fact]
    public void MessageTellsTheOwnerTheSizeAndTheFlag()
    {
        var info = new ContextOverflowInfo(16384, 20512, "", IsLlamaCpp: true);
        var message = ContextOverflow.Describe(info);
        Assert.Contains($"{16384:N0} tokens per request", message);
        Assert.Contains($"{20512:N0}", message);
        Assert.Contains("-c (--ctx-size)", message);

        // No n_ctx in the error: the detected window is used, and detection says it is llama.cpp.
        var bare = new ContextOverflowInfo(null, null, "", IsLlamaCpp: false);
        var withKnown = ContextOverflow.Describe(bare, knownWindow: 8192, serverIsLlamaCpp: true);
        Assert.Contains($"{8192:N0} tokens per request", withKnown);
        Assert.Contains("llama-server", withKnown);

        Assert.DoesNotContain("llama", ContextOverflow.Describe(bare));
    }

    [Fact]
    public void ExceptionCarriesTheInfoAndTheFriendlyMessage()
    {
        var info = new ContextOverflowInfo(4096, null, "the request exceeds the available context size", IsLlamaCpp: true);
        var ex = new ContextOverflowException(info);
        Assert.Same(info, ex.Info);
        Assert.Equal(ContextOverflow.Describe(info), ex.Message);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.StatusCode);
    }
}
