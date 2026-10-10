using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace VoiceChatbot;

/// <summary>
/// A model the app can run itself with its bundled llama.cpp server. Sizes are for the Q4_K_M quantization;
/// the download size shown is replaced by the exact one from Hugging Face once it is looked up.
/// <see cref="ApproxProjectorGb"/> is the extra download for picture support (the vision projector).
/// </summary>
public sealed record LocalModelInfo(
    string Id,
    string Name,
    string Summary,
    string FileName,
    IReadOnlyList<string> Repositories,
    double ApproxDownloadGb,
    double VramGb,
    double MinCardGb,
    string ExampleCards,
    int MaxContext,
    bool Included,
    double ApproxProjectorGb = 0.9)
{
    /// <summary>
    /// The local name of the model's picture support file (its vision projector, "mmproj"): the model's
    /// name with "mmproj" in place of the quantization, e.g. gemma-4-E4B-it-mmproj.gguf. The same whichever
    /// precision (f16, bf16...) was downloaded.
    /// </summary>
    public string ProjectorFileName
    {
        get
        {
            var stem = Path.GetFileNameWithoutExtension(FileName);
            var quantSuffix = "-" + LocalModelCatalog.Quant;
            if (stem.EndsWith(quantSuffix, StringComparison.OrdinalIgnoreCase))
                stem = stem[..^quantSuffix.Length];
            return stem + "-mmproj.gguf";
        }
    }
}

/// <summary>The graphics card llama.cpp will use: its name, video memory and whether it is a separate (discrete) card.</summary>
public sealed record GpuInfo(string Name, long VramBytes, bool Discrete)
{
    public double VramGb => VramBytes / (double)LocalModelCatalog.GiB;
}

/// <summary>How a model suits this PC, shown as a badge in the model chooser.</summary>
public enum ModelFit
{
    /// <summary>Nothing is known about the graphics card.</summary>
    Unknown,
    /// <summary>The whole model fits in video memory: fast.</summary>
    Fits,
    /// <summary>Most of it fits; llama.cpp keeps the rest on the processor: works, somewhat slower.</summary>
    PartlyOnProcessor,
    /// <summary>No usable graphics card (or only integrated graphics): runs on the processor and system memory.</summary>
    ProcessorOnly,
    /// <summary>More than this PC's video memory and system memory together.</summary>
    TooBig
}

public static class LocalModelCatalog
{
    public const long GiB = 1L << 30;
    public const string DefaultId = "gemma-4-e4b";
    public const string Quant = "Q4_K_M";

    /// <summary>Folder (under the app folder) that holds the model shipped with the installer.</summary>
    public const string BundledFolderName = "models";

    public static IReadOnlyList<LocalModelInfo> Models { get; } = new[]
    {
        new LocalModelInfo(
            "gemma-4-e4b", "Gemma 4 E4B",
            "Small and quick. Good for everyday chat, voice and short documents. Included with the app, so it works offline right away.",
            "gemma-4-E4B-it-Q4_K_M.gguf", Repositories("E4B"),
            ApproxDownloadGb: 5.3, VramGb: 6.5, MinCardGb: 8,
            ExampleCards: "RTX 3060 Ti, RTX 4060, RX 7600, Arc A750. 6 GB cards work with a little on the processor.",
            MaxContext: 131072, Included: true, ApproxProjectorGb: 0.9),
        new LocalModelInfo(
            "gemma-4-12b", "Gemma 4 12B",
            "Clearly smarter: better answers about your documents, longer and more careful replies.",
            "gemma-4-12B-it-Q4_K_M.gguf", Repositories("12B"),
            ApproxDownloadGb: 7.5, VramGb: 9.5, MinCardGb: 12,
            ExampleCards: "RTX 3060 12 GB, RTX 4070, RTX 5070, RX 6700 XT, Arc B580",
            MaxContext: 262144, Included: false, ApproxProjectorGb: 0.9),
        new LocalModelInfo(
            "gemma-4-26b-a4b", "Gemma 4 26B A4B",
            "Big \"mixture of experts\" model: close to the top model's quality but quick, because only about 4B of its 26B parameters work on each word.",
            "gemma-4-26B-A4B-it-Q4_K_M.gguf", Repositories("26B-A4B"),
            ApproxDownloadGb: 16.9, VramGb: 19, MinCardGb: 24,
            ExampleCards: "RTX 3090, RTX 4090, RTX 5090, RX 7900 XTX. 16 GB cards (RTX 4080, RX 7800 XT) run it with part on the processor.",
            MaxContext: 262144, Included: false, ApproxProjectorGb: 1.2),
        new LocalModelInfo(
            "gemma-4-31b", "Gemma 4 31B",
            "The smartest Gemma 4. Best for hard questions and long documents; slower than the others.",
            "gemma-4-31B-it-Q4_K_M.gguf", Repositories("31B"),
            ApproxDownloadGb: 18.3, VramGb: 21, MinCardGb: 24,
            ExampleCards: "RTX 3090, RTX 4090, RTX 5090, RX 7900 XTX",
            MaxContext: 262144, Included: false, ApproxProjectorGb: 1.2)
    };

    public static LocalModelInfo Default => Find(DefaultId)!;

    public static LocalModelInfo? Find(string? id) =>
        Models.FirstOrDefault(m => string.Equals(m.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Where a downloaded model is saved: the downloads folder plus its <see cref="LocalModelInfo.FileName"/>.</summary>
    public static string DownloadPath(LocalModelInfo model, string downloadsFolder) => Path.Combine(downloadsFolder, model.FileName);

    /// <summary>Where a downloaded picture support file is saved: the downloads folder plus its <see cref="LocalModelInfo.ProjectorFileName"/>.</summary>
    public static string ProjectorDownloadPath(LocalModelInfo model, string downloadsFolder) => Path.Combine(downloadsFolder, model.ProjectorFileName);

    /// <summary>
    /// The model's file on this PC: a finished download, else the copy the installer put in the app's
    /// models folder; null when neither exists.
    /// </summary>
    public static string? FindInstalledFile(LocalModelInfo model, string downloadsFolder, string appFolder) =>
        FindInstalled(model.FileName, downloadsFolder, appFolder);

    /// <summary>
    /// The model's picture support file (vision projector) on this PC, looked for like the model itself:
    /// a finished download first, then the app's models folder; null when there is none (text only).
    /// </summary>
    public static string? FindInstalledProjector(LocalModelInfo model, string downloadsFolder, string appFolder) =>
        FindInstalled(model.ProjectorFileName, downloadsFolder, appFolder);

    private static string? FindInstalled(string fileName, string downloadsFolder, string appFolder)
    {
        foreach (var path in new[] { Path.Combine(downloadsFolder, fileName), Path.Combine(appFolder, BundledFolderName, fileName) })
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    return path;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return null;
    }

    /// <summary>
    /// How <paramref name="model"/> suits a PC with <paramref name="gpu"/> (null when none was found) and
    /// <paramref name="systemRamBytes"/> of memory (0 when unknown).
    /// </summary>
    public static ModelFit Evaluate(LocalModelInfo model, GpuInfo? gpu, long systemRamBytes)
    {
        var ramGb = systemRamBytes / (double)GiB;
        // Windows and the app itself need a few GB of system memory too.
        var spareRamGb = ramGb > 0 ? Math.Max(0, ramGb - 4) : 0;

        if (gpu is { Discrete: true } && gpu.VramGb > 0)
        {
            // Cards report a little less than their label (a "12 GB" card shows about 11.9).
            if (gpu.VramGb + 0.5 >= model.VramGb)
                return ModelFit.Fits;
            if (ramGb == 0 || gpu.VramGb + spareRamGb >= model.VramGb)
                return ModelFit.PartlyOnProcessor;
            return ModelFit.TooBig;
        }

        if (ramGb == 0)
            return ModelFit.Unknown;
        return spareRamGb >= model.VramGb ? ModelFit.ProcessorOnly : ModelFit.TooBig;
    }

    /// <summary>The badge text for <paramref name="fit"/>; "" for <see cref="ModelFit.Unknown"/>.</summary>
    public static string FitLabel(ModelFit fit) => fit switch
    {
        ModelFit.Fits => "Fits your graphics card",
        ModelFit.PartlyOnProcessor => "Part runs on the processor: slower",
        ModelFit.ProcessorOnly => "Runs on the processor: slow",
        ModelFit.TooBig => "Too big for this PC",
        _ => ""
    };

    /// <summary>"7.4 GB" style size for the chooser.</summary>
    public static string FormatGb(double gb) => gb >= 10 ? $"{gb:0} GB" : $"{gb:0.#} GB";

    /// <summary>
    /// The context window (tokens) to start llama-server with: the Context window setting when it is set
    /// (at least 4096), otherwise as much as the graphics card has room for next to the model.
    /// </summary>
    public static int ChooseContext(int configured, int modelMax, GpuInfo? gpu, long modelBytes)
    {
        modelMax = modelMax > 0 ? modelMax : 131072;
        if (configured > 0)
            return Math.Clamp(configured, 4096, modelMax);

        var spare = gpu is { Discrete: true } ? gpu.VramBytes - modelBytes : 0;
        var context = spare >= 6 * GiB ? 65536
            : spare >= 3 * GiB ? 32768
            : spare >= 3 * GiB / 2 ? 16384
            : 8192;
        return Math.Min(context, modelMax);
    }

    // Hugging Face repositories that publish Q4_K_M GGUF files of a Gemma 4 size, most trusted first.
    private static IReadOnlyList<string> Repositories(string size) => new[]
    {
        $"ggml-org/gemma-4-{size}-it-GGUF",
        $"unsloth/gemma-4-{size}-it-GGUF",
        $"lmstudio-community/gemma-4-{size}-it-GGUF",
        $"bartowski/google_gemma-4-{size}-it-GGUF",
        $"bartowski/gemma-4-{size}-it-GGUF"
    };
}
