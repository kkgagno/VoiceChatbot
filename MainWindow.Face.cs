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

    // Gate-triggered rescans wait at least this long after the previous scan.
    private static readonly TimeSpan FaceAutoScanCooldown = TimeSpan.FromSeconds(15);

    private DispatcherTimer? _faceExpiryTimer;
    private DateTime _lastFaceScanUtc = DateTime.MinValue;
    private DateTime _lastFaceAutoScanUtc = DateTime.MinValue;
    private bool _faceScanInProgress;
    private bool _latestFaceScanLooksBlank;
    private bool _outdatedFaceSamplesNoticeShown;

    private async Task InitializeFacePresenceAsync()
    {
        await StopFacePresenceAsync();

        _facePolicy = new FaceAccessPolicyEvaluator(_settings.FaceFeatures.Policy);
        // Reuse the manager while the model options are unchanged so the ONNX model is not reloaded on every toggle.
        if (_faceIdentityManager == null || !ReferenceEquals(_faceIdentityManager.Options, _settings.FaceFeatures.ModelOptions))
        {
            _faceIdentityManager = new FaceIdentityManager(
                FaceServiceFactory.CreateProfileStore(_settings.FaceFeatures.ModelOptions),
                _settings.FaceFeatures.ModelOptions);
        }

        // Start unverified: an earlier scan does not carry over, and "no scan yet" must not open the gate.
        ResetFaceScanState(expired: false);
        EnsureFaceExpiryTimer();
        EnsureFaceRoleMenu();
        await UpdateFaceSampleInfoAsync();

        if (_settings.FaceFeatures.CameraFeaturesEnabled)
            _ = WarmUpFaceModelAsync();
    }

    private async Task WarmUpFaceModelAsync()
    {
        try
        {
            // Load the ONNX model off the UI thread before the first scan needs it.
            await _faceIdentityManager.WarmUpAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Face model warm-up failed: {ex.Message}");
        }

        await UpdateFaceSampleInfoAsync();
        await NotifyOutdatedFaceSamplesOnceAsync();
    }

    private async Task NotifyOutdatedFaceSamplesOnceAsync()
    {
        if (_outdatedFaceSamplesNoticeShown)
            return;

        try
        {
            var outdated = (await _faceIdentityManager.LoadProfilesAsync())
                .Select(profile => (profile.DisplayName, Count: FaceEmbeddingFormats.CountOutdated(profile)))
                .Where(entry => entry.Count > 0)
                .ToList();
            if (outdated.Count == 0)
                return;

            _outdatedFaceSamplesNoticeShown = true;
            AddSystemMessage(
                $"Face ID now reads the camera image the way the SFace model expects. {outdated.Sum(entry => entry.Count)} saved face sample(s) for "
                + $"{string.Join(", ", outdated.Select(entry => entry.DisplayName))} were made the old way and are ignored. "
                + "Re-enroll each person: Scan Face Once, then Save Face (a few samples each). Outdated samples are replaced as new ones are saved.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Face sample version check failed: {ex.Message}");
        }
    }

    private async Task StopFacePresenceAsync()
    {
        _faceExpiryTimer?.Stop();
        _faceExpiryTimer = null;

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
            _lastFaceScanUtc = DateTime.UtcNow;
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
            _ = Dispatcher.BeginInvoke(() => ExpireFaceScanIfStale());
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

        ExpireFaceScanIfStale();
        _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, _recognizedFaceIdentity);
        var allowed = IsVoiceAllowedByDecision(_faceAccessDecision);
        if (!allowed)
            RequestFaceRefreshScan();

        return allowed;
    }

    private static bool IsVoiceAllowedByDecision(FaceAccessDecision decision) =>
        !decision.ShouldMuteMicrophone && !decision.ShouldBlockWakeWord;

    private void EnsureFaceExpiryTimer()
    {
        if (_faceExpiryTimer != null)
            return;

        _faceExpiryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _faceExpiryTimer.Tick += FaceExpiryTimer_Tick;
        _faceExpiryTimer.Start();
    }

    private void FaceExpiryTimer_Tick(object? sender, EventArgs e)
    {
        if (!ExpireFaceScanIfStale())
            return;

        // While voice input is in use under gating, re-check now so hands-free keeps working for a
        // verified person. Only a failed check is reported, to keep periodic re-checks out of the chat.
        if (_settings.FaceFeatures.FaceGatingEnabled && (_autoListening || _speech.CurrentState == VoiceState.Listening))
            RequestFaceRefreshScan(quiet: true);
    }

    /// <summary>Drops a scan result older than the configured timeout. Returns true if it just expired.</summary>
    private bool ExpireFaceScanIfStale()
    {
        if (_lastFaceScanUtc == DateTime.MinValue
            || FaceScanFreshness.IsFresh(_lastFaceScanUtc, DateTime.UtcNow, _settings.FaceFeatures.IdentityTimeoutSeconds))
            return false;

        ResetFaceScanState(expired: true);
        return true;
    }

    private void ResetFaceScanState(bool expired)
    {
        _lastFaceScanUtc = DateTime.MinValue;
        _facePresenceState = FaceScanFreshness.UnverifiedState(_settings.FaceFeatures.CameraFeaturesEnabled);
        ClearRecognizedFace();
        _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, _recognizedFaceIdentity);

        UpdateFacePresenceUi(_facePresenceState);
        if (_settings.FaceFeatures.CameraFeaturesEnabled)
            FacePresenceText.Text = expired ? "Last scan expired" : "Not scanned yet";
        if (!_faceScanInProgress)
            FaceIdentityText.Text = expired ? "Identity: Unknown | last scan expired" : "Identity: Unknown";
    }

    private void ClearRecognizedFace()
    {
        _recognizedFaceIdentity = FaceIdentity.Unknown;
        _recognizedFaceName = "";
        _lastRecognizedSimilarity = 0f;
    }

    /// <summary>
    /// Starts a background scan when the gate refuses voice input, so a missing, expired or outdated
    /// result does not lock the person at the PC out. Returns true if a scan was started.
    /// </summary>
    private bool RequestFaceRefreshScan(bool quiet = false)
    {
        if (!_settings.FaceFeatures.CameraFeaturesEnabled || _faceScanInProgress)
            return false;

        var now = DateTime.UtcNow;
        var lastAttempt = _lastFaceAutoScanUtc > _lastFaceScanUtc ? _lastFaceAutoScanUtc : _lastFaceScanUtc;
        if (now - lastAttempt < FaceAutoScanCooldown)
            return false;

        _lastFaceAutoScanUtc = now;
        if (!quiet)
        {
            AddSystemMessage(_lastFaceScanUtc == DateTime.MinValue
                ? "Face gating: no recent face scan, checking the camera..."
                : "Face gating: checking the camera again...");
        }

        _ = RunFaceScanAsync(automatic: true, quiet);
        return true;
    }

    /// <summary>
    /// After a scan under gating: resumes hands-free listening the gate held back, and tells the user
    /// how a gate-started scan turned out (only a refusal when <paramref name="quiet"/>).
    /// </summary>
    private void ResumeVoiceAfterFaceScan(bool automatic, bool quiet)
    {
        if (!_settings.FaceFeatures.FaceGatingEnabled)
            return;

        var who = DescribeFaceForGate();
        if (!IsVoiceAllowedByDecision(_faceAccessDecision))
        {
            if (automatic)
                AddSystemMessage($"Face gating: {who}. Voice input stays blocked; use Scan Face Once to try again.");
            return;
        }

        var announce = automatic && !quiet;
        if (_autoListening && !_pausedListeningForTextInput && _speech.CurrentState == VoiceState.Idle)
        {
            if (announce)
                AddSystemMessage($"Face gating: {who}. Listening again.");
            _speech.StartListening();
            return;
        }

        if (announce)
            AddSystemMessage($"Face gating: {who}. Voice input is allowed now.");
    }

    private string DescribeFaceForGate()
    {
        if (!string.IsNullOrWhiteSpace(_recognizedFaceName))
            return $"{_recognizedFaceName} recognized";

        return _facePresenceState switch
        {
            FacePresenceState.MultipleFacesDetected => "multiple faces in view",
            FacePresenceState.NoFaceDetected => "no face in view",
            FacePresenceState.FaceDetected => "face not recognized",
            _ => "camera unavailable"
        };
    }

    private void EvaluateAndApplyFaceDecision()
    {
        _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, _recognizedFaceIdentity);
        ApplyFaceAccessDecision();
    }

    private async Task RecognizeLatestFaceAsync(FaceDetectionSnapshot snapshot)
    {
        if (_recognitionInProgress || snapshot.Result.Faces.Length != 1)
            return;

        _recognitionInProgress = true;
        try
        {
            var result = await _faceIdentityManager.RecognizeAsync(snapshot.Frame, snapshot.Result.Faces[0]);
            _ = Dispatcher.BeginInvoke(() => UpdateRecognizedIdentity(result));
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

    private void UpdateRecognizedIdentity(FaceRecognitionResult result)
    {
        var now = DateTime.UtcNow;
        var displayName = (result.DisplayName ?? "").Trim();

        // Each scan replaces the previous identity; nothing carries over from an earlier match.
        if (result.IsRecognized && displayName.Length > 0)
        {
            _recognizedFaceIdentity = result.Identity;
            _recognizedFaceName = displayName;
            _lastIdentifiedFaceIdentity = result.Identity;
            _lastIdentifiedFaceName = displayName;
            _lastIdentifiedFaceUtc = now;
            _lastRecognizedFaceUtc = now;
            _lastRecognizedSimilarity = result.Similarity;
            FaceIdentityText.Text =
                $"Identity: {displayName} ({FaceProfileRoles.Describe(result.Role)}) | similarity {result.Similarity:F2} ({DescribeFaceScore(result.Similarity)})";
        }
        else
        {
            ClearRecognizedFace();
            _lastRecognizedSimilarity = result.Similarity;
            FaceIdentityText.Text = result.IsAmbiguous
                ? $"Identity: Unknown | too close to call: {displayName} {result.Similarity:F2} vs {result.RunnerUpName} {result.RunnerUpSimilarity:F2}"
                : displayName.Length > 0
                    ? $"Identity: Unknown | closest {displayName}, similarity {result.Similarity:F2} ({DescribeFaceScore(result.Similarity)})"
                    : "Identity: Unknown";
        }

        EvaluateAndApplyFaceDecision();
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
            var profile = await _faceIdentityManager.FindProfileAsync(selected);
            var activeModel = _faceIdentityManager.ActiveEmbeddingModel;
            var usable = profile == null ? 0 : FaceEmbeddingFormats.CountUsable(profile, activeModel);
            var outdated = profile == null ? 0 : FaceEmbeddingFormats.CountOutdated(profile);
            var samples = outdated > 0 ? $"{usable} (+{outdated} outdated, re-enroll)" : usable.ToString();
            var role = profile == null
                ? ""
                : $" | Role: {FaceProfileRoles.Describe(profile.EffectiveRole)}{(profile.RoleInferred ? " (unconfirmed)" : "")}";
            FaceDebugText.Text =
                $"Samples for {selected}: {samples}{role} | Model: {DescribeFaceModel(activeModel)} | Threshold: {_settings.FaceFeatures.ModelOptions.RecognitionThreshold:F2}";
        }
        catch (Exception ex)
        {
            FaceDebugText.Text = $"Sample info unavailable: {ex.Message}";
        }
    }

    private static string DescribeFaceModel(string? activeModel) => activeModel switch
    {
        null => "not loaded yet",
        FaceEmbeddingFormats.SFaceModel => "SFace ONNX",
        _ => "basic fallback (SFace model unavailable)"
    };

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
            FacePolicyText.Text = _settings.FaceFeatures.CameraFeaturesEnabled
                ? "Identity controls mic and assistant mode"
                : "Face gating needs Camera features on";
        }
        else
        {
            FacePolicyText.Text = "Face gating disabled";
        }

        SaveSettings();
        _facePolicy = new FaceAccessPolicyEvaluator(_settings.FaceFeatures.Policy);
        ExpireFaceScanIfStale();
        _faceAccessDecision = _facePolicy.Evaluate(_facePresenceState, _recognizedFaceIdentity);

        // Nobody has been verified yet: scan first rather than cutting the mic off straight away.
        if (enabled
            && _lastFaceScanUtc == DateTime.MinValue
            && !IsVoiceAllowedByDecision(_faceAccessDecision)
            && RequestFaceRefreshScan())
            return;

        ApplyFaceAccessDecision();
    }

    private async void EnrollFace_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _latestFaceSnapshot;
        if (snapshot == null
            || _latestFaceScanLooksBlank
            || snapshot.Result.State == FacePresenceState.CameraUnavailable)
        {
            AddSystemMessage("No detected face is ready to save. Click Scan Face Once first, then save when the preview shows your face.");
            return;
        }

        if (snapshot.Result.State == FacePresenceState.MultipleFacesDetected || snapshot.Result.Faces.Length > 1)
        {
            AddSystemMessage("Enrollment needs exactly one face in view.");
            return;
        }

        var selected = GetSelectedFaceProfileName();

        // Copy now: a new scan can replace and dispose the snapshot while this handler awaits.
        using var frame = snapshot.Frame.Clone();
        OpenCvSharp.Rect faceBounds;
        var usedCenteredCrop = false;
        if (snapshot.Result.State == FacePresenceState.FaceDetected && snapshot.Result.Faces.Length == 1)
        {
            faceBounds = snapshot.Result.Faces[0];
        }
        else
        {
            // The detector found no face. Saving the centered crop is an explicit "try anyway".
            var answer = MessageBox.Show(
                this,
                $"The face detector did not find a face in the last scan.\n\nSave the centered part of the preview as a sample for {selected} anyway? Only do this if your face is centered in the preview.",
                "Save Face",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
                return;

            faceBounds = CreateCenteredEnrollmentFace(frame);
            usedCenteredCrop = true;
        }

        EnrollFaceBtn.IsEnabled = false;
        try
        {
            var role = await ResolveRoleForEnrollmentAsync(selected);
            if (role == null)
            {
                AddSystemMessage("Face sample not saved.");
                return;
            }

            var (sampleCount, removedOutdated) = await _faceIdentityManager.EnrollSampleAsync(
                selected,
                role.Value,
                frame,
                faceBounds);

            var identity = FaceProfileRoles.ToPolicyIdentity(role.Value);
            var roleName = FaceProfileRoles.Describe(role.Value);
            _recognizedFaceIdentity = identity;
            _recognizedFaceName = selected;
            _lastIdentifiedFaceIdentity = identity;
            _lastIdentifiedFaceName = selected;
            _lastIdentifiedFaceUtc = DateTime.UtcNow;
            _lastRecognizedFaceUtc = DateTime.UtcNow;
            FaceIdentityText.Text = $"Saved this face as {selected} ({roleName}). Samples: {sampleCount}";
            AddSystemMessage(
                $"Saved local face sample {sampleCount} for {selected} ({roleName})"
                + (usedCenteredCrop ? " from the centered crop" : "")
                + "."
                + (removedOutdated > 0 ? $" Replaced {removedOutdated} outdated sample(s)." : "")
                + " Type a new name in the profile box to add another person.");
            await RefreshFaceProfileChoicesAsync();
            await UpdateFaceSampleInfoAsync();
            EvaluateAndApplyFaceDecision();
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

    /// <summary>
    /// Role to save a sample under. A new profile, or one whose role was only inferred when roles were
    /// introduced, asks the user; otherwise the stored role is kept. Null means the user cancelled.
    /// </summary>
    private async Task<FaceProfileRole?> ResolveRoleForEnrollmentAsync(string displayName)
    {
        var profiles = await _faceIdentityManager.LoadProfilesAsync();
        var profileId = FaceProfileNames.ToProfileId(displayName);
        var existing = profiles.FirstOrDefault(profile => string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
        if (existing != null && !existing.RoleInferred)
            return existing.EffectiveRole;

        var suggested = existing?.EffectiveRole
            ?? FaceProfileRoles.SuggestForNewProfile(displayName, profiles.Any(profile => profile.EffectiveRole == FaceProfileRole.Owner));
        var intro = existing == null
            ? "Pick a role for this new face profile. It decides what face gating allows when this face is recognized."
            : "Face profiles now store a role instead of guessing it from the name. Confirm the role for this profile.";
        return PromptForFaceProfileRole(displayName, suggested, intro);
    }

    private FaceProfileRole? PromptForFaceProfileRole(string displayName, FaceProfileRole suggested, string intro)
    {
        FaceProfileRole? choice = null;
        var dialog = new Window
        {
            Title = "Face profile role",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = TryFindResource("DarkBgBrush") as Brush ?? Brushes.White,
            Foreground = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black
        };
        if (TryFindResource("UiFont") is FontFamily font)
            dialog.FontFamily = font;
        WindowTheme.UseDarkTitleBar(dialog);

        var panel = new StackPanel { Margin = new Thickness(20), Width = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Who is {displayName}?",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        });
        panel.Children.Add(new TextBlock
        {
            Text = intro,
            TextWrapping = TextWrapping.Wrap,
            Foreground = TryFindResource("TextSecondaryBrush") as Brush ?? Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 14)
        });

        void AddChoice(FaceProfileRole role, string label)
        {
            var button = new Button
            {
                Content = label,
                IsDefault = role == suggested,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 8)
            };
            if (TryFindResource(role == suggested ? "ActionButton" : "SecondaryButton") is Style style)
                button.Style = style;
            button.Click += (_, _) =>
            {
                choice = role;
                dialog.DialogResult = true;
            };
            panel.Children.Add(button);
        }

        AddChoice(FaceProfileRole.Owner, "Owner: full assistant access");
        AddChoice(FaceProfileRole.Child, "Child: kid-safe mode");
        AddChoice(FaceProfileRole.Guest, "Guest: treated like an unrecognized face");

        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0)
        };
        if (TryFindResource("GhostButton") is Style ghost)
            cancel.Style = ghost;
        panel.Children.Add(cancel);

        dialog.Content = panel;
        return dialog.ShowDialog() == true ? choice : null;
    }

    private void EnsureFaceRoleMenu()
    {
        if (EnrollFaceBtn.ContextMenu != null)
            return;

        var changeRole = new MenuItem { Header = "Change role of this profile..." };
        changeRole.Click += ChangeFaceProfileRole_Click;
        var menu = new ContextMenu();
        menu.Items.Add(changeRole);
        EnrollFaceBtn.ContextMenu = menu;
        EnrollFaceBtn.ToolTip = "Save the scanned face to this profile. Right-click to change the profile's role.";
    }

    private async void ChangeFaceProfileRole_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedFaceProfileName();
        try
        {
            var profile = await _faceIdentityManager.FindProfileAsync(selected);
            if (profile == null)
            {
                AddSystemMessage($"There is no saved face profile named {selected} yet. Scan and save a face first.");
                return;
            }

            var role = PromptForFaceProfileRole(
                profile.DisplayName,
                profile.EffectiveRole,
                $"Current role: {FaceProfileRoles.Describe(profile.EffectiveRole)}. The role decides what face gating allows when this face is recognized.");
            if (role == null)
                return;

            await _faceIdentityManager.SetRoleAsync(profile.DisplayName, role.Value);
            var identity = FaceProfileRoles.ToPolicyIdentity(role.Value);
            if (string.Equals(_lastIdentifiedFaceName, profile.DisplayName, StringComparison.OrdinalIgnoreCase))
                _lastIdentifiedFaceIdentity = identity;
            if (string.Equals(_recognizedFaceName, profile.DisplayName, StringComparison.OrdinalIgnoreCase))
            {
                _recognizedFaceIdentity = identity;
                EvaluateAndApplyFaceDecision();
            }

            AddSystemMessage($"Face profile {profile.DisplayName} now has the {FaceProfileRoles.Describe(role.Value)} role.");
            await UpdateFaceSampleInfoAsync();
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not change the face profile role: {ex.Message}");
        }
    }

    private async void FaceSnapshot_Click(object sender, RoutedEventArgs e)
    {
        await RunFaceScanAsync(automatic: false, quiet: false);
    }

    /// <summary>
    /// Captures one frame, reports what the detector really saw, and refreshes the identity and its
    /// expiry clock. <paramref name="automatic"/> scans were started by the gate and report back to it;
    /// <paramref name="quiet"/> ones only report a refusal.
    /// </summary>
    private async Task RunFaceScanAsync(bool automatic, bool quiet)
    {
        if (_faceScanInProgress)
            return;

        _faceScanInProgress = true;
        FaceSnapshotBtn.IsEnabled = false;
        FaceSnapshotBtn.Content = "Scanning...";
        FaceIdentityText.Text = "Identity: scanning camera...";
        var cameraIndex = _settings.FaceFeatures.CameraIndex;
        var modelOptions = _settings.FaceFeatures.ModelOptions;
        var scanned = false;
        try
        {
            // Camera capture and Haar detection both run on a worker thread.
            var (capture, result) = await Task.Run(() => CaptureAndDetectFace(cameraIndex, modelOptions));
            using (capture)
            {
                var snapshot = new FaceDetectionSnapshot(capture.Frame.Clone(), result, DateTime.UtcNow);

                var oldSnapshot = _latestFaceSnapshot;
                _latestFaceSnapshot = snapshot;
                _latestFaceScanLooksBlank = capture.LooksBlank;
                oldSnapshot?.Dispose();

                // The new scan replaces the old result outright: report what the detector saw, restart the
                // expiry clock, and drop the previous identity until this face is recognized.
                _facePresenceState = result.State;
                _lastFaceScanUtc = DateTime.UtcNow;
                ClearRecognizedFace();
                scanned = true;
                UpdateFacePresenceUi(result.State);
                UpdateFacePreviewFrame(snapshot.Frame);
                FaceDebugText.Text =
                    $"Last scan: {capture.FramesRead} frames | brightness {capture.MeanBrightness:F1} | contrast {capture.BrightnessStdDev:F1} | faces {result.Faces.Length}";

                if (capture.LooksBlank)
                {
                    FaceIdentityText.Text = "Identity: Unknown | camera frame is black/blank";
                    AddSystemMessage("Face scan saw a blank camera frame. Check the privacy cover/lighting, then scan again.");
                    EvaluateAndApplyFaceDecision();
                }
                else if (result.State == FacePresenceState.FaceDetected && result.Faces.Length == 1)
                {
                    await RecognizeScannedFaceAsync(snapshot);
                }
                else
                {
                    FaceIdentityText.Text = result.State switch
                    {
                        FacePresenceState.MultipleFacesDetected => "Identity: Unknown | multiple faces",
                        FacePresenceState.NoFaceDetected => automatic
                            ? "Identity: Unknown | no face detected"
                            : "Identity: Unknown | no face detected (Save Face can still try a centered crop)",
                        _ => "Identity: Unknown | face detector unavailable"
                    };
                    EvaluateAndApplyFaceDecision();
                }
            }
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Face scan failed: {ex.Message}");
        }
        finally
        {
            _faceScanInProgress = false;
            FaceSnapshotBtn.IsEnabled = true;
            FaceSnapshotBtn.Content = "Scan Face Once";
        }

        if (scanned)
            ResumeVoiceAfterFaceScan(automatic, quiet);
    }

    private static (StillCameraCaptureResult Capture, FaceDetectionResult Result) CaptureAndDetectFace(
        int cameraIndex,
        FaceModelOptions modelOptions)
    {
        var capture = OpenCvStillCameraService.CaptureFrame(cameraIndex);
        try
        {
            using var detector = new HaarCascadeFaceDetectionService(modelOptions);
            return (capture, detector.DetectFaces(capture.Frame));
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private async Task RecognizeScannedFaceAsync(FaceDetectionSnapshot snapshot)
    {
        var profiles = await _faceIdentityManager.LoadProfilesAsync();
        var activeModel = _faceIdentityManager.ActiveEmbeddingModel;
        if (!profiles.Any(profile => FaceEmbeddingFormats.CountUsable(profile, activeModel) > 0))
        {
            var selected = GetSelectedFaceProfileName();
            FaceIdentityText.Text = profiles.Any(profile => FaceEmbeddingFormats.CountOutdated(profile) > 0)
                ? $"Face detected. Saved samples are outdated; click Save Face to re-enroll as {selected}."
                : $"Face detected. Click Save Face to add this sample as {selected}.";
            EvaluateAndApplyFaceDecision();
            return;
        }

        FaceIdentityText.Text = "Face detected; recognizing...";
        FaceRecognitionResult result;
        try
        {
            result = await _faceIdentityManager.RecognizeAsync(snapshot.Frame, snapshot.Result.Faces[0]);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Face recognition failed: {ex.Message}");
            result = new FaceRecognitionResult();
        }

        // Camera features were toggled meanwhile: this scan no longer counts.
        if (!ReferenceEquals(_latestFaceSnapshot, snapshot))
            return;

        UpdateRecognizedIdentity(result);
    }

    private static OpenCvSharp.Rect CreateCenteredEnrollmentFace(Mat frame)
    {
        var side = (int)Math.Round(Math.Min(frame.Width, frame.Height) * 0.58);
        side = Math.Clamp(side, 80, Math.Min(frame.Width, frame.Height));
        var x = Math.Clamp((frame.Width - side) / 2, 0, Math.Max(0, frame.Width - side));
        var y = Math.Clamp((int)Math.Round(frame.Height * 0.16), 0, Math.Max(0, frame.Height - side));
        return new OpenCvSharp.Rect(x, y, side, side);
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
            EvaluateAndApplyFaceDecision();
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
