namespace VoiceChatbot;

public partial class SpeechEngine
{
    // ==================== Wake word turns ====================
    // A listening turn started by the always-on wake word detector (WakeWordDetector). The text wake
    // word is not required again, the chime played on detection does not count as voice, and the turn
    // gives up when nobody speaks (a false trigger) instead of listening forever.

    private const int WakeTurnChimeBuckets = 5;      // ~500 ms after the start
    private const int WakeTurnNoVoiceBuckets = 80;   // ~8 s without voice

    private bool _startingWakeTurn;
    private bool _wakeWordAlreadyHeard;
    private int _wakeTurnChimeBucketsLeft;
    private int _wakeTurnNoVoiceBucketsLeft;

    /// <summary>Like <see cref="StartListening"/>, for a turn the wake word detector started.</summary>
    public bool StartListeningAfterWakeWord()
    {
        _startingWakeTurn = true;
        try
        {
            return StartListening();
        }
        finally
        {
            _startingWakeTurn = false;
        }
    }

    // Called by StartListening for every turn, before the microphone opens.
    private void BeginWakeTurnIfStarting()
    {
        _wakeWordAlreadyHeard = _startingWakeTurn;
        _wakeTurnChimeBucketsLeft = _startingWakeTurn ? WakeTurnChimeBuckets : 0;
        _wakeTurnNoVoiceBucketsLeft = _startingWakeTurn ? WakeTurnNoVoiceBuckets : 0;
    }

    // Capture thread, once per ~100 ms buffer: true while the chime may still be playing.
    private bool IsWakeTurnChime()
    {
        if (_wakeTurnChimeBucketsLeft <= 0)
            return false;
        _wakeTurnChimeBucketsLeft--;
        return true;
    }

    // Capture thread, once per buffer: true when a wake word turn has heard no voice for too long.
    private bool WakeTurnHeardNothing()
    {
        if (_wakeTurnNoVoiceBucketsLeft <= 0 || _voiceDetected)
            return false;
        _wakeTurnNoVoiceBucketsLeft--;
        return _wakeTurnNoVoiceBucketsLeft == 0;
    }
}
