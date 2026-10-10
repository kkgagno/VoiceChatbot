using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

/// <summary>A file in a Hugging Face model repository, with the size and SHA-256 the site lists for it.</summary>
public sealed record HfFile(string Repository, string Path, long Size, string Sha256)
{
    /// <summary>The download address (Hugging Face redirects it to its file servers).</summary>
    public string Url => $"https://huggingface.co/{Repository}/resolve/main/{string.Join("/", Path.Split('/').Select(Uri.EscapeDataString))}";
}

/// <summary>
/// A model's GGUF file and its picture support file (vision projector, "mmproj"), both from the same
/// repository. <see cref="Projector"/> is null when the repository has none: the model then reads text only.
/// </summary>
public sealed record HfModelFiles(HfFile Model, HfFile? Projector);

/// <summary>
/// Finds a model's GGUF file on Hugging Face: lists each candidate repository through the public API and
/// picks the file of the wanted quantization, and the vision projector next to it. Repositories that do not
/// exist (or need a login) are skipped.
/// </summary>
public static class HuggingFaceFiles
{
    private static readonly Regex SplitPart = new(@"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Projector precisions, best first. The lookarounds keep "bf16" from counting as "f16".
    private static readonly Regex[] ProjectorPrecisions =
    {
        Precision("fp?16"),
        Precision("bf16"),
        Precision("q8_0"),
        Precision("fp?32")
    };

    public static string TreeUrl(string repository) => $"https://huggingface.co/api/models/{repository}/tree/main?recursive=true";

    /// <summary>
    /// The first file of <paramref name="quant"/> found in <see cref="LocalModelInfo.Repositories"/> and the
    /// projector from the same repository (one listing per repository), or null when no repository has the
    /// model. With <paramref name="installedModelSize"/> (a model already on this PC, which only needs its
    /// picture support) the repository whose model file has exactly that size is preferred, so the projector
    /// comes from where the model came from; otherwise the first one found is used.
    /// Network errors other than "not found" from the last repository are thrown, so the caller can say why.
    /// </summary>
    public static async Task<HfModelFiles?> ResolveAsync(HttpClient http, LocalModelInfo model, string quant, CancellationToken ct,
        long installedModelSize = 0)
    {
        Exception? lastError = null;
        HfModelFiles? firstFound = null;
        foreach (var repository in model.Repositories)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync(TreeUrl(repository), ct).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    continue;
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var file = PickQuantFile(repository, json, quant);
                if (file == null)
                    continue;

                var found = new HfModelFiles(file, PickProjectorFile(repository, json));
                if (installedModelSize <= 0 || file.Size == installedModelSize)
                    return found;
                firstFound ??= found;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                lastError = ex;
            }
        }

        if (firstFound != null)
            return firstFound;
        if (lastError != null)
            throw new HttpRequestException($"Could not look up {model.Name} on Hugging Face: {lastError.Message}", lastError);
        return null;
    }

    /// <summary>
    /// Picks the <paramref name="quant"/> GGUF from a repository listing (the JSON array the tree API returns).
    /// Skips vision projectors (mmproj) and files split into parts; prefers the plain quantization
    /// ("…-Q4_K_M.gguf") over variants such as "UD-Q4_K_M", then the file nearest the top folder.
    /// </summary>
    public static HfFile? PickQuantFile(string repository, string treeJson, string quant)
    {
        if (string.IsNullOrWhiteSpace(treeJson) || string.IsNullOrWhiteSpace(quant))
            return null;

        using var doc = JsonDocument.Parse(treeJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        var candidates = new List<(HfFile File, int Rank)>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(entry, "type"), "file", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = ReadString(entry, "path");
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (!name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("mmproj", StringComparison.OrdinalIgnoreCase) ||
                SplitPart.IsMatch(name))
                continue;

            var stem = name[..^".gguf".Length];
            var index = stem.LastIndexOf(quant, StringComparison.OrdinalIgnoreCase);
            if (index < 0 || index + quant.Length != stem.Length)
                continue; // Q4_K_M must end the name: not Q4_K_M_L or Q4_K_M-something.

            var before = index > 0 ? stem[index - 1] : '-';
            if (before is not ('-' or '_' or '.'))
                continue;

            // 0 = plain quantization, 1 = a variant such as UD-Q4_K_M; deeper folders rank lower.
            var prefix = stem[..index];
            var variant = prefix.EndsWith("UD-", StringComparison.OrdinalIgnoreCase) || prefix.EndsWith("UD_", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var depth = path.Count(c => c == '/');
            candidates.Add((ReadFile(repository, entry, path), variant * 100 + depth));
        }

        return candidates
            .OrderBy(c => c.Rank)
            .ThenBy(c => c.File.Path.Length)
            .Select(c => c.File)
            .FirstOrDefault();
    }

    /// <summary>
    /// Picks the vision projector (a .gguf with "mmproj" in its name) from a repository listing, or null when
    /// it has none. Prefers f16, then bf16, q8_0, f32 and anything else; skips files split into parts; then
    /// the file nearest the top folder.
    /// </summary>
    public static HfFile? PickProjectorFile(string repository, string treeJson)
    {
        if (string.IsNullOrWhiteSpace(treeJson))
            return null;

        using var doc = JsonDocument.Parse(treeJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        var candidates = new List<(HfFile File, int Rank)>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !string.Equals(ReadString(entry, "type"), "file", StringComparison.OrdinalIgnoreCase))
                continue;

            var path = ReadString(entry, "path");
            var name = path[(path.LastIndexOf('/') + 1)..];
            if (!name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
                !name.Contains("mmproj", StringComparison.OrdinalIgnoreCase) ||
                SplitPart.IsMatch(name))
                continue;

            var stem = name[..^".gguf".Length];
            var precision = Array.FindIndex(ProjectorPrecisions, p => p.IsMatch(stem));
            if (precision < 0)
                precision = ProjectorPrecisions.Length;
            var depth = path.Count(c => c == '/');
            candidates.Add((ReadFile(repository, entry, path), precision * 100 + depth));
        }

        return candidates
            .OrderBy(c => c.Rank)
            .ThenBy(c => c.File.Path.Length)
            .ThenBy(c => c.File.Path, StringComparer.Ordinal)
            .Select(c => c.File)
            .FirstOrDefault();
    }

    // The file's size and SHA-256 from its Git LFS entry (big files), else the plain size.
    private static HfFile ReadFile(string repository, JsonElement entry, string path)
    {
        long size = 0;
        var sha = "";
        if (entry.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object)
        {
            size = ReadLong(lfs, "size");
            sha = ReadString(lfs, "oid");
            if (sha.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = sha["sha256:".Length..];
        }
        if (size <= 0)
            size = ReadLong(entry, "size");
        if (!Sha256Hex.IsMatch(sha))
            sha = "";
        return new HfFile(repository, path, size, sha.ToLowerInvariant());
    }

    // A precision as a whole word of the file name: "-f16." or "_F16" match, "bf16" does not match "f16".
    private static Regex Precision(string pattern) =>
        new($"(?<![a-z0-9]){pattern}(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
