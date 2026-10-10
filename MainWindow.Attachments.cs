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
        // Decide from the data being pasted (or dropped): text, including spreadsheet cells that also
        // carry a picture of the selection, goes into the box as usual.
        if (GetPasteKind(e.DataObject) != ClipboardPasteKind.Image)
            return;

        if (TryAttachClipboardImage())
            e.CancelCommand();
    }

    /// <summary>
    /// Ctrl+V: attaches the clipboard picture when there is one and no text. Returns false to let the
    /// message box paste normally (always the case when the clipboard has text, such as Excel cells).
    /// </summary>
    private bool TryAttachClipboardImage()
    {
        if (ReadClipboardPasteKind() != ClipboardPasteKind.Image)
            return false;

        try
        {
            var image = GetClipboardImage();
            if (image is null)
                return false;

            return AttachClipboardImage(image);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Clipboard image attach failed: {FriendlyErrors.Describe(ex)}");
            return true;
        }
    }

    private static ClipboardPasteKind ReadClipboardPasteKind()
    {
        try
        {
            return GetPasteKind(Clipboard.GetDataObject());
        }
        catch
        {
            // The clipboard can be locked by another app (CLIPBRD_E_CANT_OPEN): fall back to a normal paste.
            return ClipboardPasteKind.Nothing;
        }
    }

    private static ClipboardPasteKind GetPasteKind(IDataObject? data)
    {
        if (data is null)
            return ClipboardPasteKind.Nothing;

        try
        {
            var text = data.GetDataPresent(DataFormats.UnicodeText, autoConvert: true)
                ? data.GetData(DataFormats.UnicodeText, autoConvert: true) as string
                : null;
            return ClipboardPastePolicy.Decide(data.GetFormats(autoConvert: true), text);
        }
        catch
        {
            return ClipboardPasteKind.Nothing;
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
            AddSystemMessage($"Clipboard image attach failed: {FriendlyErrors.Describe(ex)}");
            return true;
        }
    }

    private async void AttachDocument_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Attach document",
            Filter = DocumentFileTypes.BuildOpenFileDialogFilter(),
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        // Reading runs on a worker thread (DocumentTextService), so the window keeps painting and
        // Stop/Esc can cancel a long OCR run. The input and attach buttons stay disabled until it ends.
        var files = dialog.FileNames;
        using var cts = new CancellationTokenSource();
        _chatCts = cts;
        var reading = true;
        SetUIState("processing", "Reading document...");
        ActivityLabel.Text = "... Reading document...";

        var added = 0;
        try
        {
            for (var i = 0; i < files.Length; i++)
            {
                var fileName = files[i];
                var name = System.IO.Path.GetFileName(fileName);
                var step = files.Length > 1 ? $"Reading {name} ({i + 1} of {files.Length})" : $"Reading {name}";
                StateLabel.Text = step + "...";
                var progress = new Progress<string>(detail =>
                {
                    // Progress reports are posted; ignore any that arrive after reading finished.
                    if (reading)
                        StateLabel.Text = $"{step}: {detail}";
                });

                var result = await _documentText.ExtractAsync(fileName, cts.Token, progress);
                if (!string.IsNullOrWhiteSpace(result.Text))
                {
                    _pendingDocuments.Add(new PendingDocumentAttachment(fileName, result));
                    added++;
                    if (!string.IsNullOrWhiteSpace(result.Notice))
                        AddSystemMessage($"{name}: {result.Notice}");
                }
                else
                {
                    AddSystemMessage($"Could not read {name}: {result.Error}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            AddSystemMessage("Document reading cancelled.");
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Document attach failed: {ex.Message}");
        }
        finally
        {
            reading = false;
            if (ReferenceEquals(_chatCts, cts))
                _chatCts = null;
            SetUIState("idle", "Ready");
        }

        if (added > 0)
        {
            AddSystemMessage($"{added} document(s) ready for your next message.");
            UpdateDocumentButtonLabel();
            MessageInput.Focus();
            MessageInput.CaretIndex = MessageInput.Text.Length;
        }
    }
}
