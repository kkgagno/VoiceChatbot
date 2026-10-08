using System;

namespace VoiceChatbot;

/// <summary>
/// How long a face scan result stays valid for gating. Once it expires, the gate treats the
/// person at the PC as unverified instead of reusing the last result indefinitely.
/// </summary>
public static class FaceScanFreshness
{
    public const int DefaultTimeoutSeconds = 300;

    /// <summary>Timeout to apply: a missing or non-positive setting falls back to the default.</summary>
    public static TimeSpan EffectiveTimeout(int timeoutSeconds) =>
        TimeSpan.FromSeconds(timeoutSeconds > 0 ? timeoutSeconds : DefaultTimeoutSeconds);

    /// <summary>
    /// True while the last scan is recent enough to rely on. No scan yet (MinValue), or a clock
    /// that moved backwards past the scan time, counts as not fresh.
    /// </summary>
    public static bool IsFresh(DateTime lastScanUtc, DateTime nowUtc, int timeoutSeconds)
    {
        if (lastScanUtc == DateTime.MinValue)
            return false;

        var age = nowUtc - lastScanUtc;
        return age >= TimeSpan.Zero && age < EffectiveTimeout(timeoutSeconds);
    }

    /// <summary>
    /// Presence state used when there is no fresh scan. With the camera on, nobody has been
    /// verified, so the gate applies its no-face rule rather than the camera-unavailable pass-through.
    /// </summary>
    public static FacePresenceState UnverifiedState(bool cameraFeaturesEnabled) =>
        cameraFeaturesEnabled ? FacePresenceState.NoFaceDetected : FacePresenceState.CameraUnavailable;
}
