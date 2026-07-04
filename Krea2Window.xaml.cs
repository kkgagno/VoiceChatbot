using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace VoiceChatbot;

public partial class Krea2Window : Window
{
    private readonly ComfyUiImageClient _comfyImages;
    private readonly IReadOnlyList<string> _aspectRatios;
    private string _latestImagePath = "";

    public Krea2Window(ComfyUiImageClient comfyImages, IReadOnlyList<string> aspectRatios)
    {
        InitializeComponent();
        _comfyImages = comfyImages;
        _aspectRatios = aspectRatios.Count > 0
            ? aspectRatios
            : new[]
            {
                "1:1 (Square)",
                "3:2 (Photo)",
                "4:3 (Standard)",
                "16:9 (Widescreen)",
                "21:9 (Ultrawide)",
                "2:3 (Portrait Photo)",
                "3:4 (Portrait Standard)",
                "9:16 (Portrait Widescreen)"
            };

        foreach (var ratio in _aspectRatios)
            AspectCombo.Items.Add(ratio);
        AspectCombo.SelectedIndex = 0;
        EnableLoraToggle.IsChecked = false;
        LoraCombo.IsEnabled = false;
        Loaded += async (_, _) => await RefreshLorasAsync();
    }

    private async void RefreshLoras_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLorasAsync();
    }

    private async Task RefreshLorasAsync()
    {
        RefreshLorasBtn.IsEnabled = false;
        StatusText.Text = "Loading Krea2 LoRAs from ComfyUI...";
        try
        {
            var selected = LoraCombo.SelectedItem?.ToString() ?? "";
            LoraCombo.Items.Clear();
            var loras = await _comfyImages.ListKrea2LorasAsync(CancellationToken.None);
            foreach (var lora in loras)
                LoraCombo.Items.Add(lora);

            if (LoraCombo.Items.Count > 0)
            {
                var index = loras.ToList().FindIndex(l => l.Equals(selected, StringComparison.OrdinalIgnoreCase));
                LoraCombo.SelectedIndex = index >= 0 ? index : 0;
                StatusText.Text = $"Loaded {LoraCombo.Items.Count} Krea2 LoRA(s).";
            }
            else
            {
                StatusText.Text = "No LoRAs starting with krea2 were found.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not load LoRAs: {ex.Message}";
        }
        finally
        {
            RefreshLorasBtn.IsEnabled = true;
            UpdateLoraUi();
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var prompt = PromptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            StatusText.Text = "Enter a Krea2 prompt first.";
            return;
        }

        var enableLora = EnableLoraToggle.IsChecked == true;
        var loraName = enableLora ? LoraCombo.SelectedItem?.ToString() ?? "" : "";
        if (enableLora && string.IsNullOrWhiteSpace(loraName))
        {
            StatusText.Text = "Enable LoRA is on, but no Krea2 LoRA is selected.";
            return;
        }

        CreateBtn.IsEnabled = false;
        RefreshLorasBtn.IsEnabled = false;
        SaveImageBtn.IsEnabled = false;
        ResultImage.Source = null;
        ResultText.Text = "";
        StatusText.Text = "Creating Krea2 image on ComfyUI...";

        try
        {
            var result = await _comfyImages.CreateKrea2ImageAsync(
                prompt,
                enableLora,
                loraName,
                AspectCombo.SelectedItem?.ToString() ?? _aspectRatios[0],
                CancellationToken.None);

            _latestImagePath = result.LocalPath;
            ResultText.Text = $"Created with {result.WorkflowName}\n{result.LocalPath}";
            ResultImage.Source = LoadBitmap(result.LocalPath);
            SaveImageBtn.IsEnabled = File.Exists(_latestImagePath);
            StatusText.Text = "Krea2 image ready.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Krea2 failed: {ex.Message}";
            ResultText.Text = ex.ToString();
        }
        finally
        {
            CreateBtn.IsEnabled = true;
            RefreshLorasBtn.IsEnabled = true;
        }
    }

    private void EnableLoraToggle_Changed(object sender, RoutedEventArgs e)
    {
        UpdateLoraUi();
    }

    private void UpdateLoraUi()
    {
        LoraCombo.IsEnabled = EnableLoraToggle.IsChecked == true && LoraCombo.Items.Count > 0;
    }

    private void SaveImage_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_latestImagePath) || !File.Exists(_latestImagePath))
        {
            StatusText.Text = "Image file is no longer available.";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save Krea2 image",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg;*.jpeg|All files (*.*)|*.*",
            FileName = Path.GetFileName(_latestImagePath),
            DefaultExt = Path.GetExtension(_latestImagePath)
        };

        if (dialog.ShowDialog(this) != true)
            return;

        File.Copy(_latestImagePath, dialog.FileName, overwrite: true);
        StatusText.Text = $"Image saved to {dialog.FileName}";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(path);
        image.EndInit();
        image.Freeze();
        return image;
    }
}
