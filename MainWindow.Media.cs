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
    private async Task RunQwenImageCreateAsync(string userText, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        _chatCts = new CancellationTokenSource();
        SaveImageSettingsFromUi();
        AddUserMessage(userText);
        _history.Add("user", userText);
        var assistantMessage = AddAssistantMessage("Creating image with Qwen Image on ComfyUI...");
        SetUIState("processing", "Creating image...");

        try
        {
            var result = await _comfyImages.CreateQwenImageAsync(
                prompt,
                _settings.ImageWidth,
                _settings.ImageHeight,
                _settings.QwenCreateSteps,
                _chatCts.Token);

            _latestGeneratedImagePath = result.LocalPath;
            assistantMessage.Body.Text = BuildGeneratedImageMessage(result);
            AddGeneratedImageToAssistantMessage(assistantMessage, result.LocalPath);
            _history.Add("assistant", BuildGeneratedImageHistoryText(result));
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Body.Text = "Image creation cancelled.";
        }
        catch (Exception ex)
        {
            AppLog.Error("ComfyUI image creation failed", ex);
            assistantMessage.Body.Text = $"Image creation failed: {ex.Message}";
            AddSystemMessage($"ComfyUI image error: {ex.Message}");
        }
        finally
        {
            SetUIState("idle", "Ready");
        }
    }

    private async Task RunQwenImageEditAsync(string userText, string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        var sourcePaths = GetImageEditSourcePaths();
        if (sourcePaths.Count == 0)
        {
            AddSystemMessage("Attach an image first, or create an image before using Edit.");
            return;
        }

        _chatCts = new CancellationTokenSource();
        SaveImageSettingsFromUi();
        AddUserMessage(userText, sourcePaths);
        var sourceNames = string.Join(", ", sourcePaths.Select(Path.GetFileName));
        _history.Add("user", $"{userText}\n\nImage edit source(s): {sourceNames}");
        var isTwoImageEdit = sourcePaths.Count >= 2;
        var assistantMessage = AddAssistantMessage(isTwoImageEdit
            ? "Mixing images with Qwen Image Edit two-image workflow on ComfyUI..."
            : "Editing image with Qwen Image Edit on ComfyUI...");
        SetUIState("processing", "Editing image...");

        try
        {
            var result = isTwoImageEdit
                ? await _comfyImages.EditQwenImagesAsync(
                    sourcePaths,
                    prompt,
                    _settings.QwenEditSteps,
                    _chatCts.Token)
                : await _comfyImages.EditQwenImageAsync(
                    sourcePaths[0],
                    prompt,
                    _settings.QwenEditSteps,
                    _chatCts.Token);

            _latestGeneratedImagePath = result.LocalPath;
            _pendingImages.Clear();
            UpdateImageButtonLabel();
            assistantMessage.Body.Text = BuildGeneratedImageMessage(result);
            AddGeneratedImageToAssistantMessage(assistantMessage, result.LocalPath);
            _history.Add("assistant", BuildGeneratedImageHistoryText(result));
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Body.Text = "Image edit cancelled.";
        }
        catch (Exception ex)
        {
            AppLog.Error("ComfyUI image edit failed", ex);
            assistantMessage.Body.Text = $"Image edit failed: {ex.Message}";
            AddSystemMessage($"ComfyUI edit error: {ex.Message}");
        }
        finally
        {
            SetUIState("idle", "Ready");
        }
    }

    private async Task RunLtxVideoAsync(string userText, string prompt, int? requestedSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;

        var sourcePath = GetVideoSourceImagePath();
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            AddSystemMessage("Attach an image first, or create an image before using Video.");
            return;
        }

        _chatCts = new CancellationTokenSource();
        SaveImageSettingsFromUi();
        var seconds = requestedSeconds.HasValue ? Math.Clamp(requestedSeconds.Value, 1, 30) : _settings.VideoSeconds;
        var audioPath = File.Exists(_pendingVideoAudioPath) ? _pendingVideoAudioPath : null;
        AddUserMessage(userText, new[] { sourcePath });
        _history.Add("user", $"{userText}\n\nVideo source image: {Path.GetFileName(sourcePath)}" +
            (string.IsNullOrWhiteSpace(audioPath) ? "" : $"\nVideo speech audio: {Path.GetFileName(audioPath)}"));
        var assistantMessage = AddAssistantMessage(string.IsNullOrWhiteSpace(audioPath)
            ? "Creating video with video_ltx2_3_i2v on ComfyUI..."
            : "Creating video with video_ltx2_3_ia2v on ComfyUI...");
        SetUIState("processing", "Creating video...");

        try
        {
            var result = await _comfyImages.CreateLtxVideoAsync(
                sourcePath,
                audioPath,
                prompt,
                seconds,
                _settings.VideoFps,
                _chatCts.Token);
            var syncedVideoPath = await CopyVideoToSyncedDirectoryAsync(result.LocalPath, CancellationToken.None);

            _latestGeneratedVideoPath = result.LocalPath;
            _pendingImages.Clear();
            _pendingVideoAudioPath = "";
            UpdateImageButtonLabel();
            UpdateAudioButtonLabel();
            assistantMessage.Body.Text = BuildGeneratedVideoMessage(result, syncedVideoPath);
            AddGeneratedVideoToAssistantMessage(assistantMessage, result.LocalPath);
            _history.Add("assistant", BuildGeneratedVideoHistoryText(result, syncedVideoPath));
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Body.Text = "Video creation cancelled.";
        }
        catch (Exception ex)
        {
            AppLog.Error("ComfyUI video creation failed", ex);
            assistantMessage.Body.Text = $"Video creation failed: {ex.Message}";
            AddSystemMessage($"ComfyUI video error: {ex.Message}");
        }
        finally
        {
            SetUIState("idle", "Ready");
        }
    }

    private void SaveImageSettingsFromUi()
    {
        _settings.ComfyUiUrl = string.IsNullOrWhiteSpace(ComfyUrlBox.Text)
            ? "http://localhost:8000"
            : ComfyUrlBox.Text.Trim();
        _settings.ImageWidth = ParseBoundedInt(ImageWidthBox.Text, AppSettings.DefaultImageWidth, 256, 2048);
        _settings.ImageHeight = ParseBoundedInt(ImageHeightBox.Text, AppSettings.DefaultImageHeight, 256, 2048);
        _settings.QwenCreateSteps = ParseBoundedInt(QwenCreateStepsBox.Text, 4, 1, 80);
        _settings.QwenEditSteps = ParseBoundedInt(QwenEditStepsBox.Text, 40, 1, 80);
        _settings.VideoSeconds = ParseBoundedInt(VideoSecondsBox.Text, 6, 1, 30);
        _settings.VideoFps = ParseBoundedInt(VideoFpsBox.Text, 24, 1, 60);
        ConfigureImageClient();
        SettingsManager.Save(_settings);
    }

    private string GetImageEditSourcePath()
    {
        var attached = _pendingImages.FirstOrDefault(i => File.Exists(i.Path));
        if (attached is not null)
            return attached.Path;

        return File.Exists(_latestGeneratedImagePath) ? _latestGeneratedImagePath : "";
    }

    private List<string> GetImageEditSourcePaths()
    {
        var attached = _pendingImages
            .Where(i => File.Exists(i.Path))
            .Select(i => i.Path)
            .Take(2)
            .ToList();

        if (attached.Count > 0)
            return attached;

        return File.Exists(_latestGeneratedImagePath)
            ? new List<string> { _latestGeneratedImagePath }
            : new List<string>();
    }

    private string GetVideoSourceImagePath()
    {
        var attached = _pendingImages.FirstOrDefault(i => File.Exists(i.Path));
        if (attached is not null)
            return attached.Path;

        return File.Exists(_latestGeneratedImagePath) ? _latestGeneratedImagePath : "";
    }

    private static string BuildGeneratedImageMessage(GeneratedImageResult result)
    {
        return $"{result.WorkflowName} finished.\nPrompt: {result.Prompt}\nSaved: {result.LocalPath}";
    }

    private static string BuildGeneratedImageHistoryText(GeneratedImageResult result)
    {
        return $"{result.WorkflowName} generated an image.\nPrompt: {result.Prompt}\nLocal file: {result.LocalPath}\nRemote file: {result.RemoteFileName}";
    }

    private static string BuildGeneratedVideoMessage(GeneratedVideoResult result, string syncedVideoPath = "")
    {
        var syncedLine = string.IsNullOrWhiteSpace(syncedVideoPath)
            ? ""
            : $"\nGoogle Drive copy: {syncedVideoPath}";

        return $"{result.WorkflowName} finished.\nPrompt: {result.Prompt}\nLength: {result.Seconds} seconds at {result.Fps} FPS\nSaved: {result.LocalPath}{syncedLine}";
    }

    private static string BuildGeneratedVideoHistoryText(GeneratedVideoResult result, string syncedVideoPath = "")
    {
        var syncedLine = string.IsNullOrWhiteSpace(syncedVideoPath)
            ? ""
            : $"\nGoogle Drive copy: {syncedVideoPath}";

        return $"{result.WorkflowName} generated a video.\nPrompt: {result.Prompt}\nLength: {result.Seconds} seconds at {result.Fps} FPS\nLocal file: {result.LocalPath}{syncedLine}\nRemote file: {result.RemoteFileName}";
    }

    private static Task<string> CopyVideoToSyncedDirectoryAsync(string videoPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return Task.FromResult("");

        return Task.Run(() =>
        {
            Directory.CreateDirectory(SyncedVideoDirectory);

            var extension = Path.GetExtension(videoPath);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".mp4";

            var originalName = Path.GetFileNameWithoutExtension(videoPath);
            var safeName = Regex.Replace(originalName, @"[^\w\-. ]+", "_").Trim(' ', '.', '_');
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = "voicechatbot-video";

            var prefix = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var targetPath = Path.Combine(SyncedVideoDirectory, $"{prefix}_{safeName}{extension}");
            var suffix = 1;
            while (File.Exists(targetPath))
            {
                targetPath = Path.Combine(SyncedVideoDirectory, $"{prefix}_{safeName}_{suffix}{extension}");
                suffix++;
            }

            File.Copy(videoPath, targetPath, overwrite: false);
            return targetPath;
        }, ct);
    }

    private void AddGeneratedImageToAssistantMessage(AssistantMessageUi assistantMessage, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return;

        // Content, not Body.Parent: the body text box can be hidden or replaced by a rendered reply.
        var stack = assistantMessage.Content;
        var image = new Image
        {
            Source = new BitmapImage(new Uri(imagePath)),
            MaxWidth = 360,
            MaxHeight = 360,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 8, 0, 0)
        };
        stack.Children.Add(image);
        AddImageButtons(assistantMessage, imagePath);
        ScrollChat();
    }

    private void AddImageButtons(AssistantMessageUi assistantMessage, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return;

        assistantMessage.Actions.Visibility = Visibility.Visible;

        var saveBtn = new Button
        {
            Style = (Style)FindResource("BubbleActionButton"),
            Content = "Save Image",
            Tag = imagePath
        };
        saveBtn.Click += SaveGeneratedImage_Click;
        assistantMessage.Actions.Children.Add(saveBtn);
    }

    private void SaveGeneratedImage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string imagePath } || !File.Exists(imagePath))
        {
            AddSystemMessage("Image file is no longer available.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save image",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|All files (*.*)|*.*",
            FileName = Path.GetFileName(imagePath),
            AddExtension = true,
            DefaultExt = Path.GetExtension(imagePath)
        };

        if (dialog.ShowDialog(this) != true)
            return;

        File.Copy(imagePath, dialog.FileName, overwrite: true);
        AddSystemMessage($"Image saved to {dialog.FileName}");
    }

    private void AddGeneratedVideoToAssistantMessage(AssistantMessageUi assistantMessage, string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return;

        var stack = assistantMessage.Content;
        var player = new MediaElement
        {
            Source = new Uri(videoPath),
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Stop,
            MaxWidth = 420,
            Height = 240,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0, 8, 0, 0)
        };
        stack.Children.Add(player);
        AddVideoButtons(assistantMessage, videoPath, player);
        ScrollChat();
    }

    private void AddVideoButtons(AssistantMessageUi assistantMessage, string videoPath, MediaElement player)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            return;

        assistantMessage.Actions.Visibility = Visibility.Visible;

        var playBtn = new Button
        {
            Style = (Style)FindResource("BubbleActionButton"),
            Content = "Play Video"
        };
        playBtn.Click += (_, _) =>
        {
            player.Position = TimeSpan.Zero;
            player.Play();
        };
        assistantMessage.Actions.Children.Add(playBtn);

        var openBtn = new Button
        {
            Style = (Style)FindResource("BubbleActionButton"),
            Content = "Open Video",
            Tag = videoPath
        };
        openBtn.Click += OpenGeneratedVideo_Click;
        assistantMessage.Actions.Children.Add(openBtn);

        var saveBtn = new Button
        {
            Style = (Style)FindResource("BubbleActionButton"),
            Content = "Save Video",
            Tag = videoPath
        };
        saveBtn.Click += SaveGeneratedVideo_Click;
        assistantMessage.Actions.Children.Add(saveBtn);
    }

    private void OpenGeneratedVideo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string videoPath } || !File.Exists(videoPath))
        {
            AddSystemMessage("Video file is no longer available.");
            return;
        }

        Process.Start(new ProcessStartInfo(videoPath) { UseShellExecute = true });
    }

    private void SaveGeneratedVideo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string videoPath } || !File.Exists(videoPath))
        {
            AddSystemMessage("Video file is no longer available.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save video",
            Filter = "MP4 video (*.mp4)|*.mp4|WEBM video (*.webm)|*.webm|All files (*.*)|*.*",
            FileName = Path.GetFileName(videoPath),
            AddExtension = true,
            DefaultExt = Path.GetExtension(videoPath)
        };

        if (dialog.ShowDialog(this) != true)
            return;

        File.Copy(videoPath, dialog.FileName, overwrite: true);
        AddSystemMessage($"Video saved to {dialog.FileName}");
    }

    private static bool TryGetImageCreatePrompt(string text, out string prompt)
    {
        prompt = "";
        var trimmed = text.Trim();
        var patterns = new[]
        {
            @"^(?:please\s+)?(?:create|generate|make|draw)\s+(?:an?\s+)?image\s+(?:of|showing|with)?\s*(.+)$",
            @"^(?:please\s+)?(?:create|generate|make|draw)\s+(?:a\s+)?picture\s+(?:of|showing|with)?\s*(.+)$"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase);
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                prompt = match.Groups[1].Value.Trim();
                return true;
            }
        }

        return false;
    }

    private static bool TryGetVideoPrompt(string text, out string prompt, out int? seconds)
    {
        prompt = "";
        seconds = null;
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || !Regex.IsMatch(trimmed, @"\b(video|movie|clip)\b", RegexOptions.IgnoreCase))
            return false;

        var patterns = new[]
        {
            @"^(?:please\s+)?(?:create|generate|make)\s+(?:an?\s+)?(?:\d+\s*(?:second|seconds|sec|s)\s+)?(?:video|movie|clip)\s*(?:of|showing|with)?\s*(.+)$",
            @"^(?:please\s+)?(?:turn|make)\s+(?:this\s+)?(?:image|picture|photo)\s+into\s+(?:an?\s+)?(?:\d+\s*(?:second|seconds|sec|s)\s+)?(?:video|movie|clip)\s*(?:where|that|of|showing|with)?\s*(.*)$"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
                continue;

            seconds = TryParseVideoSeconds(trimmed);
            prompt = match.Groups.Count > 1 ? match.Groups[1].Value.Trim() : "";
            if (string.IsNullOrWhiteSpace(prompt))
                prompt = RemoveVideoCommandWords(trimmed);
            return true;
        }

        if (Regex.IsMatch(trimmed, @"\b(?:\d+\s*)?(?:second|seconds|sec|s)\s+video\b", RegexOptions.IgnoreCase))
        {
            seconds = TryParseVideoSeconds(trimmed);
            prompt = RemoveVideoCommandWords(trimmed);
            return true;
        }

        return false;
    }

    private static int? TryParseVideoSeconds(string text)
    {
        var match = Regex.Match(text, @"\b(?<n>\d{1,2})\s*(?:second|seconds|sec|s)\b", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups["n"].Value, out var seconds))
            return Math.Clamp(seconds, 1, 30);

        return null;
    }

    private static string RemoveVideoCommandWords(string text)
    {
        var cleaned = Regex.Replace(text, @"^(?:please\s+)?(?:create|generate|make|turn)\s+", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\b(?:an?\s+)?\d*\s*(?:second|seconds|sec|s)?\s*(?:video|movie|clip)\b", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
        return string.IsNullOrWhiteSpace(cleaned) ? text.Trim() : cleaned;
    }

    private static bool TryGetImageEditPrompt(string text, out string prompt)
    {
        prompt = "";
        var trimmed = text.Trim();
        var patterns = new[]
        {
            @"^(?:please\s+)?edit\s+(?:this\s+)?(?:image|picture|photo)?\s*(?:and|to)?\s*(.+)$",
            @"^(?:please\s+)?(?:change|modify)\s+(?:this\s+)?(?:image|picture|photo)\s+(?:to|and)?\s*(.+)$"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(trimmed, pattern, RegexOptions.IgnoreCase);
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                prompt = match.Groups[1].Value.Trim();
                return true;
            }
        }

        return false;
    }
}
