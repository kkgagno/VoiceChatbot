namespace VoiceChatbot;

public sealed class FaceAccessPolicyEvaluator
{
    private readonly FacePolicySettings _settings;

    public FaceAccessPolicyEvaluator(FacePolicySettings settings)
    {
        _settings = settings;
    }

    public FaceAccessDecision Evaluate(FacePresenceState presenceState, FaceIdentity identity)
    {
        var action = presenceState switch
        {
            FacePresenceState.CameraUnavailable => FaceAccessAction.NoChange,
            FacePresenceState.NoFaceDetected => _settings.NoFaceAction,
            FacePresenceState.MultipleFacesDetected => _settings.MultipleFacesAction,
            FacePresenceState.FaceDetected when identity == FaceIdentity.Keith => _settings.KeithRecognizedAction,
            FacePresenceState.FaceDetected when identity is FaceIdentity.Child1 or FaceIdentity.Child2 => _settings.ChildRecognizedAction,
            FacePresenceState.FaceDetected => _settings.UnknownFaceAction,
            _ => FaceAccessAction.NoChange
        };

        return new FaceAccessDecision
        {
            PresenceState = presenceState,
            Identity = identity,
            Action = action
        };
    }
}
