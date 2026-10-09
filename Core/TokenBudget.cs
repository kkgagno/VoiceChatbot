using System;

namespace VoiceChatbot;

/// <summary>
/// Output and context token budgets for one chat request. Normal replies use the MaxTokens setting;
/// code/SVG artifacts get a larger budget. No reply may take more than half of the context window,
/// so the prompt (history, documents, search results) always keeps room.
/// </summary>
public static class TokenBudget
{
    public const int DefaultMaxTokens = 2048;
    public const int ArtifactMaxTokens = 32768;
    public const int DefaultContextWindow = 16384;
    public const int MinContextWindow = 2048;
    public const int MinMaxTokens = 128;
    /// <summary>Room kept free for the chat template, tool definitions and estimation error.</summary>
    public const int ContextSafetyTokens = 4096;

    /// <summary>
    /// The context window to plan the prompt with (and, on Ollama, to send as num_ctx).
    /// <paramref name="detected"/> is what the model or server reports, <paramref name="configured"/>
    /// the ContextWindow setting (0 = use the reported window).
    /// <list type="bullet">
    /// <item>A server with a fixed window (<paramref name="serverWindowIsFixed"/>: llama.cpp's per-slot
    /// n_ctx, vLLM's max_model_len, ...) reports the window it actually runs with, so that value wins over
    /// the setting.</item>
    /// <item>Otherwise the detected value is a model maximum (Ollama, known cloud models), which the
    /// setting caps.</item>
    /// <item>With nothing detected the setting applies, or <paramref name="fallback"/> when it is 0.</item>
    /// </list>
    /// </summary>
    public static int ResolveContextWindow(int? detected, int configured, bool serverWindowIsFixed, int fallback)
    {
        var known = detected is > 0 ? detected.Value : 0;
        if (serverWindowIsFixed && known > 0)
            return known;

        if (configured > 0)
        {
            var cap = Math.Max(MinContextWindow, configured);
            return known > 0 ? Math.Min(cap, known) : cap;
        }

        return known > 0 ? known : fallback;
    }

    /// <summary>
    /// Output tokens for a reply: the MaxTokens setting, raised to <see cref="ArtifactMaxTokens"/> for
    /// code/SVG artifacts, and never more than half of a known context window.
    /// </summary>
    public static int ResolveMaxTokens(int configuredMaxTokens, bool isArtifactRequest, int contextWindow)
    {
        var tokens = configuredMaxTokens > 0 ? configuredMaxTokens : DefaultMaxTokens;
        if (isArtifactRequest)
            tokens = Math.Max(tokens, ArtifactMaxTokens);

        if (contextWindow > 0)
            tokens = Math.Min(tokens, Math.Max(MinMaxTokens, contextWindow / 2));

        return Math.Max(MinMaxTokens, tokens);
    }

    /// <summary>
    /// Estimated prompt tokens (system prompt, history, documents) that fit a request: the window minus
    /// the reply budget and a safety margin (4,096 tokens, or a quarter of a small window). With
    /// <paramref name="estimateScale"/> above 1 (the estimate was known to undercount) the budget shrinks
    /// by that factor. 0 or less = no known window, no limit.
    /// </summary>
    public static int PromptBudget(int contextWindow, int maxOutputTokens, double estimateScale = 1.0)
    {
        if (contextWindow <= 0)
            return int.MaxValue;

        var safety = Math.Min(ContextSafetyTokens, contextWindow / 4);
        var budget = Math.Max(contextWindow / 8, contextWindow - Math.Max(0, maxOutputTokens) - safety);
        return estimateScale > 1.0
            ? Math.Max(1, (int)(budget / estimateScale))
            : budget;
    }

    /// <summary>
    /// After the server rejected a request as too long: how much to shrink the prompt estimate for the
    /// retry. When the server said how many tokens the prompt really had, the ratio to our estimate (plus
    /// 10% headroom); otherwise 1 when the window turned out smaller than planned (trimming to it is
    /// enough), or 1.5 when the window was right and the estimate must have been low.
    /// </summary>
    public static double RetryEstimateScale(int? serverPromptTokens, int estimatedPromptTokens, bool windowShrank)
    {
        if (serverPromptTokens is int real && real > 0 && estimatedPromptTokens > 0)
            return Math.Clamp(real / (double)estimatedPromptTokens * 1.1, 1.0, 8.0);

        return windowShrank ? 1.0 : 1.5;
    }
}
