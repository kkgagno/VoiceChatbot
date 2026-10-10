using System;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace VoiceChatbot;

public partial class MainWindow
{
    // ==================== Streaming speech ====================
    // "Start speaking before the reply finishes" (off by default): speaks a streamed reply in pieces of
    // a few sentences while the model is still writing it. With the switch off, the reply is spoken once
    // it is finished (SpeakLastResponse): in pieces with the built-in Kokoro, else in one go.

    // Kokoro garbles very short clips ("Sure!"), so every piece holds at least this many characters.
    private const int StreamingSpeechMinPieceLength = 120;

    private sealed class StreamingSpeech
    {
        public StreamingSpeech(SpeechSession session) => Session = session;

        public SpeechSession Session { get; }

        // Like CleanSpeechText, only the text before the first code block is spoken.
        public SentenceChunker Chunker { get; private set; } = NewChunker();

        private static SentenceChunker NewChunker() => new()
        {
            StopAtFirstCodeBlock = true,
            MinLength = StreamingSpeechMinPieceLength,
            EveryPieceAtLeastMinLength = true
        };

        /// <summary>Starts a fresh reply segment (used between tool-call rounds).</summary>
        public void ResetChunker()
        {
            Chunker = NewChunker();
            ResetRoundText();
        }

        /// <summary>Forgets the streamed text of a tool-call round; the chunker keeps any unspoken text.</summary>
        public void ResetRoundText()
        {
            StreamedText.Clear();
            FedText = "";
        }

        public StringBuilder StreamedText { get; } = new();

        // The part of StreamedText (without reasoning) already given to the chunker.
        public string FedText { get; set; } = "";

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
            var messageClock = MessageClockElapsed();
            session.Completed += _ => Dispatcher.BeginInvoke(() =>
            {
                // A session that handed the reply over to SpeakLastResponse before anything played has no line.
                var timing = session.GetTiming(whileWriting: true);
                if (timing.FirstAudio != null)
                    AddSpeechTimingDiagnostic(timing, messageClock);
            });
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

            // Reasoning models write <think>...</think> first; only the answer after it is spoken.
            var visible = ReasoningText.StripThinking(speech.StreamedText.ToString(), streaming: true);
            // Plain-text planning notes ("The user said hi. Wait, ...") are never spoken; the answer
            // taken out of them is spoken in one go once the reply is finished.
            if (PlanningNotes.LooksLikeStart(visible))
            {
                speech.UseFinalText = true;
                return;
            }

            if (!visible.StartsWith(speech.FedText, StringComparison.Ordinal))
            {
                // A think block opened mid-reply: speak the cleaned final text instead.
                speech.UseFinalText = true;
                return;
            }

            var newText = visible[speech.FedText.Length..];
            if (newText.Length == 0)
                return;
            speech.FedText = visible;

            foreach (var sentence in speech.Chunker.Append(newText))
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
            // Stop, the mic button or a replay cut this reply's speech short: stay quiet,
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
            LooksLikeLeakedReasoningDump(streamedText) ||
            PlanningNotes.LooksLikeStart(ReasoningText.StripThinking(streamedText)))
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
    /// A tool-call round ended. A short preamble ("Let me look that up.") is kept and spoken together
    /// with the answer that follows the tool results, so it never becomes a short clip on its own.
    /// </summary>
    private void EndStreamingSpeechRound(StreamingSpeech? speech)
    {
        if (speech == null || speech.UseFinalText || speech.Session.IsCancelled)
            return;

        try
        {
            if (speech.Chunker.SawCodeBlock)
            {
                // Text after a code block is ignored by this chunker; the answer needs a fresh one.
                foreach (var sentence in speech.Chunker.Flush())
                    EnqueueSpeechSentence(speech.Session, sentence);
                speech.ResetChunker();
                return;
            }

            // A paragraph break between the preamble and the answer.
            foreach (var sentence in speech.Chunker.Append("\n\n"))
                EnqueueSpeechSentence(speech.Session, sentence);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Speech error: {ex.Message}");
        }

        speech.ResetRoundText();
    }

    /// <summary>
    /// Stops live speech after a chat error or cancellation. Returns true when the stopped session
    /// reports SpeechFinished (it had started speaking), which then ends the turn.
    /// </summary>
    private static bool CancelStreamingSpeech(StreamingSpeech? speech)
    {
        return speech?.Session.CancelAndCheckFinishReported() == true;
    }

    /// <summary>Stops live speech and ends the turn exactly once (directly, or through SpeechFinished).</summary>
    private void CancelStreamingSpeechAndFinishTurn(StreamingSpeech? speech)
    {
        if (CancelStreamingSpeech(speech))
            SetUIState("idle", "Ready");
        else
            FinishTurn();
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
