using System;
using System.Linq;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VoiceChatbot;

public partial class MainWindow
{
    // ==================== Wake word detector ====================
    // Always-on openWakeWord detector (WakeWordDetector), running in-process with the bundled models.
    // Hearing the wake word starts one listening turn, like the Listen button. It is paused while the
    // app listens, thinks or speaks.

    private WakeWordDetector? _wakeWord;
    private bool _wakeWordWired;
    private DispatcherTimer? _wakeWordPauseTimer;
    private DispatcherTimer? _wakeWordRestartTimer;
    private string _wakeWordLastHeard = "";

    private void ApplyWakeWordSettings()
    {
        if (!_wakeWordWired)
        {
            _wakeWordWired = true;
            foreach (var model in WakeWordProtocol.Models)
                WakeWordModelCombo.Items.Add(new ComboBoxItem { Content = WakeWordProtocol.DisplayName(model), Tag = model });
            WakeWordSensitivitySlider.ValueChanged += (_, _) => OnWakeWordSensitivityChanged();
            _speech.StateChanged += PauseWakeWordWhileVoiceBusy;
        }

        SelectWakeWordModel(_settings.WakeWordModel);
        WakeWordSensitivitySlider.Value = WakeWordProtocol.ThresholdToSensitivity(_settings.WakeWordThreshold);
        WakeWordDetectorToggle.IsChecked = _settings.WakeWordDetectorEnabled;
        UpdateWakeWordUi();
    }

    private void SaveWakeWordSettings()
    {
        _settings.WakeWordDetectorEnabled = WakeWordDetectorToggle.IsChecked == true;
        _settings.WakeWordModel = GetSelectedWakeWordModel();
        _settings.WakeWordThreshold = WakeWordProtocol.SensitivityToThreshold(WakeWordSensitivitySlider.Value);
    }

    // Startup, after speech init.
    private void StartWakeWordDetectorIfEnabled()
    {
        if (WakeWordDetectorToggle.IsChecked == true)
            StartWakeWordDetector();
    }

    private void WakeWordDetectorToggle_Click(object sender, RoutedEventArgs e)
    {
        if (WakeWordDetectorToggle.IsChecked == true)
            StartWakeWordDetector();
        else
            StopWakeWordDetector();
        SaveSettings();
    }

    private void WakeWordModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_wakeWordWired || _applyingSettings)
            return;
        SaveSettings();
        ScheduleWakeWordRestart();
        UpdateWakeWordUi();
    }

    private void OnWakeWordSensitivityChanged()
    {
        WakeWordSensitivityValue.Text = ((int)Math.Round(WakeWordSensitivitySlider.Value)).ToString();
        if (_applyingSettings)
            return;
        // The threshold is read when the detector starts; restart once the slider settles.
        ScheduleWakeWordRestart();
    }

    private void RestartWakeWordDetectorOnNewMicrophone()
    {
        if (_wakeWord?.IsRunning == true)
            ScheduleWakeWordRestart();
    }

    private void StartWakeWordDetector()
    {
        try
        {
            if (_wakeWord == null)
            {
                _wakeWord = new WakeWordDetector();
                _wakeWord.Detected += evt => Dispatcher.BeginInvoke(() => OnWakeWordDetected(evt));
                _wakeWord.StatusChanged += _ => Dispatcher.BeginInvoke(UpdateWakeWordUi);
                _wakeWord.Error += message => Dispatcher.BeginInvoke(() => OnWakeWordError(message));
            }

            _wakeWordLastHeard = "";
            _wakeWord.Start(
                GetSelectedWakeWordModel(),
                WakeWordProtocol.SensitivityToThreshold(WakeWordSensitivitySlider.Value),
                _speech.MicDeviceIndex);

            if (_wakeWordPauseTimer == null)
            {
                // Resuming is decided here; pausing also happens straight from the voice state change.
                _wakeWordPauseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _wakeWordPauseTimer.Tick += (_, _) => UpdateWakeWordPause();
            }
            _wakeWordPauseTimer.Start();
            UpdateWakeWordPause();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Wake word detector could not start: {ex.Message}");
        }

        UpdateWakeWordUi();
    }

    private void StopWakeWordDetector()
    {
        _wakeWordPauseTimer?.Stop();
        _wakeWordRestartTimer?.Stop();
        try { _wakeWord?.Stop(); } catch { }
        UpdateWakeWordUi();
    }

    // Window close: releases the microphone and the models.
    private void DisposeWakeWordDetector()
    {
        _wakeWordPauseTimer?.Stop();
        _wakeWordRestartTimer?.Stop();
        var detector = _wakeWord;
        _wakeWord = null;
        try { detector?.Dispose(); } catch { }
    }

    private void ScheduleWakeWordRestart()
    {
        if (WakeWordDetectorToggle.IsChecked != true || _shutdownStarted)
            return;

        if (_wakeWordRestartTimer == null)
        {
            _wakeWordRestartTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _wakeWordRestartTimer.Tick += (_, _) =>
            {
                _wakeWordRestartTimer.Stop();
                SaveSettings();
                if (WakeWordDetectorToggle.IsChecked == true && !_shutdownStarted)
                    StartWakeWordDetector();
            };
        }

        _wakeWordRestartTimer.Stop();
        _wakeWordRestartTimer.Start();
    }

    // ==================== Pause while busy ====================

    // Voice engine thread. Stop feeding audio at once so the detector never hears the assistant.
    private void PauseWakeWordWhileVoiceBusy(VoiceState state)
    {
        if (state is VoiceState.Listening or VoiceState.Speaking)
            _wakeWord?.Pause();
    }

    private void UpdateWakeWordPause()
    {
        var detector = _wakeWord;
        if (detector == null || !detector.IsRunning)
            return;

        if (IsAssistantBusyForWakeWord())
            detector.Pause();
        else
            detector.Resume();
    }

    private bool IsAssistantBusyForWakeWord()
    {
        // Auto-listen already hears everything; the wake word would only start a second turn.
        if (_autoListening)
            return true;

        // The send button is disabled while a reply (or image, search, command) is being produced.
        // The voice engine can stay in Processing after a command that never speaks, so that state
        // alone does not count.
        return _speech.CurrentState is VoiceState.Listening or VoiceState.Speaking || !SendBtn.IsEnabled;
    }

    // ==================== Detection ====================

    private void OnWakeWordDetected(WakeWordEvent evt)
    {
        try
        {
            var detector = _wakeWord;
            // _facePolicy is set once startup has finished.
            if (_shutdownStarted || detector == null || !detector.IsRunning || _facePolicy is null)
                return;

            _wakeWordLastHeard = $"heard at {DateTime.Now:HH:mm:ss} (score {evt.Score:0.00})";
            UpdateWakeWordUi();
            if (IsAssistantBusyForWakeWord())
                return;

            // Same checks and steps as the Listen button.
            if (!IsVoiceInputAllowedByFacePolicy())
            {
                ActivityLabel.Text = "Wake word heard, but voice input is blocked by local face policy.";
                return;
            }

            detector.Pause();
            try { SystemSounds.Asterisk.Play(); } catch { }
            _speech.StopSpeaking();
            _speech.ReadyForNextSpeech();
            if (!_speech.StartListeningAfterWakeWord())
            {
                SetUIState("idle", "Voice input unavailable");
                return;
            }

            ActivityLabel.Text = $"Heard \"{WakeWordProtocol.SpokenPhrase(detector.Model)}\" - listening...";
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Wake word could not start listening: {ex.Message}");
        }
    }

    private void OnWakeWordError(string message)
    {
        if (_shutdownStarted || _wakeWord == null)
            return;

        UpdateWakeWordUi();
        AddSystemMessage($"Wake word detector stopped: {message} Turn \"Listen for a wake word\" off and on to retry.");
    }

    // ==================== UI ====================

    private void UpdateWakeWordUi()
    {
        var enabled = WakeWordDetectorToggle.IsChecked == true;
        WakeWordModelCombo.IsEnabled = enabled;
        WakeWordSensitivitySlider.IsEnabled = enabled;
        WakeWordSensitivityValue.Text = ((int)Math.Round(WakeWordSensitivitySlider.Value)).ToString();

        var status = _wakeWord?.Status ?? WakeWordStatus.Off;
        var model = _wakeWord?.Model ?? GetSelectedWakeWordModel();
        var text = WakeWordProtocol.DescribeStatus(status, model, _wakeWord?.StatusDetail);
        if ((status is WakeWordStatus.Listening or WakeWordStatus.Paused) && _wakeWordLastHeard.Length > 0)
            text += $" - {_wakeWordLastHeard}";

        WakeWordStatusText.Text = text;
        WakeWordStatusText.ToolTip = string.IsNullOrWhiteSpace(_wakeWord?.StatusDetail) ? null : _wakeWord!.StatusDetail;
        WakeWordStatusText.SetResourceReference(TextBlock.ForegroundProperty, status switch
        {
            WakeWordStatus.Listening => "SuccessBrush",
            WakeWordStatus.Error => "ErrorBrush",
            WakeWordStatus.Starting or WakeWordStatus.Paused => "TextSecondaryBrush",
            _ => "TextMutedBrush"
        });
    }

    private string GetSelectedWakeWordModel() =>
        WakeWordModelCombo.SelectedItem is ComboBoxItem { Tag: string model }
            ? model
            : WakeWordProtocol.NormalizeModel(_settings.WakeWordModel);

    private void SelectWakeWordModel(string? model)
    {
        var id = WakeWordProtocol.NormalizeModel(model);
        WakeWordModelCombo.SelectedItem = WakeWordModelCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => item.Tag as string == id);
    }
}
