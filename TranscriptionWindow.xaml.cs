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
/// The chat model keeps notes by time while recording (LiveNotesPolicy decides when, TranscriptNotesWriter
/// writes them in the TranscriptNotes layout), finishes them with a full summary on Stop, and can rebuild them
/// from the whole transcript. The main chat gets the transcript and notes as context. The session is kept in
/// current-session.json, so closing the window (or the app) and opening it again continues where it was.
/// </summary>
public partial class TranscriptionWindow : Window
{
    private const double MinFontSize = 10;
    private const double MaxFontSize = 32;
    private const double DefaultFontSize = 15;
    // After the assistant stops speaking its voice can still echo for a moment.
    private static readonly TimeSpan SpeechEchoTail = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan StatusMessageTime = TimeSpan.FromSeconds(6);
    // The session state is written this long after a change (and right away on stop, clear and close).
    private static readonly TimeSpan SessionStateDelay = TimeSpan.FromSeconds(2);

    /// <summary>%APPDATA%\VoiceChatbotMini\transcripts: where each session is saved when it stops.</summary>
    public static string TranscriptsFolder { get; } = AppPaths.DataPath("transcripts");

    /// <summary>The current session (transcript, notes, time so far), restored when the window opens again.</summary>
    private static string SessionStatePath => TranscriberSessionStore.PathIn(TranscriptsFolder);

    // Chunk == null marks where Clear was pressed while recording (see Clear_Click).
    private sealed record PendingChunk(SpeechChunk? Chunk, TimeSpan At);
    private static readonly PendingChunk ClearMark = new(null, TimeSpan.Zero);

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

    // Session (UI thread). The session time is _elapsedOffset (time recorded before this window, for a restored
    // session) plus _elapsed.
    private readonly Stopwatch _elapsed = new();
    private TimeSpan _elapsedOffset;
    private string? _autoSaveFileName; // the session's file in the transcripts folder, once chosen
    private readonly DispatcherTimer _sessionStateTimer;
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
    // Summarize / Re-summarize all in progress (the button reads Cancel).
    private CancellationTokenSource? _summaryCts;
    private string? _summaryProgress;
    private int _clearCount;
    // Clear pressed while recording: the old session is saved and cleared once its last chunks are transcribed.
    private bool _clearPending;
    private TimeSpan _clearedLength; // the old session's length at the click
    private DateTime _clearedAt;     // when the new session started
    private double _fontSize = DefaultFontSize;
    private bool _closeAllowed;
    private bool _closeRequested;
    private bool _closed;

    // Live notes (UI thread): notes on what was said since the last update are added to the notes pane.
    private readonly LiveNotesPolicy _liveNotes = new();
    private readonly DispatcherTimer _liveNotesTimer;
    private CancellationTokenSource? _liveNotesCts;
    private Task<bool>? _liveNotesTask;
    private DateTime? _notesUpdatedAt; // local time of the last notes update shown
    private bool _liveNotesFailed; // the last update added nothing (the previous notes are kept)
    private bool _summaryNotRefreshed; // the last update added a section but could not refresh the summary at the top
    private string? _notesProblem; // the summary at the top could not be refreshed by the last update
    private bool _finalNotesAfterSummary; // Stop came while Summarize ran: the final notes run once it ends

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
        _sessionStateTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SessionStateDelay };
        _sessionStateTimer.Tick += (_, _) => SaveSessionState();

        ApplySettings();
        var restoredFrom = RestoreSession();
        _initializing = false;

        _assistantSpeaking = _speech.CurrentState == VoiceState.Speaking;
        _speech.StateChanged += OnSpeechStateChanged;

        UpdateRecordingUi();
        UpdateStats();
        UpdateSummaryHeader();
        if (restoredFrom is { } from)
        {
            _contextUpdated(TranscriptBox.Text, SummaryBox.Text);
            ShowStatus($"Restored your last session from {from:g}. Clear starts a new one.", sticky: true);
        }
        else
        {
            ShowStatus("Ready. Choose Microphone or PC audio and press Start (Ctrl+R).", sticky: true);
        }
    }

    public bool IsTranscribing => _isRecording;

    /// <summary>Recording time of this session, including the time recorded before a restore.</summary>
    private TimeSpan SessionElapsed => _elapsedOffset + _elapsed.Elapsed;

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
            _timelineOrigin = SessionElapsed;
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

        // Final notes (live notes on, or notes by time already there): a last section for the words added since
        // the previous update and a full summary at the top; then the same session file is saved again.
        var notesUpdated = await FinishLiveNotesAsync(stopped);
        if (_closed)
            return;

        var resavedPath = AutoSave();
        SaveSessionState();
        if (notesUpdated && !_isRecording && _notesUpdatedAt is { } at)
        {
            var problem = _notesProblem != null ? $" {_notesProblem}" : "";
            ShowStatus($"{stopped} {LiveNotesPolicy.FormatUpdated(at)}.{problem}{SavedNote(resavedPath ?? savedPath)}", sticky: true);
        }
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
                _timelineOrigin = SessionElapsed - _chunker.Position;
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
                    if (item.Chunk == null)
                    {
                        // Every chunk of the old session is in the transcript now.
                        await Dispatcher.InvokeAsync(FinishPendingClear);
                        continue;
                    }

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
            using var wav = BuildWavStream(item.Chunk!.Pcm);
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
        ElapsedText.Text = LiveTranscriptText.FormatTimestamp(SessionElapsed);

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
        ElapsedText.Text = LiveTranscriptText.FormatTimestamp(SessionElapsed);
        if (!_isRecording)
            LevelMeter.Value = 0;

        // The transcript can be corrected while stopped; while recording new lines keep arriving.
        TranscriptBox.IsReadOnly = _isRecording || finishing;
        EditHintText.Text = TranscriptBox.IsReadOnly ? "Read-only while recording" : "Editable";
        ClearBtn.IsEnabled = !finishing;
        UpdateNotesControls();
    }

    /// <summary>
    /// The notes pane is read-only while recording and while notes are being written (updates only add to it then);
    /// editable when stopped. The Summarize button reads "Update notes now" while recording, "Summarize" for
    /// empty notes, "Re-summarize all" otherwise, and "Cancel" while a summary is being written.
    /// </summary>
    private void UpdateNotesControls()
    {
        var notesBusy = _liveNotesCts != null || _summaryCts != null;
        SummaryBox.IsReadOnly = _isRecording || _isFinishing || notesBusy;

        string label;
        string icon;
        string tip;
        if (_summaryCts != null)
        {
            label = "Cancel";
            icon = "\uE711";
            tip = "Stop writing the notes; the previous notes are kept";
        }
        else if (_isRecording)
        {
            label = "Update notes now";
            icon = "\uE72C";
            tip = "Add notes on what was said since the last update now, without waiting for the live-notes interval";
        }
        else if (string.IsNullOrWhiteSpace(SummaryBox.Text))
        {
            label = "Summarize";
            icon = "\uE9D5";
            tip = "Write notes by time and a summary in the chosen style from the whole transcript; a long transcript is written section by section";
        }
        else
        {
            label = "Re-summarize all";
            icon = "\uE72C";
            tip = "Replace the notes with fresh notes by time and a summary in the chosen style, written from the whole transcript";
        }

        SummarizeBtn.Content = label;
        Ui.SetIcon(SummarizeBtn, icon);
        SummarizeBtn.ToolTip = tip;
        // While an update or the final notes are being written, wait for them.
        SummarizeBtn.IsEnabled = _summaryCts != null || (!_isFinishing && _liveNotesCts == null);
    }

    private void UpdateStats()
    {
        var words = LiveTranscriptText.CountWords(TranscriptBox.Text);
        StatsText.Text = words == 1 ? "1 word" : $"{words:N0} words";
    }

    private void UpdateSummaryHeader()
    {
        var hasSummary = !string.IsNullOrWhiteSpace(SummaryBox.Text);
        var updated = hasSummary && _notesUpdatedAt is { } at ? LiveNotesPolicy.FormatUpdated(at) : "";
        string detail;
        if (_summaryCts != null)
            detail = _summaryProgress ?? "Summarizing...";
        else if (_liveNotesCts != null)
            detail = "Updating notes...";
        else if (_liveNotesFailed)
            detail = updated.Length > 0 ? $"Notes update failed · {updated}" : "Notes update failed";
        else if (_summaryNotRefreshed)
            detail = updated.Length > 0 ? $"Summary not refreshed · {updated}" : "Summary not refreshed";
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
        ScheduleSessionStateSave();
    }

    private void SummaryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing)
            return;

        _unsavedChanges = true;
        UpdateSummaryHeader();
        UpdateNotesControls();
        _contextUpdated(TranscriptBox.Text, SummaryBox.Text);
        ScheduleSessionStateSave();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_isFinishing || _clearPending)
            return;

        if (!_isRecording)
        {
            ClearSession(null, null);
            return;
        }

        // The words already spoken belong to the old session: they are transcribed first (the queue runs in
        // order) and saved with it, while what is said from now on starts the new session at 00:00.
        var length = SessionElapsed;
        bool marked;
        lock (_audioSync)
        {
            if (_chunker?.Flush() is { } rest)
                EnqueueLocked(rest);
            marked = _queue?.Writer.TryWrite(ClearMark) == true;
            _timelineOrigin = _chunker != null ? TimeSpan.Zero - _chunker.Position : TimeSpan.Zero;
        }
        _elapsed.Restart();
        _elapsedOffset = TimeSpan.Zero;
        ElapsedText.Text = LiveTranscriptText.FormatTimestamp(TimeSpan.Zero);

        if (!marked)
        {
            ClearSession(length, DateTime.Now);
            return;
        }

        _clearPending = true;
        _clearedLength = length;
        _clearedAt = DateTime.Now;
        if (Volatile.Read(ref _pendingChunks) > 0)
            ShowStatus("Clearing once the words already spoken are transcribed...");
    }

    // UI thread: the queue reached the point where Clear was pressed while recording.
    private void FinishPendingClear()
    {
        if (!_clearPending)
            return;

        _clearPending = false;
        ClearSession(_clearedLength, _clearedAt);
    }

    /// <summary>
    /// Saves the session and starts a new one. While recording, <paramref name="length"/> is the old session's
    /// length and <paramref name="newSessionStarted"/> the click (the clock was already restarted then).
    /// </summary>
    private void ClearSession(TimeSpan? length, DateTime? newSessionStarted)
    {
        if (_closed)
            return;

        // Nothing is lost: the session so far goes to the transcripts folder first.
        var savedPath = AutoSave(length);
        if (_unsavedChanges && !string.IsNullOrWhiteSpace(TranscriptBox.Text))
            return; // the save failed: keep the text, and the error stays in the status line

        // A summary or live-notes update of the old transcript is no longer wanted.
        _clearCount++;
        _summaryCts?.Cancel();
        CancelLiveNotes();
        _liveNotes.Reset(DateTime.UtcNow);
        _notesUpdatedAt = null;
        _liveNotesFailed = false;
        _summaryNotRefreshed = false;
        _notesProblem = null;
        _finalNotesAfterSummary = false;

        TranscriptBox.Clear();
        SummaryBox.Clear();
        _summaryStyleUsed = TranscriptSummaryStyles.Summary;
        UpdateSummaryHeader();
        _unsavedChanges = false;
        _sessionStarted = newSessionStarted;
        _autoSaveFileName = null;
        DeleteSessionState();

        // A new session starts at 00:00.
        if (newSessionStarted == null)
        {
            _elapsed.Reset();
            _elapsedOffset = TimeSpan.Zero;
            ElapsedText.Text = LiveTranscriptText.FormatTimestamp(TimeSpan.Zero);
        }

        _contextUpdated("", "");
        var saved = savedPath != null ? $" The previous transcript was saved to {System.IO.Path.GetFileName(savedPath)}." : "";
        ShowStatus("Cleared." + saved, sticky: !_isRecording);
    }

    // ==================== Summary ====================

    /// <summary>
    /// While recording: "Update notes now". When stopped: Summarize / Re-summarize all (fresh notes from the
    /// whole transcript). While that runs the button cancels it.
    /// </summary>
    private void Summarize_Click(object sender, RoutedEventArgs e)
    {
        if (_summaryCts != null)
        {
            _summaryCts.Cancel();
            return;
        }

        if (_isRecording)
        {
            UpdateNotesNow();
            return;
        }

        if (_isFinishing || _liveNotesCts != null)
        {
            ShowStatus("The notes are still being written. Try again in a moment.");
            return;
        }

        _ = RebuildNotesAsync();
    }

    /// <summary>Starts a live-notes update right away, whatever the interval and the Live notes switch.</summary>
    private void UpdateNotesNow()
    {
        switch (_liveNotes.CheckNow(_summaryCts != null, TranscriptBox.Text))
        {
            case LiveNotesCheck.AlreadyRunning:
                ShowStatus("The notes are already being updated.");
                return;
            case LiveNotesCheck.TooFewNewWords:
                ShowStatus("Nothing new to add to the notes yet.");
                return;
        }

        if (_liveNotes.TryBeginNow(_summaryCts != null, TranscriptBox.Text) is { } ticket)
            StartLiveNotesUpdate(ticket);
    }

    /// <summary>
    /// Summarize (empty notes) or Re-summarize all (asks first): notes by time for each interval of the whole
    /// transcript, then a full summary at the top. The previous notes stay until the new ones are ready, so a
    /// cancel or an error keeps them.
    /// </summary>
    private async Task RebuildNotesAsync()
    {
        var transcriptSnapshot = TranscriptBox.Text;
        var transcript = transcriptSnapshot.Trim();
        if (transcript.Length == 0)
        {
            ShowStatus("No transcript to summarize yet.");
            return;
        }

        var replacing = !string.IsNullOrWhiteSpace(SummaryBox.Text);
        if (replacing &&
            MessageBox.Show(this, "Replace the current notes with a fresh summary of the whole transcript?", "Re-summarize all",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        // Things may have moved on while the question was open.
        if (_closed || _summaryCts != null || _isRecording || _isFinishing || _liveNotesCts != null ||
            !string.Equals(TranscriptBox.Text, transcriptSnapshot, StringComparison.Ordinal))
            return;

        var style = SelectedSummaryStyle;
        var systemPrompt = SystemPromptBox.Text;
        var clearCount = _clearCount;
        var window = TimeSpan.FromMinutes(SelectedIntervalMinutes);
        var length = SessionElapsed;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _summaryCts = cts;
        _summaryProgress = null;
        SummaryStyleCombo.IsEnabled = false;
        UpdateNotesControls();
        UpdateSummaryHeader();
        ShowStatus(replacing ? "Re-summarizing the whole transcript..." : $"Writing {style.ToLowerInvariant()}...", sticky: true);

        // Section by section; show which one is being written.
        var progress = new Progress<string>(message =>
        {
            if (_closed || !ReferenceEquals(_summaryCts, cts) || cts.IsCancellationRequested)
                return;
            _summaryProgress = message;
            ShowStatus(message, sticky: true);
            UpdateSummaryHeader();
        });

        try
        {
            var notes = await TranscriptNotesWriter.RebuildAsync(
                transcript,
                style,
                window,
                length > TimeSpan.Zero ? length : null,
                (request, ct) => _summarizeAsync(request, systemPrompt, ct),
                progress,
                cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_closed)
                return;

            if (notes.Length == 0)
            {
                ShowStatus("The chat model returned no notes; the previous notes are kept.", sticky: !_isRecording);
                return;
            }

            _summaryStyleUsed = style;
            _notesUpdatedAt = DateTime.Now;
            _liveNotesFailed = false;
            _summaryNotRefreshed = false;
            _notesProblem = null;
            SummaryBox.Text = notes;
            SummaryBox.ScrollToHome();
            // Live notes carry on from here with the words added after it.
            _liveNotes.MarkSummarized(transcriptSnapshot, DateTime.UtcNow);
            var saved = _isRecording ? null : AutoSave();
            SaveSessionState();
            ShowStatus($"{style} ready.{SavedNote(saved)}", sticky: !_isRecording);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (!_closed && clearCount == _clearCount)
                ShowStatus(replacing ? "Re-summarize cancelled; the previous notes are kept." : "Summary cancelled.");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Live transcriber: summary failed.", ex);
            if (!_closed)
            {
                var kept = replacing ? " The previous notes are kept." : "";
                ShowStatus($"Summary failed: {Shorten(FriendlyErrors.Describe(ex), 160)}{kept}", sticky: !_isRecording);
            }
        }
        finally
        {
            if (ReferenceEquals(_summaryCts, cts))
                _summaryCts = null;
            _summaryProgress = null;
            if (!_closed)
            {
                SummaryStyleCombo.IsEnabled = true;
                UpdateNotesControls();
                UpdateSummaryHeader();
                _ = FinishSkippedFinalNotesAsync();
            }
        }
    }

    /// <summary>
    /// Recording was started and stopped while Summarize / Re-summarize all ran, so the final notes waited for it:
    /// they run now (a section for the words recorded meanwhile and the full summary), then the session is saved.
    /// </summary>
    private async Task FinishSkippedFinalNotesAsync()
    {
        if (!_finalNotesAfterSummary || _closed || _isRecording || _isFinishing || _summaryCts != null)
            return;

        _finalNotesAfterSummary = false;
        if (await FinishLiveNotesAsync("Stopped.") && !_closed)
        {
            AutoSave();
            SaveSessionState();
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
            ShowStatus("Live notes off. Update notes now still adds notes while recording, and Summarize works when stopped.");
            return;
        }

        var every = IntervalText(SelectedIntervalMinutes);
        ShowStatus(_isRecording
            ? $"Live notes on: every {every}, notes on what was said are added by time and the summary at the top is refreshed."
            : $"Live notes on: while recording, notes are added by time every {every}; Stop writes the full summary.", sticky: !_isRecording);
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

        // A running Summarize skips this tick; the next one checks again.
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
    /// After Stop (the last chunks are transcribed): waits for an update in progress, then writes the final notes
    /// when they are due (live notes on or notes by time there: a last section and the full summary). True when
    /// either put new notes in the notes pane.
    /// </summary>
    private async Task<bool> FinishLiveNotesAsync(string stoppedMessage)
    {
        var updated = false;
        if (_liveNotesTask is { IsCompleted: false } running)
            updated = await running;

        if (_closed)
            return false;

        var ticket = _liveNotes.TryBeginFinal(_summaryCts != null, TranscriptBox.Text, SummaryBox.Text, SelectedSummaryStyle);
        // A running Summarize blocks the final notes; they run when it ends (FinishSkippedFinalNotesAsync).
        _finalNotesAfterSummary = ticket == null && _summaryCts != null;
        if (ticket == null)
            return updated;

        if (!_isRecording)
            ShowStatus($"{stoppedMessage} Writing the final notes and summary...", sticky: true);
        return await StartLiveNotesUpdate(ticket) || updated;
    }

    /// <summary>
    /// A live update (or, for the final ticket, the notes on Stop): notes on only the text added since the last
    /// update become a new section, then the summary at the top is rewritten. Errors keep the previous notes; when
    /// only the summary fails, the new section is kept. Never throws.
    /// </summary>
    private async Task<bool> RunLiveNotesAsync(LiveNotesTicket ticket)
    {
        var notesBefore = SummaryBox.Text;
        var style = SelectedSummaryStyle;
        var systemPrompt = SystemPromptBox.Text;
        var elapsed = SessionElapsed;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _liveNotesCts = cts;
        UpdateSummaryHeader();
        UpdateNotesControls();

        try
        {
            Task<string> Summarize(TranscriptSummaryRequest request, CancellationToken ct) => _summarizeAsync(request, systemPrompt, ct);
            var result = ticket.IsFinal
                ? await TranscriptNotesWriter.FinishAsync(notesBefore, ticket.NewText, ticket.Transcript, elapsed, style, Summarize, null, cts.Token)
                : await TranscriptNotesWriter.UpdateAsync(notesBefore, ticket.NewText, elapsed, style, Summarize, null, cts.Token);
            cts.Token.ThrowIfCancellationRequested();
            if (_closed || !_liveNotes.IsCurrent(ticket))
                return false; // cleared meanwhile

            if (!SameText(SummaryBox.Text, notesBefore))
            {
                // The notes were edited while the update was written: keep the edit, add to it on the next check.
                _liveNotes.Abandon(ticket);
                ShowStatus("Live notes not applied because the notes were edited meanwhile. They update on the next check.");
                return false;
            }

            _liveNotes.Complete(ticket, DateTime.UtcNow);
            _notesUpdatedAt = DateTime.Now;
            _liveNotesFailed = false;
            _summaryNotRefreshed = result.SummaryFailed;
            _notesProblem = result.SummaryError is { } error
                ? $"The summary at the top was not refreshed: {Shorten(FriendlyErrors.Describe(error), 120)}"
                : null;
            _summaryStyleUsed = style;
            if (!SameText(result.Notes, notesBefore))
                ReplaceNotesKeepingScroll(result.Notes);
            if (result.SummaryError != null)
                AppLog.Warn("Live transcriber: the summary at the top of the notes could not be refreshed.", result.SummaryError);
            ShowStatus(LiveNotesPolicy.FormatUpdated(_notesUpdatedAt.Value) + "." + (_notesProblem != null ? $" {_notesProblem}" : ""));
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
            if (!_closed)
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
            {
                UpdateSummaryHeader();
                UpdateNotesControls();
            }
        }
    }

    private void CancelLiveNotes() => _liveNotesCts?.Cancel();

    // New sections are added at the end: a reader at the end keeps following them, one who scrolled back stays put.
    private void ReplaceNotesKeepingScroll(string notes)
    {
        var box = SummaryBox;
        var atEnd = box.ExtentHeight > box.ViewportHeight && box.VerticalOffset + box.ViewportHeight >= box.ExtentHeight - 6;
        var offset = box.VerticalOffset;
        box.Text = notes;
        if (atEnd)
            box.ScrollToEnd();
        else
            box.ScrollToVerticalOffset(offset);
    }

    private static bool SameText(string? a, string? b) =>
        string.Equals((a ?? "").Replace("\r\n", "\n").Trim(), (b ?? "").Replace("\r\n", "\n").Trim(), StringComparison.Ordinal);

    private static string Shorten(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..(max - 3)].TrimEnd() + "...";
    }

    // ==================== Output ====================

    private void CopyTranscript_Click(object sender, RoutedEventArgs e) => CopyText(TranscriptBox.Text, "Transcript");

    private void CopySummary_Click(object sender, RoutedEventArgs e) => CopyText(SummaryBox.Text, "Notes");

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
    /// Saves the session to %APPDATA%\VoiceChatbotMini\transcripts\transcript_yyyyMMdd_HHmmss.md (one file per
    /// session, rewritten as it grows, also after a restore). Returns the path, or null when there was nothing new to save.
    /// </summary>
    private string? AutoSave(TimeSpan? length = null)
    {
        if (!_unsavedChanges || string.IsNullOrWhiteSpace(TranscriptBox.Text))
            return null;

        try
        {
            Directory.CreateDirectory(TranscriptsFolder);
            var started = _sessionStarted ??= DateTime.Now;
            _autoSaveFileName ??= LiveTranscriptText.AutoSaveFileName(started);
            var path = System.IO.Path.Combine(TranscriptsFolder, _autoSaveFileName);
            File.WriteAllText(path, BuildDocument(started, markdown: true, length));
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

    private string BuildDocument(DateTime started, bool markdown, TimeSpan? length = null) =>
        LiveTranscriptText.BuildDocument(
            LiveTranscriptText.DocumentTitle,
            started,
            length ?? SessionElapsed,
            TranscriptBox.Text,
            SummaryBox.Text,
            TranscriptNotes.DocumentHeading(SummaryBox.Text, _summaryStyleUsed),
            markdown);

    // ==================== Session state (restored when the window opens again) ====================

    /// <summary>
    /// Restores the last session from current-session.json: transcript, notes, the time recorded so far (recording
    /// continues from it), the live-notes progress and the session's file. Returns when it was from, or null when
    /// there was nothing to restore. A missing or unreadable file starts a new session.
    /// </summary>
    private DateTime? RestoreSession()
    {
        var state = TranscriberSessionStore.Load(SessionStatePath, out var error);
        if (error != null)
            AppLog.Warn($"Live transcriber: the last session could not be restored ({error}); starting a new one.");
        if (state == null)
            return null;

        TranscriptBox.Text = state.Transcript;
        SummaryBox.Text = state.Notes;
        _summaryStyleUsed = state.NotesStyle;
        _notesUpdatedAt = state.NotesUpdatedAt;
        _elapsedOffset = state.Elapsed;
        _sessionStarted = state.SessionStarted;
        _autoSaveFileName = state.AutoSaveFileName;
        _unsavedChanges = state.UnsavedChanges;
        _liveNotes.Restore(state.Transcript, state.ProcessedLength);
        TranscriptBox.ScrollToEnd();
        AppLog.Info($"Live transcriber: restored the session from {state.SavedAt:yyyy-MM-dd HH:mm:ss} ({LiveTranscriptText.CountWords(state.Transcript)} words).");
        return state.SessionStarted ?? state.SavedAt;
    }

    /// <summary>Writes the session state about two seconds after a change (once per burst of changes).</summary>
    private void ScheduleSessionStateSave()
    {
        if (_initializing || _closed)
            return;
        if (!_sessionStateTimer.IsEnabled)
            _sessionStateTimer.Start();
    }

    /// <summary>Writes the session state now (atomically). An empty session removes it. Never throws.</summary>
    private void SaveSessionState()
    {
        _sessionStateTimer.Stop();
        var transcript = TranscriptBox.Text;
        var notes = SummaryBox.Text;
        if (string.IsNullOrWhiteSpace(transcript) && string.IsNullOrWhiteSpace(notes))
        {
            DeleteSessionState();
            return;
        }

        try
        {
            TranscriberSessionStore.Save(SessionStatePath, new TranscriberSessionState
            {
                Transcript = transcript,
                Notes = notes,
                NotesStyle = _summaryStyleUsed,
                NotesUpdatedAt = _notesUpdatedAt,
                ElapsedMs = (long)SessionElapsed.TotalMilliseconds,
                ProcessedLength = _liveNotes.ProcessedTranscript.Length,
                SessionStarted = _sessionStarted,
                AutoSaveFileName = _autoSaveFileName ?? (_sessionStarted is { } started ? LiveTranscriptText.AutoSaveFileName(started) : null),
                UnsavedChanges = _unsavedChanges,
                SavedAt = DateTime.Now,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Live transcriber: could not keep the session for next time.", ex);
        }
    }

    private void DeleteSessionState()
    {
        _sessionStateTimer.Stop();
        if (!TranscriberSessionStore.Delete(SessionStatePath, out var error))
            AppLog.Warn($"Live transcriber: could not remove the saved session state ({error}).");
    }

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
        if (e.Key == Key.F1)
        {
            HelpWindow.Open(HelpContent.LiveTranscriberId, this);
            e.Handled = true;
            return;
        }

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
        SaveSessionState();
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
