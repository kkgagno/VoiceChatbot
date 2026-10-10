using System;

namespace VoiceChatbot;

/// <summary>
/// Which attached pictures the built-in model (llama-server) reads as they are. It decodes pictures with
/// stb_image, which reads JPEG, PNG, GIF and BMP but not WebP, HEIC, AVIF or TIFF: the app converts those
/// to JPEG with Windows before it sends them.
/// </summary>
public static class PictureFormats
{
    // The first bytes of each file format stb_image reads that people attach.
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Gif = [(byte)'G', (byte)'I', (byte)'F', (byte)'8']; // GIF87a, GIF89a
    private static readonly byte[] Bmp = [(byte)'B', (byte)'M'];

    /// <summary>
    /// True when the picture (base64, as attached) is a JPEG, PNG, GIF or BMP file, judged by its first bytes
    /// (the file name can be wrong, and pasted or phone pictures have none).
    /// </summary>
    public static bool BuiltInModelReadsAsIs(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
            return false;

        // 12 base64 characters are the first 9 bytes, enough for every signature below.
        var head = base64.AsSpan(0, Math.Min(12, base64.Length));
        Span<byte> buffer = stackalloc byte[9];
        if (!Convert.TryFromBase64Chars(head, buffer, out var count))
            return false;
        ReadOnlySpan<byte> start = buffer[..count];
        return start.StartsWith(Jpeg) || start.StartsWith(Png) || start.StartsWith(Gif) || start.StartsWith(Bmp);
    }

    /// <summary>
    /// Puts a picture's pixels (premultiplied 32-bit BGRA, as WPF's Pbgra32) onto a white background, in place,
    /// so a converted picture's transparent parts show white as in picture viewers (JPEG has no transparency).
    /// Opaque pixels do not change.
    /// </summary>
    public static void FlattenOntoWhite(byte[] pbgra)
    {
        ArgumentNullException.ThrowIfNull(pbgra);
        for (var i = 0; i + 3 < pbgra.Length; i += 4)
        {
            // Premultiplied: each color is at most the alpha, so this stays within 255.
            var white = 255 - pbgra[i + 3];
            pbgra[i] = (byte)(pbgra[i] + white);
            pbgra[i + 1] = (byte)(pbgra[i + 1] + white);
            pbgra[i + 2] = (byte)(pbgra[i + 2] + white);
            pbgra[i + 3] = 255;
        }
    }

}
