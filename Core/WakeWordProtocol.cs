using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace VoiceChatbot;

public enum WakeWordStatus
{
    Off,
    NotInstalled,
    Starting,
    Listening,
    Paused,
    Error
}

public enum WakeWordEventKind
{
    Ready,
    Wake,
    Error
}

/// <summary>One JSON line printed by Tools/WakeWord/wakeword_server.py.</summary>
public sealed record WakeWordEvent(
    WakeWordEventKind Kind,
    string Model = "",
    double Score = 0,
    string Message = "",
    string Code = "");

/// <summary>
/// The app side of the openWakeWord helper: the pretrained models it offers, the command line it is
/// started with, the events it prints, and how its state is described in the UI.
/// </summary>
public static class WakeWordProtocol
{
    public const string DefaultModel = "hey_jarvis";
    public const double DefaultThreshold = 0.5;
    public const double MinThreshold = 0.1;
    public const double MaxThreshold = 0.9;
    public const string InstallScript = @"Tools\WakeWord\install-wakeword.ps1";

    /// <summary>Pretrained openWakeWord models, in the order the UI lists them.</summary>
    public static readonly IReadOnlyList<string> Models = new[] { "hey_jarvis", "alexa", "hey_mycroft", "hey_rhasspy" };

    /// <summary>Maps loose input ("Hey Jarvis", "hey-jarvis") to a known model id, else the default.</summary>
    public static string NormalizeModel(string? model)
    {
        var key = new string((model ?? "").Trim().ToLowerInvariant()
            .Select(c => c is ' ' or '-' ? '_' : c)
            .ToArray());
        return Models.FirstOrDefault(m => m == key) ?? DefaultModel;
    }

    /// <summary>What the user says: "hey jarvis".</summary>
    public static string SpokenPhrase(string? model) => NormalizeModel(model).Replace('_', ' ');

    /// <summary>Combo box label: "Hey Jarvis".</summary>
    public static string DisplayName(string? model) =>
        string.Join(" ", SpokenPhrase(model).Split(' ').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

    public static double ClampThreshold(double threshold) =>
        double.IsFinite(threshold) ? Math.Clamp(threshold, MinThreshold, MaxThreshold) : DefaultThreshold;

    /// <summary>Slider value (10-90): higher sensitivity means a lower score is enough.</summary>
    public static int ThresholdToSensitivity(double threshold) =>
        (int)Math.Round((1 - ClampThreshold(threshold)) * 100);

    public static double SensitivityToThreshold(double sensitivity) =>
        ClampThreshold(Math.Round(1 - (double.IsFinite(sensitivity) ? sensitivity : 50) / 100, 2));

    /// <summary>Arguments for python.exe. The threshold always uses a dot, whatever the Windows locale.</summary>
    public static string BuildArguments(string scriptPath, string? model, double threshold) =>
        $"\"{scriptPath}\" --model {NormalizeModel(model)} --threshold " +
        ClampThreshold(threshold).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Parses one stdout line. Returns null for blank, non-JSON or unknown lines.</summary>
    public static WakeWordEvent? ParseEvent(string? line)
    {
        var text = line?.Trim() ?? "";
        if (!text.StartsWith('{'))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            WakeWordEventKind? kind = GetString(root, "event").ToLowerInvariant() switch
            {
                "ready" => WakeWordEventKind.Ready,
                "wake" => WakeWordEventKind.Wake,
                "error" => WakeWordEventKind.Error,
                _ => null
            };
            if (kind == null)
                return null;

            return new WakeWordEvent(
                kind.Value,
                GetString(root, "model"),
                GetDouble(root, "score"),
                GetString(root, "message"),
                GetString(root, "code"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when a failure means the helper is not set up yet: Python or the openwakeword package is
    /// missing. Exit code 9009 is what the Windows "python.exe" store alias returns without Python.
    /// </summary>
    public static bool LooksNotInstalled(string? message, int? exitCode = null, string? code = null)
    {
        if (exitCode == 9009 || string.Equals(code, "missing_package", StringComparison.OrdinalIgnoreCase))
            return true;

        var text = message ?? "";
        return text.Contains("No module named", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("ModuleNotFoundError", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Python was not found", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("not installed", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Status line shown under the switch in Voice Input.</summary>
    public static string DescribeStatus(WakeWordStatus status, string? model, string? detail = null)
    {
        var info = (detail ?? "").Trim();
        return status switch
        {
            WakeWordStatus.Off => "Off",
            WakeWordStatus.NotInstalled => $"Not installed - run {InstallScript}",
            WakeWordStatus.Starting => "Starting...",
            WakeWordStatus.Listening => $"Listening for \"{SpokenPhrase(model)}\"",
            WakeWordStatus.Paused => "Paused while the assistant listens or speaks",
            WakeWordStatus.Error => info.Length > 0 ? $"Error: {info}" : "Error",
            _ => ""
        };
    }

    private static string GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static double GetDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String &&
               double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}
