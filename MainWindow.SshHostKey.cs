using System;
using System.Windows;
using System.Windows.Controls;

namespace VoiceChatbot;

// SSH host key pinning for Model Server Control. HermesSshClient checks every connection against
// _hermesSsh.HostKeys (Core/SshHostKeyPins.cs); this file loads and saves the pins, shows the pinned
// fingerprint in the sidebar and handles "Forget host key".
public partial class MainWindow
{
    private bool _sshHostKeyUiWired;
    // The last refused key, shown under the pin while the same host:port is selected.
    private SshHostKeyCheck? _rejectedSshHostKey;

    /// <summary>Called from ApplySettings: loads the saved pins into the SSH client and fills the UI.</summary>
    private void ApplySshHostKeySettings()
    {
        _settings.HermesSshHostKeyFingerprints ??= new();
        _hermesSsh.HostKeys.Load(_settings.HermesSshHostKeyFingerprints);

        if (!_sshHostKeyUiWired)
        {
            _sshHostKeyUiWired = true;
            _hermesSsh.HostKeyTrusted += OnSshHostKeyTrusted;
            _hermesSsh.HostKeyRejected += OnSshHostKeyRejected;
            // Pins are per host:port, so show the one for whatever is typed.
            HermesSshHostBox.TextChanged += (_, _) => UpdateSshHostKeyUi();
            HermesSshPortBox.TextChanged += (_, _) => UpdateSshHostKeyUi();
        }

        UpdateSshHostKeyUi();
    }

    /// <summary>Called from SaveSettings: the SSH client holds the live pins, including ones added this session.</summary>
    private void SaveSshHostKeySettings()
    {
        _settings.HermesSshHostKeyFingerprints = _hermesSsh.HostKeys.ToDictionary();
    }

    /// <summary>Host and port as typed in the sidebar, parsed the same way SaveSettings does.</summary>
    private (string Host, int Port) GetSshHostKeyTarget()
    {
        var host = HermesSshHostBox.Text.Trim();
        var port = int.TryParse(HermesSshPortBox.Text.Trim(), out var value)
            ? Math.Clamp(value, 1, 65535)
            : 2222;
        return (host, port);
    }

    private void UpdateSshHostKeyUi()
    {
        var (host, port) = GetSshHostKeyTarget();
        var hostPort = SshHostKeyPins.HostPortKey(host, port);
        var fingerprint = _hermesSsh.HostKeys.Get(host, port);
        var pinned = fingerprint.Length > 0;
        var rejected = pinned && _rejectedSshHostKey?.HostPort == hostPort ? _rejectedSshHostKey : null;

        SshHostKeyTargetText.Text = hostPort;
        SshHostKeyText.Text = pinned ? fingerprint : "Not pinned yet. The key is trusted on the first connection.";
        SshHostKeyText.SetResourceReference(TextBlock.ForegroundProperty, pinned ? "TextPrimaryBrush" : "TextMutedBrush");
        SshHostKeyIcon.Text = rejected != null ? "\uE7BA" : pinned ? "\uE72E" : "\uE785";
        SshHostKeyIcon.SetResourceReference(TextBlock.ForegroundProperty,
            rejected != null ? "ErrorBrush" : pinned ? "SuccessBrush" : "TextMutedBrush");
        SshHostKeyRejectedText.Text = rejected == null
            ? ""
            : $"Refused a different key: {(rejected.Presented.Length > 0 ? rejected.Presented : "none")}" +
              (rejected.KeyType.Length > 0 ? $" ({rejected.KeyType})" : "");
        SshHostKeyRejectedText.Visibility = rejected == null ? Visibility.Collapsed : Visibility.Visible;
        CopySshHostKeyBtn.IsEnabled = pinned;
        ForgetSshHostKeyBtn.IsEnabled = pinned;
    }

    /// <summary>Runs on an SSH thread when a server's key was pinned on its first connection.</summary>
    private void OnSshHostKeyTrusted(SshHostKeyCheck check)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (_rejectedSshHostKey?.HostPort == check.HostPort)
                        _rejectedSshHostKey = null;
                    SaveSshHostKeySettings();
                    SaveSettings();
                    UpdateSshHostKeyUi();
                    var type = check.KeyType.Length > 0 ? $" ({check.KeyType})" : "";
                    AddSystemMessage($"Trusted SSH host key for {check.HostPort} {check.Presented}{type}. " +
                                     "Connections are refused if this key ever changes.");
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Could not save the pinned SSH host key", ex);
                }
            });
        }
        catch (Exception ex)
        {
            // The window is closing; SaveSettings at exit still picks the pin up from the SSH client.
            AppLog.Warn("Could not report the pinned SSH host key", ex);
        }
    }

    /// <summary>Runs on an SSH thread when a server sent a key that differs from its pin.</summary>
    private void OnSshHostKeyRejected(SshHostKeyCheck check)
    {
        // Whoever ran the SSH command shows the full error; this only flags it next to the pin.
        AppLog.Warn(SshHostKeyPins.DescribeMismatch(check));
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                _rejectedSshHostKey = check;
                UpdateSshHostKeyUi();
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not show the refused SSH host key", ex);
        }
    }

    private void ForgetSshHostKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (host, port) = GetSshHostKeyTarget();
            var hostPort = SshHostKeyPins.HostPortKey(host, port);
            var fingerprint = _hermesSsh.HostKeys.Get(host, port);
            if (fingerprint.Length == 0)
            {
                UpdateSshHostKeyUi();
                return;
            }

            var answer = MessageBox.Show(this,
                $"Forget the pinned SSH host key for {hostPort}?\n\n{fingerprint}\n\n" +
                "Only do this after you reinstalled or reconfigured the SSH server. " +
                "The next connection will trust whatever key the server sends.",
                "Forget SSH host key", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;

            _hermesSsh.HostKeys.Forget(host, port);
            _rejectedSshHostKey = null;
            SaveSshHostKeySettings();
            SaveSettings();
            UpdateSshHostKeyUi();
            AddSystemMessage($"Forgot the SSH host key for {hostPort} ({fingerprint}). " +
                             "The next connection will trust the key the server sends.");
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not forget the SSH host key: {ex.Message}");
        }
    }

    private void CopySshHostKey_Click(object sender, RoutedEventArgs e)
    {
        var (host, port) = GetSshHostKeyTarget();
        var fingerprint = _hermesSsh.HostKeys.Get(host, port);
        if (fingerprint.Length == 0)
            return;

        try
        {
            Clipboard.SetText(fingerprint);
        }
        catch (Exception ex)
        {
            // The clipboard can be locked by another app for a moment.
            AddSystemMessage($"Could not copy the fingerprint: {ex.Message}");
        }
    }
}
