using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace VoiceChatbot;

public sealed class SileroVad : IDisposable
{
    public const int SampleRate = 16000;
    public const int FrameSamples = 512;

    private readonly InferenceSession _session;
    private readonly List<float> _pending = new();
    private float[,,] _state = new float[2, 1, 128];
    private float[] _context = new float[64];

    public SileroVad(string modelPath)
    {
        var options = new SessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = 1,
            EnableCpuMemArena = true
        };
        _session = new InferenceSession(modelPath, options);
    }

    public void Reset()
    {
        _pending.Clear();
        _state = new float[2, 1, 128];
        _context = new float[64];
    }

    public float ProcessPcm16(byte[] buffer, int count)
    {
        for (var index = 0; index + 1 < count; index += 2)
        {
            var sample = (short)(buffer[index] | buffer[index + 1] << 8);
            _pending.Add(sample / 32768f);
        }

        var maximumProbability = 0f;
        while (_pending.Count >= FrameSamples)
        {
            var frame = _pending.GetRange(0, FrameSamples).ToArray();
            _pending.RemoveRange(0, FrameSamples);
            maximumProbability = Math.Max(maximumProbability, ProcessFrame(frame));
        }
        return maximumProbability;
    }

    private float ProcessFrame(float[] frame)
    {
        var input = new float[_context.Length + frame.Length];
        Array.Copy(_context, input, _context.Length);
        Array.Copy(frame, 0, input, _context.Length, frame.Length);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input", new DenseTensor<float>(input, [1, input.Length])),
            NamedOnnxValue.CreateFromTensor("sr", new DenseTensor<long>([(long)SampleRate], [1])),
            NamedOnnxValue.CreateFromTensor(
                "state",
                new DenseTensor<float>(_state.Cast<float>().ToArray(), [2, 1, 128]))
        };

        using var outputs = _session.Run(inputs);
        var probability = outputs.First(value => value.Name == "output").AsTensor<float>().ToArray()[0];
        var nextState = outputs.First(value => value.Name == "stateN").AsTensor<float>();

        for (var layer = 0; layer < 2; layer++)
        for (var index = 0; index < 128; index++)
            _state[layer, 0, index] = nextState[layer, 0, index];

        Array.Copy(input, input.Length - _context.Length, _context, 0, _context.Length);
        return probability;
    }

    public void Dispose() => _session.Dispose();
}
