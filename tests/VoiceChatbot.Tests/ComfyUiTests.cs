using System.Text.Json.Nodes;
using VoiceChatbot;
using Xunit;

public class ComfyJobStatusTests
{
    [Fact]
    public void ExecutionErrorReportsTheNodeAndException()
    {
        var status = JsonNode.Parse("""
            {
              "status_str": "error",
              "completed": false,
              "messages": [
                ["execution_start", { "prompt_id": "p1" }],
                ["execution_cached", { "nodes": [] }],
                ["execution_error", {
                  "prompt_id": "p1", "node_id": "9", "node_type": "KSampler",
                  "exception_type": "torch.OutOfMemoryError",
                  "exception_message": "Allocation on device \n",
                  "traceback": ["..."]
                }]
              ]
            }
            """);

        Assert.Equal("KSampler (node 9) failed with torch.OutOfMemoryError: Allocation on device",
            ComfyJobStatus.DescribeFailure(status));
        Assert.False(ComfyJobStatus.IsCompleted(status));
    }

    [Fact]
    public void InterruptedJobIsAFailure()
    {
        var status = JsonNode.Parse("""
            { "status_str": "error", "completed": false,
              "messages": [["execution_interrupted", { "node_id": 3, "node_type": "VAEDecode" }]] }
            """);

        Assert.Equal("The job was interrupted at VAEDecode (node 3).", ComfyJobStatus.DescribeFailure(status));
    }

    [Fact]
    public void ErrorWithoutMessagesStillFails()
    {
        var status = JsonNode.Parse("""{ "status_str": "error", "completed": false, "messages": [] }""");
        Assert.NotNull(ComfyJobStatus.DescribeFailure(status));
    }

    [Fact]
    public void SuccessIsNotAFailure()
    {
        var status = JsonNode.Parse("""
            { "status_str": "success", "completed": true,
              "messages": [["execution_start", {}], ["execution_success", {}]] }
            """);

        Assert.Null(ComfyJobStatus.DescribeFailure(status));
        Assert.True(ComfyJobStatus.IsCompleted(status));
        Assert.Null(ComfyJobStatus.DescribeFailure(null));
    }

    [Theory]
    [InlineData("a", ComfyQueueState.Running)]
    [InlineData("b", ComfyQueueState.Pending)]
    [InlineData("c", ComfyQueueState.Absent)]
    public void QueueState(string promptId, ComfyQueueState expected)
    {
        var queue = JsonNode.Parse("""
            { "queue_running": [[5, "a", {}, {}, []]], "queue_pending": [[6, "b", {}, {}, []]] }
            """);

        Assert.Equal(expected, ComfyJobStatus.GetQueueState(queue, promptId));
    }

    [Fact]
    public void UnreadableQueueIsUnknown() =>
        Assert.Equal(ComfyQueueState.Unknown, ComfyJobStatus.GetQueueState(JsonNode.Parse("""{ "error": "x" }"""), "a"));
}

public class ComfyWorkflowLinksTests
{
    // 1 loader -> 2 LoRA (MODEL, CLIP) -> 3 sampler (MODEL) and 4 text encoder (CLIP)
    private const string LoraGraph = """
        {
          "nodes": [
            { "id": 1, "type": "CheckpointLoaderSimple", "mode": 0,
              "outputs": [{ "type": "MODEL" }, { "type": "CLIP" }] },
            { "id": 2, "type": "LoraLoader", "mode": MODE,
              "inputs": [{ "name": "model", "type": "MODEL", "link": 10 }, { "name": "clip", "type": "CLIP", "link": 11 }],
              "outputs": [{ "type": "MODEL" }, { "type": "CLIP" }] },
            { "id": 3, "type": "KSampler", "mode": 0, "inputs": [{ "name": "model", "type": "MODEL", "link": 12 }] },
            { "id": 4, "type": "CLIPTextEncode", "mode": 0, "inputs": [{ "name": "clip", "type": "CLIP", "link": 13 }] }
          ],
          "links": [
            [10, 1, 0, 2, 0, "MODEL"],
            [11, 1, 1, 2, 1, "CLIP"],
            [12, 2, 0, 3, 0, "MODEL"],
            [13, 2, 1, 4, 0, "CLIP"]
          ]
        }
        """;

    private static Dictionary<int, ComfyWorkflowLink> Map(string json)
    {
        var workflow = JsonNode.Parse(json)!.AsObject();
        return ComfyWorkflowLinks.BuildLinkMap(workflow["links"] as JsonArray, workflow["nodes"] as JsonArray);
    }

    [Fact]
    public void LinksFromABypassedNodeAreRewiredToItsInputs()
    {
        var map = Map(LoraGraph.Replace("MODE", "4"));

        Assert.Equal(("1", 0), (map[12].OriginNodeId, map[12].OriginSlot));
        Assert.Equal(("1", 1), (map[13].OriginNodeId, map[13].OriginSlot));
    }

    [Fact]
    public void LinksFromAnActiveNodeAreUnchanged()
    {
        var map = Map(LoraGraph.Replace("MODE", "0"));
        Assert.Equal(("2", 0), (map[12].OriginNodeId, map[12].OriginSlot));
    }

    [Fact]
    public void BypassMatchesByTypeWhenTheSlotDiffers()
    {
        // Output 0 is LATENT but input 0 is MODEL: the LATENT input (index 1) is passed through.
        var map = Map("""
            {
              "nodes": [
                { "id": 5, "type": "LatentUpscaleBy", "mode": 4,
                  "inputs": [{ "name": "model", "type": "MODEL", "link": 20 }, { "name": "samples", "type": "LATENT", "link": 21 }],
                  "outputs": [{ "type": "LATENT" }] }
              ],
              "links": [
                [20, 1, 0, 5, 0, "MODEL"],
                [21, 7, 0, 5, 1, "LATENT"],
                [22, 5, 0, 8, 0, "LATENT"]
              ]
            }
            """);

        Assert.Equal(("7", 0), (map[22].OriginNodeId, map[22].OriginSlot));
    }

    [Fact]
    public void BypassWithNothingConnectedDropsTheLink()
    {
        var map = Map("""
            {
              "nodes": [
                { "id": 5, "type": "ImageScale", "mode": 4,
                  "inputs": [{ "name": "image", "type": "IMAGE", "link": null }], "outputs": [{ "type": "IMAGE" }] }
              ],
              "links": [[22, 5, 0, 8, 0, "IMAGE"]]
            }
            """);

        Assert.False(map.ContainsKey(22));
    }

    [Fact]
    public void ChainsOfBypassesAndReroutesResolveToTheRealSource()
    {
        // 1 -> Reroute 2 -> bypassed 3 -> bypassed 4 -> 5
        var map = Map("""
            {
              "nodes": [
                { "id": 2, "type": "Reroute", "mode": 0, "inputs": [{ "name": "", "type": "*", "link": 30 }] },
                { "id": 3, "type": "ImageScale", "mode": 4, "inputs": [{ "name": "image", "type": "IMAGE", "link": 31 }], "outputs": [{ "type": "IMAGE" }] },
                { "id": 4, "type": "ImageSharpen", "mode": 4, "inputs": [{ "name": "image", "type": "IMAGE", "link": 32 }], "outputs": [{ "type": "IMAGE" }] }
              ],
              "links": [
                [30, 1, 0, 2, 0, "IMAGE"],
                [31, 2, 0, 3, 0, "IMAGE"],
                [32, 3, 0, 4, 0, "IMAGE"],
                [33, 4, 0, 5, 0, "IMAGE"]
              ]
            }
            """);

        Assert.Equal(("1", 0), (map[33].OriginNodeId, map[33].OriginSlot));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    public void MutedAndBypassedNodesAreExcluded(int mode, bool excluded)
    {
        var node = JsonNode.Parse($$"""{ "id": 1, "mode": {{mode}} }""")!.AsObject();
        Assert.Equal(excluded, ComfyWorkflowLinks.IsExcluded(node));
    }

    [Fact]
    public void InputsFromMissingNodesAreRemoved()
    {
        var prompt = new Dictionary<string, object>
        {
            ["1"] = new Dictionary<string, object> { ["class_type"] = "Loader", ["inputs"] = new Dictionary<string, object>() },
            ["3"] = new Dictionary<string, object>
            {
                ["class_type"] = "KSampler",
                ["inputs"] = new Dictionary<string, object>
                {
                    ["model"] = new object[] { "1", 0 },
                    ["positive"] = new object[] { "2", 0 }, // node 2 is muted
                    ["steps"] = 20
                }
            }
        };

        Assert.Equal(1, ComfyWorkflowLinks.RemoveDanglingLinks(prompt));
        var inputs = (Dictionary<string, object>)((Dictionary<string, object>)prompt["3"])["inputs"];
        Assert.True(inputs.ContainsKey("model"));
        Assert.False(inputs.ContainsKey("positive"));
        Assert.Equal(20, inputs["steps"]);
    }
}

public class LtxVideoSizingTests
{
    [Theory]
    [InlineData(1080, 1920, 768, 1344)]
    [InlineData(1920, 1080, 1344, 768)]
    [InlineData(1024, 1024, 992, 992)]
    [InlineData(1600, 1200, 1184, 864)]
    [InlineData(1200, 1600, 864, 1184)]
    [InlineData(4000, 1000, 1760, 576)]
    [InlineData(0, 0, 768, 1344)]
    public void FitsTheSourceAspect(int sourceWidth, int sourceHeight, int width, int height) =>
        Assert.Equal(new VideoSize(width, height), LtxVideoSizing.FitToSourceAspect(sourceWidth, sourceHeight));

    [Fact]
    public void SizesAreMultiplesOf32WithinTheBudget()
    {
        const long budget = LtxVideoSizing.DefaultWidth * LtxVideoSizing.DefaultHeight;
        for (var w = 100; w <= 4000; w += 37)
        {
            foreach (var h in new[] { 300, 720, 1080, 2000, 3000 })
            {
                var size = LtxVideoSizing.FitToSourceAspect(w, h);
                Assert.Equal(0, size.Width % 32);
                Assert.Equal(0, size.Height % 32);
                Assert.True((long)size.Width * size.Height <= budget);
                Assert.True((long)size.Width * size.Height >= budget * 0.9);
            }
        }
    }

    [Theory]
    [InlineData(6, 24, 6, 24, 144)]
    [InlineData(30, 24, 30, 24, 720)]
    [InlineData(30, 30, 24, 30, 720)]
    [InlineData(30, 50, 14, 50, 700)]
    [InlineData(100, 24, 30, 24, 720)]
    [InlineData(10, 120, 10, 60, 600)]
    [InlineData(0, 0, 1, 1, 1)]
    public void ReportsTheRealLength(int seconds, int fps, int realSeconds, int realFps, int frames)
    {
        var length = LtxVideoSizing.ClampLength(seconds, fps);

        Assert.Equal(new VideoLength(realSeconds, realFps, frames), length);
        Assert.True(length.Frames <= LtxVideoSizing.MaxFrames);
    }
}
