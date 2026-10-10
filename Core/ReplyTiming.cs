using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VoiceChatbot;

/// <summary>
/// llama-server's own measurements of one reply: the "timings" object it adds to its answer (the last chunk
/// of a streamed one). <see cref="PromptTokens"/> are the prompt tokens it had to read, <see cref="CachedTokens"/>
/// the ones it reused from the previous request; <see cref="PredictedTokens"/> are the reply's tokens. Rates
/// are tokens per second. Null fields were not reported.
/// </summary>
public sealed record LlamaTimings(int? PromptTokens, double? PromptMs, double? PromptPerSecond, int? CachedTokens,
    int? PredictedTokens, double? PredictedMs, double? PredictedPerSecond)
{
    /// <summary>The "timings" object of a llama-server answer or stream chunk; null when it has none (other servers).</summary>
    public static LlamaTimings? FromResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("timings", out var timings) || timings.ValueKind != JsonValueKind.Object)
            return null;

        var result = new LlamaTimings(
            Count(timings, "prompt_n"), Number(timings, "prompt_ms"), Number(timings, "prompt_per_second"), Count(timings, "cache_n"),
            Count(timings, "predicted_n"), Number(timings, "predicted_ms"), Number(timings, "predicted_per_second"));
        return result.PromptTokens is null && result.PredictedTokens is null ? null : result;
    }

    // NaN and infinity (nothing to divide by) come as null or are left out.
    private static double? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetDouble(out var number) && double.IsFinite(number) ? number : null;

    private static int? Count(JsonElement obj, string name) =>
        Number(obj, name) is double number && number >= 0 && number <= int.MaxValue ? (int)Math.Round(number) : null;
}

/// <summary>
/// How long one chat request took, for the "Reply timing" line in the app log (and in the chat with Show
/// diagnostics). <see cref="WaitedForServer"/>: waiting for the built-in model to load before sending;
/// <see cref="FirstWords"/>: from sending to the first words of the answer (null when none came, or when it
/// was not streamed); <see cref="Total"/>: from sending to the end. <see cref="ThinkingChars"/> counts the
/// thinking the server sent apart from the answer (never shown). <see cref="Server"/> holds llama-server's
/// own measurements; null for other servers.
/// </summary>
public sealed record ReplyTiming(TimeSpan WaitedForServer, TimeSpan? FirstWords, TimeSpan Total, bool Streamed, int ThinkingChars,
    LlamaTimings? Server = null)
{
    /// <summary>Waits shorter than this (the model was already loaded) are left out of <see cref="Describe"/>.</summary>
    public static readonly TimeSpan NoticeableWait = TimeSpan.FromSeconds(0.1);

    /// <summary>
    /// One line such as "Reply timing: first words after 0.9 s, done after 2.4 s; prompt 1350 tokens (1100 reused
    /// from cache) at 900 tokens/s; reply 61 tokens at 38 tokens/s; thinking 0 characters".
    /// <paramref name="sinceMessage"/>, when given, is the time from the user's message to the end of the reply.
    /// </summary>
    public string Describe(TimeSpan? sinceMessage = null)
    {
        var parts = new List<string>();

        var clock = new StringBuilder();
        if (WaitedForServer >= NoticeableWait)
            clock.Append($"waited {Seconds(WaitedForServer)} for the model to load, then ");
        if (!Streamed)
            clock.Append($"done after {Seconds(Total)} (not streamed)");
        else if (FirstWords is { } first)
            clock.Append($"first words after {Seconds(first)}, done after {Seconds(Total)}");
        else
            clock.Append($"no answer text, done after {Seconds(Total)}");
        if (sinceMessage is { } message)
            clock.Append($" ({Seconds(message)} after your message)");
        parts.Add(clock.ToString());

        if (Server is { } server)
        {
            if (server.PromptTokens is int read)
            {
                var cached = server.CachedTokens ?? 0;
                var prompt = new StringBuilder($"prompt {Count(read + (long)cached)} tokens");
                if (cached > 0)
                    prompt.Append($" ({Count(cached)} reused from cache)");
                var rate = server.PromptPerSecond ?? (server.PromptMs is > 0 ? read / server.PromptMs.Value * 1000 : (double?)null);
                // Everything reused: nothing was read, so there is no speed.
                if (read > 0 && rate is > 0)
                    prompt.Append($" at {Rate(rate.Value)} tokens/s");
                parts.Add(prompt.ToString());
            }

            if (server.PredictedTokens is int written)
            {
                var rate = server.PredictedPerSecond ?? (server.PredictedMs is > 0 ? written / server.PredictedMs.Value * 1000 : (double?)null);
                parts.Add(written > 0 && rate is > 0
                    ? $"reply {Count(written)} tokens at {Rate(rate.Value)} tokens/s"
                    : $"reply {Count(written)} tokens");
            }
        }

        parts.Add($"thinking {Count(ThinkingChars)} characters");
        return "Reply timing: " + string.Join("; ", parts);
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";

    private static string Count(long count) => count.ToString(CultureInfo.InvariantCulture);

    // "38" tokens/s, but "3.5" on a slow processor.
    private static string Rate(double perSecond) => perSecond.ToString(perSecond < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
}

/// <summary>
/// Measures one chat request for <see cref="ReplyTiming"/>: create it when the request is about to be made,
/// call <see cref="Sent"/> when it goes out, <see cref="Answer"/> and <see cref="Thinking"/> with what arrives,
/// <see cref="ReadServerTimings"/> with each chunk, and <see cref="Finish"/> at the end. One per request.
/// </summary>
public sealed class ReplyTimer
{
    private readonly Func<TimeSpan> _now;
    private TimeSpan? _sent;
    private TimeSpan? _firstWords;
    private int _thinkingChars;
    private LlamaTimings? _server;

    public ReplyTimer()
    {
        var clock = Stopwatch.StartNew();
        _now = () => clock.Elapsed;
    }

    /// <summary>With a clock of its own (for tests): the time since the request was about to be made.</summary>
    internal ReplyTimer(Func<TimeSpan> now) => _now = now;

    /// <summary>The request is sent now, after any wait for the server to be ready. Later calls change nothing.</summary>
    public void Sent() => _sent ??= _now();

    /// <summary>Answer text arrived; the first that is not empty marks the first words.</summary>
    public void Answer(string? text)
    {
        if (_firstWords is null && !string.IsNullOrEmpty(text))
            _firstWords = _now();
    }

    /// <summary>Thinking the server sent apart from the answer (reasoning_content) arrived.</summary>
    public void Thinking(string? text) => _thinkingChars += text?.Length ?? 0;

    /// <summary>Keeps llama-server's "timings" from <paramref name="root"/> (an answer or stream chunk) when it has them.</summary>
    public void ReadServerTimings(JsonElement root)
    {
        if (LlamaTimings.FromResponse(root) is { } timings)
            _server = timings;
    }

    /// <summary>The request's timing, measured from when it was sent (from the start when <see cref="Sent"/> was not called).</summary>
    public ReplyTiming Finish(bool streamed)
    {
        var now = _now();
        var sent = _sent ?? TimeSpan.Zero;
        return new ReplyTiming(sent, _firstWords - sent, now - sent, streamed, _thinkingChars, _server);
    }
}
