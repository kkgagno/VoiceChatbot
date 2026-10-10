using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Appends to one log file per day (app-YYYYMMDD.log) and deletes files older than the retention window.
/// Thread-safe and never throws: a logging problem must not take the app down.
/// </summary>
public sealed class FileLogger
{
    public const int DefaultRetentionDays = 7;
    private const int MaxEntryChars = 32_000;
    private const int MinSecretLength = 4;
    private const string Mask = "****";
    private static readonly Regex FileNamePattern = new(@"^app-(\d{8})\.log$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;
    private DateTime _lastPrunedDay = DateTime.MinValue;

    public FileLogger(string logDirectory, int retentionDays = DefaultRetentionDays, Func<DateTime>? clock = null)
    {
        LogDirectory = logDirectory;
        RetentionDays = Math.Max(1, retentionDays);
        _clock = clock ?? (() => DateTime.Now);
    }

    public string LogDirectory { get; }
    public int RetentionDays { get; }

    /// <summary>
    /// Optional source of secret values (API keys, passwords) that are replaced by "****" in every entry,
    /// so a log file can be shared when reporting a problem.
    /// </summary>
    public Func<IEnumerable<string?>>? SecretsProvider { get; set; }

    public string GetFilePath(DateTime day) => Path.Combine(LogDirectory, $"app-{day:yyyyMMdd}.log");

    public void Info(string? message) => Write("INFO", message);
    public void Warn(string? message, Exception? ex = null) => Write("WARN", message, ex);
    public void Error(string? message, Exception? ex = null) => Write("ERROR", message, ex);

    public void Write(string level, string? message, Exception? ex = null)
    {
        try
        {
            var now = _clock();
            var bytes = Encoding.UTF8.GetBytes(Format(now, level, message, ex));
            lock (_gate)
            {
                Directory.CreateDirectory(LogDirectory);
                if (now.Date != _lastPrunedDay)
                {
                    _lastPrunedDay = now.Date;
                    PruneOldFiles(now);
                }

                // ReadWrite sharing lets a second app instance (or an open editor) append alongside us.
                using var stream = new FileStream(GetFilePath(now), FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch
        {
            // Never let logging break the caller.
        }
    }

    internal string Format(DateTime time, string level, string? message, Exception? ex)
    {
        var text = message ?? "";
        if (ex != null)
            text = text.Length == 0 ? ex.ToString() : $"{text}\n{ex}";

        text = Redact(text);
        if (text.Length > MaxEntryChars)
            text = text[..MaxEntryChars] + " ...[truncated]";

        // Indent continuation lines so every entry starts with a timestamp.
        text = text.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd('\n')
            .Replace("\n", Environment.NewLine + "    ");
        return $"{time:yyyy-MM-dd HH:mm:ss.fff} [{level,-5}] {text}{Environment.NewLine}";
    }

    private string Redact(string text)
    {
        var provider = SecretsProvider;
        if (provider == null || text.Length == 0)
            return text;

        try
        {
            foreach (var secret in provider())
            {
                if (!string.IsNullOrEmpty(secret) && secret.Length >= MinSecretLength)
                    text = text.Replace(secret, Mask, StringComparison.Ordinal);
            }
        }
        catch
        {
            // A failing provider must not stop the entry from being written.
        }
        return text;
    }

    /// <summary>Deletes app-YYYYMMDD.log files older than RetentionDays (today counts as the first day).</summary>
    internal void PruneOldFiles(DateTime now)
    {
        var cutoff = now.Date.AddDays(-(RetentionDays - 1));
        foreach (var file in Directory.EnumerateFiles(LogDirectory, "app-*.log"))
        {
            var match = FileNamePattern.Match(Path.GetFileName(file));
            if (!match.Success || !DateTime.TryParseExact(match.Groups[1].Value, "yyyyMMdd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                continue;

            if (day < cutoff)
            {
                try { File.Delete(file); } catch { /* in use or read-only: try again tomorrow */ }
            }
        }
    }
}

/// <summary>
/// App-wide log: %APPDATA%\VoiceChatbotMini\logs\app-YYYYMMDD.log, 7 days kept.
/// </summary>
public static class AppLog
{
    public static string LogDirectory { get; } = AppPaths.DataPath("logs");

    private static readonly FileLogger Logger = new(LogDirectory);

    /// <summary>Secret values to mask in log entries (see FileLogger.SecretsProvider).</summary>
    public static Func<IEnumerable<string?>>? SecretsProvider
    {
        get => Logger.SecretsProvider;
        set => Logger.SecretsProvider = value;
    }

    /// <summary>Today's log file.</summary>
    public static string CurrentFilePath => Logger.GetFilePath(DateTime.Now);

    public static void Info(string? message) => Logger.Write("INFO", message);
    public static void Warn(string? message, Exception? ex = null) => Logger.Write("WARN", message, ex);
    public static void Error(string? message, Exception? ex = null) => Logger.Write("ERROR", message, ex);
}
