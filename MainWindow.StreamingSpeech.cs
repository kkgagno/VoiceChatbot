using System;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace VoiceChatbot;

public partial class MainWindow
{
    // ==================== Streaming speech ====================
    // Speaks a streamed reply sentence by sentence while the model is still writing it.

    private sealed class StreamingSpeech
    {
        public StreamingSpeech(SpeechSession session) => Session = session;

        public SpeechSession Session { get; }

        // Like CleanSpeechText, only the text before the first code block is spoken.
        public SentenceChunker Chunker { get; private set; } = new() { StopAtFirstCodeBlock = true };

        /// <summary>Starts a fresh reply segment (used between tool-call rounds).</summary>
        public void ResetChunker()
        {
            Chunker = new SentenceChunker { StopAtFirstCodeBlock = true };
            StreamedText.Clear();
        }

        public StringBuilder StreamedText { get; } = new();

        // Set when the streamed text is not fit to speak as-is; the final reply text is spoken instead.
        public bool UseFinalText { get; set; }
    }

    private void ApplyStreamingSpeechSettings()
    {
        StreamingSpeechToggle.IsChecked = _settings.StreamingSpeechEnabled;
    }

    private void SaveStreamingSpeechSettings()
    {
        _settings.StreamingSpeechEnabled = StreamingSpeechToggle.IsChecked == true;
    }

    private void StreamingSpeechToggle_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings();
    }

    /// <summary>
    /// Starts live speech for a reply that is about to stream, or returns null when the reply
    /// should be spoken after it is finished (TTS off, setting off, or a code/script request).
    /// </summary>
    private StreamingSpeech? BeginStreamingSpeech(string modelUserText, AssistantMessageUi assistantMessage)
    {
        if (TtsToggle.IsChecked != true ||
            StreamingSpeechToggle.IsChecked != true ||
            IsCodeOrScriptRequest(modelUserText))
            return null;

        try
        {
            var session = _speech.BeginSpeechSession(GetAssistantAudioDirectory());
            AddAudioButtonsWhenSpoken(session, assistantMessage);
            return new StreamingSpeech(session);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not start speaking early: {ex.Message}");
            return null;
        }
    }

    /// <summary>Feeds one streamed token and queues every sentence it completes. UI thread only.</summary>
    private void FeedStreamingSpeech(StreamingSpeech? speech, string token)
    {
        if (speech == null || speech.UseFinalText || speech.Session.IsCancelled)
            return;

        try
        {
            speech.StreamedText.Append(token);
            foreach (var sentence in speech.Chunker.Append(token))
            {
                // The chat shows an explanation instead of leaked model reasoning; speak that at the end.
                if (LooksLikeLeakedReasoningDump(speech.StreamedText.ToString()))
                {
                    speech.UseFinalText = true;
                    return;
                }

                EnqueueSpeechSentence(speech.Session, sentence);
            }
        }
        catch (Exception ex)
        {
            speech.UseFinalText = true;
            AddSystemMessage($"Speaking early failed, the reply will be spoken when it is done: {ex.Message}");
        }
    }

    /// <summary>
    /// Called on the UI thread once the streamed reply is final. Returns true when live speech now
    /// handles the reply, false when the caller should speak the final text with SpeakLastResponse.
    /// </summary>
    private bool FinishStreamingSpeech(StreamingSpeech? speech, string streamedText, string finalText)
    {
        if (speech == null)
            return false;

        if (speech.Session.IsCancelled)
        {
            // Stop, the mic button, a replay or barge-in cut this reply's speech short: stay quiet,
            // even when the reply was going to be spoken from its final text.
            SetUIState("idle", "Ready");
            _speech.ReadyForNextSpeech();
            return true;
        }

        // A code/SVG continuation changed the reply, or the reply is not normal text:
        // fall back to speaking the final text the old way. SpeakLastResponse (or its idle path
        // when TTS was switched off meanwhile) takes over, so this session ends quietly.
        if (speech.UseFinalText ||
            !string.Equals(streamedText, finalText, StringComparison.Ordinal) ||
            LooksLikeOnlyUnusedTokens(streamedText) ||
            LooksLikeLeakedReasoningDump(streamedText))
        {
            speech.Session.Abandon();
            return false;
        }

        try
        {
            foreach (var sentence in speech.Chunker.Flush())
                EnqueueSpeechSentence(speech.Session, sentence);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Speech error: {ex.Message}");
        }

        // SpeechFinished resets the UI and restarts auto-listen after the last sentence.
        speech.Session.Complete();
        return true;
    }

    /// <summary>
    /// A tool-call round ended: speak any unfinished preamble sentence ("Let me look that up.")
    /// and start the answer that follows the tool results as a new segment of the same session.
    /// </summary>
    private void EndStreamingSpeechRound(StreamingSpeech? speech)
    {
        if (speech == null || speech.UseFinalText || speech.Session.IsCancelled)
            return;

        try
        {
            foreach (var sentence in speech.Chunker.Flush())
                EnqueueSpeechSentence(speech.Session, sentence);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Speech error: {ex.Message}");
        }

        speech.ResetChunker();
    }

    /// <summary>Stops live speech after a chat error or cancellation.</summary>
    private static void CancelStreamingSpeech(StreamingSpeech? speech)
    {
        speech?.Session.Cancel();
    }

    private static void EnqueueSpeechSentence(SpeechSession session, string sentence)
    {
        var speechText = CleanSpeechText(SpeechMarkdown.ToPlainText(sentence));
        if (!string.IsNullOrWhiteSpace(speechText))
            session.Enqueue(speechText);
    }

    /// <summary>Adds Replay/Download buttons to the bubble once the whole reply has been spoken and saved.</summary>
    private void AddAudioButtonsWhenSpoken(SpeechSession session, AssistantMessageUi? assistantMessage)
    {
        if (assistantMessage == null)
            return;

        session.Completed += audioPath =>
        {
            if (string.IsNullOrWhiteSpace(audioPath))
                return;

            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    AddAudioButtons(assistantMessage, audioPath);
                }
                catch (Exception ex)
                {
                    AddSystemMessage($"Could not add audio buttons: {ex.Message}");
                }
            }, DispatcherPriority.Background);
        };
    }
}
