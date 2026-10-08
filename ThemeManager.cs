using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace VoiceChatbot;

/// <summary>
/// Shows the theme the user chose (Settings > App, palettes in Core/ThemePalette.cs). It replaces the theme
/// brushes in Application.Current.Resources under the same keys, so everything that uses them through
/// DynamicResource updates live; brushes that code copied onto open windows with FindResource are swapped
/// too, and title bars follow. "Use Windows setting" also follows a change of the Windows app mode while
/// the app runs. Call on the UI thread.
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static string _choice = ThemePalette.Default;
    private static bool _followingWindows;

    /// <summary>True while the light theme is shown. App.xaml starts with the dark brushes.</summary>
    public static bool IsLight { get; private set; }

    /// <summary>Shows the theme for <paramref name="choice"/> (one of <see cref="ThemePalette.Choices"/>).</summary>
    public static void Apply(string? choice)
    {
        var app = Application.Current;
        if (app == null)
            return;

        _choice = ThemePalette.Normalize(choice);
        var followWindows = _choice == ThemePalette.UseWindowsSetting;
        FollowWindowsSetting(followWindows);

        var light = ThemePalette.IsLight(_choice, followWindows ? ReadAppsUseLightTheme() : null);
        if (light == IsLight)
            return;

        IsLight = light;
        var replaced = new Dictionary<Brush, Brush>(ReferenceEqualityComparer.Instance);
        foreach (var (key, value) in ThemePalette.For(light))
        {
            var brush = CreateBrush(value);
            if (app.Resources[key] is Brush old)
                replaced[old] = brush;
            app.Resources[key] = brush;
        }

        foreach (Window window in app.Windows)
            SwapLocalBrushes(window, replaced);

        WindowTheme.ApplyToOpenWindows();
        AppLog.Info($"Theme: {(light ? "light" : "dark")} ({_choice}).");
    }

    private static Brush CreateBrush(string value)
    {
        var colors = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return colors.Length == 2
            ? new LinearGradientBrush(ParseColor(colors[0]), ParseColor(colors[1]), new Point(0, 0), new Point(1, 1))
            : new SolidColorBrush(ParseColor(colors[0]));
    }

    private static Color ParseColor(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>
    /// Chat bubbles, status texts and other elements built in code got the old theme's brush instances
    /// through FindResource. Gives every such local value in the tree the new brush of the same key.
    /// DynamicResource references and bindings are left alone.
    /// </summary>
    private static void SwapLocalBrushes(DependencyObject node, IReadOnlyDictionary<Brush, Brush> replaced)
    {
        var changes = new List<(DependencyProperty Property, Brush Brush)>();
        var local = node.GetLocalValueEnumerator();
        while (local.MoveNext())
        {
            var entry = local.Current;
            if (entry.Value is Brush brush && !entry.Property.ReadOnly &&
                replaced.TryGetValue(brush, out var newBrush) &&
                !DependencyPropertyHelper.GetValueSource(node, entry.Property).IsExpression)
            {
                changes.Add((entry.Property, newBrush));
            }
        }

        foreach (var (property, brush) in changes)
            node.SetValue(property, brush);

        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject element)
                SwapLocalBrushes(element, replaced);
        }
    }

    private static int? ReadAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value ? value : null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warn($"Could not read the Windows app theme: {ex.Message}");
            return null;
        }
    }

    private static void FollowWindowsSetting(bool follow)
    {
        if (follow == _followingWindows)
            return;

        _followingWindows = follow;
        if (follow)
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        else
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    // Raised on a SystemEvents thread when the Windows light/dark app mode (among other things) changes.
    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General)
            return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (_choice == ThemePalette.UseWindowsSetting)
                    Apply(_choice);
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not follow the Windows theme.", ex);
            }
        });
    }
}
