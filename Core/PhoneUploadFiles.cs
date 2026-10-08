using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VoiceChatbot;

/// <summary>
/// Safe handling of file names the phone sends with uploads. The name is only ever shown as text;
/// files are saved under a new random name that keeps at most a short alphanumeric extension, so a
/// name such as "..\..\x.exe" or "a.png:stream" cannot place a file outside its folder.
/// </summary>
public static class PhoneUploadFiles
{
    public const string DefaultDisplayName = "attached file";
    public const int MaxDisplayNameLength = 120;
    public const int MaxExtensionLength = 10;

    /// <summary>
    /// The last path segment (either slash), without control or invisible formatting characters
    /// (such as right-to-left overrides), ':' replaced by '_', trimmed and shortened to <see cref="MaxDisplayNameLength"/>.
    /// </summary>
    public static string SafeDisplayName(string? fileName)
    {
        var name = fileName ?? "";
        var slash = name.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0)
            name = name[(slash + 1)..];

        // Runes, so emoji survive and a broken surrogate becomes U+FFFD instead of invalid text.
        var builder = new StringBuilder(name.Length);
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
                continue;
            // ':' would name an NTFS alternate data stream.
            builder.Append(rune.Value == ':' ? "_" : rune.ToString());
        }

        name = builder.ToString().Trim().TrimEnd('.').Trim();
        if (name.Length == 0)
            return DefaultDisplayName;

        if (name.Length > MaxDisplayNameLength)
        {
            var extension = SafeExtension(name);
            var stem = name[..(MaxDisplayNameLength - extension.Length)];
            if (char.IsHighSurrogate(stem[^1]))
                stem = stem[..^1];
            name = stem.TrimEnd() + extension;
        }

        return name;
    }

    /// <summary>
    /// The extension in lower case, like ".png", when it is 1 to <see cref="MaxExtensionLength"/> ASCII letters
    /// or digits; otherwise "".
    /// </summary>
    public static string SafeExtension(string? fileName)
    {
        var name = fileName ?? "";
        var dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1)
            return "";

        var extension = name[(dot + 1)..];
        if (extension.Length > MaxExtensionLength || !extension.All(char.IsAsciiLetterOrDigit))
            return "";

        return "." + extension.ToLowerInvariant();
    }

    /// <summary>
    /// A new path directly inside <paramref name="folder"/>: a random name plus the upload's safe extension.
    /// Throws if the result would not be inside the folder (it cannot be, but this is checked anyway).
    /// </summary>
    public static string CreateTempPath(string folder, string? originalFileName)
    {
        var root = Path.GetFullPath(folder);
        var path = Path.GetFullPath(Path.Combine(root, Guid.NewGuid().ToString("N") + SafeExtension(originalFileName)));
        if (!IsDirectlyInside(root, path))
            throw new InvalidOperationException("The upload path was outside its folder.");
        return path;
    }

    /// <summary>True when <paramref name="path"/> is a file directly inside <paramref name="folder"/>.</summary>
    public static bool IsDirectlyInside(string folder, string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var full = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(full);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return parent != null &&
               string.Equals(Path.TrimEndingDirectorySeparator(parent), root, comparison) &&
               Path.GetFileName(full).Length > 0;
    }
}
