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

    /// <summary>
    /// The context window to plan the prompt with (and, on Ollama, to send as num_ctx).
    /// <paramref name="detected"/> is what the model or server reports, <paramref name="configured"/>
    /// the ContextWindow setting (0 = use the reported window). A server with a fixed window
    /// (llama.cpp and other OpenAI-compatible servers) reports the window it actually runs with, so
    /// that value wins; Ollama reports the model's trained maximum, which the setting caps.
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
}
