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

    // Core/HermesCommands.cs decides what counts as a model-control command: it must start with a
    // control verb and name only models, so a question that mentions ComfyUI or a model goes to the
    // Hermes agent and "run <shell command>" is staged for approval instead of starting a model.
    private static bool TryBuildLlamaModelControlPlan(string prompt, out LlamaModelControlPlan plan)
    {
        plan = new LlamaModelControlPlan("", "", null);
        if (!HermesCommandParser.TryMatchModelControl(prompt, out var target))
            return false;

        switch (target.Action)
        {
            case HermesModelControlAction.StartComfyUi:
            case HermesModelControlAction.StopComfyUi:
            {
                var wantsComfyStop = target.Action == HermesModelControlAction.StopComfyUi;
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

            case HermesModelControlAction.StopLlama:
                plan = new LlamaModelControlPlan(
                    "Stopping current llama.cpp model processes",
                    BuildStopLlamaCommand(),
                    null);
                return true;

            default:
            {
                var batch = target.BatchPath;
                var label = Path.GetFileNameWithoutExtension(batch).Replace("start-", "", StringComparison.OrdinalIgnoreCase);
                plan = new LlamaModelControlPlan(
                    $"Starting llama.cpp model using {Path.GetFileName(batch)}",
                    BuildStartLlamaBatchCommand(batch, label),
                    target.Port);
                return true;
            }
        }
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

    private static bool TryGetDirectHermesSshCommand(string prompt, out string command) =>
        HermesCommandParser.TryGetDirectSshCommand(prompt, out command);

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

    // Set for the whole async flow of a phone chat request (it survives awaits, even when a
    // continuation resumes on the UI thread), so Hermes approvals know which device they came from.
    private static readonly AsyncLocal<bool> ServingPhoneRequest = new();

    private static HermesCommandSource CurrentHermesSource =>
        ServingPhoneRequest.Value ? HermesCommandSource.Phone : HermesCommandSource.Desktop;

    // The phone remote's chat callback (see the constructor): marks the request as coming from the phone.
    private async Task<PhoneRemoteAssistantResult> HandlePhoneRemoteChatFromPhoneAsync(PhoneRemoteUserInput input, CancellationToken ct)
    {
        ServingPhoneRequest.Value = true;
        return await HandlePhoneRemoteChatAsync(input, ct);
    }

    // MainWindow.PhoneRemote.cs reads and writes these two names directly. They are views of
    // _hermesApprovals, so the phone path gets the same two-minute expiry and per-device rules.
    private string _pendingHermesControlCommand
    {
        get => _hermesApprovals.StagedRequest;
        set
        {
            _pendingHermesRequestDraft = value ?? "";
            if (string.IsNullOrWhiteSpace(value))
                _hermesApprovals.Clear();
        }
    }

    private string _pendingHermesSshCommand
    {
        get => _hermesApprovals.GetLiveCommand(DateTime.UtcNow);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
                _hermesApprovals.Clear();
            else
                _hermesApprovals.Stage(_pendingHermesRequestDraft, value, CurrentHermesSource, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// True when this prompt may run the staged SSH command now: it has not expired, and a casual
    /// "ok/yes" only counts from the device that staged it ("approve" works from either).
    /// </summary>
    private bool IsHermesApproval(string prompt) =>
        _hermesApprovals.Evaluate(prompt, CurrentHermesSource, DateTime.UtcNow) == HermesApprovalDecision.Approved;

    private static bool IsHermesCancel(string prompt) => HermesApprovalGate.IsCancelWord(prompt);

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
        sb.AppendLine($"Say 'Hermes approve' within {HermesApprovalGate.Lifetime.TotalMinutes:0} minutes to let Hermes execute this once, or 'Hermes cancel' to clear it.");
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
            if (IsHermesCancel(hermesPrompt) && _hermesApprovals.TryCancel(out var cancelledRequest))
            {
                var cancelled = $"Cancelled pending Hermes action: {cancelledRequest}";
                assistantMessage.Body.Text = cancelled;
                _history.Add("assistant", cancelled);
                SpeakLastResponse(cancelled, assistantMessage);
                return;
            }

            var stagedCommand = _hermesApprovals.StagedCommand;
            var approval = _hermesApprovals.TryApprove(hermesPrompt, HermesCommandSource.Desktop, DateTime.UtcNow, out var command);
            if (approval is HermesApprovalDecision.Expired or HermesApprovalDecision.NeedsExplicitApproval)
            {
                var notice = approval == HermesApprovalDecision.Expired
                    ? $"The staged Hermes SSH command expired after {HermesApprovalGate.Lifetime.TotalMinutes:0} minutes, so nothing was run. Stage it again with 'Hermes run ...'.\n\nCommand: {stagedCommand}"
                    : $"That Hermes SSH command was staged from the iPhone, so it needs an explicit approval here. Say 'Hermes approve' to run it, or 'Hermes cancel' to clear it.\n\nCommand: {stagedCommand}";
                assistantMessage.Body.Text = notice;
                _history.Add("assistant", notice);
                SpeakLastResponse(notice, assistantMessage);
                return;
            }

            if (approval == HermesApprovalDecision.Approved)
            {
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
                _hermesApprovals.Stage(hermesPrompt, directCommand, HermesCommandSource.Desktop, DateTime.UtcNow);
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
            if (isModelControl || HermesApprovalGate.IsApprovalWord(hermesPrompt))
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
