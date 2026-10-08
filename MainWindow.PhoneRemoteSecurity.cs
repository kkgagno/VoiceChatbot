using System;
using System.Windows;
using System.Windows.Controls;

namespace VoiceChatbot;

// Phone remote PIN. The PIN is required: EnsurePhoneRemotePin creates a random 6-digit one before the
// remote starts if none is set, and PhoneRemoteServer checks it on every request with a per-IP lockout
// after 5 wrong tries in 10 minutes (Core/PhoneRemoteAuth.cs, Core/LoginAttemptLimiter.cs).
public partial class MainWindow
{
    private bool _phoneRemoteSecurityWired;

    /// <summary>Called from ApplySettings: hooks up the PIN box and lockout notes and fills the PIN hint.</summary>
    private void ApplyPhoneRemoteSecuritySettings()
    {
        if (!_phoneRemoteSecurityWired)
        {
            _phoneRemoteSecurityWired = true;
            _phoneRemoteServer.LockoutStarted += OnPhoneRemoteLockout;
            PhoneRemotePinBox.TextChanged += (_, _) => UpdatePhoneRemotePinUi();
            // SaveSettings hands an edited PIN to the running remote (SavePhoneRemoteSecuritySettings).
            PhoneRemotePinBox.LostFocus += (_, _) => SaveSettings();
        }

        UpdatePhoneRemotePinUi();
    }

    /// <summary>
    /// Called from SaveSettings after the PIN box was read. A running remote takes a changed PIN right away;
    /// it never goes without one, so a blank or invalid entry puts the current PIN back.
    /// </summary>
    private void SavePhoneRemoteSecuritySettings()
    {
        if (!_phoneRemoteServer.IsRunning)
            return;

        var pin = PhoneRemotePin.Normalize(_settings.PhoneRemote.Pin);
        var livePin = _phoneRemoteServer.Pin;
        if (string.Equals(pin, livePin, StringComparison.Ordinal))
            return;

        if (!PhoneRemotePin.IsValid(pin))
        {
            _settings.PhoneRemote.Pin = livePin;
            PhoneRemotePinBox.Text = livePin;
            AddSystemMessage(pin.Length == 0
                ? "The phone remote needs a PIN while it runs, so the current PIN was kept. Use New PIN for a different one."
                : "A phone remote PIN must be 4 to 64 characters without spaces, so the current PIN was kept.");
            return;
        }

        if (_phoneRemoteServer.ChangePin(pin))
        {
            UpdatePhoneRemoteUi();
            AddSystemMessage("Phone remote PIN changed. Enter the new PIN on the phone.");
        }
    }

    /// <summary>
    /// Makes sure a valid PIN is saved before the remote starts, creating a random 6-digit one if needed.
    /// </summary>
    private void EnsurePhoneRemotePin()
    {
        var pin = PhoneRemotePin.Normalize(_settings.PhoneRemote.Pin);
        if (PhoneRemotePin.IsValid(pin))
        {
            _settings.PhoneRemote.Pin = pin;
            return;
        }

        SetPhoneRemotePin(PhoneRemotePin.Generate());
        AddSystemMessage(pin.Length == 0
            ? "Created a phone remote PIN. It is shown under Phone Remote; enter it once on the phone."
            : "The phone remote PIN must be 4 to 64 characters without spaces, so a new random PIN was created. It is shown under Phone Remote.");
    }

    /// <summary>Shows and saves a PIN. SaveSettings also hands it to the remote when it is running.</summary>
    private void SetPhoneRemotePin(string pin)
    {
        _settings.PhoneRemote.Pin = pin;
        PhoneRemotePinBox.Text = pin;
        // SaveSettings is a no-op while startup applies settings, but the PIN must still be saved
        // (and handed to the remote, which starts before startup finishes).
        if (_applyingSettings)
        {
            SettingsManager.Save(_settings);
            if (_phoneRemoteServer.IsRunning && _phoneRemoteServer.ChangePin(pin))
                UpdatePhoneRemoteUi();
        }
        else
        {
            SaveSettings();
        }
        UpdatePhoneRemotePinUi();
    }

    private void NewPhoneRemotePin_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var running = _phoneRemoteServer.IsRunning;
            SetPhoneRemotePin(PhoneRemotePin.Generate());
            if (!running)
                AddSystemMessage("Created a new phone remote PIN. The phone needs it the next time the remote starts.");
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not create a new PIN: {ex.Message}");
        }
    }

    private void UpdatePhoneRemotePinUi()
    {
        if (PhoneRemotePinHintText == null)
            return;

        var pin = PhoneRemotePinBox.Text;
        string text;
        var brush = "TextMutedBrush";
        if (PhoneRemotePin.Normalize(pin).Length == 0)
        {
            text = "Required. A random 6-digit PIN is created when the remote starts.";
        }
        else if (!PhoneRemotePin.IsValid(pin))
        {
            text = "Use 4 to 64 characters without spaces.";
            brush = "WarningBrush";
        }
        else if (PhoneRemotePin.IsWeak(pin))
        {
            text = "Short PINs are easier to guess. Use New PIN for a random 6-digit one.";
            brush = "WarningBrush";
        }
        else
        {
            text = "Enter it once on the phone. 5 wrong tries lock that device out for 10 minutes; New PIN lifts lockouts.";
        }

        PhoneRemotePinHintText.Text = text;
        PhoneRemotePinHintText.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    /// <summary>Runs on a Kestrel thread when a device was locked out after too many wrong PINs.</summary>
    private void OnPhoneRemoteLockout(PhoneRemoteLockout lockout)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    AddSystemMessage($"Phone remote: {lockout.Client} entered a wrong PIN {lockout.FailedAttempts} times " +
                                     $"and is locked out for {PhoneRemoteAuthenticator.DescribeMinutes(lockout.Duration)}. " +
                                     "If that was not your phone, someone on your network is guessing the PIN.");
                }
                catch (Exception ex)
                {
                    AppLog.Warn("Could not show the phone remote lockout", ex);
                }
            });
        }
        catch (Exception ex)
        {
            // The window is closing.
            AppLog.Warn($"Phone remote: locked out {lockout.Client}", ex);
        }
    }
}
