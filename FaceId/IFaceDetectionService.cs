using OpenCvSharp;

namespace VoiceChatbot;

public sealed class FaceDetectionResult
{
    public FacePresenceState State { get; set; } = FacePresenceState.NoFaceDetected;
    public Rect[] Faces { get; set; } = [];
}

public interface IFaceDetectionService
{
    FaceDetectionResult DetectFaces(Mat frame);
}
