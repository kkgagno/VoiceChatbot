using System;
using System.Windows;
using System.Windows.Threading;

namespace VoiceChatbot;

public partial class MainWindow
{
    // ==================== Barge-in ====================
    // "Interrupt by speaking": SpeechEngine stops the reply when the user talks over it, then we listen.

    private bool _bargeInWired;

    private void ApplyBargeInSettings()
    {
        if (!_bargeInWired)
        {
            _bargeInWired = true;
            BargeInSensitivitySlider.ValueChanged += (_, _) => ApplyBargeInToEngine();
            _speech.BargeIn += OnBargeIn;
        }

        BargeInToggle.IsChecked = _settings.BargeInEnabled;
        BargeInSensitivitySlider.Value = Math.Clamp(_settings.BargeInSensitivity, 0, 100);
        ApplyBargeInToEngine();
    }

    private void SaveBargeInSettings()
    {
        _settings.BargeInEnabled = BargeInToggle.IsChecked == true;
        _settings.BargeInSensitivity = (int)Math.Round(BargeInSensitivitySlider.Value);
    }

    private void BargeInToggle_Click(object sender, RoutedEventArgs e)
    {
        ApplyBargeInToEngine();
        SaveSettings();
    }

    private void ApplyBargeInToEngine()
    {
        var enabled = BargeInToggle.IsChecked == true;
        var sensitivity = (int)Math.Round(BargeInSensitivitySlider.Value);
        BargeInSensitivityValue.Text = sensitivity.ToString();
        BargeInSensitivitySlider.IsEnabled = enabled;
        _speech.BargeInSensitivity = sensitivity;
        _speech.BargeInEnabled = enabled;
    }

    // Raised on a background thread; speech has already stopped.
    private void OnBargeIn()
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                ListenAfterBargeIn();
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Could not listen after the interruption: {ex.Message}");
            }
        });
    }

    private void ListenAfterBargeIn()
    {
        if (_pausedListeningForTextInput)
        {
            AddSystemMessage("Interrupted - mic paused while typing");
            return;
        }

        // Same check as the Listen button.
        if (!IsVoiceInputAllowedByFacePolicy())
        {
            AddSystemMessage("Interrupted. Voice input is blocked by local face policy.");
            return;
        }

        AddSystemMessage("Interrupted - listening");

        // Auto-listen may already have restarted the microphone.
        if (_speech.CurrentState == VoiceState.Listening)
            return;

        _speech.ReadyForNextSpeech();
        if (!_speech.StartListening())
        {
            SetUIState("idle", "Voice input unavailable");
            return;
        }

        // The stopped reply reports SpeechFinished a moment later, which sets the status back to "Ready".
        var restoreStatus = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        restoreStatus.Tick += (_, _) =>
        {
            restoreStatus.Stop();
            if (_speech.CurrentState != VoiceState.Listening)
                return;
            ShowListeningStatus();
        };
        restoreStatus.Start();
    }
}
