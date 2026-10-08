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
    // ==================== Connection ====================

    private async Task TestConnection()
    {
        var connected = await _ollama.PingAsync();
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = connected ? $"Connected ({_settings.ChatProvider})" : $"Disconnected ({_settings.ChatProvider})";
            StatusText.Foreground = connected ? FindResource("SuccessBrush") as SolidColorBrush : FindResource("ErrorBrush") as SolidColorBrush;
            StatusDot.Fill = connected ? FindResource("SuccessBrush") as SolidColorBrush : FindResource("ErrorBrush") as SolidColorBrush;
            UpdateActiveModelText();
        });
    }

    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        await RefreshModelsInternal();
    }

    private async void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ollama == null)
            return;

        _settings.ChatProvider = GetSelectedProvider();
        ConfigureChatClient();
        SaveSettings();
        await TestConnection();
    }

    private async Task RefreshModelsInternal()
    {
        RefreshModelsBtn.IsEnabled = false;
        RefreshModelsBtn.Content = "... Loading...";
        try
        {
            var previousModel = ModelCombo.Text;
            var models = await _ollama.ListModelsAsync();
            ModelCombo.Items.Clear();
            foreach (var m in models)
                ModelCombo.Items.Add(m);
            if (models.Count > 0)
            {
                if (!string.IsNullOrWhiteSpace(previousModel) && models.Contains(previousModel))
                    ModelCombo.SelectedItem = previousModel;
                else if (!string.IsNullOrWhiteSpace(_settings.Model) && models.Contains(_settings.Model))
                    ModelCombo.SelectedItem = _settings.Model;
                else
                    ModelCombo.SelectedIndex = 0;

                _settings.Model = ModelCombo.Text;
            }

            var endpoint = _settings.ChatProvider.Equals("OpenAI-compatible", StringComparison.OrdinalIgnoreCase)
                ? _settings.OpenAiCompatibleUrl
                : _settings.OllamaUrl;
            AddSystemMessage($"Loaded {models.Count} model(s) from {_settings.ChatProvider}: {endpoint}");

            await TestConnection();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Failed to load models: {ex.Message}");
        }
        finally
        {
            RefreshModelsBtn.IsEnabled = true;
            RefreshModelsBtn.Content = "Refresh Models";
        }
    }

    private sealed record ModelBatchOption(string DisplayName, string BatchPath, int? EndpointPort)
    {
        public override string ToString() => DisplayName;
    }

    private async Task InitializeDesktopServiceControlsAsync()
    {
        try
        {
            await RefreshAvailableModelBatchesAsync();
            _desktopModelRunning = await _ollama.PingAsync();
            _desktopRunningModel = _desktopModelRunning ? ModelCombo.Text : "";
            _comfyUiRunning = await IsComfyUiReachableAsync();
            ModelControlStatusText.Text = _desktopModelRunning
                ? $"Running model: {(_desktopRunningModel.Length > 0 ? _desktopRunningModel : "detected")}"
                : "No model detected.";
        }
        catch (Exception ex)
        {
            ModelControlStatusText.Text = $"Service check failed: {ex.Message}";
        }
        finally
        {
            UpdateDesktopServiceControls();
        }
    }

    private async Task RefreshAvailableModelBatchesAsync()
    {
        const string command = "find /mnt/c/llama.cpp -maxdepth 1 -type f -iname 'start-*.bat' -printf '%f\\n' | sort -f";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await _hermesSsh.RunAsync(command, TimeSpan.FromSeconds(15), timeout.Token);
        if (result.ExitStatus != 0 || result.TimedOut)
            throw new InvalidOperationException("Could not list model batch files over SSH.");

        var previous = (LaunchModelCombo.SelectedItem as ModelBatchOption)?.BatchPath;
        var options = result.Stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Trim())
            .Where(name => name.StartsWith("start-", StringComparison.OrdinalIgnoreCase))
            .Where(name => !name.Contains("comfyui", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name =>
            {
                var path = $"/mnt/c/llama.cpp/{name}";
                return new ModelBatchOption(
                    Path.GetFileNameWithoutExtension(name).Replace("start-", "", StringComparison.OrdinalIgnoreCase),
                    path,
                    GetEndpointPortForBatchFile(name));
            })
            .ToList();

        LaunchModelCombo.ItemsSource = options;
        LaunchModelCombo.SelectedItem = options.FirstOrDefault(option =>
            string.Equals(option.BatchPath, previous, StringComparison.OrdinalIgnoreCase));
        if (LaunchModelCombo.SelectedItem == null && options.Count > 0)
            LaunchModelCombo.SelectedIndex = 0;
    }

    private static int? GetEndpointPortForBatchFile(string batchFile)
    {
        var name = batchFile.ToLowerInvariant();
        if (name.Contains("gpt-oss-120b")) return 8084;
        if (name.Contains("mistral-medium")) return 8082;
        if (name.Contains("qwen3.6-27b")) return 8081;
        if (name.Contains("lfm2.5-8b")) return 8083;
        if (name.Contains("gemma4-12b")) return 8083;
        if (name.Contains("gemma4-4b")) return 8081;
        if (name.Contains("gemma4")) return 8080;
        return null;
    }

    private static bool CanRunComfyUiAlongsideModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || !model.Contains("gemma", StringComparison.OrdinalIgnoreCase))
            return false;

        return Regex.IsMatch(model, @"(?:^|[^0-9])(?:4b|12b)(?:[^0-9]|$)", RegexOptions.IgnoreCase);
    }

    private async Task<bool> IsComfyUiReachableAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await http.GetAsync($"{_settings.ComfyUiUrl.TrimEnd('/')}/system_stats", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateDesktopServiceControls()
    {
        var canLaunchModel = !_serviceControlBusy && !_desktopModelRunning;
        StopCurrentModelBtn.IsEnabled = !_serviceControlBusy && _desktopModelRunning;
        LaunchModelCombo.IsEnabled = canLaunchModel;
        StartSelectedModelBtn.IsEnabled = canLaunchModel && LaunchModelCombo.SelectedItem is ModelBatchOption;

        var comfyAllowed = !_desktopModelRunning || CanRunComfyUiAlongsideModel(_desktopRunningModel);
        ComfyUiStartStopBtn.IsEnabled = !_serviceControlBusy && comfyAllowed;
        ComfyUiStartStopBtn.Content = _comfyUiRunning ? "Stop ComfyUI" : "Start ComfyUI";
        ComfyUiStartStopBtn.ToolTip = comfyAllowed
            ? null
            : "ComfyUI is available only when no model, Gemma 4B, or Gemma 4 12B is running.";
    }

    private async void StopCurrentModel_Click(object sender, RoutedEventArgs e)
    {
        var plan = new LlamaModelControlPlan(
            "Stopping current llama.cpp model processes",
            BuildStopLlamaCommand(),
            null);
        await RunDesktopServiceControlAsync(plan, modelWillBeRunning: false, runningModel: "");
    }

    private async void StartSelectedModel_Click(object sender, RoutedEventArgs e)
    {
        if (LaunchModelCombo.SelectedItem is not ModelBatchOption option)
            return;

        var label = Path.GetFileNameWithoutExtension(option.BatchPath).Replace("start-", "", StringComparison.OrdinalIgnoreCase);
        var plan = new LlamaModelControlPlan(
            $"Starting llama.cpp model using {Path.GetFileName(option.BatchPath)}",
            BuildStartLlamaBatchCommand(option.BatchPath, label),
            option.EndpointPort);
        await RunDesktopServiceControlAsync(plan, modelWillBeRunning: true, runningModel: option.BatchPath);
    }

    private async void ComfyUiStartStop_Click(object sender, RoutedEventArgs e)
    {
        var prompt = _comfyUiRunning ? "stop comfyui" : "start comfyui";
        if (!TryBuildLlamaModelControlPlan(prompt, out var plan))
            return;

        if (await RunDesktopServiceControlAsync(plan, modelWillBeRunning: null, runningModel: null))
        {
            _comfyUiRunning = !_comfyUiRunning;
            ModelControlStatusText.Text = _comfyUiRunning ? "ComfyUI start command sent." : "ComfyUI stop command sent.";
            UpdateDesktopServiceControls();
        }
    }

    private async Task<bool> RunDesktopServiceControlAsync(
        LlamaModelControlPlan plan,
        bool? modelWillBeRunning,
        string? runningModel)
    {
        _serviceControlBusy = true;
        ModelControlStatusText.Text = plan.Description;
        UpdateDesktopServiceControls();

        try
        {
            using var operationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(105));
            var display = await RunLlamaModelControlPlanAsync(plan, operationTimeout.Token);
            AddSystemMessage(display);

            if (plan.EndpointPort is int port)
            {
                var endpoint = SetLlamaEndpointPort(port);
                var readiness = await WaitForLlamaEndpointReadyAsync(endpoint, TimeSpan.FromSeconds(90), operationTimeout.Token);
                AddSystemMessage(readiness.Message);
                if (!readiness.Ready)
                    throw new InvalidOperationException(readiness.Message);
                await RefreshModelsInternal();
            }

            if (modelWillBeRunning.HasValue)
            {
                _desktopModelRunning = modelWillBeRunning.Value;
                _desktopRunningModel = runningModel ?? "";
                ModelControlStatusText.Text = _desktopModelRunning
                    ? $"Running model: {Path.GetFileNameWithoutExtension(_desktopRunningModel)}"
                    : "No model running. Select a batch file to launch.";
            }

            return true;
        }
        catch (Exception ex)
        {
            ModelControlStatusText.Text = $"Control failed: {ex.Message}";
            AddSystemMessage($"Model/Comfy control failed: {ex.Message}");
            return false;
        }
        finally
        {
            _serviceControlBusy = false;
            UpdateDesktopServiceControls();
        }
    }

    private async Task RunOnUiAsync(Func<Task> action)
    {
        if (Dispatcher.CheckAccess())
        {
            await action();
            return;
        }

        var operation = Dispatcher.InvokeAsync(action);
        await await operation;
    }

    private PhoneRemoteModelState GetPhoneRemoteModelState()
    {
        PhoneRemoteModelState ReadState()
        {
            var provider = _settings.ChatProvider;
            var model = !string.IsNullOrWhiteSpace(ModelCombo?.Text) ? ModelCombo.Text : _settings.Model;
            var endpoint = _settings.ChatProvider.Equals("OpenAI-compatible", StringComparison.OrdinalIgnoreCase)
                ? _settings.OpenAiCompatibleUrl
                : _settings.OllamaUrl;
            return new PhoneRemoteModelState(provider, model, endpoint);
        }

        return Dispatcher.CheckAccess()
            ? ReadState()
            : Dispatcher.Invoke(ReadState);
    }
}
