using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VoiceChatbot;
using Xunit;

public class ThemePaletteTests
{
    [Theory]
    [InlineData("Dark", "Dark")]
    [InlineData("light", "Light")]
    [InlineData(" use windows setting ", "Use Windows setting")]
    [InlineData("", "Dark")]
    [InlineData(null, "Dark")]
    [InlineData("Solarized", "Dark")]
    public void Normalize_KeepsKnownChoicesAndFallsBackToDark(string? saved, string expected) =>
        Assert.Equal(expected, ThemePalette.Normalize(saved));

    [Theory]
    [InlineData("Dark", 1, false)]
    [InlineData("Light", 0, true)]
    [InlineData("Use Windows setting", 1, true)]
    [InlineData("Use Windows setting", 0, false)]
    [InlineData("Use Windows setting", null, false)]
    [InlineData("nonsense", 1, false)]
    public void IsLight_FollowsTheChoiceAndTheWindowsAppMode(string choice, int? appsUseLightTheme, bool expected) =>
        Assert.Equal(expected, ThemePalette.IsLight(choice, appsUseLightTheme));

    [Fact]
    public void BothPalettes_CoverEveryBrushInAppXaml()
    {
        var appXaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "App.xaml"));
        var xamlBrushes = Regex.Matches(appXaml, "<(?:SolidColorBrush|LinearGradientBrush) x:Key=\"(\\w+)\"")
            .Select(m => m.Groups[1].Value).OrderBy(k => k).ToList();

        Assert.NotEmpty(xamlBrushes);
        Assert.Equal(xamlBrushes, ThemePalette.DarkBrushes.Keys.OrderBy(k => k));
        Assert.Equal(xamlBrushes, ThemePalette.LightBrushes.Keys.OrderBy(k => k));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextAndStatusColors_ReadWellOnTheBackgrounds(bool light)
    {
        var p = ThemePalette.For(light);
        foreach (var background in new[] { "DarkBgBrush", "CardBgBrush", "SurfaceBrush", "InputBrush" })
        {
            Assert.True(Contrast(p["TextPrimaryBrush"], p[background]) >= 7, $"TextPrimary on {background}");
            Assert.True(Contrast(p["TextSecondaryBrush"], p[background]) >= 4.5, $"TextSecondary on {background}");
            Assert.True(Contrast(p["TextMutedBrush"], p[background]) >= 3, $"TextMuted on {background}");
        }

        foreach (var status in new[] { "AccentBrush", "SuccessBrush", "WarningBrush", "ErrorBrush", "PrimaryLightBrush" })
            Assert.True(Contrast(p[status], p["CardBgBrush"]) >= 4.5, $"{status} on CardBg");

        Assert.True(Contrast(p["CodeTextBrush"], p["CodeBlockBrush"]) >= 7, "code text on code block");
        // The dark theme keeps its original, brighter violet (about 4:1); the light theme must reach 4.5:1.
        if (light)
            Assert.True(Contrast("#FFFFFF", p["UserBubbleBrush"].Split(' ')[0]) >= 4.5, "white text on the user bubble");
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VoiceChatbot.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("VoiceChatbot.csproj not found above the test output folder.");
    }

    // WCAG 2 contrast ratio of two opaque "#RRGGBB" colors.
    private static double Contrast(string a, string b)
    {
        double Luminance(string hex)
        {
            hex = hex.TrimStart('#');
            double Channel(int i)
            {
                var c = int.Parse(hex.Substring(i, 2), NumberStyles.HexNumber) / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
        }

        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
