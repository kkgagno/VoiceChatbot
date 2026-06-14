using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

public sealed class InferenceCapabilities
{
    public string BackendName { get; set; } = "";
    public bool IsAvailable { get; set; }
    public bool SupportsNpu { get; set; }
    public string Reason { get; set; } = "";
}

public interface IOnnxInferenceBackend
{
    string BackendName { get; }
    bool IsAvailable { get; }
    InferenceCapabilities GetCapabilities();
    Task<float[]> RunAsync(string modelPath, IReadOnlyList<float> inputTensor, CancellationToken cancellationToken = default);
}

public sealed class CpuInferenceBackend : IOnnxInferenceBackend
{
    public string BackendName => "CPU";
    public bool IsAvailable => true;

    public InferenceCapabilities GetCapabilities()
    {
        return new InferenceCapabilities
        {
            BackendName = BackendName,
            IsAvailable = true,
            SupportsNpu = false,
            Reason = "CPU fallback is always allowed when a local ONNX model and runtime package are configured."
        };
    }

    public Task<float[]> RunAsync(string modelPath, IReadOnlyList<float> inputTensor, CancellationToken cancellationToken = default)
    {
        // TODO: Add Microsoft.ML.OnnxRuntime CPU session when the face embedding model is selected.
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Face embedding ONNX model not found.", modelPath);

        throw new NotImplementedException("CPU ONNX inference is reserved for the face embedding model implementation.");
    }
}

public sealed class WindowsMlInferenceBackend : IOnnxInferenceBackend
{
    private readonly FaceModelOptions _options;

    public WindowsMlInferenceBackend(FaceModelOptions options)
    {
        _options = options;
    }

    public string BackendName => "Windows ML";
    public bool IsAvailable => OperatingSystem.IsWindows() && _options.PreferWindowsMl;

    public InferenceCapabilities GetCapabilities()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new InferenceCapabilities
            {
                BackendName = BackendName,
                IsAvailable = false,
                SupportsNpu = false,
                Reason = "Windows ML is only available on Windows."
            };
        }

        return new InferenceCapabilities
        {
            BackendName = BackendName,
            IsAvailable = _options.PreferWindowsMl,
            SupportsNpu = false,
            Reason = _options.PreferWindowsMl
                ? "Windows ML probing is enabled; NPU detection will be added when runtime integration is implemented."
                : "Windows ML is disabled in settings; CPU fallback should be used."
        };
    }

    public Task<float[]> RunAsync(string modelPath, IReadOnlyList<float> inputTensor, CancellationToken cancellationToken = default)
    {
        // TODO: Integrate Windows ML / ONNX Runtime execution provider selection for AMD Strix Halo NPU.
        if (!IsAvailable)
            throw new InvalidOperationException("Windows ML backend is unavailable; use CPU fallback.");

        throw new NotImplementedException("Windows ML inference backend is a capability-ready stub.");
    }
}
