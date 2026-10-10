using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Options of the bundled llama-server that differ between llama.cpp versions, read from its --help.
/// <see cref="Mmproj"/>: it can load a vision projector (picture support); <see cref="NoMmprojOffload"/>:
/// that projector can be kept off the graphics card; <see cref="Reasoning"/>: it has --reasoning, which
/// turns the model's thinking off for the whole server (not just --reasoning-budget or --reasoning-format).
/// </summary>
public sealed record LlamaServerFeatures(bool Fit, bool Jinja, bool NoWebUi, bool Device = false, bool Mmproj = false, bool NoMmprojOffload = false,
    bool Reasoning = false)
{
    // "--reasoning" itself, not "--reasoning-budget" or "--reasoning-format".
    private static readonly Regex ReasoningInHelp = new(@"--reasoning(?![-\w])", RegexOptions.CultureInvariant);

    public static LlamaServerFeatures None { get; } = new(false, false, false);

    public static LlamaServerFeatures FromHelp(string? help)
    {
        help ??= "";
        return new LlamaServerFeatures(
            help.Contains("--fit", StringComparison.Ordinal),
            help.Contains("--jinja", StringComparison.Ordinal),
            help.Contains("--no-webui", StringComparison.Ordinal),
            help.Contains("--device", StringComparison.Ordinal),
            help.Contains("--mmproj", StringComparison.Ordinal),
            help.Contains("--no-mmproj-offload", StringComparison.Ordinal),
            ReasoningInHelp.IsMatch(help));
    }
}

/// <summary>The command line for the bundled llama-server.</summary>
public static class LlamaServerArgs
{
    public const string Host = "127.0.0.1";

    /// <summary>The option that turns the model's thinking on or off for the whole server ("--reasoning off").</summary>
    public const string ReasoningOption = "--reasoning";

    // How llama.cpp reports an option it does not know or a value it does not accept:
    // "error: invalid argument: --reasoning", "error while handling argument "--reasoning": ...". Not a
    // system error such as "error: Invalid argument" at the end of a line.
    private static readonly Regex ArgumentError = new(@"\berror:\s*(?:invalid|unknown|unrecognized)\s+(?:argument|option)(?::|\s+-)|\berror while handling argument",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Arguments for serving <paramref name="modelPath"/> on 127.0.0.1:<paramref name="port"/> under the name
    /// <paramref name="alias"/>, with one conversation slot of <paramref name="contextTokens"/> tokens.
    /// With <paramref name="useGpu"/> the model goes on the graphics card: newer llama.cpp sizes that to
    /// the free video memory itself (--fit, its default); older versions get every layer (-ngl 999).
    /// Without, everything stays on the processor (-ngl 0, and --device none where supported).
    /// <paramref name="projectorPath"/> (the model's picture support file) is loaded with --mmproj when this
    /// llama-server supports it; null or "" starts the model text only. With <paramref name="thinkingOff"/>
    /// ("Hide model thinking") the server answers without its thinking phase (--reasoning off) when it has
    /// that option; Gemma 4 can still think when only the request asks it not to, which delays every reply.
    /// </summary>
    public static IReadOnlyList<string> Build(string modelPath, int port, string alias, int contextTokens, bool useGpu, LlamaServerFeatures features,
        string? projectorPath = null, bool thinkingOff = false)
    {
        var args = new List<string>
        {
            "-m", modelPath,
            "--host", Host,
            "--port", port.ToString(CultureInfo.InvariantCulture),
            "--alias", alias,
            "-c", contextTokens.ToString(CultureInfo.InvariantCulture),
            "-np", "1"
        };

        if (!string.IsNullOrWhiteSpace(projectorPath) && features.Mmproj)
        {
            args.AddRange(new[] { "--mmproj", projectorPath });
            // On the processor the picture encoder stays there too.
            if (!useGpu && features.NoMmprojOffload)
                args.Add("--no-mmproj-offload");
        }

        // Chat templates with tool calling (Gemma's own template).
        if (features.Jinja)
            args.Add("--jinja");
        if (features.NoWebUi)
            args.Add("--no-webui");
        if (thinkingOff && features.Reasoning)
            args.AddRange(new[] { ReasoningOption, "off" });

        if (!useGpu)
        {
            args.AddRange(new[] { "-ngl", "0" });
            // Not even scratch memory on the graphics card: the GPU start may have failed because of it.
            if (features.Device)
                args.AddRange(new[] { "--device", "none" });
        }
        else if (!features.Fit)
            args.AddRange(new[] { "-ngl", "999" });

        return args;
    }

    /// <summary>True when llama-server's output says it could not open its port (another program has it).</summary>
    public static bool IsPortInUse(string? output) =>
        !string.IsNullOrEmpty(output) &&
        (output.Contains("couldn't bind", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("could not bind", StringComparison.OrdinalIgnoreCase) ||
         output.Contains("address already in use", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when llama-server's output says it stopped because of its command line: an option it does not
    /// know, or a value it does not accept.
    /// </summary>
    public static bool IsArgumentError(string? output) => !string.IsNullOrEmpty(output) && ArgumentError.IsMatch(output);

    /// <summary>A short reason for a failed start, from llama-server's last lines of output.</summary>
    public static string DescribeFailure(string? output)
    {
        var text = output ?? "";
        if (text.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ErrorOutOfDeviceMemory", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase))
            return "not enough memory for this model";
        // Before "failed to load model": the projector's loader prints that too ("clip_init: failed to load model").
        if (text.Contains("failed to load multimodal", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed to load CLIP", StringComparison.OrdinalIgnoreCase))
            return "its picture support file could not be loaded";
        if (text.Contains("unknown model architecture", StringComparison.OrdinalIgnoreCase))
            return "this llama.cpp is too old for the model";
        if (text.Contains("failed to load model", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error loading model", StringComparison.OrdinalIgnoreCase))
            return "the model file could not be loaded (damaged or incomplete?)";
        if (IsPortInUse(text))
            return "its port is in use by another program";
        if (IsArgumentError(text))
            return "this llama.cpp does not accept one of its start options";
        return "it stopped while loading";
    }
}
