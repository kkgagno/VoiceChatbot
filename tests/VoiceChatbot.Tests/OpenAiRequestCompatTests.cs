using System.Text.Json.Nodes;
using VoiceChatbot;
using Xunit;

public class OpenAiRequestCompatTests
{
    private const string OpenAi = "https://api.openai.com/v1";
    private const string LlamaCpp = "http://192.168.4.42:8083/v1";

    private static string Body(string model, double temperature = 0.7, int maxTokens = 2048) =>
        new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
            ["stream"] = true,
            ["temperature"] = temperature,
            ["max_tokens"] = maxTokens
        }.ToJsonString();

    private static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Fact]
    public void OpenAiGetsMaxCompletionTokensUpFront()
    {
        var body = Parse(new OpenAiRequestCompat().Apply(OpenAi, Body("gpt-4.1")));

        Assert.False(body.ContainsKey("max_tokens"));
        Assert.Equal(2048, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal(0.7, body["temperature"]!.GetValue<double>());
    }

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gpt-5-mini")]
    [InlineData("o3")]
    [InlineData("o4-mini")]
    public void OpenAiReasoningModelsDropTemperatureAndGetRoomToThink(string model)
    {
        var body = Parse(new OpenAiRequestCompat().Apply(OpenAi, Body(model)));

        Assert.False(body.ContainsKey("temperature"));
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.Equal(OpenAiRequestCompat.ReasoningMinCompletionTokens, body["max_completion_tokens"]!.GetValue<int>());
    }

    [Fact]
    public void OtherServersAreLeftAloneUntilTheyRefuseSomething()
    {
        var compat = new OpenAiRequestCompat();
        var json = Body("gemma-4-31b");

        Assert.Equal(json, compat.Apply(LlamaCpp, json));
        Assert.False(OpenAiRequestCompat.IsReasoningModel("gpt-5-chat-latest"));
        Assert.False(OpenAiRequestCompat.IsReasoningModel("gemma-4-31b"));
    }

    [Fact]
    public void LearnsMaxCompletionTokensFromTheServersAnswer()
    {
        var compat = new OpenAiRequestCompat();
        var sent = Body("some-model");
        const string error = """{"error":{"message":"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.","type":"invalid_request_error","param":"max_tokens","code":"unsupported_parameter"}}""";

        Assert.True(compat.Learn(LlamaCpp, sent, error, out var change));
        Assert.Contains("max_completion_tokens", change);

        var body = Parse(compat.Apply(LlamaCpp, sent));
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.Equal(2048, body["max_completion_tokens"]!.GetValue<int>());

        // Nothing new to learn from the same answer: no endless retries.
        Assert.False(compat.Learn(LlamaCpp, compat.Apply(LlamaCpp, sent), error, out _));
    }

    [Fact]
    public void LearnsToLeaveOutARefusedTemperature()
    {
        var compat = new OpenAiRequestCompat();
        var sent = Body("future-model");
        const string error = """{"error":{"message":"Unsupported value: 'temperature' does not support 0.7 with this model. Only the default (1) value is supported.","type":"invalid_request_error","param":"temperature","code":"unsupported_value"}}""";

        Assert.True(compat.Learn(LlamaCpp, sent, error, out _));
        Assert.False(Parse(compat.Apply(LlamaCpp, sent)).ContainsKey("temperature"));
        // Learned per model: another model on the same server still sends it.
        Assert.True(Parse(compat.Apply(LlamaCpp, Body("other-model"))).ContainsKey("temperature"));
    }

    [Fact]
    public void NeverDropsEssentialFieldsOrLearnsFromUnrelatedErrors()
    {
        var compat = new OpenAiRequestCompat();
        var sent = Body("m");

        Assert.False(compat.Learn(LlamaCpp, sent, """{"error":{"message":"Unsupported parameter: 'messages'","param":"messages","code":"unsupported_parameter"}}""", out _));
        Assert.False(compat.Learn(LlamaCpp, sent, """{"error":{"message":"Invalid API key","code":"invalid_api_key"}}""", out _));
        Assert.False(compat.Learn(LlamaCpp, sent, "", out _));
        Assert.False(compat.Learn(LlamaCpp, sent, """{"error":{"message":"Unsupported parameter: 'top_p'","param":"top_p","code":"unsupported_parameter"}}""", out _));
    }
}
