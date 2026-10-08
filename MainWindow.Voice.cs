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
        Dispatcher.Invoke(() =>
        {
            SetUIState("idle", "Ready");
            _speech.ReadyForNextSpeech();

            if (_autoListening)
            {
                // Restart recording for next utterance
                Task.Delay(100).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_autoListening && !_pausedListeningForTextInput && IsVoiceInputAllowedByFacePolicy())
                        {
                            if (_speech.CurrentState == VoiceState.Speaking)
                                return;

                            _speech.StartListening();
                        }
                    });
                });
            }
        });
    }

    private void OnListenTimedOut()
    {
        Dispatcher.Invoke(() =>
        {
            if (_autoListening)
            {
                // No speech heard, restart listening
                Task.Delay(300).ContinueWith(_ =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_autoListening && !_pausedListeningForTextInput && IsVoiceInputAllowedByFacePolicy())
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
                    StateLabel.Text = "Listening...";
                    ActivityLabel.Text = "Listening for speech...";
                    _listenStartTime = DateTime.Now;
                    break;
                case VoiceState.Processing:
                    StateIndicator.Fill = FindResource("WarningBrush") as SolidColorBrush;
                    StateLabel.Text = "Processing...";
                    ActivityLabel.Text = "Sending to Ollama...";
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

    private void MicToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_speech.CurrentState == VoiceState.Listening)
        {
            _speech.StopListening();
            _autoListening = false;
            AlwaysListenToggle.IsChecked = false;
            SetUIState("idle", "Ready");
        }
        else
        {
            if (!IsVoiceInputAllowedByFacePolicy())
            {
                AddSystemMessage("Voice input is blocked by local face policy.");
                return;
            }

            _speech.StopSpeaking();
            _speech.StartListening();
        }
    }

    private void ListenToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!IsVoiceInputAllowedByFacePolicy())
        {
            AddSystemMessage("Voice input is blocked by local face policy.");
            return;
        }

        _speech.StopSpeaking();
        _speech.ReadyForNextSpeech();
        if (!_speech.StartListening())
            SetUIState("idle", "Voice input unavailable");
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        _chatCts?.Cancel();
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
            AddSystemMessage("Auto-listen is blocked by local face policy.");
            return;
        }

        _autoListening = true;
        _pausedListeningForTextInput = false;
        SetUIState("idle", "Auto-listen active");
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
            SetUIState("idle", "Auto-listen active");
            _speech.StartListening();
        }
    }

    private void AutoDetectToggle_Click(object sender, RoutedEventArgs e)
    {
        var isAuto = AutoDetectToggle.IsChecked == true;
        WakeWordBox.IsEnabled = !isAuto;
        _speech.AutoDetect = isAuto;
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
