using System;
using System.Threading.Tasks;
using System.Windows;

namespace VoiceChatbot;

public partial class MainWindow
{
    // ==================== Whisper on the GPU ====================
    // "Use GPU for speech recognition" (Voice Input): Whisper.net runs on a Vulkan GPU when there is
    // one (SpeechEngine.WhisperRuntime.cs). The line under the switch shows what the model runs on.

    private bool _whisperGpuWired;

    // ApplySettings, before speech init: the engine reads the setting when the first model loads.
    private void ApplyWhisperGpuSettings()
    {
        if (!_whisperGpuWired)
        {
            _whisperGpuWired = true;
            _speech.WhisperRuntimeChanged += runtime => Dispatcher.BeginInvoke(() => OnWhisperRuntimeChanged(runtime));
        }

        WhisperGpuToggle.IsChecked = _settings.WhisperUseGpu;
        _speech.WhisperUseGpu = _settings.WhisperUseGpu;
        UpdateWhisperRuntimeText();
    }

    private void SaveWhisperGpuSettings() => _settings.WhisperUseGpu = WhisperGpuToggle.IsChecked == true;

    private async void WhisperGpuToggle_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        var useGpu = WhisperGpuToggle.IsChecked == true;
        WhisperRuntimeText.Text = useGpu ? "Moving Whisper to the GPU..." : "Moving Whisper to the CPU...";
        try
        {
            // Reloads the model under the transcription lock; keep it off the UI thread.
            await Task.Run(() => _speech.SetWhisperUseGpuAsync(useGpu));
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not change where Whisper runs: {ex.Message}");
        }
        UpdateWhisperRuntimeText();
    }

    // Once per model load: log what Whisper runs on.
    private void OnWhisperRuntimeChanged(string runtime)
    {
        AddDiagnosticMessage(runtime);
        UpdateWhisperRuntimeText();
    }

    private void UpdateWhisperRuntimeText()
    {
        var runtime = _speech.WhisperRuntime;
        WhisperRuntimeText.Text = runtime.Length > 0 ? runtime : "Whisper: loads with the first model";
        WhisperRuntimeText.ToolTip = runtime.Length > 0 ? runtime : null;
    }
}
