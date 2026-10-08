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
    // ==================== Face Presence ====================

    private async Task InitializeFacePresenceAsync()
    {
        await StopFacePresenceAsync();

        _facePolicy = new FaceAccessPolicyEvaluator(_settings.FaceFeatures.Policy);
        _faceIdentityManager = new FaceIdentityManager(
            FaceServiceFactory.CreateProfileStore(_settings.FaceFeatures.ModelOptions),
            _settings.FaceFeatures.ModelOptions);

        if (!_settings.FaceFeatures.CameraFeaturesEnabled)
        {
            _facePresenceState = FacePresenceState.CameraUnavailable;
            _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, FaceIdentity.Unknown);
            UpdateFacePresenceUi(_facePresenceState);
            await UpdateFaceSampleInfoAsync();
            return;
        }

        try
        {
            UpdateFacePresenceUi(FacePresenceState.CameraUnavailable);
            FacePresenceText.Text = "Manual scan mode";
            await UpdateFaceSampleInfoAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Face presence init failed: {ex.Message}");
            _facePresenceState = FacePresenceState.CameraUnavailable;
            _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, FaceIdentity.Unknown);
            UpdateFacePresenceUi(_facePresenceState);
            await UpdateFaceSampleInfoAsync();
        }
    }

    private async Task StopFacePresenceAsync()
    {
        var oldSnapshot = _latestFaceSnapshot;
        _latestFaceSnapshot = null;
        oldSnapshot?.Dispose();

        if (_facePresenceMonitor != null)
        {
            _facePresenceMonitor.StateChanged -= OnFacePresenceChanged;
            _facePresenceMonitor.DetectionUpdated -= OnFaceDetectionUpdated;
            try { await _facePresenceMonitor.StopAsync(); }
            catch (Exception ex) { Debug.WriteLine($"Face presence stop failed: {ex.Message}"); }
            _facePresenceMonitor.Dispose();
            _facePresenceMonitor = null;
        }
    }

    private void OnFacePresenceChanged(object? sender, FacePresenceState state)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _facePresenceState = state;
            _faceAccessDecision = _facePolicy.Evaluate(state, _recognizedFaceIdentity);
            UpdateFacePresenceUi(state);
            ApplyFaceAccessDecision();
        });
    }

    private void OnFaceDetectionUpdated(object? sender, FaceDetectionSnapshot snapshot)
    {
        var oldSnapshot = _latestFaceSnapshot;
        _latestFaceSnapshot = snapshot;
        oldSnapshot?.Dispose();

        if (snapshot.Result.State == FacePresenceState.FaceDetected)
        {
            var now = DateTime.UtcNow;
            if (now - _lastPreviewUpdatedUtc > TimeSpan.FromMilliseconds(700))
            {
                _lastPreviewUpdatedUtc = now;
                _ = Dispatcher.BeginInvoke(() => UpdateFacePreview(snapshot));
            }

            if (now - _lastRecognitionStartedUtc > TimeSpan.FromMilliseconds(1200))
            {
                _lastRecognitionStartedUtc = now;
                _ = RecognizeLatestFaceAsync(snapshot);
            }
        }
        else if (snapshot.Result.State == FacePresenceState.CameraUnavailable)
            _ = Dispatcher.BeginInvoke(() => MaybeExpireRecognizedIdentity());
    }

    private void UpdateFacePresenceUi(FacePresenceState state)
    {
        if (!_settings.FaceFeatures.CameraFeaturesEnabled)
        {
            FacePresenceText.Text = "Camera features off";
            FacePresenceDot.Fill = FindResource("TextMutedBrush") as Brush;
            return;
        }

        (FacePresenceText.Text, FacePresenceDot.Fill) = state switch
        {
            FacePresenceState.FaceDetected => ($"Face detected", FindResource("SuccessBrush") as SolidColorBrush ?? Brushes.Green),
            FacePresenceState.MultipleFacesDetected => ("Multiple faces", FindResource("WarningBrush") as SolidColorBrush ?? Brushes.Goldenrod),
            FacePresenceState.NoFaceDetected => ("No face detected", FindResource("WarningBrush") as SolidColorBrush ?? Brushes.Goldenrod),
            _ => ("Camera unavailable", FindResource("ErrorBrush") as SolidColorBrush ?? Brushes.Red)
        };
    }

    private bool IsVoiceInputAllowedByFacePolicy()
    {
        if (!_settings.FaceFeatures.FaceGatingEnabled)
            return true;

        _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, _recognizedFaceIdentity);
        return !_faceAccessDecision.ShouldMuteMicrophone && !_faceAccessDecision.ShouldBlockWakeWord;
    }

    private async Task RecognizeLatestFaceAsync(FaceDetectionSnapshot snapshot)
    {
        if (_recognitionInProgress || snapshot.Result.Faces.Length != 1)
            return;

        _recognitionInProgress = true;
        try
        {
            var result = await _faceIdentityManager.RecognizeAsync(snapshot.Frame, snapshot.Result.Faces[0]);
            _ = Dispatcher.BeginInvoke(() => UpdateRecognizedIdentity(result.Identity, result.DisplayName, result.Similarity, result.IsRecognized));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Face recognition failed: {ex.Message}");
        }
        finally
        {
            _recognitionInProgress = false;
        }
    }

    private void UpdateRecognizedIdentity(FaceIdentity identity, string displayName, float similarity, bool recognized)
    {
        var now = DateTime.UtcNow;
        displayName = string.IsNullOrWhiteSpace(displayName)
            ? (identity == FaceIdentity.Unknown ? "Unknown" : identity.ToString())
            : displayName.Trim();
        var sameAsCurrent = !string.IsNullOrWhiteSpace(_recognizedFaceName)
            && string.Equals(displayName, _recognizedFaceName, StringComparison.OrdinalIgnoreCase);
        var softMatch = sameAsCurrent && similarity >= 0.55f;
        var recentMatch = !string.IsNullOrWhiteSpace(_recognizedFaceName) && now - _lastRecognizedFaceUtc < TimeSpan.FromSeconds(20);

        if (recognized || softMatch)
        {
            _recognizedFaceIdentity = identity;
            _recognizedFaceName = displayName;
            _lastIdentifiedFaceIdentity = identity;
            _lastIdentifiedFaceName = displayName;
            _lastIdentifiedFaceUtc = now;
            _lastRecognizedFaceUtc = now;
            _lastRecognizedSimilarity = similarity;
        }
        else if (!recentMatch)
        {
            _recognizedFaceIdentity = FaceIdentity.Unknown;
            _recognizedFaceName = "";
            _lastRecognizedSimilarity = similarity;
        }

        FaceIdentityText.Text = !string.IsNullOrWhiteSpace(_recognizedFaceName)
            ? $"Identity: {_recognizedFaceName} | similarity {_lastRecognizedSimilarity:F2} ({DescribeFaceScore(_lastRecognizedSimilarity)})"
            : !string.IsNullOrWhiteSpace(displayName) && displayName != "Unknown"
                ? $"Identity: Unknown | closest {displayName}, similarity {similarity:F2} ({DescribeFaceScore(similarity)})"
                : "Identity: Unknown";

        _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, _recognizedFaceIdentity);
        ApplyFaceAccessDecision();
    }

    private void MaybeExpireRecognizedIdentity()
    {
        if (string.IsNullOrWhiteSpace(_recognizedFaceName))
            return;

        if (DateTime.UtcNow - _lastRecognizedFaceUtc < TimeSpan.FromSeconds(20))
        {
            FaceIdentityText.Text = $"Identity: {_recognizedFaceName} (recent)";
            return;
        }

        _recognizedFaceIdentity = FaceIdentity.Unknown;
        _recognizedFaceName = "";
        _lastRecognizedSimilarity = 0f;
        FaceIdentityText.Text = "Identity: Unknown";
        _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, _recognizedFaceIdentity);
        ApplyFaceAccessDecision();
    }

    private static string DescribeFaceScore(float similarity)
    {
        return similarity switch
        {
            >= 0.55f => "strong",
            >= 0.36f => "usable",
            >= 0.20f => "weak",
            _ => "poor"
        };
    }

    private void UpdateFacePreview(FaceDetectionSnapshot snapshot)
    {
        if (snapshot.Result.Faces.Length == 0)
            return;

        try
        {
            var faceBounds = ExpandFaceBounds(snapshot.Result.Faces[0], snapshot.Frame.Width, snapshot.Frame.Height, 0.25);
            using var face = new Mat(snapshot.Frame, faceBounds);
            Cv2.ImEncode(".jpg", face, out var bytes);

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            FacePreviewImage.Source = image;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Face preview failed: {ex.Message}");
        }
    }

    private void UpdateFacePreviewFrame(Mat frame)
    {
        try
        {
            Cv2.ImEncode(".jpg", frame, out var bytes);

            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            FacePreviewImage.Source = image;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Face full-frame preview failed: {ex.Message}");
        }
    }

    private static OpenCvSharp.Rect ExpandFaceBounds(OpenCvSharp.Rect rect, int width, int height, double expandRatio)
    {
        var expandX = (int)Math.Round(rect.Width * expandRatio);
        var expandY = (int)Math.Round(rect.Height * expandRatio);
        var x = Math.Clamp(rect.X - expandX, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y - expandY, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(rect.X + rect.Width + expandX, x + 1, width);
        var bottom = Math.Clamp(rect.Y + rect.Height + expandY, y + 1, height);
        return new OpenCvSharp.Rect(x, y, right - x, bottom - y);
    }

    private async Task UpdateFaceSampleInfoAsync()
    {
        try
        {
            var selected = GetSelectedFaceProfileName();
            var sampleCount = await _faceIdentityManager.CountSamplesAsync(selected);
            FaceDebugText.Text = $"Samples for {selected}: {sampleCount} | Model: SFace ONNX | Threshold: {_settings.FaceFeatures.ModelOptions.RecognitionThreshold:F2}";
        }
        catch (Exception ex)
        {
            FaceDebugText.Text = $"Sample info unavailable: {ex.Message}";
        }
    }

    private async Task<int> CountAllFaceSamplesAsync()
    {
        return (await _faceIdentityManager.LoadProfilesAsync()).Sum(profile => profile.Embeddings.Count);
    }

    private void ApplyFaceAccessDecision()
    {
        if (!_settings.FaceFeatures.FaceGatingEnabled)
            return;

        if (_faceAccessDecision.ShouldMuteMicrophone && _speech.CurrentState == VoiceState.Listening)
        {
            _speech.StopListening();
            _autoListening = false;
            AlwaysListenToggle.IsChecked = false;
            AddSystemMessage("Voice input paused by local face policy.");
            SetUIState("idle", "Face policy paused mic");
        }
    }

    private async void FaceFeaturesToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = FaceFeaturesToggle.IsChecked == true;
        _settings.FaceFeatures.CameraFeaturesEnabled = enabled;
        SaveSettings();
        await InitializeFacePresenceAsync();
    }

    private void FaceGatingToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = FaceGatingToggle.IsChecked == true;
        _settings.FaceFeatures.FaceGatingEnabled = enabled;

        if (enabled)
        {
            _settings.FaceFeatures.Policy.UnknownFaceAction = FaceAccessAction.MuteMic;
            _settings.FaceFeatures.Policy.NoFaceAction = FaceAccessAction.MuteMic;
            _settings.FaceFeatures.Policy.MultipleFacesAction = FaceAccessAction.GuestPrivateMode;
            FacePolicyText.Text = "Identity controls mic and assistant mode";
        }
        else
        {
            FacePolicyText.Text = "Face gating disabled";
        }

        SaveSettings();
        _facePolicy = new FaceAccessPolicyEvaluator(_settings.FaceFeatures.Policy);
        _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, _recognizedFaceIdentity);
        ApplyFaceAccessDecision();
    }

    private async void EnrollFace_Click(object sender, RoutedEventArgs e)
    {
        if (_latestFaceSnapshot == null || _latestFaceSnapshot.Result.State != FacePresenceState.FaceDetected)
        {
            AddSystemMessage("No detected face is ready to save. Click Scan Face Once first, then save when the preview shows your face.");
            return;
        }

        if (_latestFaceSnapshot.Result.Faces.Length != 1)
        {
            AddSystemMessage("Enrollment needs exactly one face in view.");
            return;
        }

        var selected = GetSelectedFaceProfileName();
        var identity = FaceProfileNames.ToPolicyIdentity(selected);

        EnrollFaceBtn.IsEnabled = false;
        try
        {
            var sampleCount = await _faceIdentityManager.EnrollSampleAsync(
                selected,
                _latestFaceSnapshot.Frame,
                _latestFaceSnapshot.Result.Faces[0]);

            _recognizedFaceIdentity = identity;
            _recognizedFaceName = selected;
            _lastIdentifiedFaceIdentity = identity;
            _lastIdentifiedFaceName = selected;
            _lastIdentifiedFaceUtc = DateTime.UtcNow;
            _lastRecognizedFaceUtc = DateTime.UtcNow;
            FaceIdentityText.Text = $"Saved this face as {selected}. Samples: {sampleCount}";
            AddSystemMessage($"Saved local face sample {sampleCount} for {selected}. Type a new name in the profile box to add another person.");
            await RefreshFaceProfileChoicesAsync();
            await UpdateFaceSampleInfoAsync();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Face enrollment failed: {ex.Message}");
        }
        finally
        {
            EnrollFaceBtn.IsEnabled = true;
        }
    }

    private async void FaceSnapshot_Click(object sender, RoutedEventArgs e)
    {
        FaceSnapshotBtn.IsEnabled = false;
        FaceSnapshotBtn.Content = "Scanning...";
        FaceIdentityText.Text = "Identity: scanning camera...";
        try
        {
            using var capture = await Task.Run(() =>
                OpenCvStillCameraService.CaptureFrame(_settings.FaceFeatures.CameraIndex));
            using var detector = new HaarCascadeFaceDetectionService(_settings.FaceFeatures.ModelOptions);
            var result = detector.DetectFaces(capture.Frame);
            var usedCenteredFallback = false;
            if (!capture.LooksBlank)
                result = StabilizeFaceScanResult(capture.Frame, result, out usedCenteredFallback);

            if (result.State == FacePresenceState.NoFaceDetected && !capture.LooksBlank)
            {
                result = new FaceDetectionResult
                {
                    State = FacePresenceState.FaceDetected,
                    Faces = [CreateCenteredEnrollmentFace(capture.Frame)]
                };
                usedCenteredFallback = true;
            }

            var snapshot = new FaceDetectionSnapshot(capture.Frame.Clone(), result, DateTime.UtcNow);

            var oldSnapshot = _latestFaceSnapshot;
            _latestFaceSnapshot = snapshot;
            oldSnapshot?.Dispose();

            _facePresenceState = result.State;
            UpdateFacePresenceUi(result.State);
            UpdateFacePreviewFrame(snapshot.Frame);
            FaceDebugText.Text =
                $"Last scan: {capture.FramesRead} frames | brightness {capture.MeanBrightness:F1} | contrast {capture.BrightnessStdDev:F1} | faces {result.Faces.Length}"
                + (usedCenteredFallback ? " | centered crop" : "");

            if (capture.LooksBlank)
            {
                FaceIdentityText.Text = "Identity: Unknown | camera frame is black/blank";
                AddSystemMessage("Face scan saw a blank camera frame. Check the privacy cover/lighting, then scan again.");
                return;
            }

            if (result.State == FacePresenceState.FaceDetected)
            {
                var selected = GetSelectedFaceProfileName();
                var totalSamples = await CountAllFaceSamplesAsync();
                if (totalSamples > 0)
                {
                    FaceIdentityText.Text = usedCenteredFallback
                        ? "Detector missed; recognizing with centered crop..."
                        : "Face detected; recognizing...";
                    await RecognizeLatestFaceAsync(snapshot);
                }
                else
                {
                    FaceIdentityText.Text = usedCenteredFallback
                        ? $"Detector missed you. Preview is using a centered crop. If that crop is your face, click Save Face as {selected}."
                        : $"Face detected. Click Save Face to add this sample as {selected}.";
                }
            }
            else
            {
                FaceIdentityText.Text = result.State == FacePresenceState.MultipleFacesDetected
                    ? "Identity: Unknown | multiple faces"
                    : "Identity: Unknown | no valid face";
            }
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Face scan failed: {ex.Message}");
        }
        finally
        {
            FaceSnapshotBtn.IsEnabled = true;
            FaceSnapshotBtn.Content = "Scan Face Once";
        }
    }

    private static OpenCvSharp.Rect CreateCenteredEnrollmentFace(Mat frame)
    {
        var side = (int)Math.Round(Math.Min(frame.Width, frame.Height) * 0.58);
        side = Math.Clamp(side, 80, Math.Min(frame.Width, frame.Height));
        var x = Math.Clamp((frame.Width - side) / 2, 0, Math.Max(0, frame.Width - side));
        var y = Math.Clamp((int)Math.Round(frame.Height * 0.16), 0, Math.Max(0, frame.Height - side));
        return new OpenCvSharp.Rect(x, y, side, side);
    }

    private static FaceDetectionResult StabilizeFaceScanResult(Mat frame, FaceDetectionResult result, out bool usedCenteredFallback)
    {
        usedCenteredFallback = false;
        if (result.State == FacePresenceState.NoFaceDetected || result.Faces.Length == 0)
        {
            usedCenteredFallback = true;
            return new FaceDetectionResult
            {
                State = FacePresenceState.FaceDetected,
                Faces = [CreateCenteredEnrollmentFace(frame)]
            };
        }

        var expectedCenter = new OpenCvSharp.Point2f(frame.Width / 2f, frame.Height * 0.43f);
        var face = result.Faces
            .OrderBy(rect =>
            {
                var centerX = rect.X + (rect.Width / 2f);
                var centerY = rect.Y + (rect.Height / 2f);
                var normalizedX = (centerX - expectedCenter.X) / Math.Max(1, frame.Width);
                var normalizedY = (centerY - expectedCenter.Y) / Math.Max(1, frame.Height);
                return (normalizedX * normalizedX) + (normalizedY * normalizedY);
            })
            .First();

        if (!IsReasonableFaceScanBox(face, frame.Width, frame.Height))
        {
            usedCenteredFallback = true;
            face = CreateCenteredEnrollmentFace(frame);
        }

        return new FaceDetectionResult
        {
            State = FacePresenceState.FaceDetected,
            Faces = [face]
        };
    }

    private static bool IsReasonableFaceScanBox(OpenCvSharp.Rect face, int frameWidth, int frameHeight)
    {
        var centerX = face.X + (face.Width / 2.0);
        var centerY = face.Y + (face.Height / 2.0);
        var expectedY = frameHeight * 0.43;
        var dx = Math.Abs(centerX - (frameWidth / 2.0)) / Math.Max(1, frameWidth);
        var dy = Math.Abs(centerY - expectedY) / Math.Max(1, frameHeight);

        if (dx > 0.28 || dy > 0.30)
            return false;

        if (centerY < frameHeight * 0.18 || centerY > frameHeight * 0.78)
            return false;

        return true;
    }

    private async void ClearFaces_Click(object sender, RoutedEventArgs e)
    {
        ClearFacesBtn.IsEnabled = false;
        try
        {
            await _faceIdentityManager.ClearAllProfilesAsync();
            _recognizedFaceIdentity = FaceIdentity.Unknown;
            _lastIdentifiedFaceIdentity = FaceIdentity.Unknown;
            _recognizedFaceName = "";
            _lastIdentifiedFaceName = "";
            _lastIdentifiedFaceUtc = DateTime.MinValue;
            _lastRecognizedSimilarity = 0f;
            FaceIdentityText.Text = "Identity: Unknown";
            AddSystemMessage("Cleared all local face samples.");
            await RefreshFaceProfileChoicesAsync();
            await UpdateFaceSampleInfoAsync();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not clear face samples: {ex.Message}");
        }
        finally
        {
            ClearFacesBtn.IsEnabled = true;
        }
    }

    private async void FaceProfileCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_faceIdentityManager == null)
            return;

        await UpdateFaceSampleInfoAsync();
    }
}
