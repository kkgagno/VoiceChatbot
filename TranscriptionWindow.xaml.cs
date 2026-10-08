using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace VoiceChatbot;

/// <summary>
/// Live Transcriber: records the microphone or PC audio, cuts it into chunks at pauses (SpeechChunker),
/// transcribes the chunks one at a time with the app's Whisper backend and appends them as "[mm:ss]" lines.
/// The chat model can summarize the transcript (TranscriptSummarizer splits a long one into parts), or keep
/// live notes up to date while recording (LiveNotesPolicy decides when), and the main chat gets both as context.
/// </summary>
public partial class TranscriptionWindow : Window
{
    private const double MinFontSize = 10;
    private const double MaxFontSize = 32;
    private const double DefaultFontSize = 15;
    // After the assistant stops speaking its voice can still echo for a moment.
    private static readonly TimeSpan SpeechEchoTail = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan StatusMessageTime = TimeSpan.FromSeconds(6);

    /// <summary>%APPDATA%\VoiceChatbot\transcripts: where each session is saved when it stops.</summary>
    public static string TranscriptsFolder { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VoiceChatbot", "transcripts");

    private sealed record PendingChunk(SpeechChunk Chunk, TimeSpan At);

    private readonly SpeechEngine _speech;
    private readonly TranscriberSettings _settings;
    private readonly Action _saveSettings;
    // (request, system message, cancellation) -> the chat model's reply as plain text. Called on the UI thread.
    private readonly Func<TranscriptSummaryRequest, string, CancellationToken, Task<string>> _summarizeAsync;
    private readonly Action<string, string> _contextUpdated;
    private readonly Action<string, string> _sendToChat;
    private readonly CancellationTokenSource _lifetimeCts = new();

    // Audio. The source raises audio on its capture thread; everything below is used under _audioSync.
    private readonly object _audioSync = new();
    private TranscriberAudioSource? _source;
    private SpeechChunker? _chunker;
    private TimeSpan _timelineOrigin; // session time at the chunker's position zero
    private Channel<PendingChunk>? _queue;
    private bool _pausedForSpeech;
    private double _peakLevel;

    // The assistant's own voice is not transcribed (set from SpeechEngine.StateChanged on any thread).
    private volatile bool _assistantSpeaking;
    private long _muteUntilTimestamp;

    // Transcription: strictly one chunk at a time, in order.
    private readonly SemaphoreSlim _chunkLock = new(1, 1);
    private Task? _queueWorker;
    private int _pendingChunks;

    // Session (UI thread).
    private readonly Stopwatch _elapsed = new();
    private readonly DispatcherTimer _uiTimer;
    private bool _initializing = true;
    private bool _isRecording;
    private bool _isFinishing; // stopped, the last chunks are still being transcribed
    private Task? _stopTask;
    private string _sourceDisplayName = "";
    private string _backendLabel = "";
    private DateTime? _sessionStarted;
    private bool _unsavedChanges;
    private DateTime _statusMessageUntil = DateTime.MinValue;
    private string _summaryStyleUsed = TranscriptSummaryStyles.Summary;
    private CancellationTokenSource? _summaryCts;
    private int _clearCount;
    private double _fontSize = DefaultFontSize;
    private bool _closeAllowed;
    private bool _closeRequested;
    private bool _closed;

    // Live notes (UI thread): the summary pane is updated with what was said since the last update.
    private readonly LiveNotesPolicy _liveNotes = new();
    private readonly DispatcherTimer _liveNotesTimer;
    private CancellationTokenSource? _liveNotesCts;
    private Task<bool>? _liveNotesTask;
    private DateTime? _notesUpdatedAt; // local time of the last live-notes update shown
    private bool _liveNotesFailed;

    public TranscriptionWindow(
        SpeechEngine speech,
        TranscriberSettings settings,
        Action saveSettings,
        Func<TranscriptSummaryRequest, string, CancellationToken, Task<string>> summarizeAsync,
        Action<string, string> contextUpdated,
        Action<string, string> sendToChat)
    {
        _speech = speech;
        _settings = settings;
        _saveSettings = saveSettings;
        _summarizeAsync = summarizeAsync;
        _contextUpdated = contextUpdated;
        _sendToChat = sendToChat;

        InitializeComponent();
        WindowTheme.UseThemedTitleBar(this);

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _uiTimer.Tick += (_, _) => RefreshLiveStatus();
        _liveNotesTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LiveNotesPolicy.CheckEvery };
        _liveNotesTimer.Tick += (_, _) => LiveNotesTick();

        ApplySettings();
        _initializing = false;

        _assistantSpeaking = _speech.CurrentState == VoiceState.Speaking;
        _speech.StateChanged += OnSpeechStateChanged;

        UpdateRecordingUi();
        UpdateStats();
        ShowStatus("Ready. Choose Microphone or PC audio and press Start (Ctrl+R).", sticky: true);
    }

    public bool IsTranscribing => _isRecording;

    public void ActivateExisting()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = _settings.Maximized ? WindowState.Maximized : WindowState.Normal;

        Activate();
        // Activate alone can leave the window behind the app that has focus.
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>
    /// Called by the main window when the app exits: stops recording, waits up to <paramref name="wait"/>
    /// for the chunks still being transcribed, saves the session and closes.
    /// </summary>
    public async Task CloseForAppExitAsync(TimeSpan wait)
    {
        if (_closed)
            return;

        StoreWindowSettings();
        await FinishPendingWorkAsync(wait);
        _closeAllowed = true;
        if (!_closed)
            Close();
    }

    // ==================== Settings ====================

    private void ApplySettings()
    {
        Width = Math.Clamp(Finite(_settings.Width, 1000), MinWidth, 6000);
        Height = Math.Clamp(Finite(_settings.Height, 760), MinHeight, 4000);
        if (MainWindow.IsSavedWindowPositionVisible(_settings.Left, _settings.Top, Width))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.Left;
            Top = _settings.Top;
        }
        if (_settings.Maximized)
            WindowState = WindowState.Maximized;

        var transcriptWeight = Finite(_settings.TranscriptPaneHeight, 0);
        var summaryWeight = Finite(_settings.SummaryPaneHeight, 0);
        if (transcriptWeight > 0 && summaryWeight > 0)
        {
            TranscriptRow.Height = new GridLength(transcriptWeight, GridUnitType.Star);
            SummaryRow.Height = new GridLength(summaryWeight, GridUnitType.Star);
        }

        SystemPromptBox.Height = Math.Clamp(Finite(_settings.SystemPromptHeight, 90), 50, 400);
        SystemPromptBox.Text = string.IsNullOrWhiteSpace(_settings.SystemPrompt)
            ? TranscriberSettings.DefaultSystemPrompt
            : _settings.SystemPrompt;
        SetFontSize(_settings.FontSize);

        SourceCombo.ItemsSource = new[] { TranscriberSettings.SourceMicrophone, TranscriberSettings.SourcePcAudio };
        SourceCombo.SelectedItem = _settings.Source == TranscriberSettings.SourcePcAudio
            ? TranscriberSettings.SourcePcAudio
            : TranscriberSettings.SourceMicrophone;

        SummaryStyleCombo.ItemsSource = TranscriptSummaryStyles.Names;
        SummaryStyleCombo.SelectedItem = TranscriptSummaryStyles.Normalize(_settings.SummaryStyle);

        var minutes = LiveNotesPolicy.NormalizeIntervalMinutes(_settings.LiveNotesIntervalMinutes);
        LiveNotesIntervalCombo.ItemsSource = LiveNotesPolicy.IntervalChoicesMinutes.Select(m => $"{m} min").ToList();
        LiveNotesIntervalCombo.SelectedIndex = IndexOfInterval(minutes);
        LiveNotesToggle.IsChecked = _settings.LiveNotes;
        _liveNotes.Enabled = _settings.LiveNotes;
        _liveNotes.Interval = TimeSpan.FromMinutes(minutes);
        _settings.LiveNotesIntervalMinutes = minutes;
    }

    /// <summary>Copies the window size, pane heights and choices into the settings object (saved by the caller).</summary>
    private void StoreWindowSettings()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty && double.IsFinite(bounds.Left) && double.IsFinite(bounds.Top) && bounds.Width > 0 && bounds.Height > 0)
        {
            _settings.Left = bounds.Left;
            _settings.Top = bounds.Top;
            _settings.Width = bounds.Width;
            _settings.Height = bounds.Height;
        }
        _settings.Maximized = WindowState == WindowState.Maximized;

        StorePaneHeights();
        if (double.IsFinite(SystemPromptBox.Height))
            _settings.SystemPromptHeight = SystemPromptBox.Height;
        _settings.SystemPrompt = SystemPromptBox.Text;
        _settings.FontSize = _fontSize;
        _settings.Source = SelectedSource;
        _settings.SummaryStyle = SelectedSummaryStyle;
        _settings.LiveNotes = LiveNotesToggle.IsChecked == true;
        _settings.LiveNotesIntervalMinutes = SelectedIntervalMinutes;
    }

    private void StorePaneHeights()
    {
        if (TranscriptRow.ActualHeight > 0 && SummaryRow.ActualHeight > 0)
        {
            _settings.TranscriptPaneHeight = Math.Round(TranscriptRow.ActualHeight);
            _settings.SummaryPaneHeight = Math.Round(SummaryRow.ActualHeight);
        }
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    private string SelectedSource => SourceCombo.SelectedItem as string ?? TranscriberSettings.SourceMicrophone;

    private string SelectedSummaryStyle => TranscriptSummaryStyles.Normalize(SummaryStyleCombo.SelectedItem as string);

    private int SelectedIntervalMinutes
    {
        get
        {
            var choices = LiveNotesPolicy.IntervalChoicesMinutes;
            var index = LiveNotesIntervalCombo.SelectedIndex;
            return index >= 0 && index < choices.Count ? choices[index] : LiveNotesPolicy.DefaultIntervalMinutes;
        }
    }

    private static int IndexOfInterval(int minutes)
    {
        var choices = LiveNotesPolicy.IntervalChoicesMinutes;
        for (var i = 0; i < choices.Count; i++)
        {
            if (choices[i] == minutes)
                return i;
        }
        return 0;
    }

    // ==================== Recording ====================

    private void StartStop_Click(object sender, RoutedEventArgs e) => ToggleRecording();

    private void ToggleRecording()
    {
        if (_isFinishing)
            return; // still transcribing the last chunks

        if (_isRecording)
            _ = StopRecordingAsync();
        else
            StartRecording();
    }

    private void StartRecording()
    {
        if (_isRecording || _isFinishing)
            return;

        var queue = Channel.CreateUnbounded<PendingChunk>(new UnboundedChannelOptions { SingleReader = true });
        lock (_audioSync)
        {
            _queue = queue;
            _chunker = new SpeechChunker(new SpeechChunkerOptions
            {
                SpeechThreshold = SpeechChunkerOptions.ThresholdForNoiseGate(_speech.NoiseGate)
            });
            _timelineOrigin = _elapsed.Elapsed;
        }

        if (!StartSource(SelectedSource))
        {
            lock (_audioSync)
            {
                _queue = null;
                _chunker = null;
            }
            queue.Writer.TryComplete();
            return;
        }

        _sessionStarted ??= DateTime.Now;
        _backendLabel = _speech.GetTranscriptionBackendStatus();
        _queueWorker = Task.Run(() => ProcessQueueAsync(queue.Reader, _lifetimeCts.Token));
        _isRecording = true;
        _elapsed.Start();
        _statusMessageUntil = DateTime.MinValue;
        _uiTimer.Start();
        _liveNotes.RestartClock(DateTime.UtcNow);
        _liveNotesTimer.Start();
        UpdateRecordingUi();
        RefreshLiveStatus();
        if (_source?.Notice is { } notice)
            ShowStatus(notice);
    }

    /// <summary>Creates and starts the audio source; on failure shows why and returns false.</summary>
    private bool StartSource(string sourceName)
    {
        var source = TranscriberAudioSource.Create(sourceName, _speech.MicDeviceIndex);
        source.AudioAvailable += OnSourceAudio;
        source.Stopped += OnSourceStopped;
        lock (_audioSync)
            _source = source;

        try
        {
            source.Start();
            _sourceDisplayName = source.DisplayName;
            AppLog.Info($"Live transcriber recording {source.DisplayName}.");
            return true;
        }
        catch (Exception ex)
        {
            lock (_audioSync)
            {
                if (ReferenceEquals(_source, source))
                    _source = null;
            }
            DisposeSource(source);

            var message = ex is TranscriberAudioException ? ex.Message : $"Could not start recording: {ex.Message}";
            AppLog.Warn($"Live transcriber: {message}", ex);
            ShowStatus(message, sticky: true);
            return false;
        }
    }

    private Task StopRecordingAsync(string? reason = null)
    {
        if (!_isRecording)
            return _stopTask ?? Task.CompletedTask;

        _stopTask = StopRecordingCoreAsync(reason);
        return _stopTask;
    }

    private async Task StopRecordingCoreAsync(string? reason)
    {
        _isRecording = false;
        _isFinishing = true;
        _elapsed.Stop();
        _liveNotesTimer.Stop();

        var source = DetachSource(flush: true);
        Channel<PendingChunk>? queue;
        lock (_audioSync)
        {
            queue = _queue;
            _queue = null;
            _chunker = null;
        }
        queue?.Writer.TryComplete();
        DisposeSource(source);

        UpdateRecordingUi();
        _statusMessageUntil = DateTime.MinValue;
        RefreshLiveStatus();

        // Let the chunks already recorded finish instead of dropping them.
        var worker = _queueWorker;
        if (worker != null)
        {
            try
            {
                await worker;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Live transcriber: the transcription queue failed.", ex);
            }
        }

        _queueWorker = null;
        _isFinishing = false;
        _uiTimer.Stop();
        if (_closed)
            return;

        // Save right away so the transcript is safe, even while the final notes are still being written.
        var savedPath = AutoSave();
        UpdateRecordingUi();
        var stopped = reason ?? "Stopped.";
        ShowStatus(stopped + SavedNote(savedPath), sticky: true);

        // Live notes: one last update with the words added since the previous one, then the same session
        // file is saved again so it has the final notes.
        var notesUpdated = await FinishLiveNotesAsync(stopped);
        if (_closed)
            return;

        var resavedPath = AutoSave();
        if (notesUpdated && !_isRecording && _notesUpdatedAt is { } at)
            ShowStatus($"{stopped} {LiveNotesPolicy.FormatUpdated(at)}.{SavedNote(resavedPath ?? savedPath)}", sticky: true);
    }

    private static string SavedNote(string? path) =>
        path != null ? $" Saved to {System.IO.Path.GetFileName(path)}." : "";

    /// <summary>
    /// Stops recording (if it runs) and waits up to <paramref name="limit"/> for pending chunks and the final live notes.
    /// </summary>
    private async Task FinishPendingWorkAsync(TimeSpan limit)
    {
        var stopping = _isRecording ? StopRecordingAsync() : IsStopping ? _stopTask : null;
        if (stopping == null)
            return;

        if (await Task.WhenAny(stopping, Task.Delay(limit)) != stopping)
        {
            AppLog.Warn("Live transcriber: chunks or the final notes were still being written when the window closed; they were dropped.");
            _lifetimeCts.Cancel();
        }
    }

    /// <summary>True from Stop until the last chunks are transcribed and the final live notes are written.</summary>
    private bool IsStopping => _stopTask is { IsCompleted: false };

    /// <summary>Takes the current source out of use; with <paramref name="flush"/> the speech buffered so far is queued.</summary>
    private TranscriberAudioSource? DetachSource(bool flush)
    {
        lock (_audioSync)
        {
            var source = _source;
            _source = null;
            if (flush && _chunker?.Flush() is { } rest)
                EnqueueLocked(rest);
            return source;
        }
    }

    private void DisposeSource(TranscriberAudioSource? source)
    {
        if (source == null)
            return;

        source.AudioAvailable -= OnSourceAudio;
        source.Stopped -= OnSourceStopped;
        try
        {
            source.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Live transcriber: could not close the audio device.", ex);
        }
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;

        _settings.Source = SelectedSource;
        if (!_isRecording)
        {
            ShowStatus(SelectedSource == TranscriberSettings.SourcePcAudio
                ? "PC audio: everything the PC plays (calls, videos) is transcribed. Press Start."
                : "Microphone: the microphone selected in the main window is transcribed. Press Start.", sticky: true);
            return;
        }

        // Switching while recording: keep what was said so far, then continue with the new source.
        DisposeSource(DetachSource(flush: true));
        lock (_audioSync)
        {
            // Re-align timestamps with the clock: the switch itself took a moment without audio.
            if (_chunker != null)
                _timelineOrigin = _elapsed.Elapsed - _chunker.Position;
        }

        if (!StartSource(SelectedSource))
        {
            var message = StatusText.Text;
            _ = StopRecordingAsync(message);
            return;
        }

        _statusMessageUntil = DateTime.MinValue;
        RefreshLiveStatus();
        if (_source?.Notice is { } notice)
            ShowStatus(notice);
    }

    // Capture thread.
    private void OnSourceAudio(TranscriberAudioSource source, byte[] buffer, int count)
    {
        lock (_audioSync)
        {
            if (!ReferenceEquals(source, _source) || _chunker == null)
                return;

            if (IsAssistantSpeaking())
            {
                // Keep what the user said before the reply started, then drop the reply's audio.
                if (_chunker.Flush() is { } before)
                    EnqueueLocked(before);
                _chunker.Skip(TimeSpan.FromSeconds(count / 2.0 / TranscriberAudioSource.SampleRate));
                _pausedForSpeech = true;
                return;
            }

            _pausedForSpeech = false;
            foreach (var chunk in _chunker.Add(buffer.AsSpan(0, count)))
                EnqueueLocked(chunk);
            _peakLevel = Math.Max(_peakLevel, _chunker.LastLevel);
        }
    }

    // Any thread.
    private void OnSourceStopped(TranscriberAudioSource source, Exception? error)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!_isRecording || !ReferenceEquals(source, _source))
                return;

            AppLog.Warn("Live transcriber: the audio device stopped.", error);
            _ = StopRecordingAsync($"Recording stopped: {DescribeDeviceError(error)}");
        });
    }

    private static string DescribeDeviceError(Exception? error)
    {
        const int deviceInvalidated = unchecked((int)0x88890004); // AUDCLNT_E_DEVICE_INVALIDATED
        return error switch
        {
            null => "the audio device stopped.",
            COMException { HResult: deviceInvalidated } => "the audio device was unplugged or changed.",
            _ when error.Message.Contains("NoDriver", StringComparison.OrdinalIgnoreCase) ||
                   error.Message.Contains("BadDeviceId", StringComparison.OrdinalIgnoreCase)
                => "the microphone was unplugged or is no longer available.",
            _ => error.Message
        };
    }

    private void OnSpeechStateChanged(VoiceState state)
    {
        var speaking = state == VoiceState.Speaking;
        if (_assistantSpeaking && !speaking)
        {
            var tail = (long)(SpeechEchoTail.TotalSeconds * Stopwatch.Frequency);
            Interlocked.Exchange(ref _muteUntilTimestamp, Stopwatch.GetTimestamp() + tail);
        }

        _assistantSpeaking = speaking;
    }

    private bool IsAssistantSpeaking() =>
        _assistantSpeaking || Stopwatch.GetTimestamp() < Interlocked.Read(ref _muteUntilTimestamp);

    // Under _audioSync.
    private void EnqueueLocked(SpeechChunk chunk)
    {
        if (_queue == null)
            return;

        Interlocked.Increment(ref _pendingChunks);
        if (!_queue.Writer.TryWrite(new PendingChunk(chunk, _timelineOrigin + chunk.Start)))
            Interlocked.Decrement(ref _pendingChunks);
    }

    // ==================== Transcription ====================

    private async Task ProcessQueueAsync(ChannelReader<PendingChunk> reader, CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    try
                    {
                        await TranscribeChunkAsync(item, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pendingChunks);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The window closed while chunks were waiting.
            Interlocked.Exchange(ref _pendingChunks, 0);
        }
    }

    private async Task TranscribeChunkAsync(PendingChunk item, CancellationToken ct)
    {
        string text;
        await _chunkLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var wav = BuildWavStream(item.Chunk.Pcm);
            text = await _speech.TranscribeWavAsync(wav, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Live transcriber: a chunk could not be transcribed.", ex);
            _ = Dispatcher.InvokeAsync(() => ShowStatus($"Transcription error: {ex.Message}"));
            return;
        }
        finally
        {
            _chunkLock.Release();
        }

        var line = LiveTranscriptText.FormatLine(item.At, LiveTranscriptText.CleanChunk(text));
        if (line.Length == 0)
            return;

        await Dispatcher.InvokeAsync(() => AppendTranscriptLine(line));
    }

    private void AppendTranscriptLine(string line)
    {
        if (_closed)
            return;

        var box = TranscriptBox;
        // Follow new text only when the reader is already at the end, not while they scroll back.
        var atBottom = box.ExtentHeight <= box.ViewportHeight ||
                       box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 6;
        var separator = box.Text.Length == 0 || box.Text.EndsWith('\n') ? "" : Environment.NewLine;
        box.AppendText(separator + line);
        if (atBottom)
            box.ScrollToEnd();

        _backendLabel = _speech.LastTranscriptionBackendUsed;
    }

    // ==================== Status ====================

    /// <summary>
    /// Shows a message in the status line. While recording the live status returns after a few seconds,
    /// unless <paramref name="sticky"/>; when stopped every message stays until the next one.
    /// </summary>
    private void ShowStatus(string message, bool sticky = false)
    {
        StatusText.Text = message;
        StatusText.ToolTip = message;
        _statusMessageUntil = sticky ? DateTime.MaxValue : DateTime.UtcNow + StatusMessageTime;
    }

    /// <summary>Timer tick while recording or finishing: elapsed time, level meter and the live status line.</summary>
    private void RefreshLiveStatus()
    {
        ElapsedText.Text = LiveTranscriptText.FormatTimestamp(_elapsed.Elapsed);

        double level;
        bool paused;
        lock (_audioSync)
        {
            level = _peakLevel;
            _peakLevel = 0;
            paused = _pausedForSpeech;
        }
        LevelMeter.Value = _isRecording && !paused ? LevelPercent(level) : 0;

        if (DateTime.UtcNow < _statusMessageUntil || (!_isRecording && !_isFinishing))
            return;

        var pending = Volatile.Read(ref _pendingChunks);
        string status;
        if (!_isRecording)
            status = pending > 0 ? $"Finishing... {Chunks(pending)} left to transcribe." : "Finishing...";
        else if (paused)
            status = "Paused while the assistant speaks, so its voice is not transcribed.";
        else
            status = $"Listening to {_sourceDisplayName} · {_backendLabel}" +
                     (pending > 0 ? $" · transcribing {Chunks(pending)}..." : "");

        StatusText.Text = status;
        StatusText.ToolTip = status;
        _statusMessageUntil = DateTime.MinValue;
    }

    private static string Chunks(int count) => count == 1 ? "1 chunk" : $"{count} chunks";

    // -60 dBFS .. 0 dBFS as 0..100.
    private static double LevelPercent(double rms) =>
        rms <= 0 ? 0 : Math.Clamp((20 * Math.Log10(rms) + 60) / 60 * 100, 0, 100);

    private void UpdateRecordingUi()
    {
        var finishing = _isFinishing;
        StartStopBtn.Content = finishing ? "Finishing..." : _isRecording ? "Stop" : "Start";
        Ui.SetIcon(StartStopBtn, _isRecording || finishing ? "" : "");
        StartStopBtn.Style = (Style)FindResource(_isRecording ? "DangerButton" : "ActionButton");
        StartStopBtn.IsEnabled = !finishing;
        RecordDot.SetResourceReference(Shape.FillProperty,
            _isRecording ? "ErrorBrush" : finishing ? "WarningBrush" : "TextMutedBrush");
        ElapsedText.Text = LiveTranscriptText.FormatTimestamp(_elapsed.Elapsed);
        if (!_isRecording)
            LevelMeter.Value = 0;

        // The transcript can be corrected while stopped; while recording new lines keep arriving.
        TranscriptBox.IsReadOnly = _isRecording || finishing;
        EditHintText.Text = TranscriptBox.IsReadOnly ? "Read-only while recording" : "Editable";
        ClearBtn.IsEnabled = !finishing;
    }

    private void UpdateStats()
    {
        var words = LiveTranscriptText.CountWords(TranscriptBox.Text);
        StatsText.Text = words == 1 ? "1 word" : $"{words:N0} words";
    }

    private void UpdateSummaryHeader()
    {
        var hasSummary = !string.IsNullOrWhiteSpace(SummaryBox.Text);
        SummaryHeaderText.Text = hasSummary ? _summaryStyleUsed.ToUpperInvariant() : "SUMMARY";

        var updated = hasSummary && _notesUpdatedAt is { } at ? LiveNotesPolicy.FormatUpdated(at) : "";
        string detail;
        if (_liveNotesCts != null)
            detail = "Updating notes...";
        else if (_liveNotesFailed)
            detail = updated.Length > 0 ? $"Update failed · {updated}" : "Notes update failed";
        else
            detail = updated;

        NotesUpdatedText.Text = detail;
        NotesUpdatedText.ToolTip = detail.Length > 0 ? detail : null;
    }

    // ==================== Text and context ====================

    private void TranscriptBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing)
            return;

        _unsavedChanges = true;
        UpdateStats();
        _contextUpdated(TranscriptBox.Text, SummaryBox.Text);
    }

    private void SummaryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing)
            return;

        _unsavedChanges = true;
        UpdateSummaryHeader();
        _contextUpdated(TranscriptBox.Text, SummaryBox.Text);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_isFinishing)
            return;

        // Nothing is lost: the session so far goes to the transcripts folder first.
        var savedPath = AutoSave();

        // A summary or live-notes update of the old transcript is no longer wanted.
        _clearCount++;
        _summaryCts?.Cancel();
        CancelLiveNotes();
        _liveNotes.Reset(DateTime.UtcNow);
        _notesUpdatedAt = null;
        _liveNotesFailed = false;

        TranscriptBox.Clear();
        SummaryBox.Clear();
        _summaryStyleUsed = TranscriptSummaryStyles.Summary;
        UpdateSummaryHeader();
        _unsavedChanges = false;
        _sessionStarted = _isRecording ? DateTime.Now : null;

        // A new session starts at 00:00.
        _elapsed.Reset();
        if (_isRecording)
            _elapsed.Start();
        lock (_audioSync)
            _timelineOrigin = _chunker != null ? TimeSpan.Zero - _chunker.Position : TimeSpan.Zero;
        ElapsedText.Text = LiveTranscriptText.FormatTimestamp(TimeSpan.Zero);

        _contextUpdated("", "");
        var saved = savedPath != null ? $" The previous transcript was saved to {System.IO.Path.GetFileName(savedPath)}." : "";
        ShowStatus("Cleared." + saved, sticky: !_isRecording);
    }

    // ==================== Summary ====================

    private async void Summarize_Click(object sender, RoutedEventArgs e)
    {
        if (_summaryCts != null)
        {
            _summaryCts.Cancel();
            return;
        }

        var transcriptSnapshot = TranscriptBox.Text;
        var transcript = transcriptSnapshot.Trim();
        if (transcript.Length == 0)
        {
            ShowStatus("No transcript to summarize yet.");
            return;
        }

        // The full summary replaces the notes, so a live-notes update in progress gives way to it.
        CancelLiveNotes();

        var style = SelectedSummaryStyle;
        var systemPrompt = SystemPromptBox.Text;
        var clearCount = _clearCount;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _summaryCts = cts;
        SummarizeBtn.Content = "Cancel";
        Ui.SetIcon(SummarizeBtn, "\uE711");
        SummaryStyleCombo.IsEnabled = false;
        ShowStatus($"Writing {style.ToLowerInvariant()}...", sticky: true);

        // A long transcript is summarized in parts; show which one is being written.
        var progress = new Progress<string>(message =>
        {
            if (!_closed && ReferenceEquals(_summaryCts, cts) && !cts.IsCancellationRequested)
                ShowStatus(message, sticky: true);
        });

        try
        {
            var summary = await TranscriptSummarizer.SummarizeAsync(
                transcript, style, (request, ct) => _summarizeAsync(request, systemPrompt, ct), progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_closed)
                return;

            if (summary.Length == 0)
            {
                ShowStatus("The chat model returned an empty summary; the previous one is kept.", sticky: !_isRecording);
                return;
            }

            _summaryStyleUsed = style;
            _notesUpdatedAt = null;
            _liveNotesFailed = false;
            SummaryBox.Text = summary;
            SummaryBox.ScrollToHome();
            UpdateSummaryHeader();
            // Live notes carry on from this summary with the words added after it.
            _liveNotes.MarkSummarized(transcriptSnapshot, DateTime.UtcNow);
            ShowStatus($"{style} ready.");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (!_closed && clearCount == _clearCount)
                ShowStatus("Summary cancelled.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Live transcriber: summary failed.", ex);
            if (!_closed)
                ShowStatus($"Summary failed: {ex.Message}", sticky: !_isRecording);
        }
        finally
        {
            _summaryCts = null;
            if (!_closed)
            {
                SummarizeBtn.Content = "Summarize";
                Ui.SetIcon(SummarizeBtn, "\uE9D5");
                SummaryStyleCombo.IsEnabled = true;
            }
        }
    }

    // ==================== Live notes ====================

    private void LiveNotesToggle_Click(object sender, RoutedEventArgs e)
    {
        var on = LiveNotesToggle.IsChecked == true;
        _settings.LiveNotes = on;
        _liveNotes.Enabled = on;
        if (!on)
        {
            CancelLiveNotes();
            ShowStatus("Live notes off. Summarize still writes a summary of the whole transcript.");
            return;
        }

        var every = IntervalText(SelectedIntervalMinutes);
        ShowStatus(_isRecording
            ? $"Live notes on: the notes below are updated every {every} while recording."
            : $"Live notes on: while recording, the notes below are updated every {every}.", sticky: !_isRecording);
    }

    private void LiveNotesIntervalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
            return;

        var minutes = SelectedIntervalMinutes;
        _settings.LiveNotesIntervalMinutes = minutes;
        _liveNotes.Interval = TimeSpan.FromMinutes(minutes);
        if (_liveNotes.Enabled)
            ShowStatus($"Live notes are updated every {IntervalText(minutes)} while recording.");
    }

    private static string IntervalText(int minutes) => minutes == 1 ? "minute" : $"{minutes} minutes";

    /// <summary>Every 15 seconds while recording: starts a live-notes update when one is due.</summary>
    private void LiveNotesTick()
    {
        if (_closed)
            return;

        // A running manual Summarize skips this tick; the next one checks again.
        var ticket = _liveNotes.TryBegin(DateTime.UtcNow, _isRecording, _summaryCts != null, TranscriptBox.Text);
        if (ticket != null)
            StartLiveNotesUpdate(ticket);
    }

    private Task<bool> StartLiveNotesUpdate(LiveNotesTicket ticket)
    {
        var task = RunLiveNotesAsync(ticket);
        _liveNotesTask = task;
        return task;
    }

    /// <summary>
    /// After Stop (the last chunks are transcribed): waits for an update in progress, then runs the final one
    /// when words were added since. True when either put new notes in the summary pane.
    /// </summary>
    private async Task<bool> FinishLiveNotesAsync(string stoppedMessage)
    {
        var updated = false;
        if (_liveNotesTask is { IsCompleted: false } running)
            updated = await running;

        if (_closed)
            return false;

        var ticket = _liveNotes.TryBeginFinal(_summaryCts != null, TranscriptBox.Text);
        if (ticket == null)
            return updated;

        if (!_isRecording)
            ShowStatus($"{stoppedMessage} Writing the final notes...", sticky: true);
        return await StartLiveNotesUpdate(ticket) || updated;
    }

    /// <summary>
    /// Sends the current notes and only the text added since the last update; on success the reply replaces
    /// the summary pane. Errors keep the previous notes. Never throws.
    /// </summary>
    private async Task<bool> RunLiveNotesAsync(LiveNotesTicket ticket)
    {
        var notesBefore = SummaryBox.Text.Trim();
        var style = SelectedSummaryStyle;
        var systemPrompt = SystemPromptBox.Text;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _liveNotesCts = cts;
        UpdateSummaryHeader();

        try
        {
            var notes = await TranscriptSummarizer.UpdateNotesAsync(
                notesBefore, ticket.NewText, style, (request, ct) => _summarizeAsync(request, systemPrompt, ct), null, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_closed || !_liveNotes.IsCurrent(ticket))
                return false; // cleared meanwhile

            if (!string.Equals(SummaryBox.Text.Trim(), notesBefore, StringComparison.Ordinal))
            {
                // The summary was edited while the notes were written: keep the edit, merge on the next check.
                _liveNotes.Abandon(ticket);
                ShowStatus("Live notes not applied because the summary was edited meanwhile. They update on the next check.");
                return false;
            }

            _liveNotes.Complete(ticket, DateTime.UtcNow);
            _notesUpdatedAt = DateTime.Now;
            _liveNotesFailed = false;
            _summaryStyleUsed = style;
            SummaryBox.Text = notes;
            ShowStatus(LiveNotesPolicy.FormatUpdated(_notesUpdatedAt.Value) + ".");
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _liveNotes.Abandon(ticket);
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Live transcriber: the live notes could not be updated.", ex);
            _liveNotes.Fail(ticket, DateTime.UtcNow);
            if (!_closed && _liveNotes.Enabled)
            {
                _liveNotesFailed = true;
                ShowStatus($"Live notes not updated, the previous notes are kept: {Shorten(FriendlyErrors.Describe(ex), 120)}");
            }
            return false;
        }
        finally
        {
            if (ReferenceEquals(_liveNotesCts, cts))
                _liveNotesCts = null;
            if (!_closed)
                UpdateSummaryHeader();
        }
    }

    private void CancelLiveNotes() => _liveNotesCts?.Cancel();

    private static string Shorten(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..(max - 3)].TrimEnd() + "...";
    }

    // ==================== Output ====================

    private void CopyTranscript_Click(object sender, RoutedEventArgs e) => CopyText(TranscriptBox.Text, "Transcript");

    private void CopySummary_Click(object sender, RoutedEventArgs e) => CopyText(SummaryBox.Text, "Summary");

    private void CopyText(string text, string what)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ShowStatus($"No {what.ToLowerInvariant()} to copy yet.");
            return;
        }

        try
        {
            Clipboard.SetText(text.Trim());
            ShowStatus($"{what} copied.");
        }
        catch (Exception ex)
        {
            ShowStatus($"Copy failed: {FriendlyErrors.Describe(ex)}");
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveAs();

    private void SaveAs()
    {
        if (string.IsNullOrWhiteSpace(TranscriptBox.Text) && string.IsNullOrWhiteSpace(SummaryBox.Text))
        {
            ShowStatus("Nothing to save yet.");
            return;
        }

        var started = _sessionStarted ?? DateTime.Now;
        var dialog = new SaveFileDialog
        {
            Title = "Save transcript",
            Filter = "Markdown (*.md)|*.md|Text (*.txt)|*.txt",
            FileName = System.IO.Path.GetFileNameWithoutExtension(LiveTranscriptText.ExportFileName(started)),
            DefaultExt = ".md",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, BuildDocument(started, LiveTranscriptText.IsMarkdownFileName(dialog.FileName)));
            ShowStatus($"Saved to {dialog.FileName}");
        }
        catch (Exception ex)
        {
            ShowStatus($"Save failed: {FriendlyErrors.Describe(ex)}", sticky: true);
        }
    }

    /// <summary>
    /// Saves the session to %APPDATA%\VoiceChatbot\transcripts\transcript_yyyyMMdd_HHmmss.md (one file per
    /// session, rewritten as it grows). Returns the path, or null when there was nothing new to save.
    /// </summary>
    private string? AutoSave()
    {
        if (!_unsavedChanges || string.IsNullOrWhiteSpace(TranscriptBox.Text))
            return null;

        try
        {
            Directory.CreateDirectory(TranscriptsFolder);
            var started = _sessionStarted ??= DateTime.Now;
            var path = System.IO.Path.Combine(TranscriptsFolder, LiveTranscriptText.AutoSaveFileName(started));
            File.WriteAllText(path, BuildDocument(started, markdown: true));
            _unsavedChanges = false;
            AppLog.Info($"Live transcript saved to {path}.");
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Live transcriber: could not save the session.", ex);
            if (!_closed)
                ShowStatus($"Could not save the transcript: {FriendlyErrors.Describe(ex)}", sticky: true);
            return null;
        }
    }

    private string BuildDocument(DateTime started, bool markdown) =>
        LiveTranscriptText.BuildDocument(
            LiveTranscriptText.DocumentTitle,
            started,
            _elapsed.Elapsed,
            TranscriptBox.Text,
            SummaryBox.Text,
            _summaryStyleUsed,
            markdown);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(TranscriptsFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{TranscriptsFolder}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowStatus($"Could not open the folder: {FriendlyErrors.Describe(ex)}");
        }
    }

    private void SendToChat_Click(object sender, RoutedEventArgs e)
    {
        var transcript = TranscriptBox.Text.Trim();
        var summary = SummaryBox.Text.Trim();
        if (transcript.Length == 0 && summary.Length == 0)
        {
            ShowStatus("Nothing to send yet. Record or paste a transcript first.");
            return;
        }

        _sendToChat(transcript, summary);
        ShowStatus("Sent to the chat. Ask about it in the main window.");
    }

    // ==================== Layout, text size and keys ====================

    private void PaneSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => StorePaneHeights();

    private void SystemPromptGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        var max = Math.Max(80, ActualHeight * 0.4);
        SystemPromptBox.Height = Math.Clamp(SystemPromptBox.ActualHeight + e.VerticalChange, 50, max);
        _settings.SystemPromptHeight = SystemPromptBox.Height;
    }

    private void ResetSystemPrompt_Click(object sender, RoutedEventArgs e)
    {
        SystemPromptBox.Text = TranscriberSettings.DefaultSystemPrompt;
    }

    private void FontSmaller_Click(object sender, RoutedEventArgs e) => SetFontSize(_fontSize - 1);

    private void FontLarger_Click(object sender, RoutedEventArgs e) => SetFontSize(_fontSize + 1);

    private void SetFontSize(double size)
    {
        _fontSize = Math.Clamp(Math.Round(Finite(size, DefaultFontSize)), MinFontSize, MaxFontSize);
        TranscriptBox.FontSize = _fontSize;
        SummaryBox.FontSize = _fontSize;
        _settings.FontSize = _fontSize;
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        SetFontSize(_fontSize + (e.Delta > 0 ? 1 : -1));
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc is deliberately not handled: it never stops recording, clears or closes anything.
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        switch (e.Key)
        {
            case Key.R:
                ToggleRecording();
                e.Handled = true;
                break;
            case Key.S:
                SaveAs();
                e.Handled = true;
                break;
            case Key.OemPlus:
            case Key.Add:
                SetFontSize(_fontSize + 1);
                e.Handled = true;
                break;
            case Key.OemMinus:
            case Key.Subtract:
                SetFontSize(_fontSize - 1);
                e.Handled = true;
                break;
            case Key.D0:
            case Key.NumPad0:
                SetFontSize(DefaultFontSize);
                e.Handled = true;
                break;
        }
    }

    // ==================== Closing ====================

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        StoreWindowSettings();
        if (_closeAllowed || (!_isRecording && !IsStopping))
            return;

        // Finish the chunks already recorded (and the final live notes) first, then close.
        e.Cancel = true;
        if (_closeRequested)
            return;

        _closeRequested = true;
        ShowStatus(_isRecording || _isFinishing
            ? "Finishing the last words before closing..."
            : "Writing the final notes before closing...", sticky: true);
        await FinishPendingWorkAsync(TimeSpan.FromSeconds(20));
        _closeAllowed = true;
        if (!_closed)
            await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closed = true;
        _isRecording = false;
        _uiTimer.Stop();
        _liveNotesTimer.Stop();
        _speech.StateChanged -= OnSpeechStateChanged;

        DisposeSource(DetachSource(flush: false));
        lock (_audioSync)
        {
            _queue?.Writer.TryComplete();
            _queue = null;
        }
        _lifetimeCts.Cancel();
        _summaryCts?.Cancel();
        CancelLiveNotes();

        AutoSave();
        _saveSettings();
    }

    private static MemoryStream BuildWavStream(byte[] audioData)
    {
        var wavStream = new MemoryStream();
        const int sampleRate = TranscriberAudioSource.SampleRate;
        const short bitsPerSample = 16;
        const short channels = 1;
        const short audioFormat = 1;
        const int fmtChunkSize = 16;
        var byteRate = sampleRate * channels * bitsPerSample / 8;
        var blockAlign = (short)(channels * bitsPerSample / 8);

        using (var writer = new BinaryWriter(wavStream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + audioData.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write(fmtChunkSize);
            writer.Write(audioFormat);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(byteRate);
            writer.Write(blockAlign);
            writer.Write(bitsPerSample);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(audioData.Length);
            writer.Write(audioData);
        }

        wavStream.Position = 0;
        return wavStream;
    }
}
