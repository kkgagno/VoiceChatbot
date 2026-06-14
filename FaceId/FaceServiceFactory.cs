namespace VoiceChatbot;

public static class FaceServiceFactory
{
    public static FacePresenceMonitor CreatePresenceMonitor(FaceFeatureSettings settings)
    {
        var camera = new OpenCvCameraService(settings.CameraIndex, settings.FrameIntervalMs);
        var detector = new HaarCascadeFaceDetectionService(settings.ModelOptions);
        return new FacePresenceMonitor(camera, detector);
    }

    public static IFaceProfileStore CreateProfileStore(FaceModelOptions options)
    {
        return new JsonFaceProfileStore(options.ProfileDirectory);
    }

    public static IOnnxInferenceBackend CreateInferenceBackend(FaceModelOptions options)
    {
        var windowsMl = new WindowsMlInferenceBackend(options);
        return windowsMl.IsAvailable ? windowsMl : new CpuInferenceBackend();
    }
}
