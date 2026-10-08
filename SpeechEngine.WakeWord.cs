using System;

namespace VoiceChatbot;

public partial class SpeechEngine
{
    // ==================== Wake phrase ====================
    // With AutoDetect off ("Only respond after the wake word"), a listening turn is answered only when its
    // transcript contains WakeWord ("hey onyx", matched by WakeWordText); anything else is ignored and
    // listening goes on. "Hey Onyx, what's the weather" sends "what's the weather". A bare "Hey Onyx"
    // raises WakePhraseHeard, and turns that start within the next 8 seconds need no phrase until one
    // utterance has been accepted. Such a turn ignores the acknowledgement sound at its start and gives
    // up when nobody speaks for about 8 seconds.

    private const int WakeTurnChimeBuckets = 5;      // ~500 ms after the start
    private const int WakeTurnNoVoiceBuckets = 80;   // ~8 s without voice
    private static readonly TimeSpan WakePhraseFollowUpWindow = TimeSpan.FromSeconds(8);

    private bool _startingWithoutWakePhrase;
    private bool _wakeWordAlreadyHeard;
    private DateTime _wakePhraseFollowUpUntilUtc = DateTime.MinValue;
    private int _wakeTurnChimeBucketsLeft;
    private int _wakeTurnNoVoiceBucketsLeft;

    /// <summary>
    /// Raised on a background thread when the user said only the wake phrase. The next listening turn
    /// is answered without it; the handler plays the acknowledgement sound.
    /// </summary>
    public event Action? WakePhraseHeard;

    /// <summary>True when the current (or next) listening turn only answers speech that contains the wake phrase.</summary>
    public bool IsWaitingForWakePhrase => RequiresWakePhrase && !_wakeWordAlreadyHeard;

    /// <summary>True for 8 seconds after a bare wake phrase, until an utterance has been answered.</summary>
    public bool HasWakePhraseFollowUp => DateTime.UtcNow < _wakePhraseFollowUpUntilUtc;

    private bool RequiresWakePhrase => !AutoDetect && WakeWordText.HasWords(WakeWord);

    /// <summary>
    /// Like <see cref="StartListening"/>, for a turn the user asked for (Listen button, hotkey, Mic):
    /// it is answered without the wake phrase.
    /// </summary>
    public bool StartListeningWithoutWakePhrase()
    {
        _startingWithoutWakePhrase = true;
        try
        {
            return StartListening();
        }
        finally
        {
            _startingWithoutWakePhrase = false;
        }
    }

    // Called by StartListening for every turn, before the microphone opens.
    private void BeginWakeTurnIfStarting()
    {
        var followUp = HasWakePhraseFollowUp;
        _wakeWordAlreadyHeard = _startingWithoutWakePhrase || followUp;
        _wakeTurnChimeBucketsLeft = followUp ? WakeTurnChimeBuckets : 0;
        _wakeTurnNoVoiceBucketsLeft = followUp ? WakeTurnNoVoiceBuckets : 0;
    }

    /// <summary>
    /// Background thread, after transcription: applies the wake phrase to a transcript. Returns the text
    /// to send, or null when the turn is over (no phrase: ignored; a bare phrase: the next utterance is
    /// answered without it).
    /// </summary>
    private string? ApplyWakePhrase(string text, bool wakeWordAlreadyHeard)
    {
        if (!RequiresWakePhrase)
            return text;

        if (!WakeWordText.TryFind(text, WakeWord, out var afterWakeWord))
        {
            if (wakeWordAlreadyHeard)
                return Accept(text);
            AppLog.Info($"Ignored speech without the wake phrase \"{WakeWord.Trim()}\": \"{Shorten(text)}\"");
            return null;
        }

        // The phrase itself is never sent.
        if (!string.IsNullOrWhiteSpace(afterWakeWord) && !IsIgnoredWhisperText(afterWakeWord))
            return Accept(afterWakeWord);

        AppLog.Info($"Heard the wake phrase \"{WakeWord.Trim()}\"; the next utterance is answered without it.");
        _wakePhraseFollowUpUntilUtc = DateTime.UtcNow + WakePhraseFollowUpWindow;
        try { WakePhraseHeard?.Invoke(); } catch { }
        return null;
    }

    // One utterance was answered: the next one needs the wake phrase again.
    private string Accept(string text)
    {
        _wakePhraseFollowUpUntilUtc = DateTime.MinValue;
        return text;
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "...";

    // Capture thread, once per ~100 ms buffer: true while the acknowledgement sound may still be playing.
    private bool IsWakeTurnChime()
    {
        if (_wakeTurnChimeBucketsLeft <= 0)
            return false;
        _wakeTurnChimeBucketsLeft--;
        return true;
    }

    // Capture thread, once per buffer: true when a follow-up turn has heard no voice for too long.
    private bool WakeTurnHeardNothing()
    {
        if (_wakeTurnNoVoiceBucketsLeft <= 0 || _voiceDetected)
            return false;
        _wakeTurnNoVoiceBucketsLeft--;
        return _wakeTurnNoVoiceBucketsLeft == 0;
    }
}
