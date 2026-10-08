using System.Windows;

namespace VoiceChatbot;

/// <summary>
/// Attached properties used by the control templates in App.xaml (icon glyphs, corner radius,
/// placeholder text, section subtitles). Keeping these separate from Content means code can keep
/// setting Button.Content to plain strings without losing the icon.
/// </summary>
public static class Ui
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static string? GetIcon(DependencyObject d) => (string?)d.GetValue(IconProperty);
    public static void SetIcon(DependencyObject d, string? value) => d.SetValue(IconProperty, value);

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.RegisterAttached(
        "IconSize", typeof(double), typeof(Ui), new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static double GetIconSize(DependencyObject d) => (double)d.GetValue(IconSizeProperty);
    public static void SetIconSize(DependencyObject d, double value) => d.SetValue(IconSizeProperty, value);

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(8)));

    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject d, CornerRadius value) => d.SetValue(CornerRadiusProperty, value);

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static string? GetPlaceholder(DependencyObject d) => (string?)d.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject d, string? value) => d.SetValue(PlaceholderProperty, value);

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.RegisterAttached(
        "Subtitle", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null));

    public static string? GetSubtitle(DependencyObject d) => (string?)d.GetValue(SubtitleProperty);
    public static void SetSubtitle(DependencyObject d, string? value) => d.SetValue(SubtitleProperty, value);
}
