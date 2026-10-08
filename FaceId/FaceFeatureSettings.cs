using System;
using System.IO;

namespace VoiceChatbot;

public sealed class FaceFeatureSettings
{
    public bool CameraFeaturesEnabled { get; set; } = false;
    public bool FaceGatingEnabled { get; set; } = false;
    public int CameraIndex { get; set; } = 0;
    public int FrameIntervalMs { get; set; } = 500;

    /// <summary>
    /// How long a face scan result counts for gating. After this the person at the PC is treated as
    /// unverified until the next scan (voice input triggers one automatically when the camera is on).
    /// </summary>
    public int IdentityTimeoutSeconds { get; set; } = FaceScanFreshness.DefaultTimeoutSeconds;

    public FaceModelOptions ModelOptions { get; set; } = new();
    public FacePolicySettings Policy { get; set; } = new();

    public static string GetDefaultProfileDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VoiceChatbot",
            "FaceProfiles");
    }
}

public sealed class FaceModelOptions
{
    public string ProfileDirectory { get; set; } = FaceFeatureSettings.GetDefaultProfileDirectory();
    public string HaarCascadePath { get; set; } = Path.Combine("Resources", "Models", "haarcascade_frontalface_default.xml");
    public string FaceEmbeddingModelPath { get; set; } = Path.Combine("Resources", "Models", "face_recognition_sface_2021dec.onnx");
    public bool PreferWindowsMl { get; set; } = false;
    public float RecognitionThreshold { get; set; } = 0.36f;

    /// <summary>Required lead of the best profile over the second-best one; closer results count as unknown.</summary>
    public float RecognitionMargin { get; set; } = FaceEmbeddingMath.DefaultAmbiguityMargin;
}

public sealed class FacePolicySettings
{
    public string KeithRecognizedAction { get; set; } = FaceAccessAction.FullAssistantAccess;
    public string ChildRecognizedAction { get; set; } = FaceAccessAction.KidSafeMode;
    public string UnknownFaceAction { get; set; } = FaceAccessAction.NoChange;
    public string NoFaceAction { get; set; } = FaceAccessAction.NoChange;
    public string MultipleFacesAction { get; set; } = FaceAccessAction.GuestPrivateMode;
}

public static class FaceAccessAction
{
    public const string NoChange = "NoChange";
    public const string FullAssistantAccess = "FullAssistantAccess";
    public const string KidSafeMode = "KidSafeMode";
    public const string MuteMic = "MuteMic";
    public const string BlockWakeWord = "BlockWakeWord";
    public const string DisableMic = "DisableMic";
    public const string GuestPrivateMode = "GuestPrivateMode";
}
