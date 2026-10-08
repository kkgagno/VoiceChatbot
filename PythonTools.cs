using System;
using System.IO;
using System.Linq;

namespace VoiceChatbot;

/// <summary>
/// Finds the Python interpreter and the bundled helper scripts that run on it
/// (Tools\Kokoro for speech output).
/// </summary>
internal static class PythonTools
{
    /// <summary>
    /// VOICECHATBOT_PYTHON when set, then the usual python.org install folders, then python.exe from PATH.
    /// </summary>
    public static string FindPythonExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("VOICECHATBOT_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(localAppData, "Programs", "Python", "Python313", "python.exe"),
            Path.Combine(localAppData, "Programs", "Python", "Python312", "python.exe"),
            Path.Combine(localAppData, "Programs", "Python", "Python311", "python.exe"),
            Path.Combine(programFiles, "Python313", "python.exe"),
            Path.Combine(programFiles, "Python312", "python.exe"),
            Path.Combine(programFiles, "Python311", "python.exe")
        };

        return candidates.FirstOrDefault(File.Exists) ?? "python.exe";
    }

    /// <summary>
    /// Looks for a helper script in Tools\{toolFolder} next to the app, then next to the app itself,
    /// then in %USERPROFILE%\VoiceChatbot. Returns null when it is not found.
    /// </summary>
    public static string? FindToolScript(string toolFolder, string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Tools", toolFolder, fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "VoiceChatbot",
                fileName)
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
