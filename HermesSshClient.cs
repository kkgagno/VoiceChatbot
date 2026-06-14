using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;

namespace VoiceChatbot;

public sealed class HermesSshClient
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 2222;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    private string _sessionId = "";
    private readonly SemaphoreSlim _terminalLock = new(1, 1);
    private SshClient? _terminalClient;
    private ShellStream? _terminalStream;

    public Task<HermesSshResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(Host))
                throw new InvalidOperationException("Hermes SSH host is not set.");
            if (string.IsNullOrWhiteSpace(User))
                throw new InvalidOperationException("Hermes SSH user is not set.");
            if (string.IsNullOrWhiteSpace(Password))
                throw new InvalidOperationException("Hermes SSH password is not set.");
            if (string.IsNullOrWhiteSpace(command))
                throw new InvalidOperationException("No SSH command was provided.");

            ct.ThrowIfCancellationRequested();

            var connection = new PasswordConnectionInfo(Host, Port, User, Password)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };

            using var client = new SshClient(connection);
            client.Connect();
            try
            {
                using var cmd = client.CreateCommand(command);
                var asyncResult = cmd.BeginExecute();
                if (!asyncResult.AsyncWaitHandle.WaitOne(timeout))
                {
                    try { cmd.CancelAsync(); } catch { }
                    return new HermesSshResult(command, -1, cmd.Result ?? "", cmd.Error ?? "", true);
                }

                var stdout = cmd.EndExecute(asyncResult) ?? "";
                return new HermesSshResult(command, cmd.ExitStatus ?? -1, stdout, cmd.Error ?? "", false);
            }
            finally
            {
                client.Disconnect();
            }
        }, ct);
    }

    public async Task<HermesSshResult> RunHermesCliAsync(string prompt, TimeSpan timeout, int maxTurns = 12, CancellationToken ct = default)
    {
        await _terminalLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var stream = await EnsureHermesTerminalAsync(ct).ConfigureAwait(false);
            DrainShell(stream, TimeSpan.FromMilliseconds(250), ct);

            stream.Write(NormalizeInteractivePrompt(prompt));
            stream.Write("\r");

            var result = ReadHermesInteractiveResult(stream, prompt, timeout, ct);
            var sessionId = TryParseSessionId(result.Stdout + "\n" + result.Stderr);
            if (!string.IsNullOrWhiteSpace(sessionId))
                _sessionId = sessionId;

            result = result with { Stdout = CleanHermesInteractiveOutput(result.Stdout, prompt) };
            if (result.TimedOut)
                ResetHermesTerminal();

            return result;
        }
        finally
        {
            _terminalLock.Release();
        }
    }

    private Task<ShellStream> EnsureHermesTerminalAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            if (_terminalClient?.IsConnected == true && _terminalStream is { CanWrite: true })
                return _terminalStream;

            ResetHermesTerminal();

            if (string.IsNullOrWhiteSpace(Host))
                throw new InvalidOperationException("Hermes SSH host is not set.");
            if (string.IsNullOrWhiteSpace(User))
                throw new InvalidOperationException("Hermes SSH user is not set.");
            if (string.IsNullOrWhiteSpace(Password))
                throw new InvalidOperationException("Hermes SSH password is not set.");

            var connection = new PasswordConnectionInfo(Host, Port, User, Password)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };

            _terminalClient = new SshClient(connection);
            _terminalClient.Connect();
            _terminalStream = _terminalClient.CreateShellStream("xterm", 120, 40, 1200, 800, 65536);
            DrainShell(_terminalStream, TimeSpan.FromMilliseconds(500), ct);
            _terminalStream.Write("TERM=xterm NO_COLOR=1 ~/.local/bin/hermes chat --accept-hooks --yolo --source tool");
            _terminalStream.Write("\r");
            var startup = ReadUntilHermesPrompt(_terminalStream, TimeSpan.FromSeconds(45), ct);
            var sessionId = TryParseSessionId(startup);
            if (!string.IsNullOrWhiteSpace(sessionId))
                _sessionId = sessionId;

            return _terminalStream;
        }, ct);
    }

    private void ResetHermesTerminal()
    {
        try { _terminalStream?.Dispose(); } catch { }
        try
        {
            if (_terminalClient?.IsConnected == true)
                _terminalClient.Disconnect();
        }
        catch { }
        try { _terminalClient?.Dispose(); } catch { }
        _terminalStream = null;
        _terminalClient = null;
    }

    private static HermesSshResult ReadHermesInteractiveResult(ShellStream stream, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        var text = ReadUntilHermesPrompt(stream, timeout, ct, requireResponse: true);
        var timedOut = !LooksReadyForNextHermesPrompt(StripAnsi(text));
        return new HermesSshResult("hermes interactive terminal", timedOut ? -1 : 0, text, "", timedOut);
    }

    private static string ReadUntilHermesPrompt(ShellStream stream, TimeSpan timeout, CancellationToken ct, bool requireResponse = false)
    {
        var output = new StringBuilder();
        var started = DateTime.UtcNow;
        var lastData = DateTime.UtcNow;
        var sawAny = false;

        while (DateTime.UtcNow - started < timeout)
        {
            ct.ThrowIfCancellationRequested();
            if (stream.DataAvailable)
            {
                var chunk = stream.Read();
                if (!string.IsNullOrEmpty(chunk))
                {
                    output.Append(chunk);
                    sawAny = true;
                    lastData = DateTime.UtcNow;
                }
            }
            else
            {
                var plain = StripAnsi(output.ToString());
                if (LooksReadyForNextHermesPrompt(plain) && (!requireResponse || DateTime.UtcNow - started > TimeSpan.FromSeconds(2)))
                    break;
                if (sawAny && DateTime.UtcNow - lastData > TimeSpan.FromSeconds(5) && DateTime.UtcNow - started > TimeSpan.FromSeconds(8))
                    break;
                Thread.Sleep(100);
            }
        }

        return output.ToString();
    }

    private static bool LooksReadyForNextHermesPrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var normalized = text.Replace("\r", "\n");
        var promptIndex = normalized.LastIndexOf("❯", StringComparison.Ordinal);
        if (promptIndex < 0)
            return false;

        var tail = normalized[promptIndex..];
        return !tail.Contains("msg=interrupt", StringComparison.OrdinalIgnoreCase)
               && !tail.Contains("Ctrl+C cancel", StringComparison.OrdinalIgnoreCase)
               && !tail.Contains("formulating", StringComparison.OrdinalIgnoreCase)
               && !tail.Contains("Initializing agent", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeInteractivePrompt(string prompt)
    {
        prompt ??= "";
        return Regex.Replace(prompt.Replace("\r", " ").Replace("\n", " "), @"\s+", " ").Trim();
    }

    private Task<HermesSshResult> RunTerminalAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(Host))
                throw new InvalidOperationException("Hermes SSH host is not set.");
            if (string.IsNullOrWhiteSpace(User))
                throw new InvalidOperationException("Hermes SSH user is not set.");
            if (string.IsNullOrWhiteSpace(Password))
                throw new InvalidOperationException("Hermes SSH password is not set.");
            if (string.IsNullOrWhiteSpace(command))
                throw new InvalidOperationException("No SSH command was provided.");

            ct.ThrowIfCancellationRequested();

            var marker = "__VOICECHATBOT_HERMES_DONE_" + Guid.NewGuid().ToString("N");
            var connection = new PasswordConnectionInfo(Host, Port, User, Password)
            {
                Timeout = TimeSpan.FromSeconds(12)
            };

            using var client = new SshClient(connection);
            client.Connect();
            try
            {
                using var stream = client.CreateShellStream("xterm", 120, 40, 1200, 800, 8192);
                DrainShell(stream, TimeSpan.FromMilliseconds(500), ct);

                stream.WriteLine(command);
                stream.WriteLine($"printf '\\n{marker}:%s\\n' \"$?\"");
                stream.WriteLine("exit");

                var output = new StringBuilder();
                var started = DateTime.UtcNow;
                while (DateTime.UtcNow - started < timeout)
                {
                    ct.ThrowIfCancellationRequested();
                    if (stream.DataAvailable)
                    {
                        output.Append(stream.Read());
                        if (output.ToString().Contains(marker, StringComparison.Ordinal))
                            break;
                    }
                    else
                    {
                        Thread.Sleep(100);
                    }
                }

                var text = output.ToString();
                var timedOut = !text.Contains(marker, StringComparison.Ordinal);
                var exitStatus = TryParseTerminalExitStatus(text, marker);
                text = CleanTerminalOutput(text, command, marker);
                return new HermesSshResult(command, exitStatus, text, "", timedOut);
            }
            finally
            {
                client.Disconnect();
            }
        }, ct);
    }

    private static void DrainShell(ShellStream stream, TimeSpan duration, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (DateTime.UtcNow - started < duration)
        {
            ct.ThrowIfCancellationRequested();
            if (stream.DataAvailable)
                _ = stream.Read();
            else
                Thread.Sleep(50);
        }
    }

    private static int TryParseTerminalExitStatus(string text, string marker)
    {
        var match = Regex.Match(text ?? "", Regex.Escape(marker) + @":(\d+)");
        return match.Success && int.TryParse(match.Groups[1].Value, out var status) ? status : -1;
    }

    private static string CleanTerminalOutput(string text, string command, string marker)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        text = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        text = Regex.Replace(text, Regex.Escape(marker) + @":\d+\s*", "");
        text = text.Replace(command, "");
        text = Regex.Replace(text, @"(?m)^\s*printf '\\n__VOICECHATBOT_HERMES_DONE_[^']+' ""\$\?""\s*$", "");
        text = Regex.Replace(text, @"(?m)^\s*exit\s*$", "");
        text = Regex.Replace(text, @"(?m)^\s*keith@[^$#]*[$#]\s*", "");
        return text.Trim();
    }

    private static string CleanHermesInteractiveOutput(string text, string prompt)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        text = StripAnsi(text).Replace("\r", "\n");
        text = text.Replace(prompt ?? "", "");
        text = Regex.Replace(text, @"(?im)^\s*session_id:\s*\S+\s*$", "");
        text = Regex.Replace(text, @"(?im)^\s*Session:\s*\S+\s*$", "");
        text = Regex.Replace(text, @"(?im)^.*(ctx|kimi-k2\.6|msg=interrupt|Ctrl\+C cancel|formulating|Initializing agent|Welcome to Hermes Agent|Available Tools|Available Skills|commits behind|Resume this session|Duration:|Messages:).*$", "");
        text = Regex.Replace(text, @"(?m)^[\s╭╮╰╯─│⚕❯░╎╏┌┐└┘├┤┬┴┼]+$", "");
        text = Regex.Replace(text, @"\n{3,}", "\n\n");
        return text.Trim();
    }

    private static string StripAnsi(string text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        text = Regex.Replace(text, @"\x1B\][^\a]*(?:\a|\x1B\\)", "");
        text = Regex.Replace(text, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
        text = Regex.Replace(text, @"\x1B[@-Z\\-_]", "");
        return text;
    }

    private static string BuildHermesChatCommand(string prompt, string sessionId, int maxTurns)
    {
        maxTurns = Math.Clamp(maxTurns, 1, 20);
        var continueArg = string.IsNullOrWhiteSpace(sessionId)
            ? ""
            : " --continue " + BashQuote(sessionId);
        return "~/.local/bin/hermes chat -Q --accept-hooks --yolo --source tool" + continueArg + " --max-turns " + maxTurns + " -q " + BashQuote(prompt);
    }

    private static bool IsMissingSession(HermesSshResult result)
    {
        var text = (result.Stdout ?? "") + "\n" + (result.Stderr ?? "");
        return text.Contains("No session found", StringComparison.OrdinalIgnoreCase);
    }

    private static string TryParseSessionId(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var match = Regex.Match(text, @"session_id:\s*(\S+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static string StripSessionId(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        return Regex.Replace(text, @"(?im)^\s*session_id:\s*\S+\s*$\r?\n?", "").Trim();
    }

    private static string BashQuote(string value)
    {
        return "'" + (value ?? "").Replace("'", "'\"'\"'") + "'";
    }
}

public sealed record HermesSshResult(string Command, int ExitStatus, string Stdout, string Stderr, bool TimedOut)
{
    public string ToDisplayText()
    {
        var status = TimedOut ? "timed out" : $"exit {ExitStatus}";
        var stdout = string.IsNullOrWhiteSpace(Stdout) ? "(no stdout)" : Stdout.Trim();
        var stderr = string.IsNullOrWhiteSpace(Stderr) ? "" : $"\n\nSTDERR:\n{Stderr.Trim()}";
        return $"Hermes SSH command completed ({status}).\n\nCOMMAND:\n{Command}\n\nSTDOUT:\n{stdout}{stderr}";
    }
}
