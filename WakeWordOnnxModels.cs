using System;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoiceChatbot;

/// <summary>
/// openWakeWord's ONNX models (Resources\Models\WakeWord) on ONNX Runtime, one CPU thread each like
/// the reference. Create and use it on one background thread.
/// </summary>
public sealed class WakeWordOnnxModels : IWakeWordModelRunner, IDisposable
{
    public const string MelModelFile = "melspectrogram.onnx";
    public const string EmbeddingModelFile = "embedding_model.onnx";

    private readonly InferenceSession _mel;
    private readonly InferenceSession _embedding;
    private readonly InferenceSession _wakeWord;
    private readonly string _melInput;
    private readonly string _embeddingInput;
    private readonly string _wakeWordInput;

    /// <param name="folder">Folder with the .onnx files.</param>
    /// <param name="wakeWordModelFile">For example hey_jarvis_v0.1.onnx.</param>
    public WakeWordOnnxModels(string folder, string wakeWordModelFile)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };

        try
        {
            _mel = Open(folder, MelModelFile, options);
            _embedding = Open(folder, EmbeddingModelFile, options);
            _wakeWord = Open(folder, wakeWordModelFile, options);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            options.Dispose();
        }

        _melInput = _mel.InputMetadata.Keys.First();
        _embeddingInput = _embedding.InputMetadata.Keys.First();
        _wakeWordInput = _wakeWord.InputMetadata.Keys.First();
    }

    /// <summary>Resources\Models\WakeWord next to the app.</summary>
    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "Resources", "Models", "WakeWord");

    public float[] MelSpectrogram(float[] samples) =>
        Run(_mel, _melInput, new DenseTensor<float>(samples, new[] { 1, samples.Length }));

    public float[] Embed(float[] windows, int count) =>
        Run(_embedding, _embeddingInput, new DenseTensor<float>(windows, new[]
        {
            count, OpenWakeWordPipeline.EmbeddingWindowFrames, OpenWakeWordPipeline.MelBins, 1
        }));

    public float Score(float[] features) =>
        Run(_wakeWord, _wakeWordInput, new DenseTensor<float>(features, new[]
        {
            1, OpenWakeWordPipeline.WakeModelFrames, OpenWakeWordPipeline.EmbeddingSize
        }))[0];

    public void Dispose()
    {
        _mel?.Dispose();
        _embedding?.Dispose();
        _wakeWord?.Dispose();
    }

    private static InferenceSession Open(string folder, string file, SessionOptions options)
    {
        var path = Path.Combine(folder, file);
        if (!File.Exists(path))
            throw new FileNotFoundException($"The wake word model file {file} is missing from {folder}. Reinstall Voice Chatbot.", path);
        return new InferenceSession(path, options);
    }

    private static float[] Run(InferenceSession session, string inputName, DenseTensor<float> input)
    {
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, input) });
        return results.First().AsEnumerable<float>().ToArray();
    }
}
