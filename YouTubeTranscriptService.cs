using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

public sealed class YouTubeTranscriptService
{
    private const int MaxTranscriptChars = 12000;

    public static bool TryExtractYouTubeUrl(string text, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = Regex.Match(
            text,
            @"https?://(?:www\.)?(?:youtube\.com/watch\?[^\s]+|youtu\.be/[^\s]+|youtube\.com/shorts/[^\s]+)",
            RegexOptions.IgnoreCase);

        if (!match.Success)
            return false;

        url = match.Value.TrimEnd('.', ',', ')', ']', '"', '\'');
        return true;
    }

    public async Task<YouTubeTranscriptResult> FetchTranscriptAsync(string url, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VoiceChatbot", "youtube-transcripts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var errors = new List<string>();
            foreach (var command in GetYtDlpCandidates())
            {
                try
                {
                    var result = await TryFetchWithYtDlpAsync(command, url, tempDir, ct);
                    if (!string.IsNullOrWhiteSpace(result.Transcript))
                        return result;
                    if (!string.IsNullOrWhiteSpace(result.Error))
                        errors.Add($"{command.DisplayName}: {result.Error}");
                }
                catch (Win32Exception ex)
                {
                    errors.Add($"{command.DisplayName}: {ex.Message}");
                }
                catch (FileNotFoundException ex)
                {
                    errors.Add($"{command.DisplayName}: {ex.Message}");
                }
            }

            return new YouTubeTranscriptResult(
                "",
                "",
                "Could not run yt-dlp. Tried: " + string.Join(" | ", errors.Take(8)));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    public async Task<YouTubeTranscriptResult> FetchAudioTranscriptAsync(
        string url,
        Func<Stream, CancellationToken, Task<string>> transcribeAsync,
        CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "VoiceChatbot", "youtube-audio", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var errors = new List<string>();
            foreach (var command in GetYtDlpCandidates())
            {
                try
                {
                    var result = await TryDownloadAudioWithYtDlpAsync(command, url, tempDir, ct);
                    if (string.IsNullOrWhiteSpace(result.AudioPath))
                    {
                        if (!string.IsNullOrWhiteSpace(result.Error))
                            errors.Add($"{command.DisplayName}: {result.Error}");
                        continue;
                    }

                    await using var stream = File.OpenRead(result.AudioPath);
                    var transcript = await transcribeAsync(stream, ct);
                    if (!string.IsNullOrWhiteSpace(transcript))
                        return new YouTubeTranscriptResult(result.Title, LimitTranscript(transcript), "");

                    errors.Add($"{command.DisplayName}: local Whisper returned an empty transcript.");
                }
                catch (Win32Exception ex)
                {
                    errors.Add($"{command.DisplayName}: {ex.Message}");
                }
                catch (FileNotFoundException ex)
                {
                    errors.Add($"{command.DisplayName}: {ex.Message}");
                }
                catch (InvalidOperationException ex)
                {
                    errors.Add($"{command.DisplayName}: {ex.Message}");
                    break;
                }
            }

            return new YouTubeTranscriptResult(
                "",
                "",
                "Could not transcribe YouTube audio. Tried: " + string.Join(" | ", errors.Take(8)));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    private static async Task<YouTubeTranscriptResult> TryFetchWithYtDlpAsync(
        YtDlpCommand command,
        string url,
        string tempDir,
        CancellationToken ct)
    {
        var outputTemplate = Path.Combine(tempDir, "transcript.%(ext)s");
        var start = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = tempDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        AddKnownToolDirectoriesToPath(start);

        foreach (var argument in command.PrefixArguments)
            start.ArgumentList.Add(argument);
        var denoPath = GetDenoPath();
        if (!string.IsNullOrWhiteSpace(denoPath))
        {
            start.ArgumentList.Add("--js-runtimes");
            start.ArgumentList.Add($"deno:{denoPath}");
        }
        start.ArgumentList.Add("--skip-download");
        start.ArgumentList.Add("--write-subs");
        start.ArgumentList.Add("--write-auto-subs");
        start.ArgumentList.Add("--sub-langs");
        start.ArgumentList.Add("en-orig,en");
        start.ArgumentList.Add("--sub-format");
        start.ArgumentList.Add("vtt");
        start.ArgumentList.Add("--print");
        start.ArgumentList.Add("title");
        start.ArgumentList.Add("--no-simulate");
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(outputTemplate);
        start.ArgumentList.Add(url);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        var vttFile = Directory.GetFiles(tempDir, "*.vtt", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (vttFile == null)
        {
            var error = stderr.ToString().Trim();
            return new YouTubeTranscriptResult(
                GetTitle(stdout.ToString()),
                "",
                string.IsNullOrWhiteSpace(error)
                    ? "yt-dlp did not find English captions for this video."
                    : error);
        }

        var transcript = ParseVtt(await File.ReadAllTextAsync(vttFile, ct));
        return new YouTubeTranscriptResult(GetTitle(stdout.ToString()), transcript, "");
    }

    private static async Task<YouTubeAudioDownloadResult> TryDownloadAudioWithYtDlpAsync(
        YtDlpCommand command,
        string url,
        string tempDir,
        CancellationToken ct)
    {
        var outputTemplate = Path.Combine(tempDir, "audio.%(ext)s");
        var start = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = tempDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        AddKnownToolDirectoriesToPath(start);

        foreach (var argument in command.PrefixArguments)
            start.ArgumentList.Add(argument);
        var denoPath = GetDenoPath();
        if (!string.IsNullOrWhiteSpace(denoPath))
        {
            start.ArgumentList.Add("--js-runtimes");
            start.ArgumentList.Add($"deno:{denoPath}");
        }
        start.ArgumentList.Add("--no-playlist");
        start.ArgumentList.Add("--extract-audio");
        start.ArgumentList.Add("--audio-format");
        start.ArgumentList.Add("wav");
        start.ArgumentList.Add("--postprocessor-args");
        start.ArgumentList.Add("ffmpeg:-ar 16000 -ac 1");
        start.ArgumentList.Add("--print");
        start.ArgumentList.Add("title");
        start.ArgumentList.Add("--no-simulate");
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(outputTemplate);
        start.ArgumentList.Add(url);

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stderr.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct);

        var wavFile = Directory.GetFiles(tempDir, "*.wav", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        if (wavFile != null)
            return new YouTubeAudioDownloadResult(GetTitle(stdout.ToString()), wavFile, "");

        var error = stderr.ToString().Trim();
        var producedFiles = Directory.GetFiles(tempDir, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(tempDir, path))
            .Take(10)
            .ToList();
        var noWavMessage = producedFiles.Count == 0
            ? "yt-dlp did not produce a WAV audio file."
            : "yt-dlp did not produce a WAV audio file. Produced: " + string.Join(", ", producedFiles);
        return new YouTubeAudioDownloadResult(
            GetTitle(stdout.ToString()),
            "",
            string.IsNullOrWhiteSpace(error)
                ? noWavMessage
                : error);
    }

    private static string ParseVtt(string vtt)
    {
        var lines = vtt.Replace("\r\n", "\n").Split('\n');
        var result = new List<string>();
        var previous = "";

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;
            if (line.Equals("WEBVTT", StringComparison.OrdinalIgnoreCase))
                continue;
            if (line.StartsWith("Kind:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Language:", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("NOTE", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("STYLE", StringComparison.OrdinalIgnoreCase))
                continue;
            if (Regex.IsMatch(line, @"^\d+$") || line.Contains("-->"))
                continue;

            line = Regex.Replace(line, "<[^>]+>", "");
            line = WebUtility.HtmlDecode(line);
            line = Regex.Replace(line, @"\s+", " ").Trim();
            if (string.IsNullOrWhiteSpace(line) || string.Equals(line, previous, StringComparison.Ordinal))
                continue;

            result.Add(line);
            previous = line;
        }

        return LimitTranscript(string.Join(" ", result));
    }

    private static string LimitTranscript(string transcript)
    {
        transcript = Regex.Replace(transcript, @"\s+", " ").Trim();
        if (transcript.Length <= MaxTranscriptChars)
            return transcript;

        return transcript[..MaxTranscriptChars] + "\n\n[Transcript truncated to fit chat context.]";
    }

    private static string GetTitle(string stdout)
    {
        return stdout.Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? "";
    }

    private static IEnumerable<YtDlpCommand> GetYtDlpCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var installedPath = Path.Combine(
                localAppData,
                "Microsoft",
                "WinGet",
                "Packages",
                "yt-dlp.yt-dlp_Microsoft.Winget.Source_8wekyb3d8bbwe",
                "yt-dlp.exe");
            var userProfileInstalledPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "Local",
                "Microsoft",
                "WinGet",
                "Packages",
                "yt-dlp.yt-dlp_Microsoft.Winget.Source_8wekyb3d8bbwe",
                "yt-dlp.exe");

            yield return new YtDlpCommand(installedPath);
            yield return new YtDlpCommand("cmd.exe", ["/d", "/c", installedPath]);
            yield return new YtDlpCommand(userProfileInstalledPath);
            yield return new YtDlpCommand("cmd.exe", ["/d", "/c", userProfileInstalledPath]);
            yield return new YtDlpCommand(Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "yt-dlp.exe"));

            var wingetPackages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(wingetPackages))
            {
                foreach (var packageDir in SafeEnumerateDirectories(wingetPackages, "yt-dlp.yt-dlp_*"))
                {
                    var path = Path.Combine(packageDir, "yt-dlp.exe");
                    if (File.Exists(path))
                    {
                        yield return new YtDlpCommand(path);
                        yield return new YtDlpCommand("cmd.exe", ["/d", "/c", path]);
                    }
                }
            }

            yield return new YtDlpCommand("yt-dlp.exe");
        }

        yield return new YtDlpCommand("yt-dlp");
    }

    private static void AddKnownToolDirectoriesToPath(ProcessStartInfo start)
    {
        var extraPaths = new List<string>();
        var denoPath = GetDenoPath();
        if (!string.IsNullOrWhiteSpace(denoPath))
            extraPaths.Add(Path.GetDirectoryName(denoPath)!);

        var ffmpegPath = GetFfmpegPath();
        if (!string.IsNullOrWhiteSpace(ffmpegPath))
            extraPaths.Add(Path.GetDirectoryName(ffmpegPath)!);

        if (extraPaths.Count == 0)
            return;

        var currentPath = start.Environment.TryGetValue("PATH", out var path)
            ? path
            : Environment.GetEnvironmentVariable("PATH") ?? "";
        start.Environment["PATH"] = string.Join(Path.PathSeparator, extraPaths.Distinct(StringComparer.OrdinalIgnoreCase)) +
                                    Path.PathSeparator +
                                    currentPath;
    }

    private static string GetDenoPath()
    {
        if (!OperatingSystem.IsWindows())
            return "";

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var exact = Path.Combine(
            localAppData,
            "Microsoft",
            "WinGet",
            "Packages",
            "DenoLand.Deno_Microsoft.Winget.Source_8wekyb3d8bbwe",
            "deno.exe");
        if (File.Exists(exact))
            return exact;

        var packageRoot = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
        return FindFirstFile(packageRoot, "deno.exe");
    }

    private static string GetFfmpegPath()
    {
        if (!OperatingSystem.IsWindows())
            return "";

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var packageRoot = Path.Combine(
            localAppData,
            "Microsoft",
            "WinGet",
            "Packages",
            "yt-dlp.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe");
        return FindFirstFile(packageRoot, "ffmpeg.exe");
    }

    private static string FindFirstFile(string root, string pattern)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.GetFiles(root, pattern, SearchOption.AllDirectories).FirstOrDefault() ?? ""
                : "";
        }
        catch
        {
            return "";
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path, string pattern)
    {
        try
        {
            return Directory.EnumerateDirectories(path, pattern, SearchOption.TopDirectoryOnly).ToList();
        }
        catch
        {
            return [];
        }
    }

    private sealed class YtDlpCommand(string fileName, string[]? prefixArguments = null)
    {
        public string FileName { get; } = fileName;
        public string[] PrefixArguments { get; } = prefixArguments ?? [];
        public string DisplayName => PrefixArguments.Length == 0
            ? FileName
            : $"{FileName} {string.Join(" ", PrefixArguments)}";
    }
}

public sealed record YouTubeTranscriptResult(string Title, string Transcript, string Error);
public sealed record YouTubeAudioDownloadResult(string Title, string AudioPath, string Error);
