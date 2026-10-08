using VoiceChatbot;
using Xunit;

public class HermesCommandParserTests
{
    [Theory]
    [InlineData("start comfyui", HermesModelControlAction.StartComfyUi)]
    [InlineData("Start ComfyUI.", HermesModelControlAction.StartComfyUi)]
    [InlineData("launch comfy ui", HermesModelControlAction.StartComfyUi)]
    [InlineData("please open comfyui", HermesModelControlAction.StartComfyUi)]
    [InlineData("restart comfy", HermesModelControlAction.StartComfyUi)]
    [InlineData("run comfyui lan", HermesModelControlAction.StartComfyUi)]
    [InlineData("stop comfyui", HermesModelControlAction.StopComfyUi)]
    [InlineData("can you shut down comfy", HermesModelControlAction.StopComfyUi)]
    [InlineData("kill the comfyui server", HermesModelControlAction.StopComfyUi)]
    [InlineData("stop the current model", HermesModelControlAction.StopLlama)]
    [InlineData("kill llama.cpp", HermesModelControlAction.StopLlama)]
    [InlineData("unload the model", HermesModelControlAction.StopLlama)]
    [InlineData("stop all llama servers", HermesModelControlAction.StopLlama)]
    [InlineData("Shut down the running model please", HermesModelControlAction.StopLlama)]
    public void ComfyAndStopCommands(string prompt, HermesModelControlAction expected)
    {
        Assert.True(HermesCommandParser.TryMatchModelControl(prompt, out var target));
        Assert.Equal(expected, target.Action);
    }

    [Theory]
    [InlineData("start gpt-oss 120b", "/mnt/c/llama.cpp/start-gpt-oss-120b.bat", 8084)]
    [InlineData("switch to gpt oss", "/mnt/c/llama.cpp/start-gpt-oss-120b.bat", 8084)]
    [InlineData("run gemma", "/mnt/c/llama.cpp/start-gemma4.bat", 8080)]
    [InlineData("load the gemma 26b a4b model", "/mnt/c/llama.cpp/start-gemma4-26b-a4b.bat", 8080)]
    [InlineData("start gemma 26b speculative", "/mnt/c/llama.cpp/start-gemma4-26b-a4b-speculative.bat", 8080)]
    [InlineData("launch gemma 12b", "/mnt/c/llama.cpp/start-gemma4-12b.bat", 8083)]
    [InlineData("start gemma e4b", "/mnt/c/llama.cpp/start-gemma4-4b.bat", 8081)]
    [InlineData("start gemma 4b", "/mnt/c/llama.cpp/start-gemma4-4b.bat", 8081)]
    [InlineData("start gemma speculative", "/mnt/c/llama.cpp/start-gemma4-speculative.bat", 8080)]
    [InlineData("start gemma 31b gpu", "/mnt/c/llama.cpp/start-gemma4-gpu.bat", 8080)]
    [InlineData("switch the model to qwen", "/mnt/c/llama.cpp/start-qwen3.6-27b-q8.bat", 8081)]
    [InlineData("change model to lfm", "/mnt/c/llama.cpp/start-lfm2.5-8b.bat", 8083)]
    [InlineData("start mistral text only", "/mnt/c/llama.cpp/start-mistral-medium-3.5-textonly.bat", 8082)]
    [InlineData("Could you please start the mistral medium 3.5 model", "/mnt/c/llama.cpp/start-mistral-medium-3.5.bat", 8082)]
    [InlineData("switch from qwen to gemma", "/mnt/c/llama.cpp/start-gemma4.bat", 8080)]
    [InlineData("go ahead and load qwen3.6 27b q8 on llama.cpp", "/mnt/c/llama.cpp/start-qwen3.6-27b-q8.bat", 8081)]
    [InlineData("start gemma 26b with speculative decoding", "/mnt/c/llama.cpp/start-gemma4-26b-a4b-speculative.bat", 8080)]
    [InlineData("load gemma using the gpu", "/mnt/c/llama.cpp/start-gemma4-gpu.bat", 8080)]
    [InlineData("start gemma in draft mode", "/mnt/c/llama.cpp/start-gemma4-speculative.bat", 8080)]
    [InlineData("I want you to start gemma", "/mnt/c/llama.cpp/start-gemma4.bat", 8080)]
    [InlineData("I'd like you to switch to qwen", "/mnt/c/llama.cpp/start-qwen3.6-27b-q8.bat", 8081)]
    public void StartModelCommands(string prompt, string batch, int port)
    {
        Assert.True(HermesCommandParser.TryMatchModelControl(prompt, out var target));
        Assert.Equal(HermesModelControlAction.StartLlama, target.Action);
        Assert.Equal(batch, target.BatchPath);
        Assert.Equal(port, target.Port);
    }

    [Theory]
    // Questions and other requests that only mention ComfyUI or a model go to the Hermes agent.
    [InlineData("what version of ComfyUI is installed?")]
    [InlineData("check if comfy is running")]
    [InlineData("show me the comfyui logs")]
    [InlineData("why did the model stop?")]
    [InlineData("is gemma faster than qwen?")]
    [InlineData("I want to switch to qwen tomorrow, what do you think?")]
    [InlineData("stop the model and start comfyui")]
    [InlineData("start gemma and then summarize the logs")]
    [InlineData("start gemma with the logs from yesterday")]
    // "run <shell command>" is staged for approval, never matched as a model plan.
    [InlineData("run nvidia-smi")]
    [InlineData("run pkill -f comfy")]
    [InlineData("run ollama stop gemma")]
    [InlineData("run cat /mnt/c/llama.cpp/start-qwen.bat")]
    [InlineData("run ls /mnt/c/ComfyUI/models")]
    [InlineData("execute start gemma")]
    [InlineData("shell stop comfyui")]
    // Nothing to resolve.
    [InlineData("stop gemma")]
    [InlineData("start the model")]
    [InlineData("restart gemma")]
    [InlineData("start")]
    [InlineData("")]
    public void NotModelControl(string prompt)
    {
        Assert.False(HermesCommandParser.TryMatchModelControl(prompt, out _));
    }

    [Theory]
    [InlineData("run nvidia-smi", "nvidia-smi")]
    [InlineData("run pkill -f comfy", "pkill -f comfy")]
    [InlineData("execute ls -la /tmp", "ls -la /tmp")]
    [InlineData("shell df -h", "df -h")]
    [InlineData("terminal uptime", "uptime")]
    [InlineData("cli hermes --version", "hermes --version")]
    public void DirectSshCommands(string prompt, string expected)
    {
        Assert.True(HermesCommandParser.TryGetDirectSshCommand(prompt, out var command));
        Assert.Equal(expected, command);
    }

    [Theory]
    [InlineData("what is running?")]
    [InlineData("run")]
    [InlineData("")]
    public void NotDirectSshCommands(string prompt) =>
        Assert.False(HermesCommandParser.TryGetDirectSshCommand(prompt, out _));

    [Fact]
    public void ShellCommandsMentioningModelsAreStagedNotRun()
    {
        // The order the app uses: model plan first, then staging.
        const string prompt = "run ollama stop gemma";
        Assert.False(HermesCommandParser.TryMatchModelControl(prompt, out _));
        Assert.True(HermesCommandParser.TryGetDirectSshCommand(prompt, out var command));
        Assert.Equal("ollama stop gemma", command);
    }
}

public class HermesApprovalGateTests
{
    private static readonly DateTime T0 = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    private static HermesApprovalGate Staged(HermesCommandSource source)
    {
        var gate = new HermesApprovalGate();
        gate.Stage("run nvidia-smi", "nvidia-smi", source, T0);
        return gate;
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("Approve.")]
    [InlineData("approved")]
    [InlineData("I approve")]
    [InlineData("approve it")]
    [InlineData("approve the command please")]
    public void ExplicitApprovalWorksFromEitherDevice(string prompt)
    {
        foreach (var source in new[] { HermesCommandSource.Desktop, HermesCommandSource.Phone })
        {
            var gate = Staged(HermesCommandSource.Desktop);
            Assert.Equal(HermesApprovalDecision.Approved, gate.TryApprove(prompt, source, T0.AddSeconds(30), out var command));
            Assert.Equal("nvidia-smi", command);
            Assert.False(gate.HasStaged);
        }
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("Yes!")]
    [InlineData("yep")]
    [InlineData("go ahead")]
    [InlineData("do it")]
    [InlineData("confirm")]
    public void CasualApprovalOnlyFromTheStagingDevice(string prompt)
    {
        var gate = Staged(HermesCommandSource.Desktop);
        Assert.Equal(HermesApprovalDecision.NeedsExplicitApproval, gate.TryApprove(prompt, HermesCommandSource.Phone, T0.AddSeconds(10), out var command));
        Assert.Equal("", command);
        Assert.True(gate.HasStaged);

        Assert.Equal(HermesApprovalDecision.Approved, gate.TryApprove(prompt, HermesCommandSource.Desktop, T0.AddSeconds(20), out command));
        Assert.Equal("nvidia-smi", command);
    }

    [Fact]
    public void PhoneStagedCommandNeedsExplicitApprovalOnDesktop()
    {
        var gate = Staged(HermesCommandSource.Phone);
        Assert.Equal(HermesApprovalDecision.NeedsExplicitApproval, gate.Evaluate("yes", HermesCommandSource.Desktop, T0));
        Assert.Equal(HermesApprovalDecision.Approved, gate.Evaluate("yes", HermesCommandSource.Phone, T0));
        Assert.Equal(HermesApprovalDecision.Approved, gate.Evaluate("approve", HermesCommandSource.Desktop, T0));
    }

    [Fact]
    public void ExpiresAfterTwoMinutes()
    {
        var gate = Staged(HermesCommandSource.Desktop);
        Assert.Equal(TimeSpan.FromMinutes(2), HermesApprovalGate.Lifetime);
        Assert.Equal("nvidia-smi", gate.GetLiveCommand(T0.AddMinutes(2)));
        Assert.Equal("", gate.GetLiveCommand(T0.AddMinutes(2).AddSeconds(1)));
        Assert.Equal(HermesApprovalDecision.Expired, gate.Evaluate("approve", HermesCommandSource.Desktop, T0.AddMinutes(3)));
        Assert.Equal(HermesApprovalDecision.Expired, gate.Evaluate("ok", HermesCommandSource.Desktop, T0.AddMinutes(3)));

        Assert.Equal(HermesApprovalDecision.Expired, gate.TryApprove("approve", HermesCommandSource.Desktop, T0.AddMinutes(3), out var command));
        Assert.Equal("", command);
        Assert.False(gate.HasStaged);
        Assert.Equal(HermesApprovalDecision.NothingPending, gate.Evaluate("approve", HermesCommandSource.Desktop, T0.AddMinutes(3)));
    }

    [Fact]
    public void ApprovedCommandRunsOnce()
    {
        var gate = Staged(HermesCommandSource.Desktop);
        Assert.Equal(HermesApprovalDecision.Approved, gate.TryApprove("approve", HermesCommandSource.Desktop, T0, out _));
        Assert.Equal(HermesApprovalDecision.NothingPending, gate.TryApprove("approve", HermesCommandSource.Desktop, T0, out var second));
        Assert.Equal("", second);
    }

    [Theory]
    [InlineData("run nvidia-smi")]
    [InlineData("what is approved?")]
    [InlineData("approve the new model plan for next week")]
    [InlineData("")]
    public void OtherPromptsAreNotApprovals(string prompt)
    {
        var gate = Staged(HermesCommandSource.Desktop);
        Assert.Equal(HermesApprovalDecision.NotAnApproval, gate.TryApprove(prompt, HermesCommandSource.Desktop, T0, out _));
        Assert.True(gate.HasStaged);
    }

    [Fact]
    public void RestagingReplacesCommandAndResetsClock()
    {
        var gate = Staged(HermesCommandSource.Phone);
        gate.Stage("run uptime", "uptime", HermesCommandSource.Desktop, T0.AddMinutes(5));
        Assert.Equal(HermesCommandSource.Desktop, gate.StagedSource);
        Assert.Equal("run uptime", gate.StagedRequest);
        Assert.Equal(HermesApprovalDecision.Approved, gate.TryApprove("ok", HermesCommandSource.Desktop, T0.AddMinutes(6), out var command));
        Assert.Equal("uptime", command);
    }

    [Fact]
    public void CancelClearsEvenAfterExpiry()
    {
        var gate = Staged(HermesCommandSource.Desktop);
        Assert.Equal("", gate.GetLiveCommand(T0.AddMinutes(10)));
        Assert.Equal("run nvidia-smi", gate.StagedRequest);
        Assert.True(gate.TryCancel(out var request));
        Assert.Equal("run nvidia-smi", request);
        Assert.False(gate.HasStaged);
        Assert.Equal("", gate.StagedRequest);
        Assert.False(gate.TryCancel(out _));
    }

    [Fact]
    public void ClearDropsTheStagedCommand()
    {
        var gate = Staged(HermesCommandSource.Desktop);
        gate.Clear();
        Assert.False(gate.HasStaged);
        Assert.Equal(HermesApprovalDecision.NothingPending, gate.Evaluate("approve", HermesCommandSource.Desktop, T0));
    }

    [Theory]
    [InlineData("cancel", true)]
    [InlineData("Never mind.", true)]
    [InlineData("abort", true)]
    [InlineData("stop", true)]
    [InlineData("cancel my subscription", false)]
    public void CancelWords(string prompt, bool expected) =>
        Assert.Equal(expected, HermesApprovalGate.IsCancelWord(prompt));
}
