using System.Globalization;
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

    [Fact]
    public void BuildsArgumentsWithInvariantDecimalPoint()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // A German Windows would otherwise write "0,45", which argparse rejects.
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var args = WakeWordProtocol.BuildArguments(@"C:\Program Files\Voice Chatbot\Tools\WakeWord\wakeword_server.py", "Alexa", 0.45);
            Assert.Equal("\"C:\\Program Files\\Voice Chatbot\\Tools\\WakeWord\\wakeword_server.py\" --model alexa --threshold 0.45", args);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void BuildArgumentsFallsBackToDefaults()
    {
        Assert.Equal("\"s.py\" --model hey_jarvis --threshold 0.90", WakeWordProtocol.BuildArguments("s.py", "unknown", 5));
    }

    [Fact]
    public void ParsesReadyEvent()
    {
        var evt = WakeWordProtocol.ParseEvent("{\"event\": \"ready\", \"model\": \"hey_jarvis\"}");

        Assert.NotNull(evt);
        Assert.Equal(WakeWordEventKind.Ready, evt!.Kind);
        Assert.Equal("hey_jarvis", evt.Model);
    }

    [Fact]
    public void ParsesWakeEventWithScore()
    {
        var evt = WakeWordProtocol.ParseEvent("  {\"event\":\"wake\",\"model\":\"alexa\",\"score\":0.873}\r\n");

        Assert.NotNull(evt);
        Assert.Equal(WakeWordEventKind.Wake, evt!.Kind);
        Assert.Equal("alexa", evt.Model);
        Assert.Equal(0.873, evt.Score, 6);
    }

    [Fact]
    public void ParsesScoreGivenAsString()
    {
        var evt = WakeWordProtocol.ParseEvent("{\"event\":\"wake\",\"score\":\"0.6\"}");

        Assert.Equal(0.6, evt!.Score, 6);
        Assert.Equal("", evt.Model);
    }

    [Fact]
    public void ParsesErrorEventWithCode()
    {
        var evt = WakeWordProtocol.ParseEvent(
            "{\"event\": \"error\", \"code\": \"missing_package\", \"message\": \"openWakeWord is not installed (No module named 'openwakeword').\"}");

        Assert.NotNull(evt);
        Assert.Equal(WakeWordEventKind.Error, evt!.Kind);
        Assert.Equal("missing_package", evt.Code);
        Assert.Contains("No module named", evt.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Downloading model...")]
    [InlineData("{not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"event\": \"progress\"}")]
    [InlineData("{\"model\": \"alexa\"}")]
    [InlineData("{\"event\": 5}")]
    public void IgnoresLinesThatAreNotEvents(string? line)
    {
        Assert.Null(WakeWordProtocol.ParseEvent(line));
    }

    [Theory]
    [InlineData("openWakeWord is not installed (No module named 'openwakeword').", null, null, true)]
    [InlineData("ModuleNotFoundError: No module named 'numpy'", null, null, true)]
    [InlineData("Python was not found; run without arguments to install from the Microsoft Store", null, null, true)]
    [InlineData("", 9009, null, true)]
    [InlineData("anything", null, "missing_package", true)]
    [InlineData("Could not load the 'alexa' wake word model: bad file", 3, "model_failed", false)]
    [InlineData("Microphone could not start", null, null, false)]
    [InlineData(null, 1, null, false)]
    public void RecognizesMissingInstall(string? message, int? exitCode, string? code, bool expected)
    {
        Assert.Equal(expected, WakeWordProtocol.LooksNotInstalled(message, exitCode, code));
    }

    [Fact]
    public void DescribesEachStatus()
    {
        Assert.Equal("Off", WakeWordProtocol.DescribeStatus(WakeWordStatus.Off, "alexa"));
        Assert.Equal("Starting...", WakeWordProtocol.DescribeStatus(WakeWordStatus.Starting, "alexa"));
        Assert.Equal("Listening for \"hey jarvis\"", WakeWordProtocol.DescribeStatus(WakeWordStatus.Listening, "hey_jarvis"));
        Assert.Equal("Listening for \"alexa\"", WakeWordProtocol.DescribeStatus(WakeWordStatus.Listening, "Alexa"));
        Assert.Contains("Paused", WakeWordProtocol.DescribeStatus(WakeWordStatus.Paused, "alexa"));
        Assert.Contains("install-wakeword.ps1", WakeWordProtocol.DescribeStatus(WakeWordStatus.NotInstalled, "alexa"));
        Assert.Equal("Error: mic unplugged", WakeWordProtocol.DescribeStatus(WakeWordStatus.Error, "alexa", " mic unplugged "));
        Assert.Equal("Error", WakeWordProtocol.DescribeStatus(WakeWordStatus.Error, "alexa"));
    }
}
