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
    private async void PhoneRemoteToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = PhoneRemoteToggle.IsChecked == true;
        SaveSettings();

        if (enabled)
            await StartPhoneRemoteIfEnabledAsync();
        else
            await StopPhoneRemoteAsync();
    }

    private async void PhoneRemoteStartStop_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
        if (_phoneRemoteServer.IsRunning)
        {
            PhoneRemoteToggle.IsChecked = false;
            _settings.PhoneRemote.Enabled = false;
            SaveSettings();
            await StopPhoneRemoteAsync();
            return;
        }

        PhoneRemoteToggle.IsChecked = true;
        _settings.PhoneRemote.Enabled = true;
        SaveSettings();
        await StartPhoneRemoteIfEnabledAsync();
    }

    private void PhoneRemoteAudioToggle_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void PhoneRemoteCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = _phoneRemoteServer.IsRunning ? _phoneRemoteServer.Url : $"https://{PhoneRemoteServerPreviewIp()}:{_settings.PhoneRemote.Port}/";
        Clipboard.SetText(url);
        AddSystemMessage($"Phone remote URL copied: {url}");
    }

    private void PhoneRemoteCert_Click(object sender, RoutedEventArgs e)
    {
        var certPath = _phoneRemoteServer.CertificateExportPath;
        if (string.IsNullOrWhiteSpace(certPath))
        {
            try
            {
                var ip = PhoneRemoteServerPreviewIp();
                var cert = PhoneRemoteCertificateManager.EnsureCertificate(ip);
                certPath = cert.CerPath;
                cert.Certificate.Dispose();
            }
            catch (Exception ex)
            {
                AddSystemMessage($"Could not create local certificate: {ex.Message}");
                return;
            }
        }

        var folder = Path.GetDirectoryName(certPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            AddSystemMessage("Certificate folder was not found.");
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        AddSystemMessage($"Install this certificate on the iPhone and fully trust it: {certPath}");
    }

    private async Task StartPhoneRemoteIfEnabledAsync()
    {
        if (_settings.PhoneRemote.Enabled != true)
        {
            UpdatePhoneRemoteUi();
            return;
        }

        try
        {
            PhoneRemoteStatusText.Text = "Starting...";
            PhoneRemoteStartStopBtn.IsEnabled = false;
            PhoneRemoteToggle.IsEnabled = false;
            var settings = new PhoneRemoteSettings
            {
                Enabled = _settings.PhoneRemote.Enabled,
                Port = _settings.PhoneRemote.Port,
                Pin = _settings.PhoneRemote.Pin,
                PlayAudioOnPhone = _settings.PhoneRemote.PlayAudioOnPhone
            };

            using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                await Task.Run(() => _phoneRemoteServer.StartAsync(settings, startupTimeout.Token))
                    .WaitAsync(TimeSpan.FromSeconds(25));
            }
            catch (TimeoutException)
            {
                try { await _phoneRemoteServer.StopAsync(); } catch { }
                throw new TimeoutException("Phone remote startup timed out. Check whether the HTTPS port is already in use or Windows is blocking the listener.");
            }
            catch (OperationCanceledException)
            {
                try { await _phoneRemoteServer.StopAsync(); } catch { }
                throw new TimeoutException("Phone remote startup timed out. Check whether the HTTPS port is already in use or Windows is blocking the listener.");
            }
            UpdatePhoneRemoteUi();
            AddSystemMessage($"Phone remote started: {_phoneRemoteServer.Url}");
            if (!string.IsNullOrWhiteSpace(_phoneRemoteServer.CertificateExportPath))
                AddSystemMessage($"iPhone certificate: {_phoneRemoteServer.CertificateExportPath}");
        }
        catch (Exception ex)
        {
            PhoneRemoteToggle.IsChecked = false;
            _settings.PhoneRemote.Enabled = false;
            SaveSettings();
            UpdatePhoneRemoteUi();
            AddSystemMessage($"Phone remote failed to start: {ex.Message}");
        }
        finally
        {
            PhoneRemoteStartStopBtn.IsEnabled = true;
            PhoneRemoteToggle.IsEnabled = true;
        }
    }

    private async Task StopPhoneRemoteAsync()
    {
        try
        {
            PhoneRemoteStatusText.Text = "Stopping...";
            PhoneRemoteStartStopBtn.IsEnabled = false;
            PhoneRemoteToggle.IsEnabled = false;
            await Task.Run(() => _phoneRemoteServer.StopAsync());
            UpdatePhoneRemoteUi();
            AddSystemMessage("Phone remote stopped.");
        }
        finally
        {
            PhoneRemoteStartStopBtn.IsEnabled = true;
            PhoneRemoteToggle.IsEnabled = true;
        }
    }

    private void UpdatePhoneRemoteUi()
    {
        if (PhoneRemoteStatusText == null)
            return;

        if (_phoneRemoteServer.IsRunning)
        {
            PhoneRemoteStatusText.Text = $"Running: {_phoneRemoteServer.Url}";
            PhoneRemoteStartStopBtn.Content = "Stop Phone Remote";
            PhoneRemoteCopyUrlBtn.IsEnabled = true;
        }
        else
        {
            PhoneRemoteStatusText.Text = "Stopped";
            PhoneRemoteStartStopBtn.Content = "Start Phone Remote";
            PhoneRemoteCopyUrlBtn.IsEnabled = true;
        }
    }

    private static string PhoneRemoteServerPreviewIp()
    {
        try
        {
            foreach (var address in System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !address.ToString().StartsWith("127.", StringComparison.Ordinal))
                {
                    return address.ToString();
                }
            }
        }
        catch
        {
            // Fall back below.
        }

        return "127.0.0.1";
    }

    private async Task<PhoneRemoteAssistantResult> HandlePhoneQwenImageCreateAsync(string userText, string prompt, CancellationToken ct)
    {
        AssistantMessageUi? assistantMessage = null;
        await Dispatcher.InvokeAsync(() =>
        {
            SaveImageSettingsFromUi();
            AddUserMessage($"[iPhone] {userText}");
            _history.Add("user", userText);
            assistantMessage = AddAssistantMessage("Creating image with Qwen Image on ComfyUI...");
            SetUIState("processing", "Phone image...");
        });

        try
        {
            var result = await _comfyImages.CreateQwenImageAsync(
                prompt,
                _settings.ImageWidth,
                _settings.ImageHeight,
                _settings.QwenCreateSteps,
                ct);

            await Dispatcher.InvokeAsync(() =>
            {
                _latestGeneratedImagePath = result.LocalPath;
                if (assistantMessage is not null)
                {
                    assistantMessage.Body.Text = BuildGeneratedImageMessage(result);
                    AddGeneratedImageToAssistantMessage(assistantMessage, result.LocalPath);
                }
                _history.Add("assistant", BuildGeneratedImageHistoryText(result));
                SetUIState("idle", "Ready");
            });

            return new PhoneRemoteAssistantResult(BuildGeneratedImageMessage(result), null, result.LocalPath);
        }
        catch (Exception ex)
        {
            var error = $"Phone image creation failed: {ex.Message}";
            await Dispatcher.InvokeAsync(() =>
            {
                if (assistantMessage is not null)
                    assistantMessage.Body.Text = error;
                AddSystemMessage(error);
                SetUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult(error, null);
        }
    }

    private async Task<PhoneRemoteAssistantResult> HandlePhoneQwenImageEditAsync(PhoneRemoteUserInput input, string userText, string prompt, CancellationToken ct)
    {
        var sourcePaths = input.ImagePaths
            .Where(File.Exists)
            .Take(2)
            .ToList();
        if (sourcePaths.Count == 0 && File.Exists(_latestGeneratedImagePath))
            sourcePaths.Add(_latestGeneratedImagePath);

        if (sourcePaths.Count == 0)
            return new PhoneRemoteAssistantResult("Attach an image first, or create an image before using Edit.", null);

        var isTwoImageEdit = sourcePaths.Count >= 2;
        AssistantMessageUi? assistantMessage = null;
        await Dispatcher.InvokeAsync(() =>
        {
            SaveImageSettingsFromUi();
            AddUserMessage($"[iPhone] {userText}", sourcePaths);
            var sourceNames = string.Join(", ", sourcePaths.Select(Path.GetFileName));
            _history.Add("user", $"{userText}\n\nImage edit source(s): {sourceNames}");
            assistantMessage = AddAssistantMessage(isTwoImageEdit
                ? "Mixing images with Qwen Image Edit two-image workflow on ComfyUI..."
                : "Editing image with Qwen Image Edit on ComfyUI...");
            SetUIState("processing", "Phone edit...");
        });

        try
        {
            var result = isTwoImageEdit
                ? await _comfyImages.EditQwenImagesAsync(
                    sourcePaths,
                    prompt,
                    _settings.QwenEditSteps,
                    ct)
                : await _comfyImages.EditQwenImageAsync(
                    sourcePaths[0],
                    prompt,
                    _settings.QwenEditSteps,
                    ct);

            await Dispatcher.InvokeAsync(() =>
            {
                _latestGeneratedImagePath = result.LocalPath;
                if (assistantMessage is not null)
                {
                    assistantMessage.Body.Text = BuildGeneratedImageMessage(result);
                    AddGeneratedImageToAssistantMessage(assistantMessage, result.LocalPath);
                }
                _history.Add("assistant", BuildGeneratedImageHistoryText(result));
                SetUIState("idle", "Ready");
            });

            return new PhoneRemoteAssistantResult(BuildGeneratedImageMessage(result), null, result.LocalPath);
        }
        catch (Exception ex)
        {
            var error = $"Phone image edit failed: {ex.Message}";
            await Dispatcher.InvokeAsync(() =>
            {
                if (assistantMessage is not null)
                    assistantMessage.Body.Text = error;
                AddSystemMessage(error);
                SetUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult(error, null);
        }
    }

    private async Task<PhoneRemoteAssistantResult> HandlePhoneLtxVideoAsync(PhoneRemoteUserInput input, string userText, string prompt, int? requestedSeconds, CancellationToken ct)
    {
        var sourcePath = input.ImagePaths.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(sourcePath) && File.Exists(_latestGeneratedImagePath))
            sourcePath = _latestGeneratedImagePath;

        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            return new PhoneRemoteAssistantResult("Attach an image first, or create an image before using Make Video.", null);

        var audioPath = input.AudioPaths.FirstOrDefault(File.Exists);
        AssistantMessageUi? assistantMessage = null;
        await Dispatcher.InvokeAsync(() =>
        {
            SaveImageSettingsFromUi();
            AddUserMessage($"[iPhone] {userText}", new[] { sourcePath });
            _history.Add("user", $"{userText}\n\nVideo source image: {Path.GetFileName(sourcePath)}" +
                (string.IsNullOrWhiteSpace(audioPath) ? "" : $"\nVideo speech audio: {Path.GetFileName(audioPath)}"));
            assistantMessage = AddAssistantMessage(string.IsNullOrWhiteSpace(audioPath)
                ? "Creating video with video_ltx2_3_i2v on ComfyUI..."
                : "Creating video with video_ltx2_3_ia2v on ComfyUI...");
            SetUIState("processing", "Phone video...");
        });

        try
        {
            var result = await _comfyImages.CreateLtxVideoAsync(
                sourcePath,
                audioPath,
                prompt,
                requestedSeconds ?? _settings.VideoSeconds,
                _settings.VideoFps,
                CancellationToken.None);
            var syncedVideoPath = await CopyVideoToSyncedDirectoryAsync(result.LocalPath, CancellationToken.None);

            await Dispatcher.InvokeAsync(() =>
            {
                _latestGeneratedVideoPath = result.LocalPath;
                if (assistantMessage is not null)
                {
                    assistantMessage.Body.Text = BuildGeneratedVideoMessage(result, syncedVideoPath);
                    AddGeneratedVideoToAssistantMessage(assistantMessage, result.LocalPath);
                }
                _history.Add("assistant", BuildGeneratedVideoHistoryText(result, syncedVideoPath));
                SetUIState("idle", "Ready");
            });

            return new PhoneRemoteAssistantResult(BuildGeneratedVideoMessage(result, syncedVideoPath), null, null, result.LocalPath);
        }
        catch (Exception ex)
        {
            var error = $"Phone video creation failed: {ex.Message}";
            await Dispatcher.InvokeAsync(() =>
            {
                if (assistantMessage is not null)
                    assistantMessage.Body.Text = error;
                AddSystemMessage(error);
                SetUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult(error, null);
        }
    }

    private static string GetPhoneCommandText(string text)
    {
        var marker = text.IndexOf("\n\nAttached", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? text[..marker].Trim() : text.Trim();
    }

    private async Task<PhoneRemoteAssistantResult> HandlePhoneHermesAsync(string userText, string hermesPrompt, CancellationToken ct)
    {
        AssistantMessageUi? assistantMessage = null;
        bool makePhoneAudio = false;
        await Dispatcher.InvokeAsync(() =>
        {
            AddUserMessage($"[iPhone] {userText}", new List<string>());
            _history.Add("user", userText);
            assistantMessage = AddAssistantMessage("");
            makePhoneAudio = _settings.PhoneRemote.PlayAudioOnPhone && TtsToggle.IsChecked == true;
            SetUIState("processing", "Phone Hermes...");
            AddSystemMessage("Phone remote running Hermes CLI over SSH.");
        });

        try
        {
            if (IsHermesCancel(hermesPrompt) && !string.IsNullOrWhiteSpace(_pendingHermesControlCommand))
            {
                var cancelled = $"Cancelled pending Hermes action: {_pendingHermesControlCommand}";
                _pendingHermesControlCommand = "";
                _pendingHermesSshCommand = "";
                await Dispatcher.InvokeAsync(() =>
                {
                    if (assistantMessage != null)
                        assistantMessage.Body.Text = cancelled;
                    _history.Add("assistant", cancelled);
                    SetUIState("idle", "Ready");
                });
                return new PhoneRemoteAssistantResult(cancelled, null);
            }

            if (IsHermesApproval(hermesPrompt) && !string.IsNullOrWhiteSpace(_pendingHermesSshCommand))
            {
                var command = _pendingHermesSshCommand;
                _pendingHermesControlCommand = "";
                _pendingHermesSshCommand = "";
                await Dispatcher.InvokeAsync(() => AddSystemMessage("Phone Hermes approval received. Running pending SSH command."));
                using var sshTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sshTimeout.CancelAfter(TimeSpan.FromSeconds(120));
                var sshResult = await _hermesSsh.RunAsync(command, TimeSpan.FromSeconds(90), sshTimeout.Token);
                var sshDisplay = sshResult.ToDisplayText();
                string? sshAudioPath = null;
                if (makePhoneAudio && !string.IsNullOrWhiteSpace(sshDisplay))
                {
                    var speechText = CleanSpeechText(sshDisplay);
                    sshAudioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (assistantMessage != null)
                    {
                        assistantMessage.Body.Text = sshDisplay;
                        if (!string.IsNullOrWhiteSpace(sshAudioPath))
                            AddAudioButtons(assistantMessage, sshAudioPath);
                    }

                    _history.Add("assistant", sshDisplay);
                    SetUIState("idle", "Ready");
                });
                await RunOnUiAsync(RefreshLlamaCppModelAfterHermesAsync);
                return new PhoneRemoteAssistantResult(sshDisplay, sshAudioPath);
            }
            else if (TryBuildLlamaModelControlPlan(hermesPrompt, out var modelPlan))
            {
                await Dispatcher.InvokeAsync(() => AddSystemMessage($"Phone remote running direct llama.cpp model control over SSH: {modelPlan.Description}"));
                var display = await RunLlamaModelControlPlanAsync(modelPlan, ct);
                if (modelPlan.EndpointPort is int endpointPort)
                {
                    string endpointUrl = "";
                    await Dispatcher.InvokeAsync(() =>
                    {
                        endpointUrl = SetLlamaEndpointPort(endpointPort);
                    });
                    display += $"{Environment.NewLine}{Environment.NewLine}App endpoint set to {endpointUrl}";
                    var readiness = await WaitForLlamaEndpointReadyAsync(endpointUrl, TimeSpan.FromSeconds(90), ct);
                    display += $"{Environment.NewLine}{readiness.Message}";
                    if (readiness.Ready && _settings.ChatProvider.Equals("OpenAI-compatible", StringComparison.OrdinalIgnoreCase))
                    {
                        await RunOnUiAsync(async () =>
                        {
                            AddSystemMessage("Refreshing model list from the new llama.cpp endpoint.");
                            await RefreshModelsInternal();
                        });
                    }
                }
                string? modelAudioPath = null;
                if (makePhoneAudio && !string.IsNullOrWhiteSpace(display))
                {
                    var speechText = CleanSpeechText(display);
                    modelAudioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (assistantMessage != null)
                    {
                        assistantMessage.Body.Text = display;
                        if (!string.IsNullOrWhiteSpace(modelAudioPath))
                            AddAudioButtons(assistantMessage, modelAudioPath);
                    }

                    _history.Add("assistant", display);
                    SetUIState("idle", "Ready");
                });
                return new PhoneRemoteAssistantResult(display, modelAudioPath);
            }
            else if (TryGetDirectHermesSshCommand(hermesPrompt, out var directCommand))
            {
                _pendingHermesControlCommand = hermesPrompt;
                _pendingHermesSshCommand = directCommand;
                var staged = await BuildHermesSshApprovalPromptAsync(hermesPrompt, directCommand, ct);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (assistantMessage != null)
                        assistantMessage.Body.Text = staged;
                    _history.Add("assistant", staged);
                    SetUIState("idle", "Ready");
                });
                return new PhoneRemoteAssistantResult(staged, null);
            }

            await Dispatcher.InvokeAsync(() => AddSystemMessage("Phone remote running Hermes CLI over SSH."));
            var isModelControl = IsHermesModelControlCommand(hermesPrompt);
            var hermesPromptForCli = BuildHermesCliPrompt(hermesPrompt);
            var hermesTimeout = isModelControl ? TimeSpan.FromSeconds(90) : TimeSpan.FromMinutes(3);
            var hermesMaxTurns = isModelControl ? 12 : 12;
            using var hermesCliTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hermesCliTimeout.CancelAfter(hermesTimeout + TimeSpan.FromSeconds(10));
            var cliResult = await _hermesSsh.RunHermesCliAsync(hermesPromptForCli, hermesTimeout, hermesMaxTurns, hermesCliTimeout.Token);
            var cleaned = CleanDisplayText(cliResult.Stdout);
            if (cliResult.TimedOut)
                cleaned = BuildHermesTimeoutMessage(hermesPrompt);
            if (string.IsNullOrWhiteSpace(cleaned))
                cleaned = cliResult.ToDisplayText();
            string? audioPath = null;
            if (makePhoneAudio && !string.IsNullOrWhiteSpace(cleaned))
            {
                var speechText = CleanSpeechText(cleaned);
                audioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (assistantMessage != null)
                {
                    assistantMessage.Body.Text = cleaned;
                    if (!string.IsNullOrWhiteSpace(audioPath))
                        AddAudioButtons(assistantMessage, audioPath);
                }

                _history.Add("assistant", $"Hermes result:\n{cleaned}");
                SetUIState("idle", "Ready");
            });

            if (isModelControl || IsHermesApproval(hermesPrompt))
                await RunOnUiAsync(RefreshLlamaCppModelAfterHermesAsync);
            return new PhoneRemoteAssistantResult(cleaned, audioPath);
        }
        catch (OperationCanceledException)
        {
            var error = BuildHermesTimeoutMessage(hermesPrompt);
            await Dispatcher.InvokeAsync(() =>
            {
                if (assistantMessage != null)
                    assistantMessage.Body.Text = error;
                AddSystemMessage(error);
                SetUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult(error, null);
        }
        catch (Exception ex)
        {
            var error = $"Hermes error: {ex.Message}";
            await Dispatcher.InvokeAsync(() =>
            {
                if (assistantMessage != null)
                    assistantMessage.Body.Text = error;
                AddSystemMessage(error);
                SetUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult(error, null);
        }
    }

    private async Task<PhoneRemoteAssistantResult> HandlePhoneRemoteChatAsync(PhoneRemoteUserInput input, CancellationToken ct)
    {
        await _phoneRemoteChatLock.WaitAsync(ct);
        try
        {
            var userText = input.Text.Trim();
            if (string.IsNullOrWhiteSpace(userText) && input.ImagesBase64.Count > 0)
                userText = "Please analyze the attached image.";
            if (string.IsNullOrWhiteSpace(userText) && input.AudioPaths.Count > 0 && input.Documents.Count > 0)
                userText = "Please transcribe and summarize the attached meeting audio. Include key points, decisions, action items, questions, names, dates, and numbers. I may ask follow-up questions about it.";
            if (string.IsNullOrWhiteSpace(userText) && input.Documents.Count > 0)
                userText = "Please answer using the attached document.";

            if (TryCreateHermesPrompt(userText, out var hermesPrompt))
                return await HandlePhoneHermesAsync(userText, hermesPrompt, ct);

            if (PiAgentService.TryCreateReadOnlyPrompt(userText, out var piPrompt, out var piBlockedReason))
            {
                AssistantMessageUi? piAssistantMessage = null;
                bool makePiPhoneAudio = false;
                await Dispatcher.InvokeAsync(() =>
                {
                    AddUserMessage($"[iPhone] {userText}", new List<string>());
                    _history.Add("user", userText);
                    piAssistantMessage = AddAssistantMessage("");
                    makePiPhoneAudio = _settings.PhoneRemote.PlayAudioOnPhone && TtsToggle.IsChecked == true;
                    SetUIState("thinking", "Phone Pi...");
                });

                var piAnswer = piBlockedReason;
                if (string.IsNullOrWhiteSpace(piAnswer))
                {
                    try
                    {
                        piAnswer = CleanDisplayText(await _piAgent.AskAsync(piPrompt, ct));
                    }
                    catch (Exception ex)
                    {
                        piAnswer = $"Pi failed: {ex.Message}";
                    }
                }

                string? piAudioPath = null;
                if (makePiPhoneAudio && !string.IsNullOrWhiteSpace(piAnswer))
                {
                    var speechText = CleanSpeechText(piAnswer);
                    piAudioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (piAssistantMessage != null)
                    {
                        piAssistantMessage.Body.Text = piAnswer;
                        if (!string.IsNullOrWhiteSpace(piAudioPath))
                            AddAudioButtons(piAssistantMessage, piAudioPath);
                    }
                    _history.Add("assistant", $"Pi result:\n{piAnswer}");
                    SetUIState("idle", "Ready");
                });

                return new PhoneRemoteAssistantResult(piAnswer, piAudioPath);
            }

            var phoneCommandText = GetPhoneCommandText(userText);
            if (TryGetVideoPrompt(phoneCommandText, out var phoneVideoPrompt, out var phoneVideoSeconds))
                return await HandlePhoneLtxVideoAsync(input, userText, phoneVideoPrompt, phoneVideoSeconds, ct);

            if (TryGetImageEditPrompt(phoneCommandText, out var phoneEditPrompt))
                return await HandlePhoneQwenImageEditAsync(input, userText, phoneEditPrompt, ct);

            if (TryGetImageCreatePrompt(phoneCommandText, out var phoneCreatePrompt))
                return await HandlePhoneQwenImageCreateAsync(userText, phoneCreatePrompt, ct);

            var modelUserText = IsManualContinuationRequest(userText)
                ? "Continue the previous assistant response from where it left off. Do not restart, do not summarize, and do not ask what to continue. If the previous response was code, SVG, markup, a list, or a long answer, continue that same content directly."
                : userText;
            var phoneImagesBase64 = input.ImagesBase64.ToList();
            var phoneImagePaths = new List<string>();
            string model = "";
            string systemPrompt = "";
            double temperature = 0.7;
            int maxTokens = 2048;
            bool makePhoneAudio = false;
            bool webSearchEnabled = false;
            string tavilyApiKey = "";
            List<ChatMessage> messagesForModel = new();
            var transientContexts = new List<ChatMessage>();
            var attachedPhoneDocuments = input.Documents
                .Select(d => new PendingDocumentAttachment(d.FileName, d.Document))
                .ToList();
            var keepPhoneDocumentsActive = input.KeepDocumentsActive;
            var phoneActiveDocumentSet = false;
            var phoneActiveDocumentCleared = false;
            if (!keepPhoneDocumentsActive && _activePhoneDocuments.Count > 0)
            {
                _activePhoneDocuments.Clear();
                phoneActiveDocumentCleared = true;
            }

            if (keepPhoneDocumentsActive && attachedPhoneDocuments.Count > 0)
            {
                _activePhoneDocuments.Clear();
                _activePhoneDocuments.AddRange(attachedPhoneDocuments);
                phoneActiveDocumentSet = true;
            }

            var phoneDocumentsForResponse = keepPhoneDocumentsActive && _activePhoneDocuments.Count > 0
                ? _activePhoneDocuments
                : attachedPhoneDocuments;
            var phoneDocumentContext = DocumentTextService.BuildContext(phoneDocumentsForResponse.Select(d => d.Document), modelUserText);
            var phoneDocumentCount = phoneDocumentsForResponse.Count;

            if (ShouldCaptureCameraForPrompt(modelUserText))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SetUIState("processing", "Phone camera...");
                    AddSystemMessage("Phone remote requested one camera photo.");
                });

                var photo = await _camera.CapturePhotoAsync(ct);
                phoneImagePaths.Add(photo.Path);
                phoneImagesBase64.Add(photo.Base64);
            }

            await Dispatcher.InvokeAsync(() =>
            {
                model = ModelCombo.Text;
                systemPrompt = GetEffectiveSystemPrompt(memoryQueryText: userText);
                temperature = TempSlider.Value;
                maxTokens = GetMaxTokensForRequest(modelUserText, model);
                makePhoneAudio = _settings.PhoneRemote.PlayAudioOnPhone && TtsToggle.IsChecked == true;
                webSearchEnabled = WebSearchToggle.IsChecked == true;
                tavilyApiKey = TavilyApiKeyBox.Password.Trim();

                AddUserMessage($"[iPhone] {userText}", phoneImagePaths);
                _history.Add("user", userText, phoneImagesBase64);
                if (IsLargePaste(modelUserText))
                    AddSystemMessage("Large paste mode: previous chat history will not be sent with this request.");
                _history.RemoveWhere(m =>
                    m.Role.Equals("system", StringComparison.OrdinalIgnoreCase) &&
                    (m.Content.StartsWith("YouTube video transcript context for ", StringComparison.Ordinal) ||
                     m.Content.StartsWith("YouTube video local audio transcription context for ", StringComparison.Ordinal)));
                messagesForModel = BuildMessagesForModel(modelUserText, phoneImagesBase64);
                if (phoneActiveDocumentCleared)
                    AddSystemMessage("Phone active document cleared.");
                if (phoneActiveDocumentSet)
                    AddSystemMessage($"{_activePhoneDocuments.Count} phone document(s) set active. Uncheck Keep doc on the phone to stop including them.");
                if (!string.IsNullOrWhiteSpace(phoneDocumentContext))
                {
                    AddSystemMessage(keepPhoneDocumentsActive
                        ? $"{phoneDocumentCount} phone active document(s) included in this response."
                        : $"{phoneDocumentCount} phone attached document(s) added to this response.");
                }
                SetUIState("thinking", "Phone remote...");
            });

            if (string.IsNullOrWhiteSpace(model))
                return new PhoneRemoteAssistantResult("Select an Ollama model in the desktop app first.", null);

            if (DocumentTextService.TryAnswerExactSentenceQuestion(modelUserText, phoneDocumentsForResponse.Select(d => d.Document), out var exactPhoneDocumentAnswer))
            {
                string? exactAudioPath = null;
                if (makePhoneAudio)
                {
                    var speechText = CleanSpeechText(exactPhoneDocumentAnswer);
                    exactAudioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    var assistantMessage = AddAssistantMessage(exactPhoneDocumentAnswer);
                    if (!string.IsNullOrWhiteSpace(exactAudioPath))
                        AddAudioButtons(assistantMessage, exactAudioPath);

                    _history.Add("assistant", exactPhoneDocumentAnswer);
                    SetUIState("idle", "Ready");
                });

                return new PhoneRemoteAssistantResult(
                    exactPhoneDocumentAnswer,
                    exactAudioPath,
                    ActiveDocumentCount: keepPhoneDocumentsActive ? _activePhoneDocuments.Count : 0);
            }

            if (YouTubeTranscriptService.TryExtractYouTubeUrl(modelUserText, out var youtubeUrl))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    SetUIState("processing", "Phone YouTube...");
                    AddSystemMessage("Phone remote fetching YouTube transcript.");
                });

                var transcriptResult = await _youtubeTranscripts.FetchTranscriptAsync(youtubeUrl, ct);
                if (string.IsNullOrWhiteSpace(transcriptResult.Transcript))
                {
                    await Dispatcher.InvokeAsync(() => AddSystemMessage("Phone remote YouTube captions unavailable. Downloading audio for local transcription."));
                    transcriptResult = await _youtubeTranscripts.FetchAudioTranscriptAsync(
                        youtubeUrl,
                        (stream, token) => _speech.TranscribeWavAsync(stream, token),
                        ct);
                }

                if (!string.IsNullOrWhiteSpace(transcriptResult.Transcript))
                {
                    var titleLine = string.IsNullOrWhiteSpace(transcriptResult.Title)
                        ? ""
                        : $"Title: {transcriptResult.Title}\n";
                    transientContexts.Add(new ChatMessage
                    {
                        Role = "system",
                        Content = $"YouTube video transcript context for {youtubeUrl}\n{titleLine}Transcript:\n{transcriptResult.Transcript}"
                    });
                    messagesForModel = BuildMessagesForModel(modelUserText, phoneImagesBase64);
                    InsertTransientContexts(messagesForModel, transientContexts);
                    await Dispatcher.InvokeAsync(() => AddSystemMessage("Phone remote YouTube transcript added to this response."));
                }
                else
                {
                    var error = $"YouTube transcript unavailable: {transcriptResult.Error}";
                    await Dispatcher.InvokeAsync(() =>
                    {
                        AddSystemMessage(error);
                        var assistantMessage = AddAssistantMessage(error);
                        _history.Add("assistant", error);
                        SetUIState("idle", "Ready");
                    });
                    return new PhoneRemoteAssistantResult(error, null);
                }
            }

            var shouldSearchWeb = webSearchEnabled && ShouldTriggerWebSearch(modelUserText);
            if (shouldSearchWeb)
            {
                if (string.IsNullOrWhiteSpace(tavilyApiKey))
                {
                    await Dispatcher.InvokeAsync(() => AddSystemMessage("Phone remote web search is on, but no Tavily API key is set."));
                }
                else
                {
                    try
                    {
                        await Dispatcher.InvokeAsync(() => SetUIState("searching", "Phone search..."));
                        _tavily.ApiKey = tavilyApiKey;
                        var webSearchQuery = RemoveWebSearchTriggerPhrases(modelUserText);
                        var searchContext = await _tavily.SearchAndBuildContextAsync(webSearchQuery, maxResults: 5, ct: ct);
                        if (!string.IsNullOrWhiteSpace(searchContext))
                        {
                            await Dispatcher.InvokeAsync(() => RememberWebSearchContext(webSearchQuery, searchContext));
                            await Dispatcher.InvokeAsync(() => AddSystemMessage("Phone remote web search results added."));
                            messagesForModel = BuildMessagesForModel(modelUserText, phoneImagesBase64);
                            InsertTransientContexts(messagesForModel, transientContexts);
                            messagesForModel.Insert(Math.Max(0, messagesForModel.Count - 1), new ChatMessage
                            {
                                Role = "system",
                                Content = "You have current web search context for this answer. Use it as the authoritative source for current facts, releases, versions, prices, dates, schedules, and news."
                            });
                            messagesForModel[^1] = new ChatMessage
                            {
                                Role = "user",
                                Content = $"{RemoveWebSearchTriggerPhrases(modelUserText)}\n\nCurrent web search context:\n{searchContext}\n\nAnswer the user's question using the current web search context above.",
                                ImagesBase64 = phoneImagesBase64
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        await Dispatcher.InvokeAsync(() => AddSystemMessage($"Phone remote web search failed: {ex.Message}"));
                    }
                }
            }

            await AddKnowledgeContextAsync(messagesForModel, userText, ct);
            ApplyDocumentContextToCurrentUserMessage(messagesForModel, phoneDocumentContext);

            var phoneContextTokens = await GetContextTokensForRequestAsync(model, ct);
            TrimMessagesToContextBudget(messagesForModel, systemPrompt, phoneContextTokens, maxTokens);
            var response = await _ollama.ChatAsync(model, messagesForModel, systemPrompt, temperature, maxTokens, ct, phoneContextTokens);
            var displayText = CleanDisplayText(response);
            string? audioPath = null;

            if (makePhoneAudio)
            {
                var speechText = CleanSpeechText(displayText);
                audioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
            }

            await Dispatcher.InvokeAsync(() =>
            {
                var assistantMessage = AddAssistantMessage(displayText);
                if (!string.IsNullOrWhiteSpace(audioPath))
                    AddAudioButtons(assistantMessage, audioPath);

                _history.Add("assistant", displayText);
                SetUIState("idle", "Ready");
            });

            return new PhoneRemoteAssistantResult(
                displayText,
                audioPath,
                ActiveDocumentCount: keepPhoneDocumentsActive ? _activePhoneDocuments.Count : 0);
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                AddSystemMessage($"Phone remote chat error: {ex.Message}");
                SetUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult($"Phone remote error: {ex.Message}", null);
        }
        finally
        {
            _phoneRemoteChatLock.Release();
        }
    }
}
