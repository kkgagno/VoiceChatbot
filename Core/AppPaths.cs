using System;
using System.IO;

namespace VoiceChatbot;

/// <summary>
/// The one place that names Voice Chatbot Mini and its folders. Mini keeps everything it saves (settings,
/// logs, memories, conversations, the knowledge index, transcripts, schedules, the phone remote certificate,
/// downloaded Whisper models, reply audio) in %APPDATA%\VoiceChatbotMini and its scratch files in
/// %TEMP%\VoiceChatbotMini, so it can be installed and run next to the full Voice Chatbot app
/// (%APPDATA%\VoiceChatbot) without either one touching the other's files.
/// </summary>
public static class AppPaths
{
    /// <summary>The product name in the window title, tray, dialogs and logs.</summary>
    public const string ProductName = "Voice Chatbot Mini";

    /// <summary>The folder name under %APPDATA% and %TEMP%.</summary>
    public const string DataFolderName = "VoiceChatbotMini";

    /// <summary>The full app's folder under %APPDATA%. Only read once, to import its settings on first run.</summary>
    public const string FullAppDataFolderName = "VoiceChatbot";

    public const string SettingsFileName = "settings.json";

    /// <summary>%APPDATA%\VoiceChatbotMini.</summary>
    public static string DataDirectory { get; } = Path.Combine(GetRoamingAppData(), DataFolderName);

    /// <summary>%APPDATA%\VoiceChatbot, the full app's data folder.</summary>
    public static string FullAppDataDirectory { get; } = Path.Combine(GetRoamingAppData(), FullAppDataFolderName);

    /// <summary>%TEMP%\VoiceChatbotMini.</summary>
    public static string TempDirectory { get; } = Path.Combine(Path.GetTempPath(), DataFolderName);

    /// <summary>%APPDATA%\VoiceChatbotMini\settings.json.</summary>
    public static string SettingsFile => DataPath(SettingsFileName);

    /// <summary>A file or folder in the data folder, for example DataPath("logs").</summary>
    public static string DataPath(params string[] parts) => Combine(DataDirectory, parts);

    /// <summary>A file or folder in the temp folder, for example TempPath("tts").</summary>
    public static string TempPath(params string[] parts) => Combine(TempDirectory, parts);

    /// <summary>
    /// First run of Mini on a PC that has the full app: when <paramref name="dataDirectory"/> has no settings.json
    /// yet but <paramref name="fullAppDataDirectory"/> has one, copies just that file (models, chats and other
    /// data stay with the full app). Its saved keys still decrypt: they are bound to the Windows user, not the
    /// app. Settings Mini does not know (the full app's extra features) are ignored when it is read. Never
    /// throws; the result says what happened, for the log.
    /// </summary>
    public static SettingsImportResult ImportFullAppSettingsIfMissing(string dataDirectory, string fullAppDataDirectory)
    {
        var target = Path.Combine(dataDirectory, SettingsFileName);
        var source = Path.Combine(fullAppDataDirectory, SettingsFileName);
        try
        {
            if (File.Exists(target))
                return new SettingsImportResult(SettingsImportStatus.AlreadyHasSettings, source, target);
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(source))
                return new SettingsImportResult(SettingsImportStatus.NothingToImport, source, target);

            Directory.CreateDirectory(dataDirectory);
            File.Copy(source, target, overwrite: false);
            return new SettingsImportResult(SettingsImportStatus.Imported, source, target);
        }
        catch (IOException) when (File.Exists(target))
        {
            // Another start of Mini wrote its settings first: keep those.
            return new SettingsImportResult(SettingsImportStatus.AlreadyHasSettings, source, target);
        }
        catch (Exception ex)
        {
            return new SettingsImportResult(SettingsImportStatus.Failed, source, target, ex.Message);
        }
    }

    private static string Combine(string root, string[] parts)
    {
        var path = root;
        foreach (var part in parts)
            path = Path.Combine(path, part);
        return path;
    }

    private static string GetRoamingAppData()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(appData) ? Path.GetTempPath() : appData;
    }
}

public enum SettingsImportStatus
{
    /// <summary>Mini already has its own settings.json; nothing was copied.</summary>
    AlreadyHasSettings,
    /// <summary>The full app has no settings.json on this PC.</summary>
    NothingToImport,
    /// <summary>The full app's settings.json was copied to Mini's data folder.</summary>
    Imported,
    /// <summary>The copy failed; Mini starts with default settings.</summary>
    Failed
}

public sealed record SettingsImportResult(SettingsImportStatus Status, string Source, string Target, string Error = "")
{
    /// <summary>A line for the app log, or "" when there is nothing worth logging.</summary>
    public string Describe() => Status switch
    {
        SettingsImportStatus.Imported => $"First run: copied the settings of the full Voice Chatbot app from {Source} to {Target}.",
        SettingsImportStatus.Failed => $"First run: could not copy the full Voice Chatbot app's settings from {Source} ({Error}). Using default settings.",
        _ => ""
    };
}
