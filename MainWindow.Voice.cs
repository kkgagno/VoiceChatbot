using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Cv2 = OpenCvSharp.Cv2;
using Mat = OpenCvSharp.Mat;

namespace VoiceChatbot;

public partial class MainWindow
{
    // ==================== Voice Events ====================

    private void OnSpeechRecognized(string text)
    {
        Dispatcher.Invoke(() =>
        {
            AddSystemMessage($"You said: \"{text}\"");
            SendMessage(text);
        });
    }

    private void OnSpeechFinished()
    {
        Dispatcher.Invoke(() => FinishTurn());
    }

    // ==================== Turn lifecycle ====================

    /// <summary>
    /// Ends a turn: resets the speech engine, sets the UI idle and, in hands-free mode, starts
    /// listening for the next utterance. UI thread. Call it for every turn outcome that does not
    /// start speech (errors, empty or cancelled replies, media commands, notices); a turn that
    /// speaks ends through SpeechFinished, which calls this once the speech is over.
    /// </summary>
    private void FinishTurn()
    {
        // A phone request that took the busy state meanwhile gives it back when it is done.
        if (!_phoneOwnsBusyState)
        {
            SetUIState("idle", "Ready");
            // The mic button can start listening before the stopped reply's turn ends.
            if (_speech.CurrentState == VoiceState.Listening)
                ShowListeningStatus();
        }
        _speech.ReadyForNextSpeech();
        ResumeAutoListenIfActive();
    }

    /// <summary>Restarts listening shortly when auto-listen is on (and not paused for typing or by face policy).</summary>
    private void ResumeAutoListenIfActive()
    {
        if (!_autoListening)
            return;

        Task.Delay(100).ContinueWith(_ =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_autoListening || _pausedListeningForTextInput || !IsVoiceInputAllowedByFacePolicy())
                    return;

                // A newer reply is being spoken; its SpeechFinished resumes listening.
                if (_speech.CurrentState == VoiceState.Speaking)
                    return;

                _speech.StartListening();
            });
        });
    }

    /// <summary>Makes <see cref="_chatCts"/> the cancellation for a new turn (Stop, Esc and Clear Chat cancel it).</summary>
    private CancellationTokenSource BeginTurnCancellation()
    {
        var cts = new CancellationTokenSource();
        _chatCts = cts;
        return cts;
    }

    /// <summary>Clears and disposes a turn's cancellation once the turn is over, so Esc sees the app as idle.</summary>
    private void EndTurnCancellation(CancellationTokenSource cts)
    {
        if (ReferenceEquals(_chatCts, cts))
            _chatCts = null;
        cts.Dispose();
    }

    /// <summary>True while the running turn was stopped (Stop, Esc, Clear Chat): nothing more should be spoken.</summary>
    private bool IsCurrentTurnCancelled => _chatCts?.IsCancellationRequested == true;

    private void OnListenTimedOut()
    {
        Dispatcher.Invoke(() =>
        {
            // After a bare wake phrase, listen for the request even if the Listen switch was turned off meanwhile.
            if (_autoListening || _speech.HasWakePhraseFollowUp)
            {
                // No speech heard (or speech without the wake word), restart listening
                Task.Delay(300).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if ((_autoListening || _speech.HasWakePhraseFollowUp) &&
                            !_pausedListeningForTextInput && IsVoiceInputAllowedByFacePolicy())
                        {
                            if (_speech.CurrentState == VoiceState.Speaking)
                                return;

                            _speech.StartListening();
                        }
                    });
                });
            }
            else
            {
                SetUIState("idle", "Ready");
            }
        });
    }

    private void OnSpeechLog(string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (message.StartsWith("Transcription backend used", StringComparison.OrdinalIgnoreCase))
                AddDiagnosticMessage(message);
            else
                AddSystemMessage($"{message}");
        });
    }

    private void OnVoiceStateChanged(VoiceState state)
    {
        Dispatcher.Invoke(() =>
        {
            switch (state)
            {
                case VoiceState.Idle:
                    StateIndicator.Fill = FindResource("SuccessBrush") as SolidColorBrush;
                    if (!_autoListening) StateLabel.Text = "Ready";
                    break;
                case VoiceState.Listening:
                    StateIndicator.Fill = FindResource("ListeningBrush") as SolidColorBrush;
                    ShowListeningStatus();
                    _listenStartTime = DateTime.Now;
                    break;
                case VoiceState.Processing:
                    StateIndicator.Fill = FindResource("WarningBrush") as SolidColorBrush;
                    StateLabel.Text = "Processing...";
                    ActivityLabel.Text = _speech.IsWaitingForWakePhrase
                        ? "Checking for the wake word..."
                        : "Sending to Ollama...";
                    break;
                case VoiceState.Speaking:
                    StateIndicator.Fill = FindResource("SpeakingBrush") as SolidColorBrush;
                    StateLabel.Text = "Speaking...";
                    ActivityLabel.Text = "";
                    break;
            }
        });
    }

    // ==================== Button Handlers ====================

    /// <summary>
    /// The Listen / Stop listening switch (also Ctrl+L, the global hotkey and the tray): Listen keeps the
    /// microphone open for a hands-free conversation, Stop listening turns it off. UI thread.
    /// </summary>
    private void ToggleListening()
    {
        if (IsListeningOn)
        {
            AlwaysListenToggle.IsChecked = false;
            // A wake-word follow-up can listen with the switch off; Stop listening ends that too.
            if (_speech.CurrentState == VoiceState.Listening)
            {
                _speech.StopListening();
                SetUIState("idle", "Ready");
            }
            return;
        }

        AlwaysListenToggle.IsChecked = true;
    }

    /// <summary>True while the microphone is meant to be on: the switch is on, or a listening turn is running.</summary>
    private bool IsListeningOn => AlwaysListenToggle.IsChecked == true || _speech.CurrentState == VoiceState.Listening;

    /// <summary>The switch reads "Listen" when the microphone is off and "Stop listening" while it is on.</summary>
    private void UpdateListenToggleLook()
    {
        var on = AlwaysListenToggle.IsChecked == true;
        AlwaysListenToggle.Content = on ? "Stop listening" : "Listen";
        Ui.SetIcon(AlwaysListenToggle, on ? "\uE71A" : "\uE720");
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        _chatCts?.Cancel();
        // Also stops ComfyUI jobs the phone started without a cancellable token. Never throws.
        _ = _comfyImages.InterruptAsync();
        _speech.StopAll();
        _autoListening = false;
        AlwaysListenToggle.IsChecked = false;
        SetUIState("idle", "Ready");
        ActivityLabel.Text = "";
    }

    private void StartAutoListen()
    {
        if (!IsVoiceInputAllowedByFacePolicy())
        {
            _autoListening = false;
            AlwaysListenToggle.IsChecked = false;
            AddSystemMessage("Listening is blocked by local face policy.");
            return;
        }

        _autoListening = true;
        _pausedListeningForTextInput = false;
        // Like the old Listen button: pressing Listen while the assistant talks stops the talking first.
        if (_speech.CurrentState == VoiceState.Speaking)
            _speech.StopSpeaking();
        _speech.ReadyForNextSpeech();
        SetUIState("idle", "Listening - microphone on");
        _speech.StartListening();
    }

    private void StopAutoListen()
    {
        _autoListening = false;
        _pausedListeningForTextInput = false;
        _speech.StopListening();
        SetUIState("idle", "Ready");
    }

    private void PauseListeningForTextInput()
    {
        if (!_autoListening || _pausedListeningForTextInput)
            return;

        _pausedListeningForTextInput = true;
        _speech.StopListening();
        SetUIState("idle", "Typing - mic paused");
    }

    private void ResumeListeningAfterTextInput()
    {
        if (!_autoListening || !_pausedListeningForTextInput)
            return;

        _pausedListeningForTextInput = false;
        if (IsVoiceInputAllowedByFacePolicy())
        {
            SetUIState("idle", "Listening - microphone on");
            _speech.StartListening();
        }
    }

    private void TtsToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = TtsToggle.IsChecked == true;
        _speech.TtsEnabled = enabled;
    }

    private void StreamToggle_Click(object sender, RoutedEventArgs e)
    {
        // No extra logic needed, checked at send time
    }

    private void MicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplySelectedMicrophone();
        SaveSettings();
        if (MicCombo.SelectedItem is AudioDeviceInfo mic)
            AddSystemMessage($"Microphone set to: {mic.Name}");
    }

    private void RefreshMics_Click(object sender, RoutedEventArgs e)
    {
        var previous = _settings.MicDeviceIndex;
        _speech.RefreshMicrophones();
        PopulateMicrophoneCombo(previous);
        SaveSettings();
        AddSystemMessage("Microphone list refreshed. If your earbuds are connected for calls/input, choose them from the Microphone dropdown.");
    }

    private async void DownloadModel_Click(object sender, RoutedEventArgs e)
    {
        var size = (WhisperModelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "base";
        _settings.WhisperModelSize = size;
        _speech.WhisperModelPath = GetWhisperModelPath(size);
        SaveSettings();
        DownloadModelBtn.IsEnabled = false;
        DownloadModelBtn.Content = $"... Downloading {size}...";

        try
        {
            await _speech.DownloadModelAsync(size, new Progress<float>(p =>
            {
                Dispatcher.Invoke(() =>
                {
                    DownloadModelBtn.Content = $"... {p * 100:F0}%";
                });
            }));

            Dispatcher.Invoke(() =>
            {
                AddSystemMessage($"Whisper {size} model downloaded! You can now use voice recognition.");
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                AddSystemMessage($"Download failed: {ex.Message}");
            });
        }
        finally
        {
            Dispatcher.Invoke(() =>
            {
                DownloadModelBtn.IsEnabled = true;
                DownloadModelBtn.Content = "Download Model";
            });
        }
    }
}
