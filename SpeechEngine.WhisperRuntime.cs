using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace VoiceChatbot;

public partial class SpeechEngine
{
    // ==================== Whisper runtime: Vulkan GPU or CPU ====================
    // Whisper.net loads one native runtime per process, when the first model loads: the Vulkan build
    // (runtimes\vulkan\win-x64, from Whisper.net.Runtime.Vulkan) when GPU use is on and a Vulkan 1.2
    // GPU is present, otherwise the CPU build (Whisper.net.Runtime). If the Vulkan build cannot load,
    // Whisper.net moves on to the CPU build by itself. With the Vulkan build loaded, turning GPU use off
    // runs the next model load on the CPU.

    private static readonly PropertyInfo? LoadedLibraryProperty = typeof(RuntimeOptions).GetProperty(
        "LoadedLibrary", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private string? _whisperModelFile;

    /// <summary>Run Whisper.net on the GPU (Vulkan) when possible. Read whenever a model loads.</summary>
    public bool WhisperUseGpu { get; set; } = true;

    /// <summary>What the loaded model runs on, for the UI: "Whisper: Vulkan GPU (AMD Radeon ...)", "Whisper: CPU", or "".</summary>
    public string WhisperRuntime { get; private set; } = "";

    /// <summary>Raised after each Whisper model load (or GPU setting change) with <see cref="WhisperRuntime"/>.</summary>
    public event Action<string>? WhisperRuntimeChanged;

    /// <summary>
    /// Turns GPU use on or off. The model is reloaded under the transcription lock, so the change
    /// applies from the next utterance. Turning the GPU on needs a restart when the CPU runtime is
    /// already loaded (GPU use was off when the first model loaded).
    /// </summary>
    public async Task SetWhisperUseGpuAsync(bool useGpu)
    {
        if (useGpu == WhisperUseGpu)
            return;

        WhisperUseGpu = useGpu;
        await _whisperLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || _whisperFactory == null || _whisperModelFile == null)
                return; // used when the first model loads

            if (useGpu && LoadedWhisperLibrary() is { } loaded && loaded != RuntimeLibrary.Vulkan)
            {
                ReportWhisperRuntime(VulkanProbe.HasGpu(out var reason)
                    ? "Whisper: CPU - restart Voice Chatbot to use the GPU"
                    : $"Whisper: CPU ({reason})");
                return;
            }

            _whisperProcessor?.Dispose();
            _whisperProcessor = null;
            _whisperFactory.Dispose();
            _whisperFactory = null;
            LoadWhisperModel(_whisperModelFile);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Could not reload the Whisper model: {ex.Message}");
        }
        finally
        {
            _whisperLock.Release();
        }
    }

    // Creates the factory and processor for a model file. Runs during Initialize or under _whisperLock.
    private void LoadWhisperModel(string modelPath)
    {
        var loaded = LoadedWhisperLibrary();
        // No Vulkan calls at all while GPU use is off.
        var gpuName = "";
        var gpuFound = WhisperUseGpu && VulkanProbe.HasGpu(out gpuName);
        if (loaded == null)
        {
            // Only read before the first model loads.
            RuntimeOptions.Instance.SetRuntimeLibraryOrder(WhisperUseGpu && gpuFound
                ? new List<RuntimeLibrary> { RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx }
                : new List<RuntimeLibrary> { RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx });
        }

        var useGpu = WhisperUseGpu && gpuFound && (loaded == null || loaded == RuntimeLibrary.Vulkan);
        RuntimeOptions.Instance.SetUseGpu(useGpu);

        var nativeLog = new List<string>();
        void Collect(WhisperLogLevel level, string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                lock (nativeLog)
                    nativeLog.Add(message.Trim());
        }

        string gpuProblem = "";
        LogProvider.Instance.OnLog += Collect;
        try
        {
            try
            {
                CreateWhisperProcessor(modelPath);
            }
            catch (Exception ex) when (useGpu)
            {
                // The GPU could not take the model: load it again on the CPU.
                gpuProblem = ex.Message;
                useGpu = false;
                RuntimeOptions.Instance.SetUseGpu(false);
                CreateWhisperProcessor(modelPath);
            }
        }
        finally
        {
            LogProvider.Instance.OnLog -= Collect;
        }

        // whisper.cpp falls back to its CPU backend by itself when the Vulkan device does not start.
        lock (nativeLog)
        {
            if (useGpu && nativeLog.Exists(line => line.Contains("failed", StringComparison.OrdinalIgnoreCase) &&
                                                   line.Contains("backend", StringComparison.OrdinalIgnoreCase)))
                gpuProblem = "the Vulkan backend did not start";
        }

        _whisperModelFile = modelPath;
        string runtime;
        if (useGpu && gpuProblem.Length == 0 && LoadedWhisperLibrary() == RuntimeLibrary.Vulkan)
            runtime = $"Whisper: Vulkan GPU ({gpuName})";
        else if (gpuProblem.Length > 0)
            runtime = $"Whisper: CPU (GPU failed: {gpuProblem})";
        else if (WhisperUseGpu && !gpuFound)
            runtime = $"Whisper: CPU ({gpuName})";
        else if (WhisperUseGpu && LoadedWhisperLibrary() != RuntimeLibrary.Vulkan && loaded == null)
            runtime = "Whisper: CPU (the Vulkan runtime could not load)";
        else
            runtime = "Whisper: CPU";
        ReportWhisperRuntime(runtime);
    }

    private void CreateWhisperProcessor(string modelPath)
    {
        var factory = WhisperFactory.FromPath(modelPath);
        try
        {
            _whisperProcessor = BuildWhisperProcessor(factory);
            _whisperFactory = factory;
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    private void ReportWhisperRuntime(string runtime)
    {
        WhisperRuntime = runtime;
        WhisperRuntimeChanged?.Invoke(runtime);
    }

    // Which native runtime Whisper.net loaded; null before the first model loads. Whisper.net 1.7.1
    // keeps this internal.
    private static RuntimeLibrary? LoadedWhisperLibrary()
    {
        try
        {
            return LoadedLibraryProperty?.GetValue(RuntimeOptions.Instance) as RuntimeLibrary?;
        }
        catch
        {
            return null;
        }
    }
}
