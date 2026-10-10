using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceChatbot;

/// <summary>
/// The desktop Live Transcriber's session as it is kept between windows and app runs
/// (%APPDATA%\VoiceChatbotMini\transcripts\current-session.json), so closing the window loses nothing.
/// </summary>
public sealed class TranscriberSessionState
{
    public int Version { get; set; } = 1;
    public string Transcript { get; set; } = "";
    public string Notes { get; set; } = "";
    /// <summary>The style the notes were written in.</summary>
    public string NotesStyle { get; set; } = TranscriptSummaryStyles.Summary;
    /// <summary>Local time of the last live-notes update shown, if any.</summary>
    public DateTime? NotesUpdatedAt { get; set; }
    /// <summary>Recording time so far; timestamps continue from here when recording starts again.</summary>
    public long ElapsedMs { get; set; }
    /// <summary>How much of the transcript the notes cover (<see cref="LiveNotesPolicy.ProcessedTranscript"/> length).</summary>
    public int ProcessedLength { get; set; }
    /// <summary>Local time the session started (its saved file is named after it).</summary>
    public DateTime? SessionStarted { get; set; }
    /// <summary>The session's file in the transcripts folder, e.g. transcript_20261008_143005.md.</summary>
    public string? AutoSaveFileName { get; set; }
    /// <summary>True when the session's file in the transcripts folder is older than this state.</summary>
    public bool UnsavedChanges { get; set; }
    /// <summary>Local time this state was written.</summary>
    public DateTime SavedAt { get; set; }

    [JsonIgnore]
    public TimeSpan Elapsed => ElapsedMs > 0 ? TimeSpan.FromMilliseconds(ElapsedMs) : TimeSpan.Zero;

    [JsonIgnore]
    public bool IsEmpty => string.IsNullOrWhiteSpace(Transcript) && string.IsNullOrWhiteSpace(Notes);
}

/// <summary>Reads and writes <see cref="TranscriberSessionState"/>: atomically, and never trusting the file's contents.</summary>
public static class TranscriberSessionStore
{
    public const string FileName = "current-session.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>The state file in <paramref name="transcriptsFolder"/>.</summary>
    public static string PathIn(string transcriptsFolder) => Path.Combine(transcriptsFolder, FileName);

    /// <summary>Writes the state to a temporary file next to <paramref name="path"/>, then replaces the old file with it.</summary>
    public static void Save(string path, TranscriberSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, JsonOptions);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder))
            Directory.CreateDirectory(folder);

        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(path))
            {
                try
                {
                    File.Replace(temp, path, null, ignoreMetadataErrors: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                {
                    // Replace can fail on some file systems; a rename over the old file is still atomic.
                    File.Move(temp, path, overwrite: true);
                }
            }
            else
            {
                File.Move(temp, path, overwrite: true);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                    File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover .tmp file is harmless.
            }
        }
    }

    /// <summary>
    /// The saved state, or null when there is none, it is empty, or it cannot be read (<paramref name="error"/> says
    /// why; a missing file is not an error). Values are made safe: a file name that is not a plain transcript file
    /// name is dropped, negative numbers become zero and the style is a known one.
    /// </summary>
    public static TranscriberSessionState? Load(string path, out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(path))
                return null;

            var state = JsonSerializer.Deserialize<TranscriberSessionState>(File.ReadAllBytes(path), JsonOptions);
            if (state == null)
            {
                error = "the file is empty";
                return null;
            }

            state.Transcript ??= "";
            state.Notes ??= "";
            state.NotesStyle = TranscriptSummaryStyles.Normalize(state.NotesStyle);
            state.ElapsedMs = Math.Max(0, state.ElapsedMs);
            state.ProcessedLength = Math.Clamp(state.ProcessedLength, 0, state.Transcript.Length);
            if (!IsSafeFileName(state.AutoSaveFileName))
                state.AutoSaveFileName = null;
            return state.IsEmpty ? null : state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Deletes the state file; false (and <paramref name="error"/>) when that failed.</summary>
    public static bool Delete(string path, out string? error)
    {
        error = null;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>True for a plain "name.md" or "name.txt" in the transcripts folder (letters, digits, '_', '-', '.').</summary>
    public static bool IsSafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.StartsWith('.'))
            return false;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
                return false;
        }
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
    }
}
