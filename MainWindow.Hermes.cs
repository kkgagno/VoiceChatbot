using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Cv2 = OpenCvSharp.Cv2;
using Mat = OpenCvSharp.Mat;

namespace VoiceChatbot;

public partial class MainWindow
{
    private static bool TryCreateHermesPrompt(string text, out string prompt)
    {
        prompt = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var match = Regex.Match(text.Trim(), @"^(?:hey\s+)?hermes(?:\s*[,:\-;]\s*|\s+)(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            return false;

        prompt = match.Groups[1].Value.Trim();
        return true;
    }

    private static bool IsHermesModelControlCommand(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var normalized = prompt.ToLowerInvariant();
        var hasControlVerb = Regex.IsMatch(normalized, @"\b(start|stop|switch|restart|load|unload|kill|launch|run|change)\b");
        var hasModelTarget = Regex.IsMatch(normalized, @"\b(llama\.?cpp|model|gpu|gpt-oss|120b|70b|30b|13b|12b|7b|comfyui|comfy)\b");
        return hasControlVerb && hasModelTarget;
    }

    private sealed record LlamaModelControlPlan(string Description, string Command, int? EndpointPort, bool RefreshLlamaModels = true);

    private static bool TryBuildLlamaModelControlPlan(string prompt, out LlamaModelControlPlan plan)
    {
        plan = new LlamaModelControlPlan("", "", null);
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var normalized = Regex.Replace(prompt.ToLowerInvariant(), @"[^a-z0-9.]+", " ").Trim();
        if (normalized.Contains("comfyui", StringComparison.Ordinal) || normalized.Contains("comfy", StringComparison.Ordinal))
        {
            var wantsComfyStop = Regex.IsMatch(normalized, @"\b(stop|kill|shutdown|shut down|terminate|unload)\b");
            var comfyBatch = wantsComfyStop
                ? "/mnt/c/llama.cpp/Stop-ComfyUI-LAN.bat"
                : "/mnt/c/llama.cpp/Start-ComfyUI-LAN.bat";
            var action = wantsComfyStop ? "Stopping" : "Starting";
            var comfyLabel = Path.GetFileNameWithoutExtension(comfyBatch).Replace("-", "_", StringComparison.OrdinalIgnoreCase);
            plan = new LlamaModelControlPlan(
                $"{action} ComfyUI using {Path.GetFileName(comfyBatch)}",
                wantsComfyStop
                    ? BuildRunWindowsBatchCommand(comfyBatch, comfyLabel, detach: false)
                    : BuildRunWindowsBatchCommand(comfyBatch, comfyLabel, detach: true),
                null,
                RefreshLlamaModels: false);
            return true;
        }

        var wantsStop = Regex.IsMatch(normalized, @"\b(stop|kill|shutdown|shut down|terminate|unload)\b")
                        && Regex.IsMatch(normalized, @"\b(current|running|llama|llama.cpp|model|server)\b");
        if (wantsStop)
        {
            plan = new LlamaModelControlPlan(
                "Stopping current llama.cpp model processes",
                BuildStopLlamaCommand(),
                null);
            return true;
        }

        var wantsStart = Regex.IsMatch(normalized, @"\b(start|switch|load|launch|run|change)\b");
        if (!wantsStart)
            return false;

        var target = ResolveLlamaBatchFile(normalized);
        if (target is null)
            return false;

        var (batch, port) = target.Value;
        var label = Path.GetFileNameWithoutExtension(batch).Replace("start-", "", StringComparison.OrdinalIgnoreCase);
        plan = new LlamaModelControlPlan(
            $"Starting llama.cpp model using {Path.GetFileName(batch)}",
            BuildStartLlamaBatchCommand(batch, label),
            port);
        return true;
    }

    private static (string BatchPath, int Port)? ResolveLlamaBatchFile(string normalizedPrompt)
    {
        if (Regex.IsMatch(normalizedPrompt, @"\bgpt\s*oss\b") || normalizedPrompt.Contains("gpt oss") || normalizedPrompt.Contains("gpt-oss") || normalizedPrompt.Contains("120b"))
            return ("/mnt/c/llama.cpp/start-gpt-oss-120b.bat", 8084);
        if (normalizedPrompt.Contains("mistral"))
            return normalizedPrompt.Contains("text")
                ? ("/mnt/c/llama.cpp/start-mistral-medium-3.5-textonly.bat", 8082)
                : ("/mnt/c/llama.cpp/start-mistral-medium-3.5.bat", 8082);
        if (normalizedPrompt.Contains("qwen"))
            return ("/mnt/c/llama.cpp/start-qwen3.6-27b-q8.bat", 8081);
        if (normalizedPrompt.Contains("lfm"))
            return ("/mnt/c/llama.cpp/start-lfm2.5-8b.bat", 8083);
        if (normalizedPrompt.Contains("gemma") && normalizedPrompt.Contains("12b"))
            return ("/mnt/c/llama.cpp/start-gemma4-12b.bat", 8083);
        if (normalizedPrompt.Contains("gemma") && (normalizedPrompt.Contains("4b") || normalizedPrompt.Contains("e4b")))
            return ("/mnt/c/llama.cpp/start-gemma4-4b.bat", 8081);
        if (normalizedPrompt.Contains("gemma") && (normalizedPrompt.Contains("spec") || normalizedPrompt.Contains("draft")))
            return ("/mnt/c/llama.cpp/start-gemma4-speculative.bat", 8080);
        if (normalizedPrompt.Contains("gemma") && (normalizedPrompt.Contains("gpu") || normalizedPrompt.Contains("31b gpu")))
            return ("/mnt/c/llama.cpp/start-gemma4-gpu.bat", 8080);
        if (normalizedPrompt.Contains("gemma") && (normalizedPrompt.Contains("26b") || normalizedPrompt.Contains("a4b")))
            return normalizedPrompt.Contains("spec")
                ? ("/mnt/c/llama.cpp/start-gemma4-26b-a4b-speculative.bat", 8080)
                : ("/mnt/c/llama.cpp/start-gemma4-26b-a4b.bat", 8080);
        if (normalizedPrompt.Contains("gemma"))
            return ("/mnt/c/llama.cpp/start-gemma4.bat", 8080);

        return null;
    }

    private static string BuildStopLlamaCommand()
    {
        return "bash -lc " + BashQuote(string.Join("\n", new[]
        {
            "set +e",
            "echo 'Stopping llama.cpp model processes...'",
            "for image in llama-server.exe llama-cli.exe server.exe main.exe; do",
            "  echo \"taskkill $image\"",
            "  timeout 12s /mnt/c/Windows/System32/taskkill.exe /F /T /IM \"$image\" 2>&1 || true",
            "done",
            "echo 'Stop command sent.'",
            "exit 0"
        }));
    }

    private static string BuildStartLlamaBatchCommand(string batchPath, string label)
    {
        return BuildRunWindowsBatchCommand(batchPath, label, detach: true);
    }

    private static string BuildRunWindowsBatchCommand(string batchPath, string label, bool detach)
    {
        var windowsBatchPath = batchPath
            .Replace("/mnt/c/", "C:\\", StringComparison.OrdinalIgnoreCase)
            .Replace("/", "\\");
        var logName = Regex.Replace(label.ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(logName))
            logName = "batch";

        var logPath = $"/tmp/voicechatbot_{logName}.log";
        var runLine = detach
            ? $"nohup /mnt/c/Windows/System32/cmd.exe /c start \"\" \"{windowsBatchPath}\" > {logPath} 2>&1 < /dev/null &"
            : $"timeout 75s /mnt/c/Windows/System32/cmd.exe /c \"{windowsBatchPath}\" > {logPath} 2>&1 || true";

        return "bash -lc " + BashQuote(string.Join("\n", new[]
        {
            "set -e",
            $"batch={BashQuote(batchPath)}",
            "if [ ! -f \"$batch\" ]; then echo \"Batch file not found: $batch\"; exit 2; fi",
            detach ? "echo 'Starting Windows batch file with:'" : "echo 'Running Windows batch file with:'",
            "echo \"$batch\"",
            "cd \"$(dirname \"$batch\")\"",
            runLine,
            (detach ? "echo 'Start command sent. Log: " : "echo 'Batch command finished or timed out. Log: ") + logPath + "'",
            "if [ -f " + BashQuote(logPath) + " ]; then tail -n 40 " + BashQuote(logPath) + " || true; fi",
            "exit 0"
        }));
    }

    private static string BashQuote(string value)
    {
        return "'" + (value ?? "").Replace("'", "'\"'\"'") + "'";
    }

    private async Task<string> RunLlamaModelControlPlanAsync(LlamaModelControlPlan plan, CancellationToken ct)
    {
        using var sshTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sshTimeout.CancelAfter(TimeSpan.FromSeconds(75));
        var result = await _hermesSsh.RunAsync(plan.Command, TimeSpan.FromSeconds(60), sshTimeout.Token);

        var output = new StringBuilder();
        output.AppendLine(plan.Description);
        output.AppendLine();
        if (result.TimedOut)
            output.AppendLine("The SSH command timed out before confirming completion.");
        else if (result.ExitStatus == 0)
            output.AppendLine("Command completed.");
        else
            output.AppendLine($"Command returned exit code {result.ExitStatus}.");

        if (!string.IsNullOrWhiteSpace(result.Stdout))
        {
            output.AppendLine();
            output.AppendLine(result.Stdout.Trim());
        }

        if (!string.IsNullOrWhiteSpace(result.Stderr))
        {
            output.AppendLine();
            output.AppendLine("Error output:");
            output.AppendLine(result.Stderr.Trim());
        }

        return output.ToString().Trim();
    }

    private string BuildLlamaEndpointUrl(int port)
    {
        var host = _settings.HermesSshHost;
        if (string.IsNullOrWhiteSpace(host))
            host = "127.0.0.1";

        try
        {
            if (Uri.TryCreate(_settings.OpenAiCompatibleUrl, UriKind.Absolute, out var uri)
                && !string.IsNullOrWhiteSpace(uri.Host))
            {
                host = uri.Host;
            }
        }
        catch
        {
            // Fall back to SSH host.
        }

        return $"http://{host}:{port}/v1";
    }

    private string SetLlamaEndpointPort(int port)
    {
        var url = BuildLlamaEndpointUrl(port);
        _settings.OpenAiCompatibleUrl = url;
        OpenAiUrlBox.Text = url;
        ConfigureChatClient();
        SettingsManager.Save(_settings);
        AddSystemMessage($"llama.cpp endpoint set to {url}");
        return url;
    }

    private async Task<(bool Ready, string Message)> WaitForLlamaEndpointReadyAsync(string endpoint, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var lastError = "";

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.TrimEnd('/')}/models");
                if (!string.IsNullOrWhiteSpace(_settings.OpenAiCompatibleApiKey))
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_settings.OpenAiCompatibleApiKey}");

                using var response = await http.SendAsync(request, requestTimeout.Token);
                var body = await response.Content.ReadAsStringAsync(requestTimeout.Token);
                if (response.IsSuccessStatusCode)
                    return (true, $"Endpoint is responding: {endpoint}");

                lastError = $"{(int)response.StatusCode}: {body.Trim()}";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        return (false, $"Endpoint set to {endpoint}, but it is not responding yet. Last check: {lastError}");
    }

    private static bool TryGetDirectHermesSshCommand(string prompt, out string command)
    {
        command = "";
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var match = Regex.Match(prompt.Trim(), @"^(?:run|execute|shell|terminal|cli)\s+(.+)$", RegexOptions.IgnoreCase);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
            return false;

        command = match.Groups[1].Value.Trim();
        return true;
    }

    private static string BuildHermesCliPrompt(string prompt)
    {
        if (!IsHermesModelControlCommand(prompt))
            return prompt;

        return string.Join(Environment.NewLine, new[]
        {
            "You are being called from Keith's VoiceChatbot over SSH on the Windows/WSL host.",
            "Handle this as a practical Hermes CLI model-control request.",
            "Do not leave the CLI waiting on a long-running llama.cpp server or batch file in the foreground.",
            "If you start a model or batch file, launch it detached/backgrounded so this command returns text promptly.",
            "You are allowed to run the commands needed to inspect and perform this model-control task.",
            "First check what is currently running if that matters, then respond with what you did.",
            "",
            "User request:",
            prompt
        });
    }

    private static string BuildHermesTimeoutMessage(string prompt)
    {
        if (IsHermesModelControlCommand(prompt))
        {
            return "Hermes did not return from that model-control request fast enough, so I stopped waiting in the app. Nothing was confirmed from Hermes. Use a direct SSH command with 'Hermes run ...' if you want me to stage an exact command for approval.";
        }

        return "Hermes timed out before returning final text. I stopped waiting in the app.";
    }

    private static bool IsHermesApproval(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var normalized = Regex.Replace(prompt.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized is "approve" or "approved" or "yes" or "yep" or "ok" or "okay" or "do it" or "go ahead" or "run it" or "execute" or "confirmed" or "confirm";
    }

    private static bool IsHermesCancel(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var normalized = Regex.Replace(prompt.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return normalized is "cancel" or "no" or "stop" or "never mind" or "nevermind" or "abort";
    }

    private async Task<string> BuildHermesSshApprovalPromptAsync(string request, string command, CancellationToken ct)
    {
        var endpoint = string.IsNullOrWhiteSpace(_settings.OpenAiCompatibleUrl)
            ? "http://localhost:8080/v1"
            : _settings.OpenAiCompatibleUrl.TrimEnd('/');

        var status = await TryInspectLlamaCppEndpointAsync(endpoint, _settings.OpenAiCompatibleApiKey, ct);
        var sb = new StringBuilder();
        sb.AppendLine("Hermes SSH command staged. I have not run anything yet.");
        sb.AppendLine();
        sb.AppendLine("Request:");
        sb.AppendLine(request);
        sb.AppendLine();
        sb.AppendLine("Command to run over SSH:");
        sb.AppendLine(command);
        sb.AppendLine();
        sb.AppendLine("Current llama.cpp endpoint check:");
        sb.AppendLine(status);
        sb.AppendLine();
        sb.AppendLine("Say 'Hermes approve' to let Hermes execute this once, or 'Hermes cancel' to clear it.");
        return sb.ToString().Trim();
    }

    private static async Task<string> TryInspectLlamaCppEndpointAsync(string endpoint, string apiKey, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint}/models");
            if (!string.IsNullOrWhiteSpace(apiKey))
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

            using var response = await http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
                return $"{endpoint}/models returned {(int)response.StatusCode}: {body.Trim()}";

            using var doc = JsonDocument.Parse(body);
            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id))
                        models.Add(id.GetString() ?? "");
                }
            }

            if (models.Count == 0 && doc.RootElement.TryGetProperty("models", out var modelArr) && modelArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in modelArr.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var name))
                        models.Add(name.GetString() ?? "");
                    else if (item.TryGetProperty("model", out var model))
                        models.Add(model.GetString() ?? "");
                }
            }

            return models.Count > 0
                ? $"Reachable at {endpoint}. Reported model(s): {string.Join(", ", models.Where(m => !string.IsNullOrWhiteSpace(m)))}"
                : $"Reachable at {endpoint}, but no model names were reported.";
        }
        catch (Exception ex)
        {
            return $"Could not reach {endpoint}/models: {ex.Message}";
        }
    }

    private async Task SendHermesAgentMessageAsync(string userText, string hermesPrompt)
    {
        _chatCts = new CancellationTokenSource();
        AssistantMessageUi? assistantMessage = null;

        AddUserMessage(userText, new List<string>());
        _history.Add("user", userText);
        assistantMessage = AddAssistantMessage("");

        SetUIState("processing", "Asking Hermes...");
        AddSystemMessage("Running Hermes CLI over SSH.");

        try
        {
            if (IsHermesCancel(hermesPrompt) && !string.IsNullOrWhiteSpace(_pendingHermesControlCommand))
            {
                var cancelled = $"Cancelled pending Hermes action: {_pendingHermesControlCommand}";
                _pendingHermesControlCommand = "";
                _pendingHermesSshCommand = "";
                assistantMessage.Body.Text = cancelled;
                _history.Add("assistant", cancelled);
                SpeakLastResponse(cancelled, assistantMessage);
                return;
            }

            if (IsHermesApproval(hermesPrompt) && !string.IsNullOrWhiteSpace(_pendingHermesSshCommand))
            {
                var command = _pendingHermesSshCommand;
                _pendingHermesControlCommand = "";
                _pendingHermesSshCommand = "";
                AddSystemMessage("Hermes approval received. Running pending SSH command.");
                using var sshTimeout = CancellationTokenSource.CreateLinkedTokenSource(_chatCts.Token);
                sshTimeout.CancelAfter(TimeSpan.FromSeconds(120));
                var result = await _hermesSsh.RunAsync(command, TimeSpan.FromSeconds(90), sshTimeout.Token);
                var display = result.ToDisplayText();
                assistantMessage.Body.Text = display;
                _history.Add("assistant", display);
                SpeakLastResponse(display, assistantMessage);
                await RefreshLlamaCppModelAfterHermesAsync();
                return;
            }
            else if (TryBuildLlamaModelControlPlan(hermesPrompt, out var modelPlan))
            {
                AddSystemMessage($"Running direct llama.cpp model control over SSH: {modelPlan.Description}");
                var display = await RunLlamaModelControlPlanAsync(modelPlan, _chatCts.Token);
                if (modelPlan.EndpointPort is int endpointPort)
                {
                    var endpointUrl = SetLlamaEndpointPort(endpointPort);
                    display += $"{Environment.NewLine}{Environment.NewLine}App endpoint set to {endpointUrl}";
                    var readiness = await WaitForLlamaEndpointReadyAsync(endpointUrl, TimeSpan.FromSeconds(90), _chatCts.Token);
                    display += $"{Environment.NewLine}{readiness.Message}";
                    if (readiness.Ready && _settings.ChatProvider.Equals("OpenAI-compatible", StringComparison.OrdinalIgnoreCase))
                    {
                        AddSystemMessage("Refreshing model list from the new llama.cpp endpoint.");
                        await RefreshModelsInternal();
                    }
                }
                assistantMessage.Body.Text = display;
                _history.Add("assistant", display);
                SpeakLastResponse(display, assistantMessage);
                return;
            }
            else if (TryGetDirectHermesSshCommand(hermesPrompt, out var directCommand))
            {
                _pendingHermesControlCommand = hermesPrompt;
                _pendingHermesSshCommand = directCommand;
                var staged = await BuildHermesSshApprovalPromptAsync(hermesPrompt, directCommand, _chatCts.Token);
                assistantMessage.Body.Text = staged;
                _history.Add("assistant", staged);
                SpeakLastResponse(staged, assistantMessage);
                return;
            }

            var isModelControl = IsHermesModelControlCommand(hermesPrompt);
            var hermesPromptForCli = BuildHermesCliPrompt(hermesPrompt);
            var hermesTimeout = isModelControl ? TimeSpan.FromSeconds(90) : TimeSpan.FromMinutes(3);
            var hermesMaxTurns = isModelControl ? 12 : 12;
            using var hermesCliTimeout = CancellationTokenSource.CreateLinkedTokenSource(_chatCts.Token);
            hermesCliTimeout.CancelAfter(hermesTimeout + TimeSpan.FromSeconds(10));
            var cliResult = await _hermesSsh.RunHermesCliAsync(hermesPromptForCli, hermesTimeout, hermesMaxTurns, hermesCliTimeout.Token);
            var cleaned = CleanDisplayText(cliResult.Stdout);
            if (cliResult.TimedOut)
                cleaned = BuildHermesTimeoutMessage(hermesPrompt);
            if (string.IsNullOrWhiteSpace(cleaned))
                cleaned = cliResult.ToDisplayText();
            assistantMessage.Body.Text = cleaned;
            _history.Add("assistant", $"Hermes result:\n{cleaned}");
            SpeakLastResponse(cleaned, assistantMessage);
            if (isModelControl || IsHermesApproval(hermesPrompt))
                await RefreshLlamaCppModelAfterHermesAsync();
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Body.Text = BuildHermesTimeoutMessage(hermesPrompt);
            SetUIState("idle", "Ready");
        }
        catch (Exception ex)
        {
            assistantMessage.Body.Text = $"Hermes error: {ex.Message}";
            AddSystemMessage($"Hermes error: {ex.Message}");
            SetUIState("idle", "Ready");
        }
    }

    private async Task RefreshLlamaCppModelAfterHermesAsync()
    {
        if (!_settings.ChatProvider.Equals("OpenAI-compatible", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            await Task.Delay(2500);
            AddSystemMessage("Refreshing llama.cpp model list after Hermes command.");
            await RefreshModelsInternal();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not auto-refresh llama.cpp model after Hermes command: {ex.Message}");
        }
    }

    private async Task SendPiAgentMessageAsync(string userText, string piPrompt, string blockedReason)
    {
        _chatCts = new CancellationTokenSource();
        AssistantMessageUi? assistantMessage = null;

        AddUserMessage(userText, new List<string>());
        _history.Add("user", userText);
        assistantMessage = AddAssistantMessage("");

        if (!string.IsNullOrWhiteSpace(blockedReason))
        {
            assistantMessage.Body.Text = blockedReason;
            SpeakLastResponse(blockedReason, assistantMessage);
            return;
        }

        SetUIState("processing", "Asking Pi...");
        AddSystemMessage("Sending read-only request to Pi.");

        try
        {
            var response = await _piAgent.AskAsync(piPrompt, _chatCts.Token);
            var cleaned = CleanDisplayText(response);
            assistantMessage.Body.Text = cleaned;
            _history.Add("assistant", $"Pi result:\n{cleaned}");
            SpeakLastResponse(cleaned, assistantMessage);
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Body.Text += " [cancelled]";
            SetUIState("idle", "Ready");
        }
        catch (Exception ex)
        {
            assistantMessage.Body.Text = $"Pi error: {ex.Message}";
            AddSystemMessage($"Pi error: {ex.Message}");
            SetUIState("idle", "Ready");
        }
    }
}
