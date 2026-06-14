using System;
using System.Collections.Generic;

namespace VoiceChatbot;

public sealed class FaceEmbedding
{
    public int Version { get; set; } = 1;
    public float[] Values { get; set; } = Array.Empty<float>();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LocalFaceProfile
{
    public string ProfileId { get; set; } = "";
    public FaceIdentity Identity { get; set; } = FaceIdentity.Unknown;
    public string DisplayName { get; set; } = "";
    public List<FaceEmbedding> Embeddings { get; set; } = new();
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public sealed class FaceRecognitionResult
{
    public FaceIdentity Identity { get; set; } = FaceIdentity.Unknown;
    public string DisplayName { get; set; } = "";
    public float Similarity { get; set; }
    public bool IsRecognized { get; set; }
}

public sealed class FaceAccessDecision
{
    public FaceIdentity Identity { get; set; } = FaceIdentity.Unknown;
    public FacePresenceState PresenceState { get; set; } = FacePresenceState.CameraUnavailable;
    public string Action { get; set; } = FaceAccessAction.NoChange;
    public bool ShouldMuteMicrophone => Action is FaceAccessAction.MuteMic or FaceAccessAction.DisableMic;
    public bool ShouldBlockWakeWord => Action == FaceAccessAction.BlockWakeWord;
    public bool IsKidSafeMode => Action == FaceAccessAction.KidSafeMode;
}
