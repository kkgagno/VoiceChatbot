using System;
using System.Text;

namespace VoiceChatbot;

/// <summary>
/// Turns the bytes of a text file (or an email part) into a string: a byte order mark wins, then a
/// charset the file names, then UTF-8 when the bytes are valid UTF-8, else Windows-1252 (what Notepad
/// and Excel wrote as "ANSI" on English Windows), so old .txt and .csv files keep their accents.
/// </summary>
public static class TextDecoding
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Windows-1252, or Latin-1 when the code page provider is missing.</summary>
    public static Encoding Ansi { get; } = GetEncoding(1252) ?? Encoding.Latin1;

    public static string Decode(byte[] bytes, string? charset = null) =>
        Decode(bytes.AsSpan(), charset);

    public static string Decode(ReadOnlySpan<byte> bytes, string? charset = null)
    {
        if (bytes.IsEmpty)
            return "";

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            return Encoding.UTF32.GetString(bytes[4..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        var named = GetEncoding(charset);
        // A file that says it is ASCII or Latin-1 is really UTF-8 when its bytes are valid UTF-8, and
        // otherwise usually Windows-1252 (smart quotes, euro sign): both fall through to the check below.
        if (named != null && named.CodePage is not (20127 or 28591 or 1252 or 65001))
            return named.GetString(bytes);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Ansi.GetString(bytes);
        }
    }

    /// <summary>True when the bytes are valid UTF-8 (plain ASCII counts).</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>The encoding for a charset name such as "utf-8", "iso-8859-1" or "windows-1251"; null when unknown.</summary>
    public static Encoding? GetEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
            return null;

        var name = charset.Trim().Trim('"', '\'').Trim();
        if (name.Length == 0)
            return null;

        // Mail programs write a few names .NET does not know.
        if (name.Equals("utf8", StringComparison.OrdinalIgnoreCase))
            name = "utf-8";
        else if (name.Equals("ansi", StringComparison.OrdinalIgnoreCase) || name.Equals("cp1252", StringComparison.OrdinalIgnoreCase))
            name = "windows-1252";
        else if (name.StartsWith("cp", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(2), out var cp))
            return GetEncoding(cp);

        try
        {
            return Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
        }

        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The encoding for a Windows code page number (1252, 1251, 932...); null when unknown.</summary>
    public static Encoding? GetEncoding(int codePage)
    {
        if (codePage <= 0)
            return null;

        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
        }

        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
