namespace VoiceChatbot;

// FaceEmbedding, LocalFaceProfile, FaceRecognitionResult and FaceProfileRole live in
// Core/FaceProfileModels.cs so their matching and migration logic can be unit-tested.

public sealed class FaceAccessDecision
{
    public FaceIdentity Identity { get; set; } = FaceIdentity.Unknown;
    public FacePresenceState PresenceState { get; set; } = FacePresenceState.CameraUnavailable;
    public string Action { get; set; } = FaceAccessAction.NoChange;
    public bool ShouldMuteMicrophone => Action is FaceAccessAction.MuteMic or FaceAccessAction.DisableMic;
    public bool ShouldBlockWakeWord => Action == FaceAccessAction.BlockWakeWord;
    public bool IsKidSafeMode => Action == FaceAccessAction.KidSafeMode;
}
