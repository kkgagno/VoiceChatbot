using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using NAudio.Wave;

namespace VoiceChatbot;

public partial class TranscriptionWindow : Window
{
    private readonly SpeechEngine _speech;
    private readonly Func<string, string, CancellationToken, Task<string>> _summarizeAsync;
    private readonly Action<string, string> _contextUpdated;
    private readonly StringBuilder _transcript = new();
    private readonly object _chunkSync = new();
    private readonly SemaphoreSlim _chunkLock = new(1, 1);
    private CancellationTokenSource? _recordingCts;
    private WaveInEvent? _waveIn;
    private MemoryStream? _chunkBuffer;
    private DateTime _chunkStartedUtc;
    private bool _isTranscribing;

    public TranscriptionWindow(
        SpeechEngine speech,
        Func<string, string, CancellationToken, Task<string>> summarizeAsync,
        Action<string, string> contextUpdated)
    {
        InitializeComponent();
        _speech = speech;
        _summarizeAsync = summarizeAsync;
        _contextUpdated = contextUpdated;
    }

    public bool IsTranscribing => _isTranscribing;

    public void ActivateExisting()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        Focus();
    }

    private void StartStop_Click(object sender, RoutedEventArgs e)
    {
        if (_isTranscribing)
            StopTranscribing();
        else
            StartTranscribing();
    }

    private void StartTranscribing()
    {
        try
        {
            _recordingCts = new CancellationTokenSource();
            _chunkBuffer = new MemoryStream();
            _chunkStartedUtc = DateTime.UtcNow;
            _waveIn = new WaveInEvent
            {
                WaveFormat = new WaveFormat(16000, 16, 1),
                BufferMilliseconds = 100,
                DeviceNumber = _speech.MicDeviceIndex
            };
            _waveIn.DataAvailable += OnAudioDataAvailable;
            _waveIn.RecordingStopped += (_, _) => Dispatcher.Invoke(() => StatusText.Text = "Stopped");
            _waveIn.StartRecording();

            _isTranscribing = true;
            StartStopBtn.Content = "Stop Transcribing";
            StatusText.Text = $"Transcribing live with {_speech.GetTranscriptionBackendStatus()}...";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not start transcription: {ex.Message}";
            StopTranscribing();
        }
    }

    private void StopTranscribing()
    {
        _isTranscribing = false;
        _recordingCts?.Cancel();
        _recordingCts?.Dispose();
        _recordingCts = null;

        try { _waveIn?.StopRecording(); } catch { }
        try { _waveIn?.Dispose(); } catch { }
        _waveIn = null;

        _ = FlushChunkAsync(CancellationToken.None);
        StartStopBtn.Content = "Start Transcribing";
        StatusText.Text = "Stopped";
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!_isTranscribing || _chunkBuffer == null)
            return;

        var shouldFlush = false;
        lock (_chunkSync)
        {
            if (_chunkBuffer == null)
                return;

            _chunkBuffer.Write(e.Buffer, 0, e.BytesRecorded);
            shouldFlush = (DateTime.UtcNow - _chunkStartedUtc).TotalSeconds >= 4.0 && _chunkBuffer.Length > 16000;
        }

        if (shouldFlush)
            _ = FlushChunkAsync(_recordingCts?.Token ?? CancellationToken.None);
    }

    private async Task FlushChunkAsync(CancellationToken ct)
    {
        if (!await _chunkLock.WaitAsync(0, ct).ConfigureAwait(false))
            return;

        MemoryStream? chunk = null;
        try
        {
            lock (_chunkSync)
            {
                if (_chunkBuffer == null || _chunkBuffer.Length < 16000)
                    return;

                chunk = BuildWavStream(_chunkBuffer.ToArray());
                _chunkBuffer.Dispose();
                _chunkBuffer = _isTranscribing ? new MemoryStream() : null;
                _chunkStartedUtc = DateTime.UtcNow;
            }
        }
        finally
        {
            _chunkLock.Release();
        }

        if (chunk == null)
            return;

        try
        {
            var text = await _speech.TranscribeWavAsync(chunk, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    AppendTranscript(text);
                    StatusText.Text = $"Transcribing live. Last chunk used {_speech.LastTranscriptionBackendUsed}.";
                }, DispatcherPriority.Background);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => StatusText.Text = $"Transcription error: {ex.Message}");
        }
        finally
        {
            chunk.Dispose();
        }
    }

    private void AppendTranscript(string text)
    {
        if (_transcript.Length > 0)
            _transcript.AppendLine();

        _transcript.Append(text.Trim());
        TranscriptBox.Text = _transcript.ToString();
        TranscriptBox.ScrollToEnd();
        EmptyTranscriptText.Visibility = string.IsNullOrWhiteSpace(TranscriptBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateStats();
        _contextUpdated(TranscriptBox.Text, SummaryBox.Text);
    }

    private async void Summarize_Click(object sender, RoutedEventArgs e)
    {
        var transcript = TranscriptBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(transcript))
        {
            StatusText.Text = "No transcript to summarize.";
            return;
        }

        SummarizeBtn.IsEnabled = false;
        SummarizeBtn.Content = "Summarizing...";
        StatusText.Text = "Summarizing transcript...";

        try
        {
            var summary = await _summarizeAsync(transcript, SystemPromptBox.Text, CancellationToken.None);
            SummaryBox.Text = summary.Trim();
            SummaryBox.ScrollToEnd();
            StatusText.Text = "Summary ready.";
            _contextUpdated(TranscriptBox.Text, SummaryBox.Text);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Summary failed: {ex.Message}";
        }
        finally
        {
            SummarizeBtn.IsEnabled = true;
            SummarizeBtn.Content = "Summarize";
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _transcript.Clear();
        TranscriptBox.Clear();
        SummaryBox.Clear();
        EmptyTranscriptText.Visibility = Visibility.Visible;
        UpdateStats();
        _contextUpdated("", "");
        StatusText.Text = _isTranscribing ? $"Transcribing live with {_speech.GetTranscriptionBackendStatus()}..." : "Ready";
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        StopTranscribing();
    }

    private void UpdateStats()
    {
        var words = Regex.Matches(TranscriptBox.Text, @"\b[\w']+\b").Count;
        StatsText.Text = $"{words} words";
    }

    private static MemoryStream BuildWavStream(byte[] audioData)
    {
        var wavStream = new MemoryStream();
        const int sampleRate = 16000;
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
