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
    private async void CameraAsk_Click(object sender, RoutedEventArgs e)
    {
        SetUIState("processing", "Capturing camera...");

        try
        {
            var photo = await _camera.CapturePhotoAsync();
            _pendingImages.Add(new PendingImageAttachment(photo.Path, photo.Base64));
            AddSystemMessage("Camera photo ready for your next message.");
            UpdateImageButtonLabel();
            MessageInput.Focus();
            MessageInput.CaretIndex = MessageInput.Text.Length;
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Camera capture failed: {ex.Message}");
        }
        finally
        {
            SetUIState("idle", "Ready");
        }
    }

    private void AttachImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach image",
            Filter = "Image files (*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp)|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|All files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        foreach (var fileName in dialog.FileNames)
        {
            try
            {
                var bytes = File.ReadAllBytes(fileName);
                _pendingImages.Add(new PendingImageAttachment(fileName, Convert.ToBase64String(bytes)));
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Could not attach {System.IO.Path.GetFileName(fileName)}: {ex.Message}");
            }
        }

        if (_pendingImages.Count > 0)
        {
            AddSystemMessage($"{_pendingImages.Count} image(s) ready for your next message.");
            UpdateImageButtonLabel();
            MessageInput.Focus();
            MessageInput.CaretIndex = MessageInput.Text.Length;
        }
    }

    private void MessageInput_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!ClipboardHasImage())
            return;

        if (TryAttachClipboardImage())
            e.CancelCommand();
    }

    private bool TryAttachClipboardImage()
    {
        try
        {
            var image = GetClipboardImage();
            if (image is null)
                return false;

            return AttachClipboardImage(image);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Clipboard image attach failed: {ex.Message}");
            return true;
        }
    }

    private static bool ClipboardHasImage()
    {
        try
        {
            return Clipboard.ContainsImage()
                || Clipboard.ContainsData(DataFormats.Bitmap)
                || Clipboard.ContainsData("PNG")
                || Clipboard.ContainsData("DeviceIndependentBitmap");
        }
        catch
        {
            return false;
        }
    }

    private static BitmapSource? GetClipboardImage()
    {
        if (Clipboard.ContainsImage())
            return Clipboard.GetImage();

        if (Clipboard.ContainsData(DataFormats.Bitmap) && Clipboard.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            return bitmap;

        if (Clipboard.ContainsData("PNG") && Clipboard.GetData("PNG") is Stream pngStream)
        {
            var decoder = BitmapDecoder.Create(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder.Frames.FirstOrDefault();
        }

        return null;
    }

    private bool AttachClipboardImage(BitmapSource image)
    {
        try
        {
            if (image is null)
                return false;

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VoiceChatbot",
                "clipboard-images");
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, $"clipboard-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(path))
                encoder.Save(stream);

            var bytes = File.ReadAllBytes(path);
            _pendingImages.Add(new PendingImageAttachment(path, Convert.ToBase64String(bytes)));
            AddSystemMessage("Clipboard image ready for your next message.");
            UpdateImageButtonLabel();
            MessageInput.Focus();
            MessageInput.CaretIndex = MessageInput.Text.Length;
            return true;
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Clipboard image attach failed: {ex.Message}");
            return true;
        }
    }

    private async void CreateImage_Click(object sender, RoutedEventArgs e)
    {
        var prompt = MessageInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            AddSystemMessage("Type an image prompt first.");
            return;
        }

        MessageInput.Clear();
        ResumeListeningAfterTextInput();
        await RunQwenImageCreateAsync($"Create image: {prompt}", prompt);
    }

    private async void EditImage_Click(object sender, RoutedEventArgs e)
    {
        var prompt = MessageInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            AddSystemMessage("Type an edit instruction first.");
            return;
        }

        MessageInput.Clear();
        ResumeListeningAfterTextInput();
        await RunQwenImageEditAsync($"Edit image: {prompt}", prompt);
    }

    private void AttachVideoAudio_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach video speech audio",
            Filter = "Audio files (*.wav;*.mp3;*.m4a;*.flac;*.ogg)|*.wav;*.mp3;*.m4a;*.flac;*.ogg|All files (*.*)|*.*",
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
            return;

        _pendingVideoAudioPath = dialog.FileName;
        AddSystemMessage($"Video audio ready: {Path.GetFileName(_pendingVideoAudioPath)}");
        UpdateAudioButtonLabel();
        MessageInput.Focus();
        MessageInput.CaretIndex = MessageInput.Text.Length;
    }

    private async void CreateVideo_Click(object sender, RoutedEventArgs e)
    {
        var prompt = MessageInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            AddSystemMessage("Type a video prompt first.");
            return;
        }

        MessageInput.Clear();
        ResumeListeningAfterTextInput();
        await RunLtxVideoAsync($"Create video: {prompt}", prompt, TryParseVideoSeconds(prompt));
    }

    private async void AttachDocument_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach document",
            Filter = "Documents (*.pdf;*.docx;*.txt;*.md;*.csv;*.json;*.xml;*.log)|*.pdf;*.docx;*.txt;*.md;*.csv;*.json;*.xml;*.log|All files (*.*)|*.*",
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        SetUIState("processing", "Reading document...");
        try
        {
            var added = 0;
            foreach (var fileName in dialog.FileNames)
            {
                var result = await _documentText.ExtractAsync(fileName);
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    _pendingDocuments.Add(new PendingDocumentAttachment(fileName, result));
                    added++;
                }
                else
                {
                    AddSystemMessage($"Could not read {System.IO.Path.GetFileName(fileName)}: {result.Error}");
                }
            }

            if (added > 0)
            {
                AddSystemMessage($"{added} document(s) ready for your next message.");
                UpdateDocumentButtonLabel();
                MessageInput.Focus();
                MessageInput.CaretIndex = MessageInput.Text.Length;
            }
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Document attach failed: {ex.Message}");
        }
        finally
        {
            SetUIState("idle", "Ready");
        }
    }
}
