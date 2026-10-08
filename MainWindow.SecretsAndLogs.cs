using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace VoiceChatbot;

// App expander: encrypted secrets status and the log folder. Secrets are encrypted by SettingsManager;
// the log itself lives in Core/FileLogger.cs (AppLog).
public partial class MainWindow
{
    /// <summary>
    /// Called once from ApplySettings: masks saved secrets in the log, fills the App expander and shows
    /// the one-time warning when saved secrets could not be decrypted.
    /// </summary>
    private void ApplySecretsAndLogsUi()
    {
        AppLog.SecretsProvider = () => new[]
        {
            _settings.OpenAiCompatibleApiKey,
            _settings.HermesSshPassword,
            _settings.TavilyApiKey,
            _settings.PhoneRemote?.Pin
        };
        AppLogFolderText.Text = AppLog.LogDirectory;

        var warning = SettingsManager.TakeLoadWarning();
        if (!string.IsNullOrEmpty(warning))
        {
            SetSecretsStatus(warning, "\uE7BA", "WarningBrush");
            AddSystemMessage(warning);
        }
        else if (!SettingsManager.SecretsEncrypted)
        {
            SetSecretsStatus("Windows data protection is unavailable, so keys and passwords are saved as plain text.",
                "\uE7BA", "WarningBrush");
        }
        else
        {
            SetSecretsStatus("API keys, the SSH password and the phone PIN are saved encrypted for your Windows account.",
                "\uE72E", "SuccessBrush");
        }
    }

    private void SetSecretsStatus(string text, string glyph, string brushKey)
    {
        SecretsStatusText.Text = text;
        SecretsStatusIcon.Text = glyph;
        SecretsStatusIcon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppLog.LogDirectory);
            // Select today's file when there is one so it is easy to attach to a bug report.
            var today = AppLog.CurrentFilePath;
            var args = File.Exists(today) ? $"/select,\"{today}\"" : $"\"{AppLog.LogDirectory}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not open the logs folder: {ex.Message}");
        }
    }
}
