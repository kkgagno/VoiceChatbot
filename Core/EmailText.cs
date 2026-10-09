using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Readable text from a saved email (.eml) or web archive (.mht): the From, To, Cc, Date and Subject
/// headers, then the message text. MIME parts are followed: text/plain is preferred, an HTML-only
/// message is converted to text, quoted-printable and base64 are decoded in the part's charset,
/// encoded header words (=?utf-8?B?...?=) are decoded, and attachments are listed by name but not read.
/// </summary>
public static class EmailText
{
    public const int DefaultMaxChars = 2_000_000;
    private const int MaxDepth = 12;

    private static readonly string[] ShownHeaders = { "From", "To", "Cc", "Date", "Subject" };

    private static readonly Regex EncodedWord = new(
        @"=\?(?<charset>[^?\s]+)\?(?<enc>[bBqQ])\?(?<text>[^?]*)\?=",
        RegexOptions.CultureInvariant);
    private static readonly Regex SpaceBetweenEncodedWords = new(
        @"(?<=\?=)\s+(?==\?[^?\s]+\?[bBqQ]\?)",
        RegexOptions.CultureInvariant);

    /// <summary>A parsed MIME entity: its headers (names in any case) and its raw body (one char per byte).</summary>
    private sealed record Entity(Dictionary<string, string> Headers, string Body);

    /// <summary>True when the first bytes look like the headers of an email or MIME document.</summary>
    public static bool LooksLikeEmail(string? start)
    {
        if (string.IsNullOrEmpty(start))
            return false;
        return Regex.IsMatch(start, @"^(?:[ \t]*\r?\n)*(?:Return-Path|Received|From|To|Subject|Date|MIME-Version|Message-ID|Content-Type|X-[A-Za-z-]+|Delivered-To|Reply-To):",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static string Extract(byte[] raw, int maxChars = DefaultMaxChars)
    {
        if (raw == null || raw.Length == 0)
            return "";

        // Latin-1 maps every byte to one char, so a part's bytes can be recovered exactly for its charset.
        var text = Encoding.Latin1.GetString(raw);
        if (text.StartsWith("\u00EF\u00BB\u00BF", StringComparison.Ordinal))
            text = text[3..];

        var root = ParseEntity(text);
        var output = new StringBuilder();
        foreach (var name in ShownHeaders)
        {
            if (root.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                output.Append(name).Append(": ").Append(DecodeHeader(value)).Append('\n');
        }

        var attachments = new List<string>();
        var body = new StringBuilder();
        AppendEntity(root, body, attachments, 0, maxChars);

        if (attachments.Count > 0)
            output.Append("Attachments: ").Append(string.Join(", ", attachments.Distinct(StringComparer.OrdinalIgnoreCase))).Append('\n');
        if (output.Length > 0)
            output.Append('\n');
        output.Append(body);

        var result = Normalize(output.ToString());
        return result.Length > maxChars ? result[..maxChars] : result;
    }

    private static void AppendEntity(Entity entity, StringBuilder output, List<string> attachments, int depth, int maxChars)
    {
        if (depth > MaxDepth || output.Length >= maxChars)
            return;

        var (mediaType, parameters) = ParseHeaderWithParameters(Header(entity, "Content-Type"));
        if (mediaType.Length == 0)
            mediaType = "text/plain";
        var (disposition, dispositionParameters) = ParseHeaderWithParameters(Header(entity, "Content-Disposition"));
        var fileName = FirstNonEmpty(Parameter(dispositionParameters, "filename"), Parameter(parameters, "name"));

        if (depth > 0 && (disposition == "attachment" || (fileName.Length > 0 && !mediaType.StartsWith("text/", StringComparison.Ordinal) && !mediaType.StartsWith("multipart/", StringComparison.Ordinal))))
        {
            // Attachments are named, not read; inline images in an HTML mail are not listed.
            if (fileName.Length > 0 && !(disposition == "inline" && mediaType.StartsWith("image/", StringComparison.Ordinal)))
                attachments.Add(DecodeHeader(fileName));
            return;
        }

        if (mediaType.StartsWith("multipart/", StringComparison.Ordinal))
        {
            var boundary = Parameter(parameters, "boundary");
            var parts = boundary.Length == 0 ? new List<Entity>() : SplitMultipart(entity.Body, boundary);
            if (parts.Count == 0)
            {
                AppendText(output, DecodeBody(entity, parameters));
                return;
            }

            if (mediaType == "multipart/alternative")
            {
                // Prefer the plain text version; use the HTML one when the plain text is missing or empty.
                foreach (var part in parts.OrderByDescending(AlternativeRank))
                {
                    var candidate = new StringBuilder();
                    AppendEntity(part, candidate, attachments, depth + 1, maxChars);
                    if (candidate.ToString().Trim().Length > 0)
                    {
                        AppendText(output, candidate.ToString());
                        return;
                    }
                }
                return;
            }

            // mixed, related, signed, report...: every readable part in order (a related message's
            // images and a signature are not text, so they are skipped).
            foreach (var part in parts)
                AppendEntity(part, output, attachments, depth + 1, maxChars);
            return;
        }

        if (mediaType == "message/rfc822")
        {
            var inner = ParseEntity(Encoding.Latin1.GetString(DecodeTransfer(entity)));
            var forwarded = new StringBuilder("---------- Forwarded message ----------\n");
            foreach (var name in ShownHeaders)
            {
                if (inner.Headers.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
                    forwarded.Append(name).Append(": ").Append(DecodeHeader(value)).Append('\n');
            }
            forwarded.Append('\n');
            AppendEntity(inner, forwarded, attachments, depth + 1, maxChars);
            AppendText(output, forwarded.ToString());
            return;
        }

        if (mediaType == "text/html" || mediaType == "application/xhtml+xml")
        {
            AppendText(output, HtmlDocumentText.ToPlainText(DecodeBody(entity, parameters)));
            return;
        }

        if (mediaType.StartsWith("text/", StringComparison.Ordinal) && mediaType is not ("text/calendar" or "text/x-vcard" or "text/vcard" or "text/css" or "text/rtf"))
        {
            AppendText(output, DecodeBody(entity, parameters));
            return;
        }

        if (fileName.Length > 0)
            attachments.Add(DecodeHeader(fileName));
    }

    private static int AlternativeRank(Entity part)
    {
        var (type, _) = ParseHeaderWithParameters(Header(part, "Content-Type"));
        return type switch
        {
            "" or "text/plain" => 3,
            "text/html" => 2,
            _ when type.StartsWith("multipart/", StringComparison.Ordinal) => 1,
            _ => 0
        };
    }

    private static void AppendText(StringBuilder output, string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return;
        if (output.Length > 0)
            output.Append("\n\n");
        output.Append(trimmed);
    }

    private static string DecodeBody(Entity entity, Dictionary<string, string> contentTypeParameters)
    {
        var bytes = DecodeTransfer(entity);
        return TextDecoding.Decode(bytes, Parameter(contentTypeParameters, "charset"));
    }

    private static byte[] DecodeTransfer(Entity entity)
    {
        var encoding = Header(entity, "Content-Transfer-Encoding").Trim().ToLowerInvariant();
        return encoding switch
        {
            "base64" => DecodeBase64(entity.Body),
            "quoted-printable" => DecodeQuotedPrintable(entity.Body, header: false),
            _ => Encoding.Latin1.GetBytes(entity.Body)
        };
    }

    // ==================== MIME structure ====================

    private static Entity ParseEntity(string text)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var position = 0;
        string? currentName = null;
        var currentValue = new StringBuilder();

        void Commit()
        {
            if (currentName != null && !headers.ContainsKey(currentName))
                headers[currentName] = currentValue.ToString().Trim();
            currentName = null;
            currentValue.Clear();
        }

        while (position < text.Length)
        {
            var lineEnd = text.IndexOf('\n', position);
            var next = lineEnd < 0 ? text.Length : lineEnd + 1;
            var line = text.AsSpan(position, (lineEnd < 0 ? text.Length : lineEnd) - position).TrimEnd('\r');

            if (line.IsEmpty)
            {
                position = next;
                break; // the blank line that ends the headers
            }

            if (headers.Count == 0 && currentName == null && line.StartsWith("From ", StringComparison.Ordinal))
            {
                position = next; // an mbox "From sender date" line before the headers
                continue;
            }

            if ((line[0] == ' ' || line[0] == '\t') && currentName != null)
            {
                currentValue.Append(' ').Append(line.Trim());
            }
            else
            {
                var colon = line.IndexOf(':');
                if (colon <= 0 || line[..colon].ContainsAny(' ', '\t'))
                {
                    // Not a header line: the headers are missing, this is already the body.
                    if (headers.Count == 0 && currentName == null)
                        return new Entity(headers, text[position..]);
                    position = next;
                    continue;
                }

                Commit();
                currentName = line[..colon].ToString();
                currentValue.Append(line[(colon + 1)..].Trim());
            }

            position = next;
        }

        Commit();
        return new Entity(headers, position < text.Length ? text[position..] : "");
    }

    private static List<Entity> SplitMultipart(string body, string boundary)
    {
        var parts = new List<Entity>();
        var delimiter = "--" + boundary;
        var start = -1; // start of the current part's content
        var position = 0;
        while (position <= body.Length)
        {
            var index = body.IndexOf(delimiter, position, StringComparison.Ordinal);
            if (index < 0)
                break;

            var atLineStart = index == 0 || body[index - 1] == '\n';
            if (!atLineStart)
            {
                position = index + delimiter.Length;
                continue;
            }

            if (start >= 0)
            {
                // The line break before the delimiter belongs to the delimiter.
                var end = index;
                if (end > start && body[end - 1] == '\n')
                    end--;
                if (end > start && body[end - 1] == '\r')
                    end--;
                parts.Add(ParseEntity(body[start..Math.Max(start, end)]));
            }

            var after = index + delimiter.Length;
            if (after + 2 <= body.Length && body[after] == '-' && body[after + 1] == '-')
                return parts; // closing delimiter

            var lineEnd = body.IndexOf('\n', after);
            if (lineEnd < 0)
                return parts;
            start = lineEnd + 1;
            position = start;
        }

        // No closing delimiter (a cut-off message): keep the last part.
        if (start >= 0 && start < body.Length)
            parts.Add(ParseEntity(body[start..]));
        return parts;
    }

    private static string Header(Entity entity, string name) =>
        entity.Headers.TryGetValue(name, out var value) ? value : "";

    /// <summary>Splits "text/plain; charset=utf-8; name=\"a b.txt\"" into a lower-case value and its parameters.</summary>
    private static (string Value, Dictionary<string, string> Parameters) ParseHeaderWithParameters(string header)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(header))
            return ("", parameters);

        var segments = SplitParameters(header);
        var value = segments.Count > 0 ? segments[0].Trim().ToLowerInvariant() : "";
        var continued = new SortedDictionary<string, SortedDictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var segment in segments.Skip(1))
        {
            var equals = segment.IndexOf('=');
            if (equals <= 0)
                continue;
            var key = segment[..equals].Trim();
            var raw = segment[(equals + 1)..].Trim();
            if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                raw = raw[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");

            // RFC 2231: name*=utf-8''a%20b.pdf, and name*0= / name*1= continuations.
            var extended = key.EndsWith('*');
            if (extended)
            {
                key = key[..^1];
                raw = DecodeRfc2231(raw);
            }

            var star = key.IndexOf('*');
            if (star > 0 && int.TryParse(key[(star + 1)..], out var order))
            {
                var baseKey = key[..star];
                if (!continued.TryGetValue(baseKey, out var pieces))
                    continued[baseKey] = pieces = new SortedDictionary<int, string>();
                pieces[order] = raw;
                continue;
            }

            if (!parameters.ContainsKey(key) || extended)
                parameters[key] = raw;
        }

        foreach (var (key, pieces) in continued)
        {
            if (!parameters.ContainsKey(key))
                parameters[key] = string.Concat(pieces.Values);
        }

        return (value, parameters);
    }

    private static List<string> SplitParameters(string header)
    {
        var segments = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < header.Length; i++)
        {
            var c = header[i];
            if (c == '"' && (i == 0 || header[i - 1] != '\\'))
                quoted = !quoted;
            if (c == ';' && !quoted)
            {
                segments.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        segments.Add(current.ToString());
        return segments;
    }

    private static string DecodeRfc2231(string value)
    {
        var parts = value.Split('\'', 3);
        var charset = parts.Length == 3 ? parts[0] : "";
        var encoded = parts.Length == 3 ? parts[2] : value;
        var bytes = new List<byte>(encoded.Length);
        for (var i = 0; i < encoded.Length; i++)
        {
            if (encoded[i] == '%' && i + 2 < encoded.Length && IsHex(encoded[i + 1]) && IsHex(encoded[i + 2]))
            {
                bytes.Add(System.Convert.ToByte(encoded.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.Add((byte)(encoded[i] & 0xFF));
            }
        }
        return TextDecoding.Decode(bytes.ToArray(), charset.Length > 0 ? charset : null);
    }

    private static string Parameter(Dictionary<string, string> parameters, string name) =>
        parameters.TryGetValue(name, out var value) ? value.Trim() : "";

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    // ==================== Decoding ====================

    /// <summary>Decodes RFC 2047 encoded words and raw UTF-8 bytes in a header value (given one char per byte).</summary>
    public static string DecodeHeader(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        // Raw 8-bit headers (not allowed, but common) are usually UTF-8.
        var text = value;
        if (text.Any(c => c > 0x7F) && text.All(c => c <= 0xFF))
            text = TextDecoding.Decode(Encoding.Latin1.GetBytes(text));

        if (!text.Contains("=?", StringComparison.Ordinal))
            return text.Trim();

        // White space between two encoded words is not part of the text.
        text = SpaceBetweenEncodedWords.Replace(text, "");
        return EncodedWord.Replace(text, match =>
        {
            var charset = match.Groups["charset"].Value;
            var star = charset.IndexOf('*'); // RFC 2231 language: utf-8*en
            if (star >= 0)
                charset = charset[..star];
            var payload = match.Groups["text"].Value;
            var bytes = match.Groups["enc"].Value is "b" or "B"
                ? DecodeBase64(payload)
                : DecodeQuotedPrintable(payload, header: true);
            return TextDecoding.Decode(bytes, charset);
        }).Trim();
    }

    public static byte[] DecodeQuotedPrintable(string text, bool header)
    {
        var bytes = new List<byte>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '=')
            {
                if (i + 2 < text.Length && IsHex(text[i + 1]) && IsHex(text[i + 2]))
                {
                    bytes.Add((byte)((HexValue(text[i + 1]) << 4) | HexValue(text[i + 2])));
                    i += 2;
                    continue;
                }

                // Soft line break: "=" at the end of a line (maybe followed by spaces).
                var j = i + 1;
                while (j < text.Length && (text[j] == ' ' || text[j] == '\t'))
                    j++;
                if (j < text.Length && text[j] == '\r')
                    j++;
                if (j < text.Length && text[j] == '\n')
                {
                    i = j;
                    continue;
                }
                if (j >= text.Length)
                    break;

                bytes.Add((byte)'=');
                continue;
            }

            if (header && c == '_')
            {
                bytes.Add((byte)' ');
                continue;
            }

            bytes.Add((byte)(c & 0xFF));
        }

        return bytes.ToArray();
    }

    public static byte[] DecodeBase64(string text)
    {
        var clean = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/')
                clean.Append(c);
            else if (c == '-')
                clean.Append('+'); // URL-safe alphabet
            else if (c == '_')
                clean.Append('/');
        }

        // Drop a dangling character and restore missing padding.
        var length = clean.Length - clean.Length % 4;
        var remainder = clean.Length % 4;
        if (remainder == 1)
            clean.Length = length;
        else if (remainder > 1)
            clean.Append('=', 4 - remainder);

        try
        {
            return System.Convert.FromBase64String(clean.ToString());
        }
        catch (FormatException)
        {
            return Array.Empty<byte>();
        }
    }

    private static bool IsHex(char c) => HexValue(c) >= 0;

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };

    private static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var output = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var raw in lines)
        {
            var line = raw.Replace('\u00A0', ' ').TrimEnd();
            if (line.Trim().Length == 0)
            {
                blank++;
                continue;
            }

            if (output.Length > 0)
                output.Append(blank > 0 ? "\n\n" : "\n");
            output.Append(line);
            blank = 0;
        }

        return output.ToString();
    }
}
