using VoiceChatbot;
using Xunit;

public class TranscriberCommandTests
{
    [Theory]
    [InlineData("{ryzen}", false)]
    [InlineData("{ryzen} {input}", false)]
    [InlineData("call {ryzen} {input}", false)]
    [InlineData("", false)]
    [InlineData("call \"%USERPROFILE%\\VoiceChatbot\\tools\\ryzen-ai-whisper-transcribe.bat\" {input}", true)]
    [InlineData("whisper-npu.exe {INPUT}", true)]
    public void IsUsable(string command, bool expected) =>
        Assert.Equal(expected, TranscriberCommand.IsUsable(command));

    [Theory]
    [InlineData("call \"%USERPROFILE%\\VoiceChatbot\\tools\\ryzen-ai-whisper-transcribe.bat\" {input}", "%USERPROFILE%\\VoiceChatbot\\tools\\ryzen-ai-whisper-transcribe.bat")]
    [InlineData("C:\\tools\\npu.exe {input}", "C:\\tools\\npu.exe")]
    [InlineData("python -m whisper {input}", null)]
    public void GetProgramPath(string command, string? expected) =>
        Assert.Equal(expected, TranscriberCommand.GetProgramPath(command));

    [Fact]
    public void NotFound()
    {
        Assert.True(TranscriberCommand.IsNotFoundError("'{ryzen}' is not recognized as an internal or external command,", 1));
        Assert.True(TranscriberCommand.IsNotFoundError("", 9009));
        Assert.False(TranscriberCommand.IsNotFoundError("model failed to load", 1));
    }
}
