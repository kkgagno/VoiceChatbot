using VoiceChatbot;
using Xunit;

public class WakeWordProtocolTests
{
    [Theory]
    [InlineData("hey_jarvis", "hey_jarvis")]
    [InlineData("Hey Jarvis", "hey_jarvis")]
    [InlineData(" ALEXA ", "alexa")]
    [InlineData("hey-mycroft", "hey_mycroft")]
    [InlineData("hey_rhasspy", "hey_rhasspy")]
    [InlineData("timer", "hey_jarvis")]
    [InlineData("", "hey_jarvis")]
    [InlineData(null, "hey_jarvis")]
    public void NormalizeModelMapsToKnownIds(string? input, string expected)
    {
        Assert.Equal(expected, WakeWordProtocol.NormalizeModel(input));
    }

    [Fact]
    public void OffersTheFourPretrainedModelsWithJarvisFirst()
    {
        Assert.Equal(new[] { "hey_jarvis", "alexa", "hey_mycroft", "hey_rhasspy" }, WakeWordProtocol.Models);
        Assert.Equal(WakeWordProtocol.DefaultModel, WakeWordProtocol.Models[0]);
    }

    [Fact]
    public void NamesModelsForSpeechAndDisplay()
    {
        Assert.Equal("hey jarvis", WakeWordProtocol.SpokenPhrase("hey_jarvis"));
        Assert.Equal("Hey Jarvis", WakeWordProtocol.DisplayName("hey_jarvis"));
        Assert.Equal("Alexa", WakeWordProtocol.DisplayName("alexa"));
        Assert.Equal("Hey Rhasspy", WakeWordProtocol.DisplayName("hey_rhasspy"));
    }

    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(0.0, 0.1)]
    [InlineData(1.0, 0.9)]
    [InlineData(double.NaN, 0.5)]
    [InlineData(double.PositiveInfinity, 0.5)]
    public void ClampsThreshold(double input, double expected)
    {
        Assert.Equal(expected, WakeWordProtocol.ClampThreshold(input), 6);
    }

    [Theory]
    [InlineData(0.5, 50)]
    [InlineData(0.3, 70)]
    [InlineData(0.8, 20)]
    [InlineData(0.0, 90)]
    [InlineData(1.0, 10)]
    public void ThresholdAndSensitivityAreMirrorImages(double threshold, int sensitivity)
    {
        Assert.Equal(sensitivity, WakeWordProtocol.ThresholdToSensitivity(threshold));
        Assert.Equal(WakeWordProtocol.ClampThreshold(threshold), WakeWordProtocol.SensitivityToThreshold(sensitivity), 6);
    }

    [Fact]
    public void SensitivityRoundsToTwoDecimalsAndStaysInRange()
    {
        Assert.Equal(0.35, WakeWordProtocol.SensitivityToThreshold(65.4), 6);
        Assert.Equal(0.1, WakeWordProtocol.SensitivityToThreshold(100), 6);
        Assert.Equal(0.9, WakeWordProtocol.SensitivityToThreshold(0), 6);
        Assert.Equal(0.5, WakeWordProtocol.SensitivityToThreshold(double.NaN), 6);
    }

    [Theory]
    [InlineData("hey_jarvis", "hey_jarvis_v0.1.onnx")]
    [InlineData("Alexa", "alexa_v0.1.onnx")]
    [InlineData("hey-rhasspy", "hey_rhasspy_v0.1.onnx")]
    [InlineData("unknown", "hey_jarvis_v0.1.onnx")]
    public void NamesTheBundledModelFile(string model, string expected)
    {
        Assert.Equal(expected, WakeWordProtocol.ModelFileName(model));
    }

    [Fact]
    public void BundlesAModelFileForEveryOfferedModel()
    {
        var folder = Path.Combine(FindRepoRoot(), "Resources", "Models", "WakeWord");
        foreach (var file in WakeWordProtocol.Models.Select(WakeWordProtocol.ModelFileName)
                     .Concat(new[] { "melspectrogram.onnx", "embedding_model.onnx" }))
            Assert.True(new FileInfo(Path.Combine(folder, file)).Length > 100_000, $"{file} is missing or empty");
    }

    [Fact]
    public void DescribesEachStatus()
    {
        Assert.Equal("Off", WakeWordProtocol.DescribeStatus(WakeWordStatus.Off, "alexa"));
        Assert.Equal("Loading the wake word model...", WakeWordProtocol.DescribeStatus(WakeWordStatus.Starting, "alexa"));
        Assert.Equal("Listening for \"hey jarvis\"", WakeWordProtocol.DescribeStatus(WakeWordStatus.Listening, "hey_jarvis"));
        Assert.Equal("Listening for \"alexa\"", WakeWordProtocol.DescribeStatus(WakeWordStatus.Listening, "Alexa"));
        Assert.Contains("Paused", WakeWordProtocol.DescribeStatus(WakeWordStatus.Paused, "alexa"));
        Assert.Equal("Error: mic unplugged", WakeWordProtocol.DescribeStatus(WakeWordStatus.Error, "alexa", " mic unplugged "));
        Assert.Equal("Error", WakeWordProtocol.DescribeStatus(WakeWordStatus.Error, "alexa"));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "VoiceChatbot.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("VoiceChatbot.csproj was not found above the test folder.");
    }
}
