using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

public sealed class ComfyUiImageClient : IDisposable
{
    private const int DefaultVideoWidth = 768;
    private const int DefaultVideoHeight = 1344;
    private const string QwenEditTwoImageWorkflowName = "image_qwen_image_edit_2511_2";
    private const string Krea2WorkflowName = "image_krea2_turbo_t2i_OFFICIAL";

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(20)
    };

    public string BaseUrl { get; set; } = "http://localhost:8000";

    public async Task<IReadOnlyList<string>> ListKrea2LorasAsync(CancellationToken ct)
    {
        var objectInfo = await _http.GetFromJsonAsync<JsonObject>($"{NormalizeBaseUrl()}/object_info", ct)
            ?? new JsonObject();
        var loras = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var classEntry in objectInfo)
        {
            var classType = classEntry.Key;
            if (classEntry.Value is not JsonObject classInfo)
                continue;

            foreach (var inputName in EnumerateLikelyLoraInputs(classInfo))
            {
                if (!TryGetComboOptions(objectInfo, classType, inputName, out var options))
                    continue;

                foreach (var option in options)
                {
                    if (IsKrea2LoraOption(option))
                        loras.Add(option);
                }
            }
        }

        return loras.ToList();
    }

    public async Task<GeneratedImageResult> CreateQwenImageAsync(
        string prompt,
        int width,
        int height,
        int steps,
        CancellationToken ct)
    {
        var workflow = BuildQwenCreateWorkflow(prompt, width, height, Math.Max(1, steps));
        var image = await QueueAndWaitForImageAsync(workflow, "11", ct);
        var localPath = await DownloadImageAsync(image, "qwen-create", ct);

        return new GeneratedImageResult(
            localPath,
            prompt,
            "Qwen Image 2512",
            image.FileName,
            image.Subfolder,
            image.Type);
    }

    public async Task<GeneratedImageResult> CreateKrea2ImageAsync(
        string prompt,
        bool enableLora,
        string loraName,
        string aspectRatio,
        CancellationToken ct)
    {
        var workflow = await LoadWorkflowAsync(Krea2WorkflowName, ct);
        PatchKrea2Workflow(workflow, prompt, enableLora, loraName, aspectRatio);

        var image = await QueueAndWaitForImageAsync(workflow, null, ct);
        var localPath = await DownloadImageAsync(image, "krea2", ct);

        return new GeneratedImageResult(
            localPath,
            prompt,
            Krea2WorkflowName,
            image.FileName,
            image.Subfolder,
            image.Type);
    }

    public async Task<GeneratedImageResult> EditQwenImageAsync(
        string inputImagePath,
        string prompt,
        int steps,
        CancellationToken ct)
    {
        var uploadedName = await UploadImageAsync(inputImagePath, ct);
        var workflow = BuildQwenEditWorkflow(uploadedName, prompt, Math.Max(1, steps));
        var image = await QueueAndWaitForImageAsync(workflow, "15", ct);
        var localPath = await DownloadImageAsync(image, "qwen-edit", ct);

        return new GeneratedImageResult(
            localPath,
            prompt,
            "Qwen Image Edit 2511",
            image.FileName,
            image.Subfolder,
            image.Type);
    }

    public async Task<GeneratedImageResult> EditQwenImagesAsync(
        IReadOnlyList<string> inputImagePaths,
        string prompt,
        int steps,
        CancellationToken ct)
    {
        var validImages = inputImagePaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            .Take(2)
            .ToList();

        if (validImages.Count < 2)
            throw new InvalidOperationException("Two source images are required for the two-image Qwen edit workflow.");

        var uploadedNames = new List<string>();
        foreach (var imagePath in validImages)
            uploadedNames.Add(await UploadImageAsync(imagePath, ct));

        var workflow = await LoadWorkflowAsync(QwenEditTwoImageWorkflowName, ct);
        PatchQwenTwoImageEditWorkflow(workflow, uploadedNames[0], uploadedNames[1], prompt);

        var image = await QueueAndWaitForImageAsync(workflow, null, ct);
        var localPath = await DownloadImageAsync(image, "qwen-edit-mix", ct);

        return new GeneratedImageResult(
            localPath,
            prompt,
            QwenEditTwoImageWorkflowName,
            image.FileName,
            image.Subfolder,
            image.Type);
    }

    public async Task<GeneratedVideoResult> CreateLtxVideoAsync(
        string inputImagePath,
        string? inputAudioPath,
        string prompt,
        int seconds,
        int fps,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(inputImagePath) || !File.Exists(inputImagePath))
            throw new InvalidOperationException("A source image is required for video generation.");

        var uploadedImageName = await UploadImageAsync(inputImagePath, ct);
        var uploadedAudioName = !string.IsNullOrWhiteSpace(inputAudioPath) && File.Exists(inputAudioPath)
            ? await UploadInputFileAsync(inputAudioPath, ct)
            : "";
        var workflowName = string.IsNullOrWhiteSpace(uploadedAudioName)
            ? "video_ltx2_3_i2v"
            : "video_ltx2_3_ia2v";

        var workflow = await LoadWorkflowAsync(workflowName, ct);
        PatchVideoWorkflow(workflow, uploadedImageName, uploadedAudioName, prompt, seconds, fps);

        var media = await QueueAndWaitForMediaAsync(workflow, ct);
        var localPath = await DownloadMediaAsync(media, "ltx-video", "generated-videos", ct);

        return new GeneratedVideoResult(
            localPath,
            prompt,
            workflowName,
            seconds,
            fps,
            media.FileName,
            media.Subfolder,
            media.Type);
    }

    private async Task<string> UploadImageAsync(string imagePath, CancellationToken ct) =>
        await UploadInputFileAsync(imagePath, ct);

    private async Task<string> UploadInputFileAsync(string filePath, CancellationToken ct)
    {
        var extension = Path.GetExtension(filePath);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".png";
        var uploadName = $"voicechatbot-{DateTime.Now:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}{extension}";

        await using var fs = File.OpenRead(filePath);
        using var form = new MultipartFormDataContent();
        var filePart = new StreamContent(fs);
        filePart.Headers.ContentType = new MediaTypeHeaderValue(GetInputContentType(filePath));
        form.Add(filePart, "image", uploadName);
        form.Add(new StringContent("input"), "type");
        form.Add(new StringContent("true"), "overwrite");

        using var response = await _http.PostAsync($"{NormalizeBaseUrl()}/upload/image", form, ct);
        await EnsureComfySuccessAsync(response, $"uploading {Path.GetFileName(filePath)}", ct);
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
            ?? throw new InvalidOperationException("ComfyUI upload returned no response.");

        return json["name"]?.GetValue<string>()
            ?? throw new InvalidOperationException("ComfyUI upload did not return an input filename.");
    }

    private async Task<Dictionary<string, object>> LoadWorkflowAsync(string workflowName, CancellationToken ct)
    {
        var fileName = workflowName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? workflowName
            : workflowName + ".json";

        Exception? lastError = null;
        foreach (var relativePath in await GetCandidateWorkflowPathsAsync(fileName, ct))
        {
            var url = $"{NormalizeBaseUrl()}/api/userdata/{Uri.EscapeDataString(relativePath)}";
            try
            {
                var json = await _http.GetStringAsync(url, ct);
                var node = JsonNode.Parse(json) as JsonObject
                    ?? throw new InvalidOperationException("Workflow JSON was not an object.");
                return await ConvertWorkflowToPromptAsync(node, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException($"Could not load ComfyUI workflow '{workflowName}': {lastError?.Message}");
    }

    private async Task<List<string>> GetCandidateWorkflowPathsAsync(string fileName, CancellationToken ct)
    {
        var candidates = new List<string>
        {
            "workflows/" + fileName,
            "workflows/Krea2 Safe/" + fileName
        };

        try
        {
            await AddWorkflowMatchesFromDirectoryAsync("", fileName, candidates, ct);
        }
        catch
        {
            // Older or locked-down ComfyUI builds may not allow directory listing.
            // The direct candidate paths above still cover the common saved-workflow layout.
        }

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task AddWorkflowMatchesFromDirectoryAsync(
        string subdir,
        string fileName,
        List<string> candidates,
        CancellationToken ct)
    {
        var dir = string.IsNullOrWhiteSpace(subdir)
            ? "workflows"
            : "workflows/" + subdir.Trim('/').Replace('\\', '/');
        var url = $"{NormalizeBaseUrl()}/api/userdata?dir={Uri.EscapeDataString(dir)}";
        var entries = await _http.GetFromJsonAsync<List<string>>(url, ct) ?? new List<string>();

        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
                continue;

            var normalizedEntry = entry.Replace('\\', '/').Trim('/');
            var relativePath = string.IsNullOrWhiteSpace(subdir)
                ? normalizedEntry
                : subdir.Trim('/').Replace('\\', '/') + "/" + normalizedEntry;

            if (normalizedEntry.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(Path.GetFileName(normalizedEntry), fileName, StringComparison.OrdinalIgnoreCase))
                    candidates.Add("workflows/" + relativePath);
            }
            else
            {
                await AddWorkflowMatchesFromDirectoryAsync(relativePath, fileName, candidates, ct);
            }
        }
    }

    private async Task<ComfyImageRef> QueueAndWaitForImageAsync(Dictionary<string, object> workflow, string? saveNodeId, CancellationToken ct)
    {
        var request = new
        {
            prompt = workflow,
            client_id = "voicechatbot-qwen-image"
        };

        using var response = await _http.PostAsJsonAsync($"{NormalizeBaseUrl()}/prompt", request, ct);
        await EnsureComfySuccessAsync(response, "queueing image workflow", ct);
        var queued = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
            ?? throw new InvalidOperationException("ComfyUI queue returned no response.");
        var promptId = queued["prompt_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("ComfyUI queue did not return a prompt id.");

        for (var i = 0; i < 360; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            var history = await _http.GetFromJsonAsync<JsonObject>($"{NormalizeBaseUrl()}/history/{promptId}", ct);
            if (history is null || !history.TryGetPropertyValue(promptId, out var promptNode) || promptNode is not JsonObject promptObj)
                continue;

            var outputs = promptObj["outputs"] as JsonObject;
            var firstImage = !string.IsNullOrWhiteSpace(saveNodeId)
                ? (outputs?[saveNodeId] as JsonObject)?["images"] as JsonArray
                : null;
            var imageObject = firstImage?.OfType<JsonObject>().FirstOrDefault() ?? FindFirstImage(outputs);
            if (imageObject is not null)
            {
                return new ComfyImageRef(
                    imageObject["filename"]?.GetValue<string>() ?? "",
                    imageObject["subfolder"]?.GetValue<string>() ?? "",
                    imageObject["type"]?.GetValue<string>() ?? "output");
            }

            var status = promptObj["status"] as JsonObject;
            var completed = status?["completed"]?.GetValue<bool>() == true;
            if (completed)
                throw new InvalidOperationException("ComfyUI completed but did not return an image.");
        }

        throw new TimeoutException("Timed out waiting for ComfyUI image output.");
    }

    private async Task<ComfyMediaRef> QueueAndWaitForMediaAsync(Dictionary<string, object> workflow, CancellationToken ct)
    {
        var request = new
        {
            prompt = workflow,
            client_id = "voicechatbot-comfy-video"
        };

        using var response = await _http.PostAsJsonAsync($"{NormalizeBaseUrl()}/prompt", request, ct);
        await EnsureComfySuccessAsync(response, "queueing video workflow", ct);
        var queued = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: ct)
            ?? throw new InvalidOperationException("ComfyUI queue returned no response.");
        var promptId = queued["prompt_id"]?.GetValue<string>()
            ?? throw new InvalidOperationException("ComfyUI queue did not return a prompt id.");

        for (var i = 0; i < 900; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            var history = await _http.GetFromJsonAsync<JsonObject>($"{NormalizeBaseUrl()}/history/{promptId}", ct);
            if (history is null || !history.TryGetPropertyValue(promptId, out var promptNode) || promptNode is not JsonObject promptObj)
                continue;

            var outputs = promptObj["outputs"] as JsonObject;
            var media = FindFirstMedia(outputs);
            if (media is not null)
                return media;

            var status = promptObj["status"] as JsonObject;
            var completed = status?["completed"]?.GetValue<bool>() == true;
            if (completed)
                throw new InvalidOperationException("ComfyUI completed but did not return a video.");
        }

        throw new TimeoutException("Timed out waiting for ComfyUI video output.");
    }

    private async Task<string> DownloadImageAsync(ComfyImageRef image, string prefix, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(image.FileName))
            throw new InvalidOperationException("ComfyUI returned an empty image filename.");

        var url = $"{NormalizeBaseUrl()}/view?filename={Uri.EscapeDataString(image.FileName)}&type={Uri.EscapeDataString(image.Type)}&subfolder={Uri.EscapeDataString(image.Subfolder)}";
        var bytes = await _http.GetByteArrayAsync(url, ct);

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceChatbot",
            "generated-images");
        Directory.CreateDirectory(dir);

        var extension = Path.GetExtension(image.FileName);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".png";

        var localPath = Path.Combine(dir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(localPath, bytes, ct);
        return localPath;
    }

    private async Task<string> DownloadMediaAsync(ComfyMediaRef media, string prefix, string folderName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(media.FileName))
            throw new InvalidOperationException("ComfyUI returned an empty media filename.");

        var url = $"{NormalizeBaseUrl()}/view?filename={Uri.EscapeDataString(media.FileName)}&type={Uri.EscapeDataString(media.Type)}&subfolder={Uri.EscapeDataString(media.Subfolder)}";
        var bytes = await _http.GetByteArrayAsync(url, ct);

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceChatbot",
            folderName);
        Directory.CreateDirectory(dir);

        var extension = Path.GetExtension(media.FileName);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".mp4";

        var localPath = Path.Combine(dir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(localPath, bytes, ct);
        return localPath;
    }

    private Dictionary<string, object> BuildQwenCreateWorkflow(string prompt, int width, int height, int steps)
    {
        return new()
        {
            ["1"] = Node("UNETLoader", new()
            {
                ["unet_name"] = "qwen_image_2512_fp8_e4m3fn.safetensors",
                ["weight_dtype"] = "default"
            }),
            ["2"] = Node("CLIPLoader", new()
            {
                ["clip_name"] = "qwen_2.5_vl_7b_fp8_scaled.safetensors",
                ["type"] = "qwen_image",
                ["device"] = "default"
            }),
            ["3"] = Node("VAELoader", new() { ["vae_name"] = "qwen_image_vae.safetensors" }),
            ["4"] = Node("EmptySD3LatentImage", new()
            {
                ["width"] = width,
                ["height"] = height,
                ["batch_size"] = 1
            }),
            ["5"] = Node("LoraLoaderModelOnly", new()
            {
                ["model"] = Link("1"),
                ["lora_name"] = "Qwen-Image-2512-Lightning-4steps-V1.0-fp32.safetensors",
                ["strength_model"] = 1.0
            }),
            ["6"] = Node("ModelSamplingAuraFlow", new()
            {
                ["model"] = Link("5"),
                ["shift"] = 3.1
            }),
            ["7"] = Node("CLIPTextEncode", new()
            {
                ["clip"] = Link("2"),
                ["text"] = prompt
            }),
            ["8"] = Node("CLIPTextEncode", new()
            {
                ["clip"] = Link("2"),
                ["text"] = ""
            }),
            ["9"] = Node("KSampler", new()
            {
                ["model"] = Link("6"),
                ["positive"] = Link("7"),
                ["negative"] = Link("8"),
                ["latent_image"] = Link("4"),
                ["seed"] = Random.Shared.NextInt64(1, long.MaxValue),
                ["steps"] = steps,
                ["cfg"] = 1.0,
                ["sampler_name"] = "euler",
                ["scheduler"] = "simple",
                ["denoise"] = 1.0
            }),
            ["10"] = Node("VAEDecode", new()
            {
                ["samples"] = Link("9"),
                ["vae"] = Link("3")
            }),
            ["11"] = Node("SaveImage", new()
            {
                ["images"] = Link("10"),
                ["filename_prefix"] = "VoiceChatbot_Qwen_Image_2512"
            })
        };
    }

    private Dictionary<string, object> BuildQwenEditWorkflow(string inputImageName, string prompt, int steps)
    {
        return new()
        {
            ["1"] = Node("LoadImage", new() { ["image"] = inputImageName }),
            ["2"] = Node("UNETLoader", new()
            {
                ["unet_name"] = "qwen_image_edit_2511_bf16.safetensors",
                ["weight_dtype"] = "default"
            }),
            ["3"] = Node("CLIPLoader", new()
            {
                ["clip_name"] = "qwen_2.5_vl_7b_fp8_scaled.safetensors",
                ["type"] = "qwen_image",
                ["device"] = "default"
            }),
            ["4"] = Node("VAELoader", new() { ["vae_name"] = "qwen_image_vae.safetensors" }),
            ["5"] = Node("FluxKontextImageScale", new() { ["image"] = Link("1") }),
            ["6"] = Node("VAEEncode", new()
            {
                ["pixels"] = Link("5"),
                ["vae"] = Link("4")
            }),
            ["7"] = Node("TextEncodeQwenImageEditPlus", new()
            {
                ["clip"] = Link("3"),
                ["vae"] = Link("4"),
                ["image1"] = Link("5"),
                ["prompt"] = prompt
            }),
            ["8"] = Node("TextEncodeQwenImageEditPlus", new()
            {
                ["clip"] = Link("3"),
                ["vae"] = Link("4"),
                ["image1"] = Link("5"),
                ["prompt"] = ""
            }),
            ["9"] = Node("FluxKontextMultiReferenceLatentMethod", new()
            {
                ["conditioning"] = Link("7"),
                ["reference_latents_method"] = "index_timestep_zero"
            }),
            ["10"] = Node("FluxKontextMultiReferenceLatentMethod", new()
            {
                ["conditioning"] = Link("8"),
                ["reference_latents_method"] = "index_timestep_zero"
            }),
            ["11"] = Node("ModelSamplingAuraFlow", new()
            {
                ["model"] = Link("2"),
                ["shift"] = 3.1
            }),
            ["12"] = Node("CFGNorm", new()
            {
                ["model"] = Link("11"),
                ["strength"] = 1.0
            }),
            ["13"] = Node("KSampler", new()
            {
                ["model"] = Link("12"),
                ["positive"] = Link("9"),
                ["negative"] = Link("10"),
                ["latent_image"] = Link("6"),
                ["seed"] = Random.Shared.NextInt64(1, long.MaxValue),
                ["steps"] = steps,
                ["cfg"] = 4.0,
                ["sampler_name"] = "euler",
                ["scheduler"] = "simple",
                ["denoise"] = 1.0
            }),
            ["14"] = Node("VAEDecode", new()
            {
                ["samples"] = Link("13"),
                ["vae"] = Link("4")
            }),
            ["15"] = Node("SaveImage", new()
            {
                ["images"] = Link("14"),
                ["filename_prefix"] = "VoiceChatbot_Qwen_Image_Edit_2511"
            })
        };
    }

    private static object[] Link(string nodeId, int outputSlot = 0) => new object[] { nodeId, outputSlot };

    private static Dictionary<string, object> Node(string classType, Dictionary<string, object> inputs, string? title = null)
    {
        var node = new Dictionary<string, object>
        {
            ["class_type"] = classType,
            ["inputs"] = inputs
        };

        if (!string.IsNullOrWhiteSpace(title))
            node["_meta"] = new Dictionary<string, object> { ["title"] = title };

        return node;
    }

    private string NormalizeBaseUrl() => BaseUrl.Trim().TrimEnd('/');

    private static async Task EnsureComfySuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = ExtractComfyError(body);
        var status = (int)response.StatusCode;
        var reason = string.IsNullOrWhiteSpace(response.ReasonPhrase)
            ? response.StatusCode.ToString()
            : response.ReasonPhrase;

        throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? $"ComfyUI failed while {action}: HTTP {status} {reason}."
            : $"ComfyUI failed while {action}: HTTP {status} {reason}. {detail}");
    }

    private static string ExtractComfyError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "";

        try
        {
            if (JsonNode.Parse(body) is JsonObject obj)
            {
                var parts = new List<string>();
                foreach (var key in new[] { "error", "message", "detail", "node_errors" })
                {
                    if (obj[key] is null)
                        continue;

                    var text = obj[key] is JsonValue value && value.TryGetValue<string>(out var s)
                        ? s
                        : obj[key]!.ToJsonString();
                    if (!string.IsNullOrWhiteSpace(text))
                        parts.Add($"{key}: {text}");
                }

                if (parts.Count > 0)
                    return TrimErrorText(string.Join(" ", parts));
            }
        }
        catch (JsonException)
        {
        }

        return TrimErrorText(body);
    }

    private static string TrimErrorText(string text)
    {
        text = WebUtility.HtmlDecode(text).Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= 900 ? text : text[..900] + "...";
    }

    private async Task<Dictionary<string, object>> ConvertWorkflowToPromptAsync(JsonObject workflow, CancellationToken ct)
    {
        if (workflow.All(kvp => kvp.Value is JsonObject obj && obj["class_type"] is not null))
            return ConvertApiPrompt(workflow);

        if (workflow["nodes"] is not JsonArray nodes)
            throw new InvalidOperationException("Workflow is not a ComfyUI API prompt and does not contain UI workflow nodes.");

        var objectInfo = await _http.GetFromJsonAsync<JsonObject>($"{NormalizeBaseUrl()}/object_info", ct)
            ?? new JsonObject();
        var linksById = BuildWorkflowLinkMap(workflow["links"] as JsonArray, nodes);
        var subgraphsById = GetSubgraphsById(workflow);
        var prompt = new Dictionary<string, object>();

        foreach (var nodeToken in nodes.OfType<JsonObject>())
        {
            var nodeId = GetNodeId(nodeToken);
            var classType = nodeToken["type"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(classType))
                continue;
            if (IsDisabledWorkflowNode(nodeToken))
                continue;

            if (subgraphsById.TryGetValue(classType, out var subgraph))
                RedirectSubgraphOutputLinks(nodeToken, subgraph, linksById);
        }

        foreach (var nodeToken in nodes.OfType<JsonObject>())
        {
            var nodeId = GetNodeId(nodeToken);
            var classType = nodeToken["type"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(nodeId) || string.IsNullOrWhiteSpace(classType))
                continue;
            if (IsDisabledWorkflowNode(nodeToken))
                continue;
            if (IsNonExecutableWorkflowNode(classType))
                continue;
            if (subgraphsById.TryGetValue(classType, out var subgraph))
            {
                ExpandSubgraphNode(nodeToken, subgraph, linksById, objectInfo, prompt);
                continue;
            }

            var inputs = new Dictionary<string, object>();
            if (nodeToken["inputs"] is JsonArray inputSlots)
            {
                foreach (var inputSlot in inputSlots.OfType<JsonObject>())
                {
                    var inputName = inputSlot["name"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(inputName))
                        continue;

                    var linkId = TryGetInt(inputSlot["link"]);
                    if (linkId.HasValue && linksById.TryGetValue(linkId.Value, out var link))
                        inputs[inputName] = Link(link.OriginNodeId, link.OriginSlot);
                }
            }

            var widgetNames = GetWidgetInputNames(nodeToken, objectInfo, classType).ToList();
            if (nodeToken["widgets_values"] is JsonArray values)
            {
                for (var i = 0; i < values.Count && i < widgetNames.Count; i++)
                {
                    var name = widgetNames[i];
                    if (!inputs.ContainsKey(name) && values[i] is not null)
                        inputs[name] = JsonNodeToWidgetObject(values[i]!, objectInfo, classType, name);
                }
            }

            NormalizeConvertedInputs(inputs, objectInfo, classType);

            prompt[nodeId] = Node(classType, inputs, GetWorkflowNodeTitle(nodeToken, classType));
        }

        if (prompt.Count == 0)
            throw new InvalidOperationException("Could not convert ComfyUI UI workflow to an API prompt.");

        return prompt;
    }

    private static Dictionary<string, object> ConvertApiPrompt(JsonObject workflow)
    {
        var prompt = new Dictionary<string, object>();
        foreach (var kvp in workflow)
        {
            if (kvp.Value is not JsonObject node)
                continue;

            var classType = node["class_type"]?.GetValue<string>() ?? "";
            if (IsNonExecutableWorkflowNode(classType))
                continue;

            var inputObjects = new Dictionary<string, object>();
            if (node["inputs"] is JsonObject inputs)
            {
                foreach (var input in inputs)
                {
                    if (input.Value is null)
                        continue;
                    inputObjects[input.Key] = JsonNodeToObject(input.Value);
                }
            }

            var title = "";
            if (node["_meta"] is JsonObject meta)
                title = meta["title"]?.GetValue<string>() ?? "";

            prompt[kvp.Key] = Node(classType, inputObjects, title);
        }

        return prompt;
    }

    private static bool IsNonExecutableWorkflowNode(string classType)
    {
        return classType.Equals("MarkdownNote", StringComparison.OrdinalIgnoreCase)
            || classType.Equals("Note", StringComparison.OrdinalIgnoreCase)
            || classType.Equals("NotePlus", StringComparison.OrdinalIgnoreCase)
            || classType.Equals("PrimitiveNode", StringComparison.OrdinalIgnoreCase)
            || classType.Equals("Reroute", StringComparison.OrdinalIgnoreCase)
            || classType.Contains("StickyNote", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, JsonObject> GetSubgraphsById(JsonObject workflow)
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (workflow["definitions"]?["subgraphs"] is not JsonArray subgraphs)
            return result;

        foreach (var subgraph in subgraphs.OfType<JsonObject>())
        {
            var id = subgraph["id"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(id))
                result[id] = subgraph;
        }

        return result;
    }

    private static void RedirectSubgraphOutputLinks(
        JsonObject nodeToken,
        JsonObject subgraph,
        Dictionary<int, WorkflowLink> outerLinksById)
    {
        var nodeId = GetNodeId(nodeToken);
        if (string.IsNullOrWhiteSpace(nodeId))
            return;

        var internalLinksById = BuildWorkflowLinkMap(subgraph["links"] as JsonArray, subgraph["nodes"] as JsonArray);
        var outputs = subgraph["outputs"] as JsonArray;
        if (outputs is null)
            return;

        for (var outputSlot = 0; outputSlot < outputs.Count; outputSlot++)
        {
            if (outputs[outputSlot] is not JsonObject output)
                continue;

            var outputLinkId = (output["linkIds"] as JsonArray)?.Select(TryGetInt).FirstOrDefault(id => id.HasValue);
            if (!outputLinkId.HasValue || !internalLinksById.TryGetValue(outputLinkId.Value, out var internalOrigin))
                continue;

            foreach (var outerLinkId in outerLinksById
                .Where(kvp => kvp.Value.OriginNodeId == nodeId && kvp.Value.OriginSlot == outputSlot)
                .Select(kvp => kvp.Key)
                .ToList())
            {
                outerLinksById[outerLinkId] = new WorkflowLink(nodeId + "_" + internalOrigin.OriginNodeId, internalOrigin.OriginSlot);
            }
        }
    }

    private static void ExpandSubgraphNode(
        JsonObject nodeToken,
        JsonObject subgraph,
        Dictionary<int, WorkflowLink> outerLinksById,
        JsonObject objectInfo,
        Dictionary<string, object> prompt)
    {
        var nodeId = GetNodeId(nodeToken);
        if (string.IsNullOrWhiteSpace(nodeId) || subgraph["nodes"] is not JsonArray subgraphNodes)
            return;

        var prefix = nodeId + "_";
        var externalInputs = GetSubgraphExternalInputs(nodeToken, outerLinksById);
        var internalLinksById = BuildWorkflowLinkMap(subgraph["links"] as JsonArray, subgraphNodes);

        foreach (var subNodeToken in subgraphNodes.OfType<JsonObject>())
        {
            var subNodeId = GetNodeId(subNodeToken);
            var classType = subNodeToken["type"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(subNodeId) || string.IsNullOrWhiteSpace(classType))
                continue;
            if (IsDisabledWorkflowNode(subNodeToken))
                continue;
            if (IsNonExecutableWorkflowNode(classType))
                continue;

            var inputs = new Dictionary<string, object>();
            if (subNodeToken["inputs"] is JsonArray inputSlots)
            {
                foreach (var inputSlot in inputSlots.OfType<JsonObject>())
                {
                    var inputName = inputSlot["name"]?.GetValue<string>() ?? "";
                    if (string.IsNullOrWhiteSpace(inputName))
                        continue;

                    var linkId = TryGetInt(inputSlot["link"]);
                    if (!linkId.HasValue || !internalLinksById.TryGetValue(linkId.Value, out var link))
                        continue;

                    if (link.OriginNodeId == "-10")
                    {
                        if (externalInputs.TryGetValue(link.OriginSlot, out var externalLink))
                            inputs[inputName] = Link(externalLink.OriginNodeId, externalLink.OriginSlot);
                    }
                    else
                    {
                        inputs[inputName] = Link(prefix + link.OriginNodeId, link.OriginSlot);
                    }
                }
            }

            var widgetNames = GetWidgetInputNames(subNodeToken, objectInfo, classType).ToList();
            if (subNodeToken["widgets_values"] is JsonArray values)
            {
                for (var i = 0; i < values.Count && i < widgetNames.Count; i++)
                {
                    var name = widgetNames[i];
                    if (!inputs.ContainsKey(name) && values[i] is not null)
                        inputs[name] = JsonNodeToWidgetObject(values[i]!, objectInfo, classType, name);
                }
            }

            NormalizeConvertedInputs(inputs, objectInfo, classType);

            prompt[prefix + subNodeId] = Node(classType, inputs, GetWorkflowNodeTitle(subNodeToken, classType));
        }
    }

    private static string GetWorkflowNodeTitle(JsonObject nodeToken, string classType)
    {
        var title = nodeToken["title"]?.GetValue<string>() ?? "";
        if (!string.IsNullOrWhiteSpace(title))
            return title;

        return nodeToken["properties"]?["Node name for S&R"]?.GetValue<string>() ?? classType;
    }

    private static Dictionary<int, WorkflowLink> GetSubgraphExternalInputs(
        JsonObject nodeToken,
        Dictionary<int, WorkflowLink> outerLinksById)
    {
        var result = new Dictionary<int, WorkflowLink>();
        if (nodeToken["inputs"] is not JsonArray inputs)
            return result;

        for (var i = 0; i < inputs.Count; i++)
        {
            if (inputs[i] is not JsonObject input)
                continue;

            var linkId = TryGetInt(input["link"]);
            if (linkId.HasValue && outerLinksById.TryGetValue(linkId.Value, out var link))
                result[i] = link;
        }

        return result;
    }

    private static bool IsDisabledWorkflowNode(JsonObject node)
    {
        return TryGetInt(node["mode"]) == 4;
    }

    private static Dictionary<int, WorkflowLink> BuildWorkflowLinkMap(JsonArray? links, JsonArray? nodes = null)
    {
        var map = new Dictionary<int, WorkflowLink>();
        if (links is null)
            return map;

        foreach (var link in links)
        {
            if (link is JsonArray arr && arr.Count >= 4)
            {
                var linkId = TryGetInt(arr[0]);
                var originNodeId = TryGetInt(arr[1]);
                var originSlot = TryGetInt(arr[2]) ?? 0;
                if (linkId.HasValue && originNodeId.HasValue)
                    map[linkId.Value] = new WorkflowLink(originNodeId.Value.ToString(), originSlot);
                continue;
            }

            if (link is JsonObject obj)
            {
                var linkId = TryGetInt(obj["id"]);
                var originNodeId = TryGetInt(obj["origin_id"]);
                var originSlot = TryGetInt(obj["origin_slot"]) ?? 0;
                if (linkId.HasValue && originNodeId.HasValue)
                    map[linkId.Value] = new WorkflowLink(originNodeId.Value.ToString(), originSlot);
            }
        }

        ResolveRerouteLinks(map, nodes);
        return map;
    }

    private static void ResolveRerouteLinks(Dictionary<int, WorkflowLink> linksById, JsonArray? nodes)
    {
        if (nodes is null)
            return;

        var rerouteInputLinks = new Dictionary<string, int>();
        foreach (var node in nodes.OfType<JsonObject>())
        {
            var nodeId = GetNodeId(node);
            var classType = node["type"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(nodeId) || !classType.Equals("Reroute", StringComparison.OrdinalIgnoreCase))
                continue;

            var inputLink = (node["inputs"] as JsonArray)?
                .OfType<JsonObject>()
                .Select(input => TryGetInt(input["link"]))
                .FirstOrDefault(id => id.HasValue);
            if (inputLink.HasValue)
                rerouteInputLinks[nodeId] = inputLink.Value;
        }

        foreach (var linkId in linksById.Keys.ToList())
            linksById[linkId] = ResolveRerouteOrigin(linksById[linkId], linksById, rerouteInputLinks, new HashSet<string>());
    }

    private static WorkflowLink ResolveRerouteOrigin(
        WorkflowLink link,
        Dictionary<int, WorkflowLink> linksById,
        Dictionary<string, int> rerouteInputLinks,
        HashSet<string> seen)
    {
        if (!rerouteInputLinks.TryGetValue(link.OriginNodeId, out var inputLinkId) || !seen.Add(link.OriginNodeId))
            return link;

        return linksById.TryGetValue(inputLinkId, out var previous)
            ? ResolveRerouteOrigin(previous, linksById, rerouteInputLinks, seen)
            : link;
    }

    private static IEnumerable<string> GetWidgetInputNames(JsonObject nodeToken, JsonObject objectInfo, string classType)
    {
        var names = GetWidgetInputNamesFromNode(nodeToken).ToList();
        return names.Count > 0 ? names : GetWidgetInputNamesFromObjectInfo(objectInfo, classType);
    }

    private static IEnumerable<string> GetWidgetInputNamesFromNode(JsonObject nodeToken)
    {
        if (nodeToken["inputs"] is not JsonArray inputSlots)
            yield break;

        foreach (var input in inputSlots.OfType<JsonObject>())
        {
            var widgetName = input["widget"]?["name"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrWhiteSpace(widgetName))
                yield return widgetName;
        }
    }

    private static IEnumerable<string> GetWidgetInputNamesFromObjectInfo(JsonObject objectInfo, string classType)
    {
        var inputInfo = objectInfo[classType]?["input"] as JsonObject;
        if (inputInfo is null)
            yield break;

        foreach (var sectionName in new[] { "required", "optional" })
        {
            if (inputInfo[sectionName] is not JsonObject section)
                continue;

            foreach (var kvp in section)
            {
                if (kvp.Value is not JsonArray spec || spec.Count == 0)
                    continue;

                var first = spec[0];
                if (first is JsonValue value && value.TryGetValue<string>(out var typeName))
                {
                    if (IsWidgetType(typeName))
                        yield return kvp.Key;
                    continue;
                }

                if (first is JsonArray)
                    yield return kvp.Key;
            }
        }
    }

    private static bool IsWidgetType(string typeName)
    {
        var normalized = typeName.Trim().ToUpperInvariant();
        return normalized is "STRING" or "INT" or "FLOAT" or "BOOLEAN" or "COMBO" or "SEED"
            || normalized.EndsWith("_NAME", StringComparison.Ordinal);
    }

    private static string GetNodeId(JsonObject node)
    {
        if (node["id"] is JsonValue idValue)
        {
            if (idValue.TryGetValue<int>(out var id))
                return id.ToString();
            if (idValue.TryGetValue<string>(out var text))
                return text;
        }

        return "";
    }

    private static int? TryGetInt(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<int>(out var intValue))
            return intValue;
        if (value.TryGetValue<long>(out var longValue))
            return (int)longValue;
        if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed))
            return parsed;
        return null;
    }

    private static object JsonNodeToObject(JsonNode node)
    {
        if (node is JsonArray arr && arr.Count == 2 && arr[0] is not null)
        {
            var slot = TryGetInt(arr[1]) ?? 0;
            if (arr[0] is JsonValue first)
            {
                if (first.TryGetValue<string>(out var idText))
                    return Link(idText, slot);
                if (first.TryGetValue<int>(out var idInt))
                    return Link(idInt.ToString(), slot);
            }
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var s)) return s;
            if (value.TryGetValue<int>(out var i)) return i;
            if (value.TryGetValue<long>(out var l)) return l;
            if (value.TryGetValue<double>(out var d)) return d;
            if (value.TryGetValue<bool>(out var b)) return b;
        }

        return JsonSerializer.Deserialize<object>(node.ToJsonString()) ?? "";
    }

    private static object JsonNodeToWidgetObject(JsonNode node, JsonObject objectInfo, string classType, string inputName)
    {
        if (TryGetComboOptions(objectInfo, classType, inputName, out var options) &&
            node is JsonValue value)
        {
            if (value.TryGetValue<int>(out var intIndex) && intIndex >= 0 && intIndex < options.Count)
                return options[intIndex];
            if (value.TryGetValue<long>(out var longIndex) && longIndex >= 0 && longIndex < options.Count)
                return options[(int)longIndex];
        }

        var converted = JsonNodeToObject(node);
        if (converted is string text)
        {
            if (TryGetInputType(objectInfo, classType, inputName, out var typeName))
            {
                if (typeName.Equals("FLOAT", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(text, out var parsedFloat))
                    return parsedFloat;
                if ((typeName.Equals("INT", StringComparison.OrdinalIgnoreCase) ||
                     typeName.Equals("SEED", StringComparison.OrdinalIgnoreCase)) &&
                    long.TryParse(text, out var parsedInt))
                    return parsedInt;
            }
        }

        return converted;
    }

    private static void NormalizeConvertedInputs(Dictionary<string, object> inputs, JsonObject objectInfo, string classType)
    {
        if (!classType.Equals("KSampler", StringComparison.OrdinalIgnoreCase))
            return;

        if (inputs.TryGetValue("sampler_name", out var samplerObj) &&
            TryGetComboOptions(objectInfo, classType, "sampler_name", out var samplerOptions))
        {
            if (samplerObj is int samplerIndex && samplerIndex >= 0 && samplerIndex < samplerOptions.Count)
                inputs["sampler_name"] = samplerOptions[samplerIndex];
            else if (samplerObj is long samplerLong && samplerLong >= 0 && samplerLong < samplerOptions.Count)
                inputs["sampler_name"] = samplerOptions[(int)samplerLong];
        }

        if (inputs.TryGetValue("scheduler", out var schedulerObj) &&
            TryGetComboOptions(objectInfo, classType, "scheduler", out var schedulerOptions))
        {
            var schedulerText = schedulerObj?.ToString() ?? "";
            if (!schedulerOptions.Contains(schedulerText) &&
                inputs.TryGetValue("denoise", out var denoiseObj) &&
                schedulerOptions.Contains(denoiseObj?.ToString() ?? ""))
            {
                inputs["scheduler"] = denoiseObj?.ToString() ?? schedulerOptions[0];
                inputs["denoise"] = 1.0;
            }
        }

        if (inputs.TryGetValue("denoise", out var denoiseValue) &&
            denoiseValue is string denoiseText &&
            !double.TryParse(denoiseText, out _))
        {
            inputs["denoise"] = 1.0;
        }
    }

    private static bool TryGetInputType(JsonObject objectInfo, string classType, string inputName, out string typeName)
    {
        typeName = "";
        var spec = GetInputSpec(objectInfo, classType, inputName);
        if (spec?[0] is JsonValue value && value.TryGetValue<string>(out var text))
        {
            typeName = text;
            return true;
        }

        return false;
    }

    private static bool TryGetComboOptions(JsonObject objectInfo, string classType, string inputName, out List<string> options)
    {
        options = new List<string>();
        var spec = GetInputSpec(objectInfo, classType, inputName);
        if (spec?[0] is not JsonArray arr)
            return false;

        options = arr
            .OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var text) ? text : "")
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        return options.Count > 0;
    }

    private static IEnumerable<string> EnumerateLikelyLoraInputs(JsonObject classInfo)
    {
        var inputInfo = classInfo["input"] as JsonObject;
        if (inputInfo is null)
            yield break;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sectionName in new[] { "required", "optional" })
        {
            if (inputInfo[sectionName] is not JsonObject section)
                continue;

            foreach (var input in section)
            {
                var normalized = NormalizeWorkflowKey(input.Key);
                if ((normalized.Contains("lora") || normalized.Contains("loraname"))
                    && input.Value is JsonArray spec
                    && spec[0] is JsonArray)
                {
                    if (seen.Add(input.Key))
                        yield return input.Key;
                }
            }
        }
    }

    private static bool IsKrea2LoraOption(string option)
    {
        if (string.IsNullOrWhiteSpace(option))
            return false;

        var normalized = NormalizeWorkflowKey(option);
        if (normalized.StartsWith("krea2") || normalized.Contains("krea2safe"))
            return true;

        var parts = option
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Any(part =>
            part.StartsWith("krea2", StringComparison.OrdinalIgnoreCase) ||
            NormalizeWorkflowKey(part).StartsWith("krea2"));
    }

    private static JsonArray? GetInputSpec(JsonObject objectInfo, string classType, string inputName)
    {
        var inputInfo = objectInfo[classType]?["input"] as JsonObject;
        if (inputInfo is null)
            return null;

        foreach (var sectionName in new[] { "required", "optional" })
        {
            if (inputInfo[sectionName] is JsonObject section &&
                section[inputName] is JsonArray spec)
                return spec;
        }

        return null;
    }

    private static void PatchVideoWorkflow(Dictionary<string, object> workflow, string imageName, string audioName, string prompt, int seconds, int fps)
    {
        seconds = Math.Clamp(seconds, 1, 30);
        fps = Math.Clamp(fps, 1, 60);
        var frames = Math.Clamp(seconds * fps, 1, 720);

        foreach (var node in workflow.Values.OfType<Dictionary<string, object>>())
        {
            var classType = node.TryGetValue("class_type", out var classObj) ? classObj?.ToString() ?? "" : "";
            if (!node.TryGetValue("inputs", out var inputsObj) || inputsObj is not Dictionary<string, object> inputs)
                continue;

            var lowerClass = classType.ToLowerInvariant();
            var title = "";
            if (node.TryGetValue("_meta", out var metaObj)
                && metaObj is Dictionary<string, object> meta
                && meta.TryGetValue("title", out var titleObj))
            {
                title = titleObj?.ToString() ?? "";
            }
            var lowerTitle = title.ToLowerInvariant();
            foreach (var key in inputs.Keys.ToList())
            {
                var lowerKey = key.ToLowerInvariant();
                if (lowerClass.Contains("loadimage") && (lowerKey is "image" or "filename" or "file" || lowerKey.Contains("image")))
                    inputs[key] = imageName;

                if (!string.IsNullOrWhiteSpace(audioName)
                    && IsAudioLoaderInput(lowerClass, lowerKey))
                    inputs[key] = audioName;

                if (IsPromptInput(lowerClass, lowerTitle, lowerKey, inputs[key]))
                    inputs[key] = prompt;

                if (lowerKey is "seed" or "noise_seed")
                    inputs[key] = Random.Shared.NextInt64(1, long.MaxValue);

                if (lowerKey is "seconds" or "duration" or "duration_seconds" or "video_seconds")
                    inputs[key] = seconds;

                if (IsVideoWidthInput(lowerTitle, lowerKey))
                    inputs[key] = DefaultVideoWidth;

                if (IsVideoHeightInput(lowerTitle, lowerKey))
                    inputs[key] = DefaultVideoHeight;

                if (lowerKey is "fps" or "frame_rate" or "framerate")
                    inputs[key] = fps;

                if (lowerKey is "frames" or "num_frames" or "frame_count" or "length" or "video_frames")
                    inputs[key] = frames;

                if (lowerKey is "filename_prefix" or "prefix")
                    inputs[key] = "VoiceChatbot_LTX2_3_IA2V";
            }
        }
    }

    private static void PatchKrea2Workflow(
        Dictionary<string, object> workflow,
        string prompt,
        bool enableLora,
        string loraName,
        string aspectRatio)
    {
        aspectRatio = NormalizeKrea2AspectRatio(aspectRatio);
        foreach (var node in workflow.Values.OfType<Dictionary<string, object>>())
        {
            var classType = node.TryGetValue("class_type", out var classObj) ? classObj?.ToString() ?? "" : "";
            if (!node.TryGetValue("inputs", out var inputsObj) || inputsObj is not Dictionary<string, object> inputs)
                continue;

            var title = "";
            if (node.TryGetValue("_meta", out var metaObj)
                && metaObj is Dictionary<string, object> meta
                && meta.TryGetValue("title", out var titleObj))
            {
                title = titleObj?.ToString() ?? "";
            }

            var lowerClass = classType.ToLowerInvariant();
            var lowerTitle = title.ToLowerInvariant();
            foreach (var key in inputs.Keys.ToList())
            {
                var lowerKey = key.ToLowerInvariant();

                if (IsPromptInput(lowerClass, lowerTitle, lowerKey, inputs[key]))
                    inputs[key] = prompt;

                if (lowerKey is "seed" or "noise_seed")
                    inputs[key] = Random.Shared.NextInt64(1, long.MaxValue);

                if (IsKrea2EnableLoraInput(lowerTitle, lowerKey))
                    inputs[key] = enableLora;

                if (enableLora
                    && !string.IsNullOrWhiteSpace(loraName)
                    && IsKrea2LoraNameInput(lowerClass, lowerTitle, lowerKey))
                {
                    inputs[key] = loraName;
                }

                if (IsKrea2AspectRatioInput(lowerClass, lowerTitle, lowerKey))
                    inputs[key] = aspectRatio;

                if (lowerKey is "filename_prefix" or "prefix")
                    inputs[key] = "VoiceChatbot_Krea2_Turbo";
            }
        }
    }

    private static bool IsKrea2EnableLoraInput(string lowerTitle, string lowerKey)
    {
        var normalizedKey = NormalizeWorkflowKey(lowerKey);
        var normalizedTitle = NormalizeWorkflowKey(lowerTitle);
        return normalizedKey is "enablelora" or "uselora" or "loraenabled" ||
               normalizedKey.Contains("enablelora") ||
               normalizedTitle.Contains("enablelora");
    }

    private static bool IsKrea2LoraNameInput(string lowerClass, string lowerTitle, string lowerKey)
    {
        var normalizedKey = NormalizeWorkflowKey(lowerKey);
        return normalizedKey is "loraname" or "lora" ||
               normalizedKey.Contains("loraname") ||
               (lowerClass.Contains("lora") && normalizedKey.Contains("name")) ||
               (lowerTitle.Contains("lora") && normalizedKey.Contains("name"));
    }

    private static bool IsKrea2AspectRatioInput(string lowerClass, string lowerTitle, string lowerKey)
    {
        var normalizedKey = NormalizeWorkflowKey(lowerKey);
        return normalizedKey is "aspectratio" or "ratio" ||
               normalizedKey.Contains("aspectratio") ||
               (lowerClass.Contains("resolution") && normalizedKey.Contains("ratio")) ||
               (lowerTitle.Contains("resolution") && normalizedKey.Contains("ratio"));
    }

    private static string NormalizeWorkflowKey(string value) =>
        Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]", "");

    private static string NormalizeKrea2AspectRatio(string value)
    {
        var trimmed = value.Trim();
        return trimmed switch
        {
            "1:1" => "1:1 (Square)",
            "3:2" => "3:2 (Photo)",
            "4:3" => "4:3 (Standard)",
            "16:9" => "16:9 (Widescreen)",
            "21:9" => "21:9 (Ultrawide)",
            "2:3" => "2:3 (Portrait Photo)",
            "3:4" => "3:4 (Portrait Standard)",
            "9:16" => "9:16 (Portrait Widescreen)",
            _ => string.IsNullOrWhiteSpace(trimmed) ? "1:1 (Square)" : trimmed
        };
    }

    private static void PatchQwenTwoImageEditWorkflow(Dictionary<string, object> workflow, string imageName1, string imageName2, string prompt)
    {
        var loadImageIndex = 0;

        foreach (var node in workflow.Values.OfType<Dictionary<string, object>>())
        {
            var classType = node.TryGetValue("class_type", out var classObj) ? classObj?.ToString() ?? "" : "";
            if (!node.TryGetValue("inputs", out var inputsObj) || inputsObj is not Dictionary<string, object> inputs)
                continue;

            var title = "";
            if (node.TryGetValue("_meta", out var metaObj)
                && metaObj is Dictionary<string, object> meta
                && meta.TryGetValue("title", out var titleObj))
            {
                title = titleObj?.ToString() ?? "";
            }

            var lowerClass = classType.ToLowerInvariant();
            var lowerTitle = title.ToLowerInvariant();

            if (lowerClass.Contains("loadimage"))
            {
                loadImageIndex++;
                var imageName = loadImageIndex == 1 ? imageName1 : imageName2;
                foreach (var key in inputs.Keys.ToList())
                {
                    var lowerKey = key.ToLowerInvariant();
                    if (lowerKey is "image" or "filename" or "file" || lowerKey.Contains("image"))
                        inputs[key] = imageName;
                }
            }

            foreach (var key in inputs.Keys.ToList())
            {
                var lowerKey = key.ToLowerInvariant();
                if (IsPromptInput(lowerClass, lowerTitle, lowerKey, inputs[key]))
                    inputs[key] = prompt;

                if (lowerKey is "filename_prefix" or "prefix")
                    inputs[key] = "VoiceChatbot_Qwen_Image_Edit_2511_2";
            }
        }
    }

    private static bool IsPromptInput(string lowerClass, string lowerTitle, string lowerKey, object value)
    {
        if (value is not string text)
            return false;
        if (lowerClass.Contains("negative") || lowerTitle.Contains("negative") || lowerKey.Contains("negative"))
            return false;
        if (LooksLikeNegativePrompt(text))
            return false;
        if (lowerKey is "prompt" or "positive" || lowerKey.Contains("positive_prompt") || lowerKey.Contains("prompt_text"))
            return true;
        if (lowerClass.Contains("primitivestring") && lowerKey is "value" && lowerTitle.Contains("prompt"))
            return true;

        if (string.IsNullOrWhiteSpace(text) && !lowerClass.Contains("positive"))
            return false;

        return lowerKey is "text" or "caption" or "description"
            || lowerKey.Contains("positive_prompt")
            || lowerKey.Contains("prompt_text");
    }

    private static bool IsVideoWidthInput(string lowerTitle, string lowerKey)
    {
        if (lowerKey is "width" or "resize_type.width")
            return true;

        return lowerTitle is "width" && lowerKey is "value";
    }

    private static bool IsVideoHeightInput(string lowerTitle, string lowerKey)
    {
        if (lowerKey is "height" or "resize_type.height")
            return true;

        return lowerTitle is "height" && lowerKey is "value";
    }

    private static bool LooksLikeNegativePrompt(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var lower = text.ToLowerInvariant();
        var matches = 0;
        foreach (var token in new[]
        {
            "blurry", "low quality", "watermark", "logo", "ugly", "deformed",
            "extra limbs", "duplicate", "flicker", "jitter", "unreadable text",
            "static pose", "frozen frame", "warped", "motion trails"
        })
        {
            if (lower.Contains(token))
                matches++;
        }

        return matches >= 2;
    }

    private static bool IsAudioLoaderInput(string lowerClass, string lowerKey)
    {
        if (!(lowerClass.Contains("loadaudio") || lowerClass is "audioinput"))
            return false;

        return lowerKey is "audio" or "audio_file" or "filename" or "file";
    }

    private static ComfyMediaRef? FindFirstMedia(JsonObject? outputs)
    {
        if (outputs is null)
            return null;

        foreach (var output in outputs.Select(kvp => kvp.Value).OfType<JsonObject>())
        {
            foreach (var key in new[] { "videos", "gifs", "images" })
            {
                if (output[key] is not JsonArray mediaArray)
                    continue;

                var media = mediaArray.OfType<JsonObject>().FirstOrDefault();
                if (media is null)
                    continue;

                return new ComfyMediaRef(
                    media["filename"]?.GetValue<string>() ?? "",
                    media["subfolder"]?.GetValue<string>() ?? "",
                    media["type"]?.GetValue<string>() ?? "output");
            }
        }

        return null;
    }

    private static JsonObject? FindFirstImage(JsonObject? outputs)
    {
        if (outputs is null)
            return null;

        foreach (var output in outputs.Select(kvp => kvp.Value).OfType<JsonObject>())
        {
            if (output["images"] is not JsonArray images)
                continue;

            var image = images.OfType<JsonObject>().FirstOrDefault();
            if (image is not null)
                return image;
        }

        return null;
    }

    private static string GetInputContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".wav" => "audio/wav",
            ".mp3" => "audio/mpeg",
            ".m4a" => "audio/mp4",
            ".flac" => "audio/flac",
            ".ogg" => "audio/ogg",
            _ => "image/png"
        };
    }

    public void Dispose() => _http.Dispose();

    private sealed record ComfyImageRef(string FileName, string Subfolder, string Type);
    private sealed record ComfyMediaRef(string FileName, string Subfolder, string Type);
    private sealed record WorkflowLink(string OriginNodeId, int OriginSlot);
}

public sealed record GeneratedImageResult(
    string LocalPath,
    string Prompt,
    string WorkflowName,
    string RemoteFileName,
    string RemoteSubfolder,
    string RemoteType);

public sealed record GeneratedVideoResult(
    string LocalPath,
    string Prompt,
    string WorkflowName,
    int Seconds,
    int Fps,
    string RemoteFileName,
    string RemoteSubfolder,
    string RemoteType);
