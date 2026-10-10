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

        var file = await HuggingFaceFiles.ResolveAsync(http, model, "Q4_K_M", CancellationToken.None);

        Assert.Equal("unsloth/gemma-4-12B-it-GGUF", file!.Repository);
        Assert.Equal(7400000000, file.Size);
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

    [Theory]
    [InlineData("llama_model_load: error loading model: failed to open file", "the model file could not be loaded (damaged or incomplete?)")]
    [InlineData("ggml_vulkan: Device memory allocation of size 4000000 failed. ErrorOutOfDeviceMemory", "not enough memory for this model")]
    [InlineData("couldn't bind HTTP server socket, hostname: 127.0.0.1, port: 8080", "its port is in use by another program")]
    [InlineData("llama_model_load: error loading model architecture: unknown model architecture: 'gemma9'", "this llama.cpp is too old for the model")]
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

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
