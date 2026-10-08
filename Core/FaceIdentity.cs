namespace VoiceChatbot;

/// <summary>
/// Policy identity used by the face gate and the prompt context. Keith is the owner slot and
/// Child1/Child2 the kid-safe slot. Profiles map to these through their <see cref="FaceProfileRole"/>,
/// not through their names.
/// </summary>
public enum FaceIdentity
{
    Keith,
    Child1,
    Child2,
    Unknown
}
