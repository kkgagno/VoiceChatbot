using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceChatbot;

public enum WakeWordStatus
{
    Off,
    Starting,
    Listening,
    Paused,
    Error
}

/// <summary>A detection reported by WakeWordDetector.</summary>
public sealed record WakeWordEvent(string Model, double Score);

/// <summary>
/// The app side of the openWakeWord detector: the pretrained models it offers, their files under
/// Resources\Models\WakeWord, and how its state is described in the UI.
/// </summary>
public static class WakeWordProtocol
{
    public const string DefaultModel = "hey_jarvis";
    public const double DefaultThreshold = 0.5;
    public const double MinThreshold = 0.1;
    public const double MaxThreshold = 0.9;

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

    /// <summary>The model's file in Resources\Models\WakeWord: "hey_jarvis_v0.1.onnx".</summary>
    public static string ModelFileName(string? model) => $"{NormalizeModel(model)}_v0.1.onnx";

    /// <summary>Status line shown under the switch in Voice Input.</summary>
    public static string DescribeStatus(WakeWordStatus status, string? model, string? detail = null)
    {
        var info = (detail ?? "").Trim();
        return status switch
        {
            WakeWordStatus.Off => "Off",
            WakeWordStatus.Starting => "Loading the wake word model...",
            WakeWordStatus.Listening => $"Listening for \"{SpokenPhrase(model)}\"",
            WakeWordStatus.Paused => "Paused while the assistant listens or speaks",
            WakeWordStatus.Error => info.Length > 0 ? $"Error: {info}" : "Error",
            _ => ""
        };
    }
}
