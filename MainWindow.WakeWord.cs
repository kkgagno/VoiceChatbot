using System;
using System.Media;
using System.Windows;
using System.Windows.Controls;

namespace VoiceChatbot;

public partial class MainWindow
{
    // ==================== Wake word ====================
    // "Only respond after the wake word" (Voice Input) turns AutoDetect off: the app keeps listening
    // (Listen) and answers only speech that contains the wake phrase, "hey onyx" by default
    // (SpeechEngine.WakeWord.cs). Speech without it is ignored without a chat message. A bare
    // "Hey Onyx" plays a sound and the next utterance is answered without the phrase.

    private bool _wakeWordWired;

    private bool WakeWordOnly => WakeWordOnlyToggle.IsChecked == true;

    // ApplySettings, before speech init.
    private void ApplyWakeWordSettings()
    {
        if (!_wakeWordWired)
        {
            _wakeWordWired = true;
            WakeWordBox.TextChanged += (_, _) =>
            {
                _speech.WakeWord = WakeWordBox.Text;
                UpdateWakeWordUi();
            };
            _speech.WakePhraseHeard += () => Dispatcher.BeginInvoke(OnWakePhraseHeard);
            AlwaysListenToggle.Checked += (_, _) => UpdateWakeWordUi();
            AlwaysListenToggle.Unchecked += (_, _) => UpdateWakeWordUi();
        }

        WakeWordOnlyToggle.IsChecked = !_settings.AutoDetectVoice;
        WakeWordBox.Text = _settings.WakeWord;
        _speech.AutoDetect = _settings.AutoDetectVoice;
        _speech.WakeWord = _settings.WakeWord;
        UpdateWakeWordUi();
    }

    private void SaveWakeWordSettings()
    {
        _settings.AutoDetectVoice = !WakeWordOnly;
        _settings.WakeWord = WakeWordBox.Text.Trim();
    }

    // Startup, once everything is ready: wait for the wake word straight away.
    private void StartWaitingForWakeWordIfEnabled()
    {
        if (WakeWordOnly && AlwaysListenToggle.IsChecked != true)
            AlwaysListenToggle.IsChecked = true;
    }

    private void WakeWordOnlyToggle_Click(object sender, RoutedEventArgs e)
    {
        _speech.AutoDetect = !WakeWordOnly;
        SaveSettings();

        // Waiting for the wake word needs the microphone on: turn on Listen.
        if (WakeWordOnly && AlwaysListenToggle.IsChecked != true)
            AlwaysListenToggle.IsChecked = true;
        else if (_speech.CurrentState == VoiceState.Listening)
            ShowListeningStatus();
        UpdateWakeWordUi();
    }

    // UI thread, after a bare "Hey Onyx": the next utterance is answered without the phrase.
    private void OnWakePhraseHeard()
    {
        try { SystemSounds.Asterisk.Play(); } catch { }
        ActivityLabel.Text = $"Heard \"{WakePhraseForDisplay}\" - listening...";
    }

    private string WakePhraseForDisplay => WakeWordBox.Text.Trim();

    /// <summary>Status bar text while the microphone is on: waiting for the wake word, or listening.</summary>
    private void ShowListeningStatus()
    {
        if (_speech.IsWaitingForWakePhrase)
        {
            StateLabel.Text = "Waiting for wake word";
            ActivityLabel.Text = $"Waiting for \"{WakePhraseForDisplay}\"";
        }
        else
        {
            StateLabel.Text = "Listening...";
            ActivityLabel.Text = "Listening for speech...";
        }
    }

    private void UpdateWakeWordUi()
    {
        string text;
        string brush;
        if (!WakeWordOnly)
        {
            text = "Off: everything you say is answered";
            brush = "TextMutedBrush";
        }
        else if (!WakeWordText.HasWords(WakeWordBox.Text))
        {
            text = "Type a wake word below; until then everything is answered";
            brush = "WarningBrush";
        }
        else if (AlwaysListenToggle.IsChecked == true)
        {
            text = $"Waiting for \"{WakePhraseForDisplay}\"";
            brush = "SuccessBrush";
        }
        else
        {
            text = $"Press Listen to wait for \"{WakePhraseForDisplay}\"";
            brush = "TextSecondaryBrush";
        }

        WakeWordStatusText.Text = text;
        WakeWordStatusText.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }
}
