using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

/// <summary>
/// Downloads a public http/https page and returns readable text for the model (fetch_web_page).
/// Local-network and loopback addresses are refused, redirects are checked hop by hop, and the
/// download and returned text are capped. Failures come back as "Error: ..." text, never exceptions.
/// </summary>
public sealed class WebPageReader : IDisposable
{
    public const int DefaultMaxChars = 8000;
    private const int MaxDownloadBytes = 3_000_000;
    private const int MaxRedirects = 5;

    private readonly HttpClient _http;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveHost;

    public WebPageReader(HttpMessageHandler? handler = null, Func<string, CancellationToken, Task<IPAddress[]>>? resolveHost = null)
    {
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(8)
        })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _resolveHost = resolveHost ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
    }

    public async Task<string> FetchReadableTextAsync(string? url, CancellationToken ct = default, int maxChars = DefaultMaxChars)
    {
        if (!TryNormalizeUrl(url, out var uri, out var error))
            return $"Error: {error}";

        try
        {
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                var blocked = await CheckResolvedAddressAsync(uri, ct).ConfigureAwait(false);
                if (blocked is not null)
                    return $"Error: {blocked}";

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) VoiceChatbot/1.0");
                request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.5");
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    var location = response.Headers.Location;
                    if (location is null)
                        return $"Error: {uri.Host} sent a redirect without a location.";

                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (!TryNormalizeUrl(next.ToString(), out uri, out error))
                        return $"Error: redirected to a blocked address ({error})";
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return $"Error: {uri.Host} returned HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).";

                var contentType = response.Content.Headers.ContentType;
                var mediaType = contentType?.MediaType?.ToLowerInvariant() ?? "";
                if (!IsReadableMediaType(mediaType))
                    return $"Error: {uri} is {mediaType}, not a readable web page.";

                var raw = await ReadCappedTextAsync(response.Content, contentType, ct).ConfigureAwait(false);
                var isHtml = mediaType.Contains("html", StringComparison.Ordinal) ||
                             (mediaType.Length == 0 && Regex.IsMatch(raw, @"<html|<body|<p[\s>]", RegexOptions.IgnoreCase));
                var title = isHtml ? HtmlText.ExtractTitle(raw) : "";
                var text = isHtml ? HtmlText.ToPlainText(raw) : HtmlText.NormalizeWhitespace(raw);
                return HtmlText.FormatPage(uri, title, text, maxChars);
            }

            return "Error: too many redirects.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return $"Error: {uri.Host} did not respond in time.";
        }
        catch (Exception ex)
        {
            return $"Error: could not read {uri}: {ex.Message}";
        }
    }

    /// <summary>Accepts http/https URLs (adds https:// when no scheme is given) that do not point at a local address.</summary>
    public static bool TryNormalizeUrl(string? input, out Uri uri, out string error)
    {
        uri = null!;
        var text = (input ?? "").Trim().Trim('<', '>', '"', '\'', '`').Trim();
        if (text.Length == 0)
        {
            error = "no URL was given.";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
        {
            error = $"\"{input}\" is not a valid URL.";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "only http and https URLs can be read.";
            return false;
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            error = "URLs with user names or passwords are not supported.";
            return false;
        }

        if (IsLocalHostName(parsed.Host))
        {
            error = "local network and loopback addresses are not allowed.";
            return false;
        }

        uri = parsed;
        error = "";
        return true;
    }

    /// <summary>Loopback, private, link-local, carrier-grade NAT, multicast and unspecified addresses.</summary>
    public static bool IsNonPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
            address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0 || b[0] == 10 || b[0] == 127 ||
                   (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||
                   (b[0] == 169 && b[1] == 254) ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168) ||
                   b[0] >= 224;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast ||
                   (b[0] & 0xFE) == 0xFC; // fc00::/7 unique local
        }

        return true;
    }

    private static bool IsLocalHostName(string host)
    {
        var name = host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (IPAddress.TryParse(name, out var literal))
            return IsNonPublicAddress(literal);

        return !name.Contains('.') ||
               name == "localhost" ||
               name.EndsWith(".localhost", StringComparison.Ordinal) ||
               name.EndsWith(".local", StringComparison.Ordinal) ||
               name.EndsWith(".lan", StringComparison.Ordinal) ||
               name.EndsWith(".internal", StringComparison.Ordinal) ||
               name.EndsWith(".home.arpa", StringComparison.Ordinal);
    }

    private async Task<string?> CheckResolvedAddressAsync(Uri uri, CancellationToken ct)
    {
        var host = uri.Host.Trim('[', ']');
        if (IPAddress.TryParse(host, out _))
            return null; // Literal addresses were checked by TryNormalizeUrl.

        IPAddress[] addresses;
        try
        {
            addresses = await _resolveHost(host, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return $"could not find the server {host}.";
        }

        if (addresses.Length == 0)
            return $"could not find the server {host}.";

        return addresses.Any(IsNonPublicAddress)
            ? $"{host} points at a local network address, which is not allowed."
            : null;
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool IsReadableMediaType(string mediaType) =>
        mediaType.Length == 0 ||
        mediaType.StartsWith("text/", StringComparison.Ordinal) ||
        mediaType is "application/json" or "application/xml" or "application/xhtml+xml" or
            "application/rss+xml" or "application/atom+xml" or "application/ld+json";

    private static async Task<string> ReadCappedTextAsync(HttpContent content, MediaTypeHeaderValue? contentType, CancellationToken ct)
    {
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while (buffer.Length < MaxDownloadBytes &&
               (read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), ct).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        var encoding = Encoding.UTF8;
        var charset = contentType?.CharSet?.Trim('"', ' ');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { encoding = Encoding.GetEncoding(charset); }
            catch (ArgumentException) { /* Unknown charset: keep UTF-8. */ }
        }

        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Turns HTML into plain readable text. Regex based: good enough for model context, not a full parser.</summary>
public static class HtmlText
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant;

    public static string ExtractTitle(string html)
    {
        var match = Regex.Match(html ?? "", @"<title[^>]*>(.*?)</title\s*>", Options);
        return match.Success ? NormalizeWhitespace(WebUtility.HtmlDecode(Regex.Replace(match.Groups[1].Value, "<[^>]+>", " "))) : "";
    }

    public static string ToPlainText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return "";

        var text = Regex.Replace(html, @"<!--.*?-->", " ", Options);
        text = Regex.Replace(text, @"<(script|style|noscript|svg|template|head|iframe|nav|footer|aside)\b[^>]*>.*?</\1\s*>", " ", Options);
        text = Regex.Replace(text, @"<(script|style|noscript|svg|template|head|iframe|nav|footer|aside)\b[^>]*/?>", " ", Options);

        // Prefer the page's main content when it is marked up and substantial.
        var main = PreferredSection(text, "main") ?? PreferredSection(text, "article");
        if (main is not null)
            text = main;

        text = Regex.Replace(text, @"<li\b[^>]*>", "\n- ", Options);
        text = Regex.Replace(text, @"<h[1-6]\b[^>]*>", "\n\n", Options);
        text = Regex.Replace(text, @"<(br|hr)\b[^>]*>", "\n", Options);
        text = Regex.Replace(text, @"</(p|div|tr|h[1-6]|section|article|main|header|blockquote|pre|table|ul|ol|dl|dt|dd|figure|figcaption)\s*>", "\n", Options);
        text = Regex.Replace(text, @"</t[dh]\s*>", " ", Options);
        text = Regex.Replace(text, @"<[^>]+>", " ", Options);
        text = WebUtility.HtmlDecode(text);
        return NormalizeWhitespace(text);
    }

    /// <summary>Collapses spaces inside lines, trims lines and keeps at most one blank line in a row.</summary>
    public static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(line => Regex.Replace(line.Replace(' ', ' '), @"[ \t\f\v]+", " ").Trim());
        var joined = string.Join("\n", lines);
        return Regex.Replace(joined, @"\n{3,}", "\n\n").Trim();
    }

    /// <summary>Formats the tool result: title, URL, then the text capped at maxChars.</summary>
    public static string FormatPage(Uri uri, string title, string text, int maxChars)
    {
        maxChars = Math.Max(200, maxChars);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(title))
            sb.AppendLine($"Title: {title}");
        sb.AppendLine($"URL: {uri}");
        sb.AppendLine();

        if (string.IsNullOrWhiteSpace(text))
        {
            sb.Append("(The page has no readable text. It may need JavaScript to show its content.)");
            return sb.ToString();
        }

        if (text.Length <= maxChars)
        {
            sb.Append(text);
            return sb.ToString();
        }

        var cut = text.LastIndexOfAny(new[] { ' ', '\n' }, maxChars - 1, Math.Min(200, maxChars));
        var shown = text[..(cut > 0 ? cut : maxChars)].TrimEnd();
        sb.AppendLine(shown);
        sb.Append($"[Page text truncated: showing {shown.Length:N0} of {text.Length:N0} characters.]");
        return sb.ToString();
    }

    private static string? PreferredSection(string html, string tag)
    {
        var start = Regex.Match(html, $@"<{tag}\b[^>]*>", Options);
        if (!start.Success)
            return null;

        var end = html.LastIndexOf($"</{tag}", StringComparison.OrdinalIgnoreCase);
        if (end <= start.Index)
            return null;

        var section = html[start.Index..end];
        var visibleChars = Regex.Replace(section, "<[^>]+>", "").Count(c => !char.IsWhiteSpace(c));
        return visibleChars >= 300 ? section : null;
    }
}
