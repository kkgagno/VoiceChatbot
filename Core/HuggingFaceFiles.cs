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
/// Finds a model's GGUF file on Hugging Face: lists each candidate repository through the public API and
/// picks the file of the wanted quantization. Repositories that do not exist (or need a login) are skipped.
/// </summary>
public static class HuggingFaceFiles
{
    private static readonly Regex SplitPart = new(@"-\d{5}-of-\d{5}\.gguf$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string TreeUrl(string repository) => $"https://huggingface.co/api/models/{repository}/tree/main?recursive=true";

    /// <summary>
    /// The first file of <paramref name="quant"/> found in <see cref="LocalModelInfo.Repositories"/>, or null.
    /// Network errors other than "not found" from the last repository are thrown, so the caller can say why.
    /// </summary>
    public static async Task<HfFile?> ResolveAsync(HttpClient http, LocalModelInfo model, string quant, CancellationToken ct)
    {
        Exception? lastError = null;
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
                if (file != null)
                    return file;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                lastError = ex;
            }
        }

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

            // 0 = plain quantization, 1 = a variant such as UD-Q4_K_M; deeper folders rank lower.
            var prefix = stem[..index];
            var variant = prefix.EndsWith("UD-", StringComparison.OrdinalIgnoreCase) || prefix.EndsWith("UD_", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            var depth = path.Count(c => c == '/');
            candidates.Add((new HfFile(repository, path, size, sha.ToLowerInvariant()), variant * 100 + depth));
        }

        return candidates
            .OrderBy(c => c.Rank)
            .ThenBy(c => c.File.Path.Length)
            .Select(c => c.File)
            .FirstOrDefault();
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;
}
