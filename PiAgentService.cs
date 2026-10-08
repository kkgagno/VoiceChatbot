using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

public sealed class PiAgentService
{
    private readonly string _workingDirectory;
    private readonly TimeSpan _timeout;

    public PiAgentService(string workingDirectory, TimeSpan? timeout = null)
    {
        _workingDirectory = workingDirectory;
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
    }

    // Only an explicit address ("ask pi ...", "pi agent, ...") routes to Pi; see PiRequestParser.
    public static bool TryCreateReadOnlyPrompt(string userText, out string prompt, out string blockedReason) =>
        PiRequestParser.TryCreateReadOnlyPrompt(userText, out prompt, out blockedReason);

    public async Task<string> AskAsync(string prompt, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var command in GetPiCommandCandidates())
        {
            try
            {
                return await RunPiAsync(command, prompt, ct);
            }
            catch (Win32Exception ex)
            {
                errors.Add($"{command}: {ex.Message}");
            }
            catch (FileNotFoundException ex)
            {
                errors.Add($"{command}: {ex.Message}");
            }
        }

        return "Pi was not found on PATH. Install it, then restart this app. Example: npm install -g @earendil-works/pi-coding-agent";
    }

    private async Task<string> RunPiAsync(PiCommand command, string prompt, CancellationToken externalCt)
    {
        using var timeoutCts = new CancellationTokenSource(_timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(externalCt, timeoutCts.Token);

        var start = new ProcessStartInfo
        {
            FileName = command.FileName,
            WorkingDirectory = Directory.Exists(_workingDirectory)
                ? _workingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in command.PrefixArguments)
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--provider");
        start.ArgumentList.Add("llama-cpp");
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add("local-llama");
        start.ArgumentList.Add("--tools");
        start.ArgumentList.Add("read,grep,find,ls");
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(prompt);

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

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            TryKill(process);
            return "Pi timed out before it finished. Try a narrower file or directory question.";
        }
        catch
        {
            TryKill(process);
            throw;
        }

        var output = stdout.ToString().Trim();
        var error = stderr.ToString().Trim();

        if (process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
            return output;

        if (!string.IsNullOrWhiteSpace(error))
            return $"Pi exited with code {process.ExitCode}: {error}";

        return process.ExitCode == 0
            ? "Pi finished without returning text."
            : $"Pi exited with code {process.ExitCode}.";
    }

    private static IEnumerable<PiCommand> GetPiCommandCandidates()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var piCli = Path.Combine(appData, "npm", "node_modules", "@earendil-works", "pi-coding-agent", "dist", "cli.js");
        var nodeExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");

        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(nodeExe) && File.Exists(piCli))
                yield return new PiCommand(nodeExe, [piCli]);

            yield return new PiCommand(Path.Combine(appData, "npm", "pi.cmd"));
            yield return new PiCommand(Path.Combine(userProfile, "AppData", "Roaming", "npm", "pi.cmd"));
            yield return new PiCommand("pi.cmd");
            yield return new PiCommand("pi.exe");
        }

        yield return new PiCommand("pi");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort cleanup only.
        }
    }

    private sealed class PiCommand(string fileName, string[]? prefixArguments = null)
    {
        public string FileName { get; } = fileName;
        public string[] PrefixArguments { get; } = prefixArguments ?? [];
    }
}
