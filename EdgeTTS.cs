using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

/// <summary>
/// Client for Microsoft Edge TTS - same voices as Edge Read Aloud, free, no API key.
/// Generates audio via Microsoft's speech synthesis endpoint.
/// </summary>
public static class EdgeTTS
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Popular Edge TTS voices
    public static List<string> Voices { get; } = new()
    {
        "en-US-JennyNeural",
        "en-US-GuyNeural",
        "en-US-AriaNeural",
        "en-US-DavisNeural",
        "en-US-AmberNeural",
        "en-US-AnaNeural",
        "en-US-AndrewNeural",
        "en-US-EmmaNeural",
        "en-US-BrianNeural",
        "en-US-ChristopherNeural",
        "en-US-ElizabethNeural",
        "en-US-EricNeural",
        "en-US-MichelleNeural",
        "en-US-RogerNeural",
        "en-GB-SoniaNeural",
        "en-GB-RyanNeural",
        "en-GB-MiaNeural",
        "en-GB-ThomasNeural",
        "en-AU-NatashaNeural",
        "en-AU-WilliamNeural",
        "en-CA-ClaraNeural",
        "en-CA-LiamNeural",
        "de-DE-KatjaNeural",
        "de-DE-ConradNeural",
        "fr-FR-DeniseNeural",
        "fr-FR-HenriNeural",
        "es-ES-ElviraNeural",
        "es-ES-AlvaroNeural",
        "it-IT-ElsaNeural",
        "it-IT-IsabellaNeural",
        "ja-JP-NanamiNeural",
        "ja-JP-KeitaNeural",
        "ko-KR-SunHiNeural",
        "ko-KR-InJoonNeural",
        "zh-CN-XiaoxiaoNeural",
        "zh-CN-YunxiNeural",
        "pt-BR-FranciscaNeural",
        "pt-BR-AntonioNeural",
        "ru-RU-DariyaNeural",
        "ru-RU-DmitriNeural",
    };

    /// <summary>
    /// Synthesize speech using Microsoft Edge TTS endpoint.
    /// Returns MP3 audio bytes.
    /// </summary>
    public static async Task<byte[]> SynthesizeAsync(string text, string voice = "en-US-JennyNeural",
        string rate = "+0%", string volume = "+0%", CancellationToken ct = default)
    {
        // Build SSML
        var ssml = $@"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'>
<voice name='{voice}'>
<prosody rate='{rate}' volume='{volume}'>
{System.Security.SecurityElement.Escape(text)}
</prosody>
</voice>
</speak>";

        // Generate required headers
        var requestId = Guid.NewGuid().ToString("N");
        var date = DateTime.UtcNow.ToString("R");

        // Use the public cognitive services endpoint (no key needed for Edge voices)
        var url = $"https://southeastasia.tts.speech.microsoft.com/cognitiveservices/v1";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        request.Headers.Add("X-Microsoft-OutputFormat", "audio-16khz-128kbitrate-mono-mp3");
        request.Headers.Add("Content-Type", "application/ssml+xml");
        request.Headers.Add("X-RequestId", requestId);

        // Authorization token for Edge TTS
        var jwt = GenerateToken();
        request.Headers.Add("Authorization", $"Bearer {jwt}");

        request.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private static string GenerateToken()
    {
        // Edge TTS uses a simple JWT-like token
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expiry = now + 600;

        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"none\",\"typ\":\"JWT\"}"));
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"exp\":{expiry},\"iss\":\"edge-tts\"}}"));
        var sig = Convert.ToBase64String(Encoding.UTF8.GetBytes(""));

        return $"{header}.{payload}.{sig}";
    }
}