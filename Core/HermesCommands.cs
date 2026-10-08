using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

public enum HermesModelControlAction
{
    StartComfyUi,
    StopComfyUi,
    StopLlama,
    StartLlama
}

/// <param name="BatchPath">The WSL path of the llama.cpp start script (StartLlama only).</param>
/// <param name="Port">The port that script serves on (StartLlama only).</param>
public sealed record HermesModelControlTarget(HermesModelControlAction Action, string BatchPath = "", int? Port = null);

/// <summary>
/// Parses what follows "Hermes" in a chat message. A model-control request runs a known start/stop
/// script at once, so it only matches a short command that begins with a control verb and names
/// nothing but models ("start gemma 26b", "switch to gpt-oss", "stop the current model",
/// "start comfyui"). Questions that mention ComfyUI or a model ("what version of ComfyUI is installed",
/// "why did the model stop") go to the Hermes agent, and "run &lt;shell command&gt;" is staged for approval
/// even when the command mentions a model name.
/// </summary>
public static class HermesCommandParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly HashSet<string> StartVerbs = new(StringComparer.Ordinal) { "start", "switch", "load", "launch", "run", "change" };
    private static readonly HashSet<string> ComfyOnlyStartVerbs = new(StringComparer.Ordinal) { "open", "restart" };
    private static readonly HashSet<string> StopVerbs = new(StringComparer.Ordinal) { "stop", "kill", "shutdown", "terminate", "unload" };

    // Words that may surround the model name without changing what the command means.
    private static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "to", "from", "on", "up", "down", "over", "back", "now", "again", "instead", "please",
        "for", "me", "us", "my", "it", "this", "that", "all", "current", "currently", "running", "model", "models",
        "server", "servers", "llama", "llama.cpp", "cpp", "local", "version", "thanks", "thank", "you"
    };

    private static readonly HashSet<string> ModelModifiers = new(StringComparer.Ordinal)
    {
        "text", "textonly", "only", "spec", "speculative", "draft", "gpu", "medium", "instruct", "ui", "lan"
    };

    private static readonly HashSet<string> StopTargets = new(StringComparer.Ordinal)
    {
        "current", "running", "llama", "llama.cpp", "model", "models", "server", "servers"
    };

    private static readonly Regex ModelFamilyToken = new(@"^(?:gpt|oss|gptoss|mistral|qwen|lfm|gemma|comfy|comfyui)[a-z0-9.]*$", Options);
    private static readonly Regex SizeOrVersionToken = new(@"^[a-z]?\d+(?:\.\d+)?[a-z]?\d*$", Options);
    private static readonly Regex DirectCommandRegex = new(@"^(?:run|execute|shell|terminal|cli)\s+(?<c>.+)$", Options | RegexOptions.Singleline);

    /// <summary>"run nvidia-smi" style requests: the shell command to stage for approval.</summary>
    public static bool TryGetDirectSshCommand(string? prompt, out string command)
    {
        command = "";
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var match = DirectCommandRegex.Match(prompt.Trim());
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups["c"].Value))
            return false;

        command = match.Groups["c"].Value.Trim();
        return true;
    }

    public static bool TryMatchModelControl(string? prompt, out HermesModelControlTarget target)
    {
        target = new HermesModelControlTarget(HermesModelControlAction.StartLlama);
        if (string.IsNullOrWhiteSpace(prompt))
            return false;

        var normalized = Regex.Replace(prompt.ToLowerInvariant(), @"[^a-z0-9.]+", " ").Trim();
        normalized = Regex.Replace(normalized, @"\bshut\s+down\b", "shutdown");
        normalized = Regex.Replace(normalized, @"\bcomfy\s+ui\b", "comfyui");
        var tokens = normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('.'))
            .Where(t => t.Length > 0)
            .ToList();

        var index = SkipPoliteLead(tokens);
        if (index >= tokens.Count)
            return false;

        var verb = tokens[index];
        var rest = tokens.Skip(index + 1).ToList();
        var isStop = StopVerbs.Contains(verb);
        var isStart = StartVerbs.Contains(verb);
        var isComfyStart = ComfyOnlyStartVerbs.Contains(verb);
        if (!isStop && !isStart && !isComfyStart)
            return false;

        // Anything besides model names and filler ("ollama stop gemma", "cat start-qwen.bat",
        // "and then summarize the logs") is not a plain model-control command.
        if (rest.Count == 0 || !rest.All(IsModelControlWord))
            return false;

        if (rest.Contains("comfy") || rest.Contains("comfyui"))
        {
            target = new HermesModelControlTarget(isStop ? HermesModelControlAction.StopComfyUi : HermesModelControlAction.StartComfyUi);
            return true;
        }

        if (isComfyStart)
            return false;

        if (isStop)
        {
            if (!rest.Any(StopTargets.Contains))
                return false;

            target = new HermesModelControlTarget(HermesModelControlAction.StopLlama);
            return true;
        }

        // "switch from qwen to gemma": the model after the last "to" is the one to start.
        var lastTo = rest.LastIndexOf("to");
        var batch = lastTo >= 0 && lastTo < rest.Count - 1
            ? ResolveLlamaBatchFile(string.Join(" ", rest.Skip(lastTo + 1)))
            : null;
        batch ??= ResolveLlamaBatchFile(string.Join(" ", rest));
        if (batch is null)
            return false;

        target = new HermesModelControlTarget(HermesModelControlAction.StartLlama, batch.Value.BatchPath, batch.Value.Port);
        return true;
    }

    /// <summary>The llama.cpp start script and port for a normalized (lower case, punctuation as spaces) model description.</summary>
    public static (string BatchPath, int Port)? ResolveLlamaBatchFile(string normalizedPrompt)
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
        // 26B A4B before 4B and the generic speculative script: "a4b" contains "4b", and
        // "gemma 26b speculative" means the 26B speculative script.
        if (normalizedPrompt.Contains("gemma") && (normalizedPrompt.Contains("26b") || normalizedPrompt.Contains("a4b")))
            return normalizedPrompt.Contains("spec")
                ? ("/mnt/c/llama.cpp/start-gemma4-26b-a4b-speculative.bat", 8080)
                : ("/mnt/c/llama.cpp/start-gemma4-26b-a4b.bat", 8080);
        if (normalizedPrompt.Contains("gemma") && Regex.IsMatch(normalizedPrompt, @"\be?4b\b"))
            return ("/mnt/c/llama.cpp/start-gemma4-4b.bat", 8081);
        if (normalizedPrompt.Contains("gemma") && (normalizedPrompt.Contains("spec") || normalizedPrompt.Contains("draft")))
            return ("/mnt/c/llama.cpp/start-gemma4-speculative.bat", 8080);
        if (normalizedPrompt.Contains("gemma") && (normalizedPrompt.Contains("gpu") || normalizedPrompt.Contains("31b gpu")))
            return ("/mnt/c/llama.cpp/start-gemma4-gpu.bat", 8080);
        if (normalizedPrompt.Contains("gemma"))
            return ("/mnt/c/llama.cpp/start-gemma4.bat", 8080);

        return null;
    }

    private static bool IsModelControlWord(string token) =>
        FillerWords.Contains(token)
        || ModelModifiers.Contains(token)
        || ModelFamilyToken.IsMatch(token)
        || SizeOrVersionToken.IsMatch(token);

    // "please", "can you", "could you please", "go ahead and" before the verb.
    private static int SkipPoliteLead(IReadOnlyList<string> tokens)
    {
        var i = 0;
        while (i < tokens.Count)
        {
            if (tokens[i] is "please" or "now" or "hey" or "ok" or "okay")
                i++;
            else if (tokens[i] is "can" or "could" or "would" or "will" && i + 1 < tokens.Count && tokens[i + 1] == "you")
                i += 2;
            else if (tokens[i] == "go" && i + 2 < tokens.Count && tokens[i + 1] == "ahead" && tokens[i + 2] == "and")
                i += 3;
            else
                break;
        }

        return i;
    }
}

public enum HermesCommandSource
{
    Desktop,
    Phone
}

public enum HermesApprovalDecision
{
    /// <summary>The prompt is not an approval.</summary>
    NotAnApproval,
    /// <summary>An approval, but no command is staged.</summary>
    NothingPending,
    /// <summary>The staged command may run now.</summary>
    Approved,
    /// <summary>The staged command is older than <see cref="HermesApprovalGate.Lifetime"/>; it is not run.</summary>
    Expired,
    /// <summary>A casual "ok"/"yes" from the other device; only "approve" may cross devices.</summary>
    NeedsExplicitApproval
}

/// <summary>
/// Holds the one "Hermes run ..." SSH command waiting for approval. A staged command expires after
/// <see cref="Lifetime"/>. "Hermes approve" runs it from either device; a casual "Hermes ok/yes"
/// only counts from the device that staged it. Thread-safe: the phone remote calls in from server threads.
/// </summary>
public sealed class HermesApprovalGate
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private static readonly Regex ExplicitApproval = new(
        @"^(?:i\s+)?approved?(?:\s+(?:it|that|this|command|the\s+command|the\s+ssh\s+command))?(?:\s+please)?$",
        RegexOptions.CultureInvariant);

    private readonly object _lock = new();
    private string _request = "";
    private string _command = "";
    private HermesCommandSource _source;
    private DateTime _stagedUtc;

    public bool HasStaged
    {
        get { lock (_lock) return _command.Length > 0; }
    }

    /// <summary>The request that staged the command, even after it expired (so it can still be cancelled by name).</summary>
    public string StagedRequest
    {
        get { lock (_lock) return _command.Length > 0 ? _request : ""; }
    }

    public string StagedCommand
    {
        get { lock (_lock) return _command; }
    }

    public HermesCommandSource StagedSource
    {
        get { lock (_lock) return _source; }
    }

    public void Stage(string request, string command, HermesCommandSource source, DateTime nowUtc)
    {
        lock (_lock)
        {
            _request = request ?? "";
            _command = (command ?? "").Trim();
            _source = source;
            _stagedUtc = nowUtc;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _request = "";
            _command = "";
            _stagedUtc = default;
        }
    }

    /// <summary>The staged command while it is still valid, otherwise "".</summary>
    public string GetLiveCommand(DateTime nowUtc)
    {
        lock (_lock)
            return _command.Length > 0 && !IsExpiredLocked(nowUtc) ? _command : "";
    }

    /// <summary>What <paramref name="prompt"/> from <paramref name="source"/> would do now, without changing anything.</summary>
    public HermesApprovalDecision Evaluate(string? prompt, HermesCommandSource source, DateTime nowUtc)
    {
        var isExplicit = IsExplicitApproval(prompt);
        if (!isExplicit && !IsCasualApproval(prompt))
            return HermesApprovalDecision.NotAnApproval;

        lock (_lock)
        {
            if (_command.Length == 0)
                return HermesApprovalDecision.NothingPending;
            if (IsExpiredLocked(nowUtc))
                return HermesApprovalDecision.Expired;
            if (!isExplicit && source != _source)
                return HermesApprovalDecision.NeedsExplicitApproval;
            return HermesApprovalDecision.Approved;
        }
    }

    /// <summary>
    /// Like <see cref="Evaluate"/>, and on <see cref="HermesApprovalDecision.Approved"/> hands over the
    /// command and clears it, so it runs once. An expired command is cleared as well.
    /// </summary>
    public HermesApprovalDecision TryApprove(string? prompt, HermesCommandSource source, DateTime nowUtc, out string command)
    {
        command = "";
        lock (_lock)
        {
            var decision = Evaluate(prompt, source, nowUtc);
            if (decision == HermesApprovalDecision.Approved)
                command = _command;
            if (decision is HermesApprovalDecision.Approved or HermesApprovalDecision.Expired)
                Clear();
            return decision;
        }
    }

    /// <summary>Clears a staged command (expired or not) and returns the request that staged it.</summary>
    public bool TryCancel(out string request)
    {
        lock (_lock)
        {
            request = _request;
            if (_command.Length == 0)
                return false;

            Clear();
            return true;
        }
    }

    /// <summary>"approve", "approved", "I approve", "approve it".</summary>
    public static bool IsExplicitApproval(string? prompt) => ExplicitApproval.IsMatch(Normalize(prompt));

    /// <summary>"yes", "ok", "do it", "go ahead" and similar.</summary>
    public static bool IsCasualApproval(string? prompt) =>
        Normalize(prompt) is "yes" or "yep" or "ok" or "okay" or "do it" or "go ahead" or "run it" or "execute" or "confirmed" or "confirm";

    public static bool IsApprovalWord(string? prompt) => IsExplicitApproval(prompt) || IsCasualApproval(prompt);

    public static bool IsCancelWord(string? prompt) =>
        Normalize(prompt) is "cancel" or "no" or "stop" or "never mind" or "nevermind" or "abort";

    private bool IsExpiredLocked(DateTime nowUtc) => nowUtc - _stagedUtc > Lifetime;

    private static string Normalize(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return "";

        var normalized = Regex.Replace(prompt.Trim().ToLowerInvariant(), @"[^\p{L}\p{N}\s]", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }
}
