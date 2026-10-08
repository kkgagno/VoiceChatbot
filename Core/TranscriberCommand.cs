using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>Checks for the external (AMD Ryzen AI Whisper) transcriber command line.</summary>
public static class TranscriberCommand
{
    // Any {placeholder} except {input}, e.g. a leftover "{ryzen}" from an old settings file.
    private static readonly Regex UnknownPlaceholder = new(@"\{(?!input\})[^{}\s]*\}", RegexOptions.IgnoreCase);

    private static readonly Regex QuotedProgram = new(
        @"^\s*(?:call\s+)?""(?<path>[^""]+\.(?:bat|cmd|exe|ps1|py))""", RegexOptions.IgnoreCase);

    private static readonly Regex BareProgram = new(
        @"^\s*(?:call\s+)?(?<path>[^\s""]+\.(?:bat|cmd|exe|ps1|py))(?:\s|$)", RegexOptions.IgnoreCase);

    /// <summary>False for blank commands and ones holding placeholders other than {input}.</summary>
    public static bool IsUsable(string? command)
    {
        var text = command?.Trim() ?? "";
        return text.Length > 0 && !UnknownPlaceholder.IsMatch(text);
    }

    /// <summary>
    /// The script or program the command starts, when it names one by path (env vars not expanded),
    /// e.g. "%USERPROFILE%\VoiceChatbot\tools\ryzen-ai-whisper-transcribe.bat". Null for plain commands.
    /// </summary>
    public static string? GetProgramPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;

        var match = QuotedProgram.Match(command);
        if (!match.Success)
            match = BareProgram.Match(command);
        return match.Success ? match.Groups["path"].Value : null;
    }

    /// <summary>True when the shell could not find the command or script at all (not a transcription error).</summary>
    public static bool IsNotFoundError(string? message, int exitCode)
    {
        if (exitCode == 9009 || exitCode == 127)
            return true;

        var text = message ?? "";
        return text.Contains("is not recognized as an internal or external command", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("The system cannot find the path specified", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("The system cannot find the file specified", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("command not found", StringComparison.OrdinalIgnoreCase);
    }
}
