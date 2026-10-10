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
    private async void PhoneRemoteToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = PhoneRemoteToggle.IsChecked == true;
        SaveSettings();

        try
        {
            if (enabled)
                await StartPhoneRemoteIfEnabledAsync();
            else
                await StopPhoneRemoteAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Phone remote start/stop failed.", ex);
            UpdatePhoneRemoteUi();
            AddSystemMessage($"Phone remote: {FriendlyErrors.Describe(ex)}");
        }
    }

    // Start/Stop only starts or stops the server; "Start with app" is a separate choice it never changes.
    private async void PhoneRemoteStartStop_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettings();
            if (_phoneRemoteServer.IsRunning)
                await StopPhoneRemoteAsync();
            else
                await StartPhoneRemoteAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Phone remote start/stop failed.", ex);
            UpdatePhoneRemoteUi();
            AddSystemMessage($"Phone remote: {FriendlyErrors.Describe(ex)}");
        }
    }

    private void PhoneRemoteAudioToggle_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    private void PhoneRemoteCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = _phoneRemoteServer.IsRunning ? _phoneRemoteServer.Url : $"https://{PhoneRemoteServerPreviewIp()}:{_settings.PhoneRemote.Port}/";
        try
        {
            Clipboard.SetText(url);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not copy the phone remote URL ({url}): {FriendlyErrors.Describe(ex)}");
            return;
        }
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

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not open the certificate folder: {FriendlyErrors.Describe(ex)}");
        }
        AddSystemMessage($"Install this certificate on the iPhone and fully trust it: {certPath}");
    }

    private async Task StartPhoneRemoteIfEnabledAsync()
    {
        if (_settings.PhoneRemote.Enabled != true)
        {
            UpdatePhoneRemoteUi();
            return;
        }

        await StartPhoneRemoteAsync();
    }

    /// <summary>
    /// Starts the phone remote server. A failure is reported but leaves "Start with app" as it is, so a
    /// launch before Wi-Fi is up does not turn the remote off for the next start.
    /// </summary>
    private async Task StartPhoneRemoteAsync()
    {
        if (_phoneRemoteServer.IsRunning)
        {
            UpdatePhoneRemoteUi();
            return;
        }

        try
        {
            PhoneRemoteStatusText.Text = "Starting...";
            PhoneRemoteStartStopBtn.IsEnabled = false;
            PhoneRemoteToggle.IsEnabled = false;
            EnsurePhoneRemotePin();
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
            AddSystemMessage($"Phone remote started: {_phoneRemoteServer.Url} (web transcriber: {_phoneRemoteServer.Url.TrimEnd('/')}{WebTranscriber.PagePath})");
            if (!string.IsNullOrWhiteSpace(_phoneRemoteServer.CertificateExportPath))
                AddSystemMessage($"iPhone certificate: {_phoneRemoteServer.CertificateExportPath}");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Phone remote failed to start.", ex);
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
            PhoneRemoteStatusText.Text = $"Running: {_phoneRemoteServer.Url}\nPIN: {_phoneRemoteServer.Pin}";
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

    // ==================== Web transcriber (the remote's /transcribe page) ====================

    /// <summary>
    /// The web transcriber's summary and live-notes requests: run on the UI thread like the desktop
    /// transcriber's, with its system message, but not announced in the chat (the chat only gets the
    /// transcript as context when the page sends it with Send to chat). Called on a Kestrel thread.
    /// </summary>
    private Task<string> SummarizeWebTranscriptAsync(TranscriptSummaryRequest request, CancellationToken ct) =>
        Dispatcher.InvokeAsync(() => RequestTranscriptSummaryAsync(request, _settings.Transcriber.SystemPrompt, ct)).Task.Unwrap();

    /// <summary>
    /// The web transcriber's Send to chat: the transcript and notes become the transcription context of the
    /// desktop chat and the phone remote chat. Returns the message the page shows. Called on a Kestrel thread.
    /// </summary>
    private async Task<string> SendWebTranscriptToChatAsync(string transcript, string notes)
    {
        var message = "";
        await Dispatcher.InvokeAsync(() =>
        {
            OnLiveTranscriptionContextUpdated(transcript, notes);
            var context = DescribeTranscriptContext(transcript, notes, "notes");
            var desktopWindow = _transcriptionWindow != null
                ? " The desktop Transcribe window is open too: its next change replaces this context."
                : "";
            AddSystemMessage($"Web transcriber: transcript added to the chat context {context}{desktopWindow} Ask about it here or in the phone remote.");
            message = $"Sent to the chat on the PC {context}{desktopWindow} Ask about it in the desktop chat or the remote.";
        });
        return message;
    }

    // True while a phone request has set the shared busy state (Send disabled). UI thread only.
    private bool _phoneOwnsBusyState;

    /// <summary>
    /// SetUIState for phone requests. The phone only takes the busy state when no desktop turn is
    /// running, and only gives back the state it took: it never re-enables Send during a desktop turn.
    /// UI thread.
    /// </summary>
    private void SetPhoneUIState(string state, string label)
    {
        if (state == "idle")
        {
            if (!_phoneOwnsBusyState)
                return;

            _phoneOwnsBusyState = false;
            // A desktop turn that started meanwhile sets idle itself when it ends.
            if (_chatCts == null)
                SetUIState("idle", "Ready");
            return;
        }

        if (_chatCts != null && !_phoneOwnsBusyState)
            return; // The desktop turn owns the busy state and its label.

        _phoneOwnsBusyState = true;
        SetUIState(state, label);
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
                    SetPhoneUIState("thinking", "Phone Pi...");
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
                        SetAssistantMessageText(piAssistantMessage, piAnswer);
                        if (!string.IsNullOrWhiteSpace(piAudioPath))
                            AddAudioButtons(piAssistantMessage, piAudioPath);
                    }
                    _history.Add("assistant", $"Pi result:\n{piAnswer}");
                    SetPhoneUIState("idle", "Ready");
                });

                return new PhoneRemoteAssistantResult(MarkdownText.ToPlainText(piAnswer), piAudioPath);
            }

            var modelUserText = IsManualContinuationRequest(userText)
                ? "Continue the previous assistant response from where it left off. Do not restart, do not summarize, and do not ask what to continue. If the previous response was code, SVG, markup, a list, or a long answer, continue that same content directly."
                : userText;
            var phoneImagesBase64 = input.ImagesBase64.ToList();
            // Shown as thumbnails on the desktop, like images attached there.
            var phoneImagePaths = input.ImagePaths.Where(File.Exists).ToList();
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

            await Dispatcher.InvokeAsync(() =>
            {
                model = ModelCombo.Text;
                // Same prompt as the desktop, including the code/SVG instruction for code requests.
                systemPrompt = GetEffectiveSystemPrompt(modelUserText, userText);
                temperature = TempSlider.Value;
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
                SetPhoneUIState("thinking", "Phone remote...");
            });

            if (string.IsNullOrWhiteSpace(model))
            {
                const string noModel = "Select a model in the desktop app first.";
                await Dispatcher.InvokeAsync(() =>
                {
                    AddSystemMessage($"Phone remote: {noModel}");
                    SetPhoneUIState("idle", "Ready");
                });
                return new PhoneRemoteAssistantResult(noModel, null);
            }

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
                    SetPhoneUIState("idle", "Ready");
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
                    SetPhoneUIState("processing", "Phone YouTube...");
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
                        SetPhoneUIState("idle", "Ready");
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
                        await Dispatcher.InvokeAsync(() => SetPhoneUIState("searching", "Phone search..."));
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

            await AddKnowledgeContextAsync(messagesForModel, userText, modelUserText, phoneDocumentContext, model, ct);
            ApplyDocumentContextToCurrentUserMessage(messagesForModel, phoneDocumentContext);

            var phoneContextTokens = await GetContextTokensForRequestAsync(model, ct);
            maxTokens = GetMaxTokensForRequest(modelUserText, phoneContextTokens);
            TrimMessagesToContextBudget(messagesForModel, systemPrompt, phoneContextTokens, maxTokens);
            var response = await _ollama.ChatAsync(model, messagesForModel, systemPrompt, temperature, maxTokens, ct, phoneContextTokens);
            response = await CompleteCodeArtifactIfNeededAsync(
                response, modelUserText, messagesForModel, systemPrompt, model, temperature, maxTokens, phoneContextTokens, ct);
            // Code-preserving cleaning, as on the desktop: the history keeps the code intact and only the
            // spoken copy and the phone's plain-text copy are simplified.
            var isCodeResponse = IsCodeOrScriptRequest(modelUserText) || ContainsFencedCodeBlock(response);
            var displayText = CleanDisplayText(response, preserveCodeBlocks: isCodeResponse);
            string? audioPath = null;

            if (makePhoneAudio)
            {
                var speechText = CleanSpeechText(displayText);
                audioPath = await _speech.CreateSpeechAudioFileAsync(speechText, GetAssistantAudioDirectory());
            }

            await Dispatcher.InvokeAsync(() =>
            {
                var assistantMessage = AddFinishedAssistantMessage(displayText);
                if (!string.IsNullOrWhiteSpace(audioPath))
                    AddAudioButtons(assistantMessage, audioPath);

                _history.Add("assistant", displayText);
                SetPhoneUIState("idle", "Ready");
            });

            return new PhoneRemoteAssistantResult(
                MarkdownText.ToPlainText(displayText),
                audioPath,
                ActiveDocumentCount: keepPhoneDocumentsActive ? _activePhoneDocuments.Count : 0);
        }
        catch (Exception ex)
        {
            AppLog.Error("Phone remote chat failed", ex);
            await Dispatcher.InvokeAsync(() =>
            {
                AddSystemMessage($"Phone remote chat error: {ex.Message}");
                SetPhoneUIState("idle", "Ready");
            });
            return new PhoneRemoteAssistantResult($"Phone remote error: {ex.Message}", null);
        }
        finally
        {
            // Every path gives back the busy state it took, including early returns and cancellation.
            try { await Dispatcher.InvokeAsync(() => SetPhoneUIState("idle", "Ready")); }
            catch (Exception ex) { AppLog.Warn("Could not reset the UI after a phone request.", ex); }
            _phoneRemoteChatLock.Release();
        }
    }
}
