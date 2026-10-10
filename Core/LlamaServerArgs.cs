using System;
using System.Collections.Generic;
using System.Globalization;

namespace VoiceChatbot;

/// <summary>Options of the bundled llama-server that differ between llama.cpp versions, read from its --help.</summary>
public sealed record LlamaServerFeatures(bool Fit, bool Jinja, bool NoWebUi, bool Device = false)
{
    public static LlamaServerFeatures None { get; } = new(false, false, false);

    public static LlamaServerFeatures FromHelp(string? help)
    {
        help ??= "";
        return new LlamaServerFeatures(
            help.Contains("--fit", StringComparison.Ordinal),
            help.Contains("--jinja", StringComparison.Ordinal),
            help.Contains("--no-webui", StringComparison.Ordinal),
            help.Contains("--device", StringComparison.Ordinal));
    }
}

/// <summary>The command line for the bundled llama-server.</summary>
public static class LlamaServerArgs
{
    public const string Host = "127.0.0.1";

    /// <summary>
    /// Arguments for serving <paramref name="modelPath"/> on 127.0.0.1:<paramref name="port"/> under the name
    /// <paramref name="alias"/>, with one conversation slot of <paramref name="contextTokens"/> tokens.
    /// With <paramref name="useGpu"/> the model goes on the graphics card: newer llama.cpp sizes that to
    /// the free video memory itself (--fit, its default); older versions get every layer (-ngl 999).
    /// Without, everything stays on the processor (-ngl 0, and --device none where supported).
    /// </summary>
    public static IReadOnlyList<string> Build(string modelPath, int port, string alias, int contextTokens, bool useGpu, LlamaServerFeatures features)
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

        // Chat templates with tool calling (Gemma's own template).
        if (features.Jinja)
            args.Add("--jinja");
        if (features.NoWebUi)
            args.Add("--no-webui");

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

    /// <summary>A short reason for a failed start, from llama-server's last lines of output.</summary>
    public static string DescribeFailure(string? output)
    {
        var text = output ?? "";
        if (text.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ErrorOutOfDeviceMemory", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase))
            return "not enough memory for this model";
        if (text.Contains("unknown model architecture", StringComparison.OrdinalIgnoreCase))
            return "this llama.cpp is too old for the model";
        if (text.Contains("failed to load model", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error loading model", StringComparison.OrdinalIgnoreCase))
            return "the model file could not be loaded (damaged or incomplete?)";
        if (IsPortInUse(text))
            return "its port is in use by another program";
        return "it stopped while loading";
    }
}
