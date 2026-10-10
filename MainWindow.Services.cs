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

namespace VoiceChatbot;

public partial class MainWindow
{
    // ==================== Connection ====================

    private async Task TestConnection()
    {
        if (IsBuiltInProvider)
        {
            ShowBuiltInConnectionStatus();
            return;
        }

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

        UpdateProviderPanels();
        // Startup and the model chooser set the provider themselves.
        if (_applyingSettings)
            return;

        _settings.ChatProvider = GetSelectedProvider();
        // Picked by hand: a download chosen earlier no longer switches the provider back.
        _switchToModelWhenDownloaded = null;
        if (IsBuiltInProvider)
            ShowBuiltInModelInCombo();
        else
            ShowSavedModelInCombo();
        ConfigureChatClient();
        SaveSettings();
        EnsureLocalModelRunning();
        await TestConnection();
    }

    private async Task RefreshModelsInternal()
    {
        if (IsBuiltInProvider)
        {
            ShowBuiltInModelInCombo();
            await TestConnection();
            return;
        }

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

            AddSystemMessage($"Loaded {models.Count} model(s) from {_settings.ChatProvider}: {CurrentChatEndpoint}");
            // The server may have been restarted with another context size (-c): ask it again.
            ScheduleContextWindowStatusRefresh(forceDetect: true);

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

    // Tavily errors (bad key, used-up credits, rate limit) are shown as they happen, before the
    // "answering from model knowledge" note, from desktop, phone and scheduled searches alike.
    private void ShowWebSearchFailure(string message)
    {
        if (Dispatcher.CheckAccess())
            AddSystemMessage(message);
        else
            Dispatcher.InvokeAsync(() => AddSystemMessage(message));
    }

    private PhoneRemoteModelState GetPhoneRemoteModelState()
    {
        PhoneRemoteModelState ReadState()
        {
            var provider = _settings.ChatProvider;
            var model = !string.IsNullOrWhiteSpace(ModelCombo?.Text) ? ModelCombo.Text : _settings.Model;
            return new PhoneRemoteModelState(provider, model, CurrentChatEndpoint);
        }

        return Dispatcher.CheckAccess()
            ? ReadState()
            : Dispatcher.Invoke(ReadState);
    }
}
