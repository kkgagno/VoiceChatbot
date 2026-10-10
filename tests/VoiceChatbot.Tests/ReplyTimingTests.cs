using System;
using System.Text.Json;
using VoiceChatbot;
using Xunit;

public class ReplyTimingTests
{
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ReadsLlamaServerTimingsFromTheLastChunk()
    {
        // llama-server's last streamed chunk (with stream_options.include_usage): usage, no choices, and its timings.
        var chunk = Json("""
            {"choices":[],"object":"chat.completion.chunk","usage":{"completion_tokens":61,"prompt_tokens":1350,"total_tokens":1411},
             "timings":{"cache_n":1100,"prompt_n":250,"prompt_ms":277.8,"prompt_per_token_ms":1.11,"prompt_per_second":900.0,
                        "predicted_n":61,"predicted_ms":1605.3,"predicted_per_token_ms":26.3,"predicted_per_second":38.0}}
            """);

        var timings = LlamaTimings.FromResponse(chunk)!;
        Assert.Equal(250, timings.PromptTokens);
        Assert.Equal(1100, timings.CachedTokens);
        Assert.Equal(900.0, timings.PromptPerSecond);
        Assert.Equal(277.8, timings.PromptMs);
        Assert.Equal(61, timings.PredictedTokens);
        Assert.Equal(38.0, timings.PredictedPerSecond);

        // Other servers (Ollama, OpenAI) have none.
        Assert.Null(LlamaTimings.FromResponse(Json("""{"choices":[{"delta":{"content":"Hi"}}]}""")));
        Assert.Null(LlamaTimings.FromResponse(Json("""{"timings":{}}""")));
        Assert.Null(LlamaTimings.FromResponse(Json("[1, 2]")));
        // Older versions without cache_n, and speeds that could not be worked out (sent as null).
        var older = LlamaTimings.FromResponse(Json("""{"timings":{"prompt_n":0,"prompt_ms":0,"prompt_per_second":null,"predicted_n":5}}"""))!;
        Assert.Null(older.CachedTokens);
        Assert.Null(older.PromptPerSecond);
        Assert.Equal(0, older.PromptTokens);
    }

    [Fact]
    public void DescribesWhereTheTimeOfAReplyWent()
    {
        var server = new LlamaTimings(250, 277.8, 900.0, 1100, 61, 1605.3, 38.2);
        var timing = new ReplyTiming(TimeSpan.Zero, TimeSpan.FromSeconds(0.94), TimeSpan.FromSeconds(2.4), Streamed: true, ThinkingChars: 0, server);

        Assert.Equal("Reply timing: first words after 0.9 s, done after 2.4 s; prompt 1350 tokens (1100 reused from cache) at 900 tokens/s; " +
                     "reply 61 tokens at 38 tokens/s; thinking 0 characters", timing.Describe());
        Assert.Equal("Reply timing: first words after 0.9 s, done after 2.4 s (3.1 s after your message); prompt 1350 tokens (1100 reused from cache) " +
                     "at 900 tokens/s; reply 61 tokens at 38 tokens/s; thinking 0 characters", timing.Describe(TimeSpan.FromSeconds(3.14)));
    }

    [Fact]
    public void DescribesRepliesWithoutServerTimings()
    {
        // Ollama, OpenAI: only the clock.
        Assert.Equal("Reply timing: first words after 1.2 s, done after 5.0 s; thinking 0 characters",
            new ReplyTiming(TimeSpan.FromSeconds(0.02), TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(5), true, 0).Describe());
        Assert.Equal("Reply timing: done after 3.0 s (not streamed); thinking 0 characters",
            new ReplyTiming(TimeSpan.Zero, null, TimeSpan.FromSeconds(3), false, 0).Describe());
        // Only tool calls, no answer text.
        Assert.Equal("Reply timing: no answer text, done after 0.7 s; thinking 0 characters",
            new ReplyTiming(TimeSpan.Zero, null, TimeSpan.FromSeconds(0.7), true, 0).Describe());
    }

    [Fact]
    public void DescribesLoadingThinkingAndSlowSpeeds()
    {
        // The model was still loading, it thought first, everything was in the cache, and it runs on the processor.
        var server = new LlamaTimings(0, 0, null, 900, 40, 11428, null);
        var timing = new ReplyTiming(TimeSpan.FromSeconds(6.26), TimeSpan.FromSeconds(9.0), TimeSpan.FromSeconds(12.4), true, 1834, server);
        Assert.Equal("Reply timing: waited 6.3 s for the model to load, then first words after 9.0 s, done after 12.4 s; " +
                     "prompt 900 tokens (900 reused from cache); reply 40 tokens at 3.5 tokens/s; thinking 1834 characters", timing.Describe());

        // Speeds missing but times given: worked out from them. Nothing cached: no cache note.
        var noRates = new ReplyTiming(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), true, 0,
            new LlamaTimings(500, 1000, null, 0, 0, 0, null));
        Assert.Equal("Reply timing: first words after 1.0 s, done after 2.0 s; prompt 500 tokens at 500 tokens/s; reply 0 tokens; thinking 0 characters",
            noRates.Describe());
    }

    [Fact]
    public void TimerMeasuresFromSendingTheRequest()
    {
        var now = TimeSpan.Zero;
        var timer = new ReplyTimer(() => now);

        now = TimeSpan.FromSeconds(2); // Waiting for the built-in model to load.
        timer.Sent();
        now = TimeSpan.FromSeconds(2.5);
        timer.Thinking("Let me think");
        timer.Answer("");
        now = TimeSpan.FromSeconds(3);
        timer.Answer("Hello");
        timer.Sent(); // A resend changes nothing.
        now = TimeSpan.FromSeconds(4);
        timer.Answer(" there");
        timer.Thinking(null);
        timer.ReadServerTimings(Json("""{"choices":[{"delta":{"content":"!"}}]}"""));
        timer.ReadServerTimings(Json("""{"choices":[],"timings":{"prompt_n":10,"prompt_per_second":100,"predicted_n":3,"predicted_per_second":30}}"""));
        timer.ReadServerTimings(Json("""{"choices":[]}"""));
        now = TimeSpan.FromSeconds(5);

        var timing = timer.Finish(streamed: true);
        Assert.Equal(TimeSpan.FromSeconds(2), timing.WaitedForServer);
        Assert.Equal(TimeSpan.FromSeconds(1), timing.FirstWords);
        Assert.Equal(TimeSpan.FromSeconds(3), timing.Total);
        Assert.Equal(12, timing.ThinkingChars);
        Assert.Equal(10, timing.Server?.PromptTokens);
        Assert.True(timing.Streamed);

        // No answer text at all.
        var silent = new ReplyTimer(() => TimeSpan.FromSeconds(1)).Finish(streamed: true);
        Assert.Null(silent.FirstWords);
        Assert.Equal(TimeSpan.FromSeconds(1), silent.Total);
    }
}
