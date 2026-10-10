using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using VoiceChatbot;
using Xunit;

public class LocalModelTests
{
    private const long GiB = LocalModelCatalog.GiB;

    [Fact]
    public void CatalogHasTheIncludedDefaultAndUniqueFiles()
    {
        var models = LocalModelCatalog.Models;
        Assert.Equal(LocalModelCatalog.DefaultId, LocalModelCatalog.Default.Id);
        Assert.True(LocalModelCatalog.Default.Included);
        Assert.Single(models, m => m.Included);
        Assert.Equal(models.Count, models.Select(m => m.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(models, m =>
        {
            Assert.EndsWith("Q4_K_M.gguf", m.FileName);
            Assert.NotEmpty(m.Repositories);
            Assert.True(m.MinCardGb >= m.VramGb || m.Included, $"{m.Id}: the minimum card should hold the model");
        });
        Assert.Equal("gemma-4-12b", LocalModelCatalog.Find("GEMMA-4-12B ")!.Id);
        Assert.Null(LocalModelCatalog.Find("nope"));
    }

    [Theory]
    [InlineData("gemma-4-e4b", 12, true, 32, ModelFit.Fits)]
    [InlineData("gemma-4-12b", 12, true, 32, ModelFit.Fits)]
    [InlineData("gemma-4-31b", 12, true, 32, ModelFit.PartlyOnProcessor)]
    [InlineData("gemma-4-31b", 8, true, 8, ModelFit.TooBig)]
    [InlineData("gemma-4-31b", 24, true, 32, ModelFit.Fits)]
    [InlineData("gemma-4-e4b", 0, false, 16, ModelFit.ProcessorOnly)]
    [InlineData("gemma-4-26b-a4b", 0, false, 16, ModelFit.TooBig)]
    public void EvaluateMatchesTheGraphicsCard(string id, int vramGb, bool discrete, int ramGb, ModelFit expected)
    {
        var model = LocalModelCatalog.Find(id)!;
        // Cards report a little less than their label.
        var gpu = vramGb > 0 ? new GpuInfo("GPU", vramGb * GiB - 200L * 1024 * 1024, discrete) : null;
        Assert.Equal(expected, LocalModelCatalog.Evaluate(model, gpu, ramGb * GiB));
    }

    [Fact]
    public void ContextFollowsTheSettingOrTheFreeVideoMemory()
    {
        var gpu = new GpuInfo("RTX", 24 * GiB, true);
        Assert.Equal(16384, LocalModelCatalog.ChooseContext(16384, 131072, gpu, 5 * GiB));
        Assert.Equal(4096, LocalModelCatalog.ChooseContext(1000, 131072, gpu, 5 * GiB));
        Assert.Equal(131072, LocalModelCatalog.ChooseContext(1_000_000, 131072, gpu, 5 * GiB));
        Assert.Equal(65536, LocalModelCatalog.ChooseContext(0, 131072, gpu, 5 * GiB));
        Assert.Equal(8192, LocalModelCatalog.ChooseContext(0, 131072, new GpuInfo("small", 6 * GiB, true), 5 * GiB));
        Assert.Equal(8192, LocalModelCatalog.ChooseContext(0, 131072, null, 5 * GiB));
    }

    [Fact]
    public void FindsADownloadBeforeTheIncludedCopy()
    {
        var root = Path.Combine(Path.GetTempPath(), "vc-models-" + Guid.NewGuid().ToString("N"));
        var downloads = Path.Combine(root, "downloads");
        var app = Path.Combine(root, "app");
        try
        {
            var model = LocalModelCatalog.Default;
            Assert.Null(LocalModelCatalog.FindInstalledFile(model, downloads, app));

            Directory.CreateDirectory(Path.Combine(app, LocalModelCatalog.BundledFolderName));
            var bundled = Path.Combine(app, LocalModelCatalog.BundledFolderName, model.FileName);
            File.WriteAllText(bundled, "gguf");
            Assert.Equal(bundled, LocalModelCatalog.FindInstalledFile(model, downloads, app));

            Directory.CreateDirectory(downloads);
            var downloaded = LocalModelCatalog.DownloadPath(model, downloads);
            File.WriteAllText(downloaded, "gguf");
            Assert.Equal(downloaded, LocalModelCatalog.FindInstalledFile(model, downloads, app));

            // An unfinished download (.part) is not a model.
            var other = LocalModelCatalog.Find("gemma-4-12b")!;
            File.WriteAllText(ModelDownloader.PartPath(LocalModelCatalog.DownloadPath(other, downloads)), "half");
            Assert.Null(LocalModelCatalog.FindInstalledFile(other, downloads, app));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EachModelHasItsOwnPictureSupportFileName()
    {
        Assert.Equal("gemma-4-E4B-it-mmproj.gguf", LocalModelCatalog.Default.ProjectorFileName);
        Assert.Equal("gemma-4-26B-A4B-it-mmproj.gguf", LocalModelCatalog.Find("gemma-4-26b-a4b")!.ProjectorFileName);
        var models = LocalModelCatalog.Models;
        Assert.Equal(models.Count, models.Select(m => m.ProjectorFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(models, m =>
        {
            Assert.EndsWith("-mmproj.gguf", m.ProjectorFileName);
            Assert.NotEqual(m.FileName, m.ProjectorFileName, StringComparer.OrdinalIgnoreCase);
            Assert.InRange(m.ApproxProjectorGb, 0.5, 2);
        });
        Assert.Equal(Path.Combine("dl", "gemma-4-E4B-it-mmproj.gguf"), LocalModelCatalog.ProjectorDownloadPath(LocalModelCatalog.Default, "dl"));
    }

    [Fact]
    public void FindsThePictureSupportFileLikeTheModel()
    {
        var root = Path.Combine(Path.GetTempPath(), "vc-models-" + Guid.NewGuid().ToString("N"));
        var downloads = Path.Combine(root, "downloads");
        var app = Path.Combine(root, "app");
        try
        {
            var model = LocalModelCatalog.Default;
            Assert.Null(LocalModelCatalog.FindInstalledProjector(model, downloads, app));

            // The included model without its picture support: the model is found, the projector is not.
            Directory.CreateDirectory(Path.Combine(app, LocalModelCatalog.BundledFolderName));
            File.WriteAllText(Path.Combine(app, LocalModelCatalog.BundledFolderName, model.FileName), "gguf");
            Assert.NotNull(LocalModelCatalog.FindInstalledFile(model, downloads, app));
            Assert.Null(LocalModelCatalog.FindInstalledProjector(model, downloads, app));

            var bundled = Path.Combine(app, LocalModelCatalog.BundledFolderName, model.ProjectorFileName);
            File.WriteAllText(bundled, "mmproj");
            Assert.Equal(bundled, LocalModelCatalog.FindInstalledProjector(model, downloads, app));

            // A downloaded one comes first; an unfinished (.part) or empty one does not count.
            Directory.CreateDirectory(downloads);
            var downloaded = LocalModelCatalog.ProjectorDownloadPath(model, downloads);
            File.WriteAllText(ModelDownloader.PartPath(downloaded), "half");
            Assert.Equal(bundled, LocalModelCatalog.FindInstalledProjector(model, downloads, app));
            File.WriteAllText(downloaded, "");
            Assert.Equal(bundled, LocalModelCatalog.FindInstalledProjector(model, downloads, app));
            File.WriteAllText(downloaded, "mmproj");
            Assert.Equal(downloaded, LocalModelCatalog.FindInstalledProjector(model, downloads, app));

            // A projector alone is not a model.
            var other = LocalModelCatalog.Find("gemma-4-12b")!;
            File.WriteAllText(LocalModelCatalog.ProjectorDownloadPath(other, downloads), "mmproj");
            Assert.Null(LocalModelCatalog.FindInstalledFile(other, downloads, app));
            Assert.NotNull(LocalModelCatalog.FindInstalledProjector(other, downloads, app));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    // unsloth names its projectors by precision only.
    [InlineData("mmproj-BF16.gguf|mmproj-F16.gguf|mmproj-F32.gguf", "mmproj-F16.gguf")]
    [InlineData("mmproj-F32.gguf|mmproj-BF16.gguf", "mmproj-BF16.gguf")]
    [InlineData("mmproj-F32.gguf|mmproj-Q8_0.gguf", "mmproj-Q8_0.gguf")]
    [InlineData("mmproj-other.gguf|mmproj-F32.gguf", "mmproj-F32.gguf")]
    // bartowski: "bf16" contains "f16" but is the second choice.
    [InlineData("mmproj-google_gemma-4-E4B-it-bf16.gguf|mmproj-google_gemma-4-E4B-it-f16.gguf", "mmproj-google_gemma-4-E4B-it-f16.gguf")]
    [InlineData("mmproj-google_gemma-4-E4B-it-bf16.gguf", "mmproj-google_gemma-4-E4B-it-bf16.gguf")]
    // ggml-org.
    [InlineData("mmproj-model-f16.gguf", "mmproj-model-f16.gguf")]
    [InlineData("mmproj-model.gguf", "mmproj-model.gguf")]
    // Split files are skipped; the top folder wins.
    [InlineData("mmproj-F16-00001-of-00002.gguf|mmproj-F16-00002-of-00002.gguf|mmproj-BF16.gguf", "mmproj-BF16.gguf")]
    [InlineData("vision/mmproj-F16.gguf|mmproj-F16.gguf", "mmproj-F16.gguf")]
    [InlineData("gemma-4-E4B-it-Q4_K_M.gguf|README.md", null)]
    public void PicksThePictureSupportFileByPrecision(string paths, string? expected)
    {
        var entries = paths.Split('|').Select(path => $$$"""{"type":"file","path":"{{{path}}}","size":134,"lfs":{"oid":"{{{new string('a', 64)}}}","size":900000000}}""");
        var tree = "[" + string.Join(",", entries) + """,{"type":"directory","path":"mmproj"}]""";

        var file = HuggingFaceFiles.PickProjectorFile("unsloth/gemma-4-E4B-it-GGUF", tree);

        Assert.Equal(expected, file?.Path);
        if (file != null)
        {
            Assert.Equal(900000000, file.Size);
            Assert.Equal(new string('a', 64), file.Sha256);
        }
    }

    [Fact]
    public void TheModelFileIsNeverAProjectorAndTheOtherWayRound()
    {
        var tree = """
        [
          {"type":"file","path":"mmproj-gemma-4-E4B-it-Q4_K_M.gguf","size":10},
          {"type":"file","path":"gemma-4-E4B-it-Q4_K_M.gguf","size":20}
        ]
        """;
        Assert.Equal("gemma-4-E4B-it-Q4_K_M.gguf", HuggingFaceFiles.PickQuantFile("r", tree, "Q4_K_M")!.Path);
        Assert.Equal("mmproj-gemma-4-E4B-it-Q4_K_M.gguf", HuggingFaceFiles.PickProjectorFile("r", tree)!.Path);
        Assert.Null(HuggingFaceFiles.PickProjectorFile("r", "{\"error\":\"Repository not found\"}"));
    }

    [Fact]
    public async Task ThePictureSupportFileComesFromTheModelsOwnRepository()
    {
        var model = LocalModelCatalog.Find("gemma-4-12b")!;
        var requests = new System.Collections.Concurrent.ConcurrentBag<string>();
        var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            requests.Add(url);
            // ggml-org has the model but no projector; unsloth has both; lmstudio-community another size.
            var json = url.Contains("/ggml-org/", StringComparison.Ordinal)
                ? """[{"type":"file","path":"gemma-4-12B-it-Q4_K_M.gguf","size":1,"lfs":{"size":7000000000}}]"""
                : url.Contains("/unsloth/", StringComparison.Ordinal)
                    ? """[{"type":"file","path":"gemma-4-12B-it-Q4_K_M.gguf","size":1,"lfs":{"size":7100000000}},{"type":"file","path":"mmproj-F16.gguf","size":1,"lfs":{"size":850000000}}]"""
                    : url.Contains("/lmstudio-community/", StringComparison.Ordinal)
                        ? """[{"type":"file","path":"gemma-4-12B-it-Q4_K_M.gguf","size":1,"lfs":{"size":7200000000}},{"type":"file","path":"mmproj-model-f16.gguf","size":1,"lfs":{"size":860000000}}]"""
                        : null;
            return json == null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        });
        using var http = new HttpClient(handler);

        // A new download: the first repository with the model, and only its own projector (none: text only).
        var fresh = await HuggingFaceFiles.ResolveAsync(http, model, "Q4_K_M", CancellationToken.None);
        Assert.Equal("ggml-org/gemma-4-12B-it-GGUF", fresh!.Model.Repository);
        Assert.Null(fresh.Projector);
        Assert.Single(requests);

        // A model already on this PC: the repository whose file has its exact size, with that repository's projector.
        requests.Clear();
        var installed = await HuggingFaceFiles.ResolveAsync(http, model, "Q4_K_M", CancellationToken.None, installedModelSize: 7100000000);
        Assert.Equal("unsloth/gemma-4-12B-it-GGUF", installed!.Model.Repository);
        Assert.Equal("unsloth/gemma-4-12B-it-GGUF", installed.Projector!.Repository);
        Assert.Equal("mmproj-F16.gguf", installed.Projector.Path);
        Assert.Equal(2, requests.Count);
        Assert.Equal(requests.Count, requests.Distinct().Count()); // One listing per repository.

        // No repository has that size: the first one found, as for a new download.
        var unknown = await HuggingFaceFiles.ResolveAsync(http, model, "Q4_K_M", CancellationToken.None, installedModelSize: 123);
        Assert.Equal("ggml-org/gemma-4-12B-it-GGUF", unknown!.Model.Repository);
        Assert.Null(unknown.Projector);
    }

    [Fact]
    public void PicksThePlainQ4KMFileFromARepositoryListing()
    {
        const string sha = "a3f1c2d4e5b6a7980f1e2d3c4b5a69788796a5b4c3d2e1f0a9b8c7d6e5f4a3b2";
        var tree = """
        [
          {"type":"directory","path":"BF16"},
          {"type":"file","path":"README.md","size":1200},
          {"type":"file","path":"mmproj-gemma-4-E4B-it-Q4_K_M.gguf","size":100,"lfs":{"oid":"SHA","size":100}},
          {"type":"file","path":"gemma-4-E4B-it-Q4_K_M_L.gguf","size":5,"lfs":{"oid":"SHA","size":5}},
          {"type":"file","path":"gemma-4-E4B-it-UD-Q4_K_M.gguf","size":6,"lfs":{"oid":"SHA","size":6}},
          {"type":"file","path":"gemma-4-E4B-it-Q4_K_M.gguf","size":134,"lfs":{"oid":"SHA","size":5690000000,"pointerSize":134}},
          {"type":"file","path":"gemma-4-E4B-it-Q8_0.gguf","size":134,"lfs":{"oid":"SHA","size":8000000000}}
        ]
        """.Replace("SHA", sha);

        var file = HuggingFaceFiles.PickQuantFile("ggml-org/gemma-4-E4B-it-GGUF", tree, "Q4_K_M")!;

        Assert.Equal("gemma-4-E4B-it-Q4_K_M.gguf", file.Path);
        Assert.Equal(5690000000, file.Size);
        Assert.Equal(sha, file.Sha256);
        Assert.Equal("https://huggingface.co/ggml-org/gemma-4-E4B-it-GGUF/resolve/main/gemma-4-E4B-it-Q4_K_M.gguf", file.Url);
    }

    [Fact]
    public void FallsBackToVariantsAndSkipsSplitFiles()
    {
        var tree = """
        [
          {"type":"file","path":"Q4_K_M/model-Q4_K_M-00001-of-00002.gguf","size":10},
          {"type":"file","path":"gemma-4-31B-it-UD-Q4_K_M.gguf","size":10,"lfs":{"oid":"not-a-sha","size":17000000000}}
        ]
        """;

        var file = HuggingFaceFiles.PickQuantFile("unsloth/gemma-4-31B-it-GGUF", tree, "Q4_K_M")!;

        Assert.Equal("gemma-4-31B-it-UD-Q4_K_M.gguf", file.Path);
        Assert.Equal(17000000000, file.Size);
        Assert.Equal("", file.Sha256);
        Assert.Null(HuggingFaceFiles.PickQuantFile("x/y", "[]", "Q4_K_M"));
        Assert.Null(HuggingFaceFiles.PickQuantFile("x/y", "{\"error\":\"Repository not found\"}", "Q4_K_M"));
    }

    [Fact]
    public async Task ResolveSkipsMissingRepositories()
    {
        var model = LocalModelCatalog.Find("gemma-4-12b")!;
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/ggml-org/", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""[{"type":"file","path":"gemma-4-12B-it-Q4_K_M.gguf","size":1,"lfs":{"size":7400000000}}]""")
            };
        });
        using var http = new HttpClient(handler);

        var files = await HuggingFaceFiles.ResolveAsync(http, model, "Q4_K_M", CancellationToken.None);

        Assert.Equal("unsloth/gemma-4-12B-it-GGUF", files!.Model.Repository);
        Assert.Equal(7400000000, files.Model.Size);
        Assert.Null(files.Projector); // That listing has no mmproj: text only, not an error.
    }

    [Fact]
    public async Task DownloadResumesFromThePartFileAndChecksTheHash()
    {
        var data = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var folder = Path.Combine(Path.GetTempPath(), "vc-dl-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(folder, "model.gguf");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(ModelDownloader.PartPath(target), data[..100_000]);

        long? requestedFrom = null;
        var handler = new FakeHandler(request =>
        {
            requestedFrom = request.Headers.Range?.Ranges.Single().From;
            var from = (int)(requestedFrom ?? 0);
            var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(data[from..])
            };
            return response;
        });
        using var http = new HttpClient(handler);
        try
        {
            await ModelDownloader.DownloadAsync(http, "https://example.test/model.gguf", target, data.Length, sha, null, CancellationToken.None);

            Assert.Equal(100_000, requestedFrom);
            Assert.Equal(data, File.ReadAllBytes(target));
            Assert.False(File.Exists(ModelDownloader.PartPath(target)));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadStartsOverWhenTheServerIgnoresTheRangeAndRejectsADamagedFile()
    {
        var data = new byte[50_000];
        new Random(5).NextBytes(data);
        var folder = Path.Combine(Path.GetTempPath(), "vc-dl-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(folder, "model.gguf");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(ModelDownloader.PartPath(target), new byte[10_000]);

        // Always the whole file with 200 OK.
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
        using var http = new HttpClient(handler);
        try
        {
            await ModelDownloader.DownloadAsync(http, "https://example.test/m", target, data.Length, null, null, CancellationToken.None);
            Assert.Equal(data, File.ReadAllBytes(target));

            var wrongSha = new string('0', 64);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                ModelDownloader.DownloadAsync(http, "https://example.test/m", target + "2", data.Length, wrongSha, null, CancellationToken.None));
            Assert.False(File.Exists(ModelDownloader.PartPath(target + "2")));
            Assert.False(File.Exists(target + "2"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ADownloadThatStopsSendingIsResumedOnANewConnection()
    {
        var data = new byte[40_000];
        new Random(9).NextBytes(data);
        var folder = Path.Combine(Path.GetTempPath(), "vc-dl-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(folder, "model.gguf");
        var requests = 0;
        var handler = new FakeHandler(request =>
        {
            requests++;
            var from = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
            // First connection: half the file, then silence.
            HttpContent content = requests == 1
                ? new StreamContent(new StallingStream(data[..20_000]))
                : new ByteArrayContent(data[from..]);
            return new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = content };
        });
        using var http = new HttpClient(handler);
        var previous = ModelDownloader.StallTimeout;
        ModelDownloader.StallTimeout = TimeSpan.FromMilliseconds(300);
        try
        {
            await ModelDownloader.DownloadAsync(http, "https://example.test/m", target, data.Length, null, null, CancellationToken.None);
            Assert.Equal(data, File.ReadAllBytes(target));
            Assert.Equal(2, requests);
        }
        finally
        {
            ModelDownloader.StallTimeout = previous;
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ADownloadFetchesTheModelAndItsPictureSupportUnderOneProgressBar()
    {
        var model = LocalModelCatalog.Find("gemma-4-12b")!;
        var modelData = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 3)).ToArray();
        var projectorData = Enumerable.Range(0, 50_000).Select(i => (byte)(i * 5)).ToArray();
        var root = Path.Combine(Path.GetTempPath(), "vc-dl-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "models");
        var app = Path.Combine(root, "app");
        using var http = new HttpClient(new FakeHandler(request => ServeRepository(request, modelData, projectorData)));
        using var downloads = new ModelDownloads(http, app);
        var finished = new List<ModelDownloadResult>();
        var progress = new System.Collections.Concurrent.ConcurrentQueue<(DownloadProgress Progress, string Status)>();
        downloads.Finished += result => finished.Add(result);
        downloads.Changed += () => progress.Enqueue((downloads.Progress, downloads.Status));
        try
        {
            var looked = await downloads.LookupAsync(model, folder, CancellationToken.None);
            Assert.Equal(modelData.Length, looked!.Model.Size);
            Assert.Equal(projectorData.Length, looked.Projector!.Size);

            var result = await downloads.StartAsync(model, folder);

            Assert.True(result.Success, result.Error);
            Assert.False(result.ProjectorOnly);
            Assert.Equal(modelData, File.ReadAllBytes(LocalModelCatalog.DownloadPath(model, folder)));
            Assert.Equal(projectorData, File.ReadAllBytes(LocalModelCatalog.ProjectorDownloadPath(model, folder)));
            Assert.Equal(LocalModelCatalog.ProjectorDownloadPath(model, folder), result.ProjectorPath);
            Assert.Single(finished);
            Assert.Null(downloads.Current);

            // One bar over both files: it never goes past the total and reaches it with the second file.
            var total = modelData.Length + projectorData.Length;
            var downloading = progress.Where(p => p.Progress.Phase == DownloadPhase.Downloading && p.Progress.Total > 0).ToList();
            Assert.All(downloading, p => Assert.Equal(total, p.Progress.Total));
            Assert.Contains(downloading, p => p.Progress.Done == total && p.Status.StartsWith("Downloading picture support for Gemma 4 12B", StringComparison.Ordinal));
            Assert.Contains(downloading, p => p.Progress.Done == modelData.Length && p.Status.StartsWith("Downloading Gemma 4 12B:", StringComparison.Ordinal));
            Assert.Equal("Gemma 4 12B downloaded.", downloads.Status);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AModelOnThisPcGetsOnlyItsPictureSupport()
    {
        var model = LocalModelCatalog.Default;
        var modelData = new byte[30_000];
        new Random(3).NextBytes(modelData);
        var projectorData = new byte[20_000];
        new Random(4).NextBytes(projectorData);
        var root = Path.Combine(Path.GetTempPath(), "vc-dl-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "models");
        var app = Path.Combine(root, "app");
        // The included model, without its picture support (an older installer).
        Directory.CreateDirectory(Path.Combine(app, LocalModelCatalog.BundledFolderName));
        File.WriteAllBytes(Path.Combine(app, LocalModelCatalog.BundledFolderName, model.FileName), modelData);
        var fileRequests = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var http = new HttpClient(new FakeHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/resolve/", StringComparison.Ordinal))
                fileRequests.Add(request.RequestUri.AbsoluteUri);
            return ServeRepository(request, modelData, projectorData);
        }));
        using var downloads = new ModelDownloads(http, app);
        try
        {
            var result = await downloads.StartAsync(model, folder);

            Assert.True(result.Success, result.Error);
            Assert.True(result.ProjectorOnly);
            Assert.Equal(Path.Combine(app, LocalModelCatalog.BundledFolderName, model.FileName), result.FilePath);
            Assert.Equal(LocalModelCatalog.ProjectorDownloadPath(model, folder), result.ProjectorPath);
            Assert.Equal(projectorData, File.ReadAllBytes(result.ProjectorPath));
            Assert.False(File.Exists(LocalModelCatalog.DownloadPath(model, folder)));
            Assert.All(fileRequests, url => Assert.Contains("mmproj", url));
            Assert.Equal("Picture support for Gemma 4 E4B downloaded.", downloads.Status);

            // Everything there: nothing more to fetch.
            fileRequests.Clear();
            var again = await downloads.StartAsync(model, folder);
            Assert.True(again.Success);
            Assert.Empty(fileRequests);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ADamagedPictureSupportDownloadFailsButKeepsTheModel()
    {
        var model = LocalModelCatalog.Find("gemma-4-31b")!;
        var modelData = new byte[10_000];
        new Random(6).NextBytes(modelData);
        var projectorData = new byte[5_000];
        new Random(7).NextBytes(projectorData);
        var root = Path.Combine(Path.GetTempPath(), "vc-dl-" + Guid.NewGuid().ToString("N"));
        var folder = Path.Combine(root, "models");
        // The projector's listed checksum does not match what is served.
        using var http = new HttpClient(new FakeHandler(request => ServeRepository(request, modelData, projectorData, projectorSha: new string('0', 64))));
        using var downloads = new ModelDownloads(http, Path.Combine(root, "app"));
        var finished = 0;
        downloads.Finished += _ => Interlocked.Increment(ref finished);
        try
        {
            var result = await downloads.StartAsync(model, folder);

            Assert.False(result.Success);
            Assert.False(result.Cancelled);
            Assert.Contains("damaged", result.Error);
            Assert.Equal(1, finished);
            Assert.Equal(modelData, File.ReadAllBytes(LocalModelCatalog.DownloadPath(model, folder)));
            Assert.Null(LocalModelCatalog.FindInstalledProjector(model, folder, Path.Combine(root, "app")));
            Assert.StartsWith("Download of picture support for Gemma 4 31B failed", downloads.Status);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    // A Hugging Face repository with a Q4_K_M model and an f16 projector: the tree listing (with sizes and
    // SHA-256s) for the first repository, and the files themselves.
    private static HttpResponseMessage ServeRepository(HttpRequestMessage request, byte[] modelData, byte[] projectorData, string? projectorSha = null)
    {
        var url = request.RequestUri!.AbsoluteUri;
        if (!url.Contains("/ggml-org/", StringComparison.Ordinal))
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        if (url.Contains("/api/models/", StringComparison.Ordinal))
        {
            var size = url.Contains("gemma-4-E4B", StringComparison.Ordinal) ? "E4B" : url.Contains("gemma-4-12B", StringComparison.Ordinal) ? "12B" : "31B";
            var modelSha = Convert.ToHexString(SHA256.HashData(modelData)).ToLowerInvariant();
            var mmprojSha = projectorSha ?? Convert.ToHexString(SHA256.HashData(projectorData)).ToLowerInvariant();
            var json = $$$"""
            [
              {"type":"file","path":"gemma-4-{{{size}}}-it-Q4_K_M.gguf","size":134,"lfs":{"oid":"{{{modelSha}}}","size":{{{modelData.Length}}}}},
              {"type":"file","path":"mmproj-model-f16.gguf","size":134,"lfs":{"oid":"{{{mmprojSha}}}","size":{{{projectorData.Length}}}}},
              {"type":"file","path":"mmproj-model-bf16.gguf","size":134,"lfs":{"oid":"{{{mmprojSha}}}","size":1}}
            ]
            """;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }

        var data = url.Contains("mmproj-model-f16.gguf", StringComparison.Ordinal) ? projectorData
            : url.EndsWith("Q4_K_M.gguf", StringComparison.Ordinal) ? modelData
            : null;
        if (data == null)
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        var from = (int)(request.Headers.Range?.Ranges.Single().From ?? 0);
        return new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new ByteArrayContent(data[from..]) };
    }

    // Returns its bytes, then waits forever (until cancelled) instead of ending.
    private sealed class StallingStream : Stream
    {
        private readonly MemoryStream _data;

        public StallingStream(byte[] data) => _data = new MemoryStream(data);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = _data.Read(buffer.Span);
            if (read > 0)
                return read;
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void ServerArgumentsDependOnTheLlamaCppVersion()
    {
        var newer = LlamaServerFeatures.FromHelp("-ngl, --gpu-layers N\n-fit, --fit [on|off]\n--jinja\n--no-webui");
        var args = LlamaServerArgs.Build(@"C:\Models\a b.gguf", 50123, "gemma-4-e4b", 16384, useGpu: true, newer);

        Assert.Equal(new[] { "-m", @"C:\Models\a b.gguf", "--host", "127.0.0.1", "--port", "50123", "--alias", "gemma-4-e4b",
            "-c", "16384", "-np", "1", "--jinja", "--no-webui" }, args);

        var older = LlamaServerArgs.Build("m.gguf", 1, "x", 8192, useGpu: true, LlamaServerFeatures.FromHelp("--jinja"));
        Assert.Equal(new[] { "-ngl", "999" }, older.TakeLast(2));
        Assert.DoesNotContain("--no-webui", older);

        var processor = LlamaServerArgs.Build("m.gguf", 1, "x", 8192, useGpu: false, newer);
        Assert.Equal(new[] { "-ngl", "0" }, processor.TakeLast(2));
        var processorOnly = LlamaServerArgs.Build("m.gguf", 1, "x", 8192, useGpu: false,
            LlamaServerFeatures.FromHelp("--device <dev1,dev2,..>  comma-separated list of devices"));
        Assert.Equal(new[] { "-ngl", "0", "--device", "none" }, processorOnly.TakeLast(4));
    }

    [Fact]
    public void PictureSupportIsLoadedWithMmprojWhenTheServerHasIt()
    {
        const string help = "-m, --model FNAME\n-mm, --mmproj FILE   path to a multimodal projector file\n--mmproj-url URL\n" +
                            "--no-mmproj-offload   do not offload multimodal projector to GPU\n--fit [on|off]\n--jinja\n--no-webui\n--device <dev1,dev2,..>";
        var features = LlamaServerFeatures.FromHelp(help);
        Assert.True(features.Mmproj);
        Assert.True(features.NoMmprojOffload);
        Assert.False(LlamaServerFeatures.FromHelp("--jinja").Mmproj);
        Assert.False(LlamaServerFeatures.None.Mmproj);

        var gpu = LlamaServerArgs.Build(@"C:\Models\m.gguf", 50123, "gemma-4-e4b", 16384, useGpu: true, features, @"C:\Models\m mmproj.gguf");
        Assert.Equal(new[] { "-m", @"C:\Models\m.gguf", "--host", "127.0.0.1", "--port", "50123", "--alias", "gemma-4-e4b",
            "-c", "16384", "-np", "1", "--mmproj", @"C:\Models\m mmproj.gguf", "--jinja", "--no-webui" }, gpu);

        // On the processor the projector stays there too.
        var processor = LlamaServerArgs.Build("m.gguf", 1, "x", 8192, useGpu: false, features, "p.gguf");
        Assert.Equal(new[] { "--mmproj", "p.gguf", "--no-mmproj-offload" }, processor.Skip(12).Take(3));
        Assert.Equal(new[] { "-ngl", "0", "--device", "none" }, processor.TakeLast(4));

        // No projector, or a llama-server that cannot load one: the same command line as before.
        Assert.Equal(LlamaServerArgs.Build("m.gguf", 1, "x", 8192, true, features), LlamaServerArgs.Build("m.gguf", 1, "x", 8192, true, features, ""));
        var old = LlamaServerFeatures.FromHelp("--jinja\n--fit");
        Assert.DoesNotContain("--mmproj", LlamaServerArgs.Build("m.gguf", 1, "x", 8192, true, old, "p.gguf"));
    }

    [Theory]
    [InlineData("llama_model_load: error loading model: failed to open file", "the model file could not be loaded (damaged or incomplete?)")]
    [InlineData("ggml_vulkan: Device memory allocation of size 4000000 failed. ErrorOutOfDeviceMemory", "not enough memory for this model")]
    [InlineData("couldn't bind HTTP server socket, hostname: 127.0.0.1, port: 8080", "its port is in use by another program")]
    [InlineData("llama_model_load: error loading model architecture: unknown model architecture: 'gemma9'", "this llama.cpp is too old for the model")]
    [InlineData("clip_init: failed to load model 'mmproj.gguf': unknown projector type\nmtmd_init_from_file: error: Failed to load CLIP model from mmproj.gguf\n" +
                "srv load_model: failed to load multimodal model, 'mmproj.gguf'", "its picture support file could not be loaded")]
    [InlineData("", "it stopped while loading")]
    public void DescribesWhyTheServerDidNotStart(string output, string expected)
    {
        Assert.Equal(expected, LlamaServerArgs.DescribeFailure(output));
    }

    [Theory]
    [InlineData("192.168.1.50", 8080, true, "http://192.168.1.50:8080/v1")]
    [InlineData("192.168.1.50:1234", 8080, true, "http://192.168.1.50:1234/v1")]
    [InlineData("http://host:8080/v1/", 8080, true, "http://host:8080/v1")]
    [InlineData("https://api.example.com", 8080, true, "https://api.example.com/v1")]
    [InlineData("localhost", 11434, false, "http://localhost:11434")]
    [InlineData("http://10.0.0.5:11434", 11434, false, "http://10.0.0.5:11434")]
    [InlineData("", 11434, false, "")]
    [InlineData("ftp://x", 11434, false, "")]
    public void NormalizesTypedServerAddresses(string input, int port, bool addV1, string expected)
    {
        Assert.Equal(expected, ChatProviders.NormalizeServerUrl(input, port, addV1));
    }

    [Fact]
    public void DescribesDownloadProgress()
    {
        var progress = new DownloadProgress(DownloadPhase.Downloading, 2 * GiB, 8 * GiB, 50 * 1024 * 1024);
        Assert.Equal("25% (2.0 GB of 8.0 GB, 50 MB/s, about 3 min left)", ModelDownloads.Describe(progress));
        Assert.Equal("50%", ModelDownloads.Describe(new DownloadProgress(DownloadPhase.Checking, 4, 8, 0)));
        Assert.True(ChatProviders.IsBuiltIn("built-in model"));
        Assert.True(ChatProviders.IsOpenAiCompatible("llama.cpp"));
        Assert.False(ChatProviders.IsOpenAiCompatible(ChatProviders.BuiltIn));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F' }, true)] // JPEG
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D }, true)] // PNG
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 1, 0 }, true)] // GIF
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0x36, 0, 0, 0 }, true)] // BMP
    [InlineData(new byte[] { (byte)'B', (byte)'M' }, true)] // short file, padded base64
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0x24, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, false)] // WebP
    [InlineData(new byte[] { 0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'h', (byte)'e', (byte)'i', (byte)'c' }, false)] // HEIC
    [InlineData(new byte[] { 0, 0, 0, 0x1C, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'a', (byte)'v', (byte)'i', (byte)'f' }, false)] // AVIF
    [InlineData(new byte[] { (byte)'I', (byte)'I', 0x2A, 0, 8, 0, 0, 0 }, false)] // TIFF
    [InlineData(new byte[] { (byte)'P', (byte)'N', (byte)'G' }, false)] // not a PNG signature
    public void KnowsWhichPicturesTheBuiltInModelReadsAsTheyAre(byte[] start, bool readsAsIs)
    {
        // A long picture: only its first bytes count.
        var picture = start.Concat(new byte[start.Length > 2 ? 100 : 0]).ToArray();
        Assert.Equal(readsAsIs, PictureFormats.BuiltInModelReadsAsIs(Convert.ToBase64String(picture)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("/9j")] // a JPEG start, cut off (not whole base64)
    public void TreatsBrokenPicturesAsNeedingConversion(string? base64)
    {
        Assert.False(PictureFormats.BuiltInModelReadsAsIs(base64));
    }

    [Fact]
    public void FlattensTransparentPicturesOntoWhite()
    {
        // Premultiplied BGRA: opaque red, fully transparent, half-transparent black, half-transparent blue.
        var pixels = new byte[]
        {
            0, 0, 255, 255,
            0, 0, 0, 0,
            0, 0, 0, 128,
            128, 0, 0, 128
        };
        PictureFormats.FlattenOntoWhite(pixels);
        Assert.Equal(new byte[]
        {
            0, 0, 255, 255,
            255, 255, 255, 255,
            127, 127, 127, 255,
            255, 127, 127, 255
        }, pixels);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
