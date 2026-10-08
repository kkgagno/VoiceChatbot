using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceChatbot;

/// <summary>
/// The theme choices (Settings > App) and the brushes of the dark and light themes, keyed by the
/// resource keys in App.xaml. ThemeManager.cs puts the chosen palette into the application resources.
/// A value is "#RRGGBB" or "#AARRGGBB", or two colors separated by a space for a diagonal gradient.
/// </summary>
public static class ThemePalette
{
    public const string Dark = "Dark";
    public const string Light = "Light";
    public const string UseWindowsSetting = "Use Windows setting";
    public const string Default = Dark;

    /// <summary>The choices in the order the settings list shows them.</summary>
    public static IReadOnlyList<string> Choices { get; } = new[] { Dark, Light, UseWindowsSetting };

    /// <summary>The saved choice when it is one of <see cref="Choices"/> (any case), otherwise <see cref="Default"/>.</summary>
    public static string Normalize(string? choice) =>
        Choices.FirstOrDefault(c => string.Equals(c, choice?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Default;

    /// <summary>
    /// True when <paramref name="choice"/> means the light theme. <paramref name="appsUseLightTheme"/> is the
    /// AppsUseLightTheme registry value (0 dark, 1 light), or null when it could not be read: the app then stays dark.
    /// </summary>
    public static bool IsLight(string? choice, int? appsUseLightTheme) => Normalize(choice) switch
    {
        Light => true,
        UseWindowsSetting => appsUseLightTheme is int value && value != 0,
        _ => false
    };

    public static IReadOnlyDictionary<string, string> For(bool light) => light ? LightBrushes : DarkBrushes;

    /// <summary>The original dark theme; the same values App.xaml starts with.</summary>
    public static IReadOnlyDictionary<string, string> DarkBrushes { get; } = new Dictionary<string, string>
    {
        ["PrimaryBrush"] = "#7C6CFF",
        ["PrimaryDarkBrush"] = "#6A5AF0",
        ["PrimaryLightBrush"] = "#9488FF",
        ["PrimarySoftBrush"] = "#267C6CFF",
        ["AccentBrush"] = "#22D3EE",
        ["AccentSoftBrush"] = "#2622D3EE",
        ["DarkBgBrush"] = "#0B0D13",
        ["PanelBgBrush"] = "#11141C",
        ["CardBgBrush"] = "#181C27",
        ["SurfaceBrush"] = "#151924",
        ["SurfaceHoverBrush"] = "#1E2331",
        ["InputBrush"] = "#0F121A",
        ["BorderBrush"] = "#262B3A",
        ["BorderStrongBrush"] = "#353B4F",
        ["SuccessBrush"] = "#34D399",
        ["SuccessSoftBrush"] = "#2634D399",
        ["WarningBrush"] = "#FBBF24",
        ["ErrorBrush"] = "#F87171",
        ["ErrorSoftBrush"] = "#26F87171",
        ["TextPrimaryBrush"] = "#E7E9F1",
        ["TextSecondaryBrush"] = "#9AA2B6",
        ["TextMutedBrush"] = "#6B7389",
        ["ListeningBrush"] = "#22D3EE",
        ["SpeakingBrush"] = "#9488FF",
        ["HoverOverlayBrush"] = "#FFFFFF",
        ["TooltipBgBrush"] = "#1F2433",
        ["PopupBgBrush"] = "#1B2030",
        ["ToggleThumbBrush"] = "#C9CEDB",
        ["ScrollThumbBrush"] = "#3A4156",
        ["ScrollThumbHoverBrush"] = "#525A72",
        ["CodeBlockBrush"] = "#0A0C12",
        ["CodeTextBrush"] = "#E3E6F0",
        ["UserBubbleBrush"] = "#7C6CFF #5B4DE0",
        ["BrandGradientBrush"] = "#7C6CFF #22D3EE",
    };

    /// <summary>
    /// Near-white backgrounds, white cards, slate text and the violet primary and cyan accent darkened
    /// enough to read on white. PrimaryLightBrush is text on soft violet here, so it is the darker violet.
    /// </summary>
    public static IReadOnlyDictionary<string, string> LightBrushes { get; } = new Dictionary<string, string>
    {
        ["PrimaryBrush"] = "#6C5CE7",
        ["PrimaryDarkBrush"] = "#5A4BD6",
        ["PrimaryLightBrush"] = "#5B4BD5",
        ["PrimarySoftBrush"] = "#1F6C5CE7",
        ["AccentBrush"] = "#0B7C96",
        ["AccentSoftBrush"] = "#1F0B7C96",
        ["DarkBgBrush"] = "#F7F8FB",
        ["PanelBgBrush"] = "#EEF0F5",
        ["CardBgBrush"] = "#FFFFFF",
        ["SurfaceBrush"] = "#FFFFFF",
        ["SurfaceHoverBrush"] = "#EEF0F5",
        ["InputBrush"] = "#F5F6FA",
        ["BorderBrush"] = "#E3E6EE",
        ["BorderStrongBrush"] = "#C3C9D6",
        ["SuccessBrush"] = "#047857",
        ["SuccessSoftBrush"] = "#1F047857",
        ["WarningBrush"] = "#B45309",
        ["ErrorBrush"] = "#DC2626",
        ["ErrorSoftBrush"] = "#1FDC2626",
        ["TextPrimaryBrush"] = "#1F2430",
        ["TextSecondaryBrush"] = "#5B6478",
        ["TextMutedBrush"] = "#7A8397",
        ["ListeningBrush"] = "#0B7C96",
        ["SpeakingBrush"] = "#6C5CE7",
        ["HoverOverlayBrush"] = "#1F2430",
        ["TooltipBgBrush"] = "#FFFFFF",
        ["PopupBgBrush"] = "#FFFFFF",
        ["ToggleThumbBrush"] = "#FFFFFF",
        ["ScrollThumbBrush"] = "#C3C9D6",
        ["ScrollThumbHoverBrush"] = "#A3ABBC",
        ["CodeBlockBrush"] = "#F1F3F7",
        ["CodeTextBrush"] = "#1F2430",
        ["UserBubbleBrush"] = "#6C5CE7 #5546D6",
        ["BrandGradientBrush"] = "#6C5CE7 #0B7C96",
    };

    /// <summary>The background color of the window, "#RRGGBB", for the title bar.</summary>
    public static string WindowBackground(bool light) => For(light)["DarkBgBrush"];
}
