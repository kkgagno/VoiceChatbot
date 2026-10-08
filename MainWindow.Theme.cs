using System;
using System.Windows.Controls;

namespace VoiceChatbot;

// Theme choice in the App expander. ThemeManager.cs does the switching; App.OnStartup applies the saved
// theme before this window is created.
public partial class MainWindow
{
    private bool _loadingThemeChoices;

    /// <summary>Called once from ApplySettings: fills the Theme combo.</summary>
    private void ApplyThemeSetting()
    {
        _settings.Theme = ThemePalette.Normalize(_settings.Theme);
        _loadingThemeChoices = true;
        try
        {
            ThemeCombo.ItemsSource = ThemePalette.Choices;
            ThemeCombo.SelectedItem = _settings.Theme;
        }
        finally
        {
            _loadingThemeChoices = false;
        }
    }

    private void ThemeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingThemeChoices || _settings == null || ThemeCombo.SelectedItem is not string choice)
            return;

        _settings.Theme = ThemePalette.Normalize(choice);
        try
        {
            ThemeManager.Apply(_settings.Theme);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not switch the theme.", ex);
            AddSystemMessage($"Could not switch the theme: {FriendlyErrors.Describe(ex)}");
        }
        SaveSettings();
    }
}
