using System.Linq;
using VoiceChatbot;
using Xunit;

public class GlobalHotkeysTests
{
    [Theory]
    [InlineData("Off", "Off")]
    [InlineData("off", "Off")]
    [InlineData("Ctrl+Alt+Space", "Ctrl+Alt+Space")]
    [InlineData("ctrl + alt + space", "Ctrl+Alt+Space")]
    [InlineData("CTRL+SHIFT+SPACE", "Ctrl+Shift+Space")]
    [InlineData(" Ctrl+Alt+L ", "Ctrl+Alt+L")]
    [InlineData("", "Ctrl+Alt+Space")]
    [InlineData("   ", "Ctrl+Alt+Space")]
    [InlineData(null, "Ctrl+Alt+Space")]
    [InlineData("Ctrl+Alt+Delete", "Ctrl+Alt+Space")]
    public void Normalize(string? input, string expected) =>
        Assert.Equal(expected, GlobalHotkeys.Normalize(input));

    [Fact]
    public void ChoicesStartWithOffAndContainTheDefault()
    {
        Assert.Equal(GlobalHotkeys.Off, GlobalHotkeys.Choices[0]);
        Assert.Contains(GlobalHotkeys.Default, GlobalHotkeys.Choices);
        Assert.NotEqual(GlobalHotkeys.Off, GlobalHotkeys.Default);
        Assert.Equal(GlobalHotkeys.Choices.Count, GlobalHotkeys.Choices.Distinct().Count());
    }

    [Fact]
    public void EveryChoiceNormalizesToItself()
    {
        foreach (var choice in GlobalHotkeys.Choices)
            Assert.Equal(choice, GlobalHotkeys.Normalize(choice));
    }

    [Theory]
    [InlineData("Ctrl+Alt+Space", GlobalHotkeys.ModControl | GlobalHotkeys.ModAlt, 0x20u)]
    [InlineData("Ctrl+Shift+Space", GlobalHotkeys.ModControl | GlobalHotkeys.ModShift, 0x20u)]
    [InlineData("Ctrl+Alt+L", GlobalHotkeys.ModControl | GlobalHotkeys.ModAlt, 0x4Cu)]
    public void KeysForEachChoice(string choice, uint modifiers, uint virtualKey)
    {
        Assert.True(GlobalHotkeys.TryGetKeys(choice, out var keys));
        Assert.Equal(choice, keys.Name);
        Assert.Equal(modifiers, keys.Modifiers);
        Assert.Equal(virtualKey, keys.VirtualKey);
    }

    [Fact]
    public void OffHasNoKeys()
    {
        Assert.False(GlobalHotkeys.TryGetKeys(GlobalHotkeys.Off, out var keys));
        Assert.Equal(default, keys);
    }

    [Fact]
    public void BlankOrUnknownChoiceRegistersTheDefault()
    {
        Assert.True(GlobalHotkeys.TryGetKeys(null, out var keys));
        Assert.Equal(GlobalHotkeys.Default, keys.Name);
        Assert.True(GlobalHotkeys.TryGetKeys("Win+H", out keys));
        Assert.Equal(GlobalHotkeys.Default, keys.Name);
    }

    [Fact]
    public void EveryChoiceButOffHasDistinctKeys()
    {
        var keys = GlobalHotkeys.Choices
            .Where(c => c != GlobalHotkeys.Off)
            .Select(c => GlobalHotkeys.TryGetKeys(c, out var k) ? (k.Modifiers, k.VirtualKey) : default)
            .ToList();

        Assert.DoesNotContain(default, keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void RegisterModifiersAddNoRepeat()
    {
        GlobalHotkeys.TryGetKeys(GlobalHotkeys.CtrlAltSpace, out var keys);

        var modifiers = GlobalHotkeys.RegisterModifiers(keys);

        Assert.Equal(GlobalHotkeys.ModControl | GlobalHotkeys.ModAlt | GlobalHotkeys.ModNoRepeat, modifiers);
        Assert.Equal(0u, keys.Modifiers & GlobalHotkeys.ModNoRepeat);
    }

    [Fact]
    public void AlreadyRegisteredFailureSaysAnotherAppHasIt()
    {
        var message = GlobalHotkeys.DescribeRegisterFailure("ctrl+shift+space", GlobalHotkeys.ErrorHotkeyAlreadyRegistered);

        Assert.Contains("Ctrl+Shift+Space", message);
        Assert.Contains("already used by another app", message);
        Assert.Contains("Settings > App", message);
    }

    [Theory]
    [InlineData(5, "(Windows error 5)")]
    [InlineData(0, "hotkey.")]
    public void OtherFailuresGiveTheErrorCode(int error, string expected)
    {
        var message = GlobalHotkeys.DescribeRegisterFailure(GlobalHotkeys.CtrlAltL, error);

        Assert.Contains("Could not set the Ctrl+Alt+L hotkey", message);
        Assert.Contains(expected, message);
        Assert.DoesNotContain("already used", message);
    }
}

public class TrayTooltipTests
{
    [Theory]
    [InlineData(null, null, "Voice Chatbot Mini")]
    [InlineData("", "", "Voice Chatbot Mini")]
    [InlineData("Listening...", null, "Voice Chatbot Mini - Listening...")]
    [InlineData(null, "Ctrl+Alt+Space", "Voice Chatbot Mini (Ctrl+Alt+Space to talk)")]
    [InlineData("Speaking...", "Ctrl+Alt+L", "Voice Chatbot Mini - Speaking... (Ctrl+Alt+L to talk)")]
    [InlineData("  Two\r\n  lines ", "Off", "Voice Chatbot Mini - Two lines")]
    public void Format(string? status, string? hotkey, string expected) =>
        Assert.Equal(expected, TrayTooltip.Format(status, hotkey));

    [Fact]
    public void LongTextIsCutToTheNotifyIconLimit()
    {
        var text = TrayTooltip.Format(new string('a', 300), "Ctrl+Alt+Space");

        Assert.Equal(TrayTooltip.MaxLength, text.Length);
        Assert.EndsWith("...", text);
        Assert.StartsWith("Voice Chatbot Mini - aaa", text);
    }

    [Fact]
    public void CutNeverSplitsASurrogatePair()
    {
        // Place an emoji (two UTF-16 units) so it straddles the cut point.
        var prefix = "Voice Chatbot Mini - ";
        var status = new string('a', TrayTooltip.MaxLength - 3 - 1 - prefix.Length) + "\U0001F600" + new string('b', 50);

        var text = TrayTooltip.Format(status, null);

        Assert.True(text.Length <= TrayTooltip.MaxLength);
        Assert.False(char.IsHighSurrogate(text[^4]));
        Assert.EndsWith("a...", text);
    }
}
