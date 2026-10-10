using System;

namespace VoiceChatbot;

/// <summary>The Chat Backend providers (the Provider list's entries, saved in settings.json).</summary>
public static class ChatProviders
{
    /// <summary>The model the app runs itself with its bundled llama.cpp server (LocalModelServer).</summary>
    public const string BuiltIn = "Built-in model";
    public const string Ollama = "Ollama";
    /// <summary>llama.cpp's llama-server, LM Studio, vLLM, OpenAI's own API and other /v1/chat/completions servers.</summary>
    public const string OpenAiCompatible = "OpenAI-compatible";

    public const string OpenAiCloudUrl = "https://api.openai.com/v1";

    public static bool IsBuiltIn(string? provider) => string.Equals(provider?.Trim(), BuiltIn, StringComparison.OrdinalIgnoreCase);

    public static bool IsOpenAiCompatible(string? provider) =>
        string.Equals(provider?.Trim(), OpenAiCompatible, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider?.Trim(), "llama.cpp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A typed server address made usable: "192.168.1.50" becomes "http://192.168.1.50:&lt;defaultPort&gt;"
    /// (plus "/v1" with <paramref name="addV1"/> when no path was given). "" when it is not an address.
    /// </summary>
    public static string NormalizeServerUrl(string? input, int defaultPort, bool addV1)
    {
        var text = (input ?? "").Trim().TrimEnd('/');
        if (text.Length == 0)
            return "";
        var hadScheme = text.Contains("://", StringComparison.Ordinal);
        if (!hadScheme)
            text = "http://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
            return "";

        var authority = text[(text.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
        var portTyped = authority.StartsWith('[') ? authority.Contains("]:", StringComparison.Ordinal) : authority.Contains(':');
        var builder = new UriBuilder(uri);
        if (!hadScheme && !portTyped)
            builder.Port = defaultPort;
        var path = builder.Path.TrimEnd('/');
        if (addV1 && path.Length == 0)
            path = "/v1";
        builder.Path = path;
        return builder.Uri.AbsoluteUri.TrimEnd('/');
    }
}
