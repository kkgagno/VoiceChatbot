using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

/// <summary>
/// Helpers for talking to a remote Kokoro server (Kokoro-FastAPI or any OpenAI-compatible
/// /v1/audio/speech endpoint). Accepts loose user input such as "192.168.1.20",
/// "192.168.1.20:8880", "http://tts-box:8880/v1" or a full /v1/audio/speech URL.
/// </summary>
public static class KokoroEndpoint
{
    public const int DefaultPort = 8880;
    public const string ModeAuto = "Auto";
    public const string ModeRemoteOnly = "Remote only";
    public const string ModeLocalOnly = "Local only";
    public static readonly string[] Modes = { ModeAuto, ModeRemoteOnly, ModeLocalOnly };

    // One shared client with a short connect timeout so an unreachable host fails fast
    // instead of stalling speech for minutes.
    internal static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromMinutes(3)
    };

    public static string NormalizeMode(string? mode) =>
        Modes.FirstOrDefault(m => string.Equals(m, mode?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? ModeAuto;

    /// <summary>Returns the server base URL (scheme://host:port[/prefix]) or "" when input is blank/invalid.</summary>
    public static string NormalizeBaseUrl(string? input)
    {
        var text = (input ?? "").Trim().TrimEnd('/');
        if (text.Length == 0)
            return "";

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            // Bare host or host:port. Add the Kokoro-FastAPI default port when none is given.
            var hostPart = text.Split('/')[0];
            var hasPort = hostPart.StartsWith('[')
                ? hostPart.Contains("]:", StringComparison.Ordinal)
                : hostPart.Count(c => c == ':') == 1;
            text = "http://" + (hasPort ? text : hostPart + ":" + DefaultPort + text[hostPart.Length..]);
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
            return "";

        var path = uri.AbsolutePath.TrimEnd('/');
        foreach (var suffix in new[] { "/v1/audio/speech", "/v1/audio/voices", "/v1/audio", "/v1" })
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                path = path[..^suffix.Length];
                break;
            }
        }

        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    public static string SpeechUrl(string baseUrl) => baseUrl + "/v1/audio/speech";

    public static async Task<KokoroProbeResult> ProbeAsync(string? input, CancellationToken ct = default)
    {
        var baseUrl = NormalizeBaseUrl(input);
        if (baseUrl.Length == 0)
            return new KokoroProbeResult(false, "", "Enter a host such as 192.168.1.50 or http://192.168.1.50:8880", new());

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        var started = DateTime.UtcNow;

        try
        {
            using var response = await Http.GetAsync(baseUrl + "/v1/audio/voices", cts.Token).ConfigureAwait(false);
            var latency = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                var voices = ParseVoices(body);
                return new KokoroProbeResult(true, baseUrl,
                    voices.Count > 0 ? $"Connected in {latency} ms, {voices.Count} voices" : $"Connected in {latency} ms",
                    voices);
            }

            // Not Kokoro-FastAPI; any HTTP answer at /health still means the host is up.
            using var health = await Http.GetAsync(baseUrl + "/health", cts.Token).ConfigureAwait(false);
            latency = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            return health.IsSuccessStatusCode
                ? new KokoroProbeResult(true, baseUrl, $"Reachable in {latency} ms", new())
                : new KokoroProbeResult(false, baseUrl, $"Server answered HTTP {(int)response.StatusCode}", new());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new KokoroProbeResult(false, baseUrl, "Timed out - check the host, port and firewall", new());
        }
        catch (HttpRequestException ex)
        {
            return new KokoroProbeResult(false, baseUrl, $"Unreachable: {ex.Message}", new());
        }
    }

    private static List<string> ParseVoices(string json)
    {
        var result = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var array = root.ValueKind == JsonValueKind.Array
                ? root
                : root.TryGetProperty("voices", out var v) ? v : default;
            if (array.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in array.EnumerateArray())
            {
                var name = item.ValueKind switch
                {
                    JsonValueKind.String => item.GetString(),
                    JsonValueKind.Object when item.TryGetProperty("id", out var id) => id.GetString(),
                    JsonValueKind.Object when item.TryGetProperty("name", out var n) => n.GetString(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(name))
                    result.Add(name.Trim());
            }
        }
        catch (JsonException) { }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>"am_onyx" -> "am_onyx (American Male)", matching the existing voice list format.</summary>
    public static string DescribeVoice(string voice)
    {
        if (voice.Length < 3 || voice[2] != '_')
            return voice;

        var language = char.ToLowerInvariant(voice[0]) switch
        {
            'a' => "American",
            'b' => "British",
            'e' => "Spanish",
            'f' => "French",
            'h' => "Hindi",
            'i' => "Italian",
            'j' => "Japanese",
            'p' => "Portuguese",
            'z' => "Mandarin",
            _ => ""
        };
        var gender = char.ToLowerInvariant(voice[1]) switch
        {
            'f' => "Female",
            'm' => "Male",
            _ => ""
        };
        var label = $"{language} {gender}".Trim();
        return label.Length == 0 ? voice : $"{voice} ({label})";
    }

    /// <summary>Kokoro language code is the first letter of the voice id.</summary>
    public static string LanguageForVoice(string voice)
    {
        var first = voice.Length > 0 ? char.ToLowerInvariant(voice[0]) : 'a';
        return "abefhijpz".Contains(first) ? first.ToString() : "a";
    }
}

public sealed record KokoroProbeResult(bool Ok, string BaseUrl, string Message, List<string> Voices);
