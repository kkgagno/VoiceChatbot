using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

public enum LocalModelState
{
    Stopped,
    Starting,
    Ready,
    Failed
}

/// <summary>
/// Runs the bundled llama.cpp server (llama\llama-server.exe in the app folder) on 127.0.0.1 with the chosen
/// model, for the "Built-in model" chat provider. It tries the graphics card first and falls back to the
/// processor when that fails, restarts when the model or context size changes, and is tied to this app
/// with a Windows job object, so it never outlives it (not even after a crash). Its output goes to
/// logs\llama-server.log.
/// </summary>
public sealed class LocalModelServer : IDisposable
{
    public const string ServerFolderName = "llama";
    public const string ServerExeName = "llama-server.exe";

    private static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(10);
    private const int OutputLinesKept = 80;

    private readonly object _gate = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly KillOnCloseJob? _job = KillOnCloseJob.TryCreate();
    private readonly Queue<string> _recentOutput = new();
    private Process? _process;
    private Task<bool>? _startTask;
    private CancellationTokenSource? _startCts;
    private (string Path, string Alias, int Context)? _wanted;
    private LlamaServerFeatures? _features;
    private StreamWriter? _log;
    private int _port;
    private bool _disposed;

    public LocalModelState State { get; private set; } = LocalModelState.Stopped;

    /// <summary>What the server is doing, for the sidebar ("Loading Gemma 4 E4B...", "Ready on the graphics card").</summary>
    public string StatusMessage { get; private set; } = "";

    /// <summary>The model's name in requests and in the model list.</summary>
    public string Alias { get; private set; } = "";

    public string ModelPath { get; private set; } = "";

    public int ContextTokens { get; private set; }

    /// <summary>True when the graphics card could not be used and the model runs on the processor.</summary>
    public bool RunningOnProcessor { get; private set; }

    /// <summary>
    /// The server stopped by itself after the model had loaded (State is Failed). The next chat request
    /// starts it again.
    /// </summary>
    public bool CrashedAfterReady { get; private set; }

    /// <summary>Raised on a background thread whenever <see cref="State"/> or <see cref="StatusMessage"/> changes.</summary>
    public event Action? StateChanged;

    /// <summary>Lines llama-server printed (also in its log), for the app log on failures.</summary>
    public event Action<string>? Log;

    /// <summary>The OpenAI-compatible address, http://127.0.0.1:&lt;port&gt;/v1; "" before the first start.</summary>
    public string BaseUrl
    {
        get
        {
            lock (_gate)
                return _port == 0 ? "" : $"http://{LlamaServerArgs.Host}:{_port}/v1";
        }
    }

    public static string ServerExePath => Path.Combine(AppContext.BaseDirectory, ServerFolderName, ServerExeName);

    public static bool IsInstalled => File.Exists(ServerExePath);

    public static string LogFilePath => Path.Combine(AppLog.LogDirectory, "llama-server.log");

    /// <summary>
    /// Starts the server with <paramref name="modelPath"/>, or restarts it when another model or context size
    /// is asked for. Returns the start, which completes with true once the model is loaded. Calling it again
    /// with the same model while it starts or runs returns the same task.
    /// </summary>
    public Task<bool> StartAsync(string modelPath, string alias, int contextTokens)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var wanted = (modelPath, alias, contextTokens);
            if (_startTask != null && _wanted == wanted && State is LocalModelState.Starting or LocalModelState.Ready &&
                (State == LocalModelState.Starting || _process is { HasExited: false }))
                return _startTask;

            StopLocked();
            _wanted = wanted;
            Alias = alias;
            ModelPath = modelPath;
            ContextTokens = contextTokens;
            RunningOnProcessor = false;
            CrashedAfterReady = false;
            var cts = new CancellationTokenSource();
            _startCts = cts;
            _startTask = Task.Run(() => RunAsync(modelPath, alias, contextTokens, cts.Token));
            return _startTask;
        }
    }

    /// <summary>
    /// Waits until the model is loaded (restarting the server if it stopped since), for chat requests.
    /// Throws with the reason when it cannot start.
    /// </summary>
    public async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        // A Restart, a new model or context size replaces the start being waited for: wait for the new one.
        for (var replaced = 0; ; replaced++)
        {
            Task<bool>? start;
            (string Path, string Alias, int Context)? restart = null;
            lock (_gate)
            {
                start = _startTask;
                if (_wanted is { } wanted && (start == null || CrashedAfterReady))
                    restart = wanted;
            }

            if (restart is { } again)
                start = StartAsync(again.Path, again.Alias, again.Context);
            if (start == null)
                throw new InvalidOperationException("The built-in model is not started. Choose a model under Chat Backend.");

            if (await start.WaitAsync(ct).ConfigureAwait(false))
                return;

            lock (_gate)
            {
                if (_startTask != null && !ReferenceEquals(_startTask, start) && replaced < 5)
                    continue;
            }
            throw new InvalidOperationException($"The built-in model could not start: {StatusMessage}");
        }
    }

    /// <summary>Stops the server (the chosen model is kept for the next start).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
            _wanted = null;
        }
        SetState(LocalModelState.Stopped, "Stopped");
    }

    private async Task<bool> RunAsync(string modelPath, string alias, int contextTokens, CancellationToken ct)
    {
        try
        {
            var exe = ServerExePath;
            if (!File.Exists(exe))
                return Fail($"{ServerFolderName}\\{ServerExeName} is missing from the app folder. Reinstall Voice Chatbot Mini.", ct);
            if (!File.Exists(modelPath))
                return Fail($"the model file is missing: {modelPath}", ct);

            SetState(LocalModelState.Starting, $"Loading {alias}...", ct);
            OpenLog();
            _features ??= await ReadFeaturesAsync(exe, ct).ConfigureAwait(false);

            var portRetries = 0;
            foreach (var useGpu in new[] { true, false })
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var port = EnsurePort(newPort: false);
                    var args = LlamaServerArgs.Build(modelPath, port, alias, contextTokens, useGpu, _features);
                    SetState(LocalModelState.Starting, useGpu ? $"Loading {alias}..." : $"Loading {alias} on the processor...", ct);
                    var process = Launch(exe, args, ct);
                    var ready = await WaitForHealthAsync(process, port, ct).ConfigureAwait(false);
                    if (ready)
                    {
                        lock (_gate)
                        {
                            if (!ct.IsCancellationRequested)
                                RunningOnProcessor = !useGpu;
                        }
                        SetState(LocalModelState.Ready, useGpu
                            ? $"{alias} ready ({contextTokens / 1024}K context)"
                            : $"{alias} ready on the processor ({contextTokens / 1024}K context, slower)", ct);
                        return true;
                    }

                    var output = RecentOutput();
                    KillProcess(process);
                    if (LlamaServerArgs.IsPortInUse(output) && portRetries++ < 3)
                    {
                        EnsurePort(newPort: true);
                        continue;
                    }

                    Write($"llama-server did not start ({LlamaServerArgs.DescribeFailure(output)}).");
                    break;
                }

                if (useGpu)
                    Write("Trying again with the model on the processor only.");
            }

            return Fail(LlamaServerArgs.DescribeFailure(RecentOutput()), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message, ct);
        }
    }

    private bool Fail(string reason, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;
        SetState(LocalModelState.Failed, reason, ct);
        AppLog.Warn($"Built-in model: could not start ({reason}). See {LogFilePath}.");
        return false;
    }

    private async Task<LlamaServerFeatures> ReadFeaturesAsync(string exe, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            };
            psi.ArgumentList.Add("--help");
            using var process = Process.Start(psi);
            if (process == null)
                return LlamaServerFeatures.None;
            _job?.Assign(process);

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                KillProcess(process);
            }

            var help = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            var features = LlamaServerFeatures.FromHelp(help);
            Write($"llama-server options: fit={features.Fit}, jinja={features.Jinja}, no-webui={features.NoWebUi}.");
            return features;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Write($"Could not read llama-server --help: {ex.Message}");
            return LlamaServerFeatures.None;
        }
    }

    private Process Launch(string exe, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        lock (_recentOutput)
            _recentOutput.Clear();
        Write($"Starting: {ServerExeName} {string.Join(" ", args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}");

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => OnOutput(e.Data);
        process.ErrorDataReceived += (_, e) => OnOutput(e.Data);
        process.Exited += (_, _) => OnProcessExited(process);
        process.Start();
        _job?.Assign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_gate)
        {
            // Stopped or restarted with another model meanwhile: this server is not wanted any more.
            if (ct.IsCancellationRequested)
            {
                KillProcess(process);
                process.Dispose();
                ct.ThrowIfCancellationRequested();
            }
            _process = process;
        }
        return process;
    }

    private async Task<bool> WaitForHealthAsync(Process process, int port, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var url = $"http://{LlamaServerArgs.Host}:{port}/health";
        while (clock.Elapsed < LoadTimeout)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
                return false;

            try
            {
                using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.OK)
                    return true;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }

            await Task.Delay(500, ct).ConfigureAwait(false);
        }

        Write($"llama-server was not ready after {LoadTimeout.TotalMinutes:0} minutes.");
        return false;
    }

    // A server that was ready and then stopped by itself (crash, killed in Task Manager): say so now
    // instead of at the next message, which starts it again.
    private void OnProcessExited(Process process)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_process, process) || State != LocalModelState.Ready)
                return;
            CrashedAfterReady = true;
        }
        Write($"llama-server stopped unexpectedly (exit code {SafeExitCode(process)}).");
        SetState(LocalModelState.Failed, "it stopped unexpectedly; it starts again with your next message");
    }

    private int EnsurePort(bool newPort)
    {
        lock (_gate)
        {
            if (_port == 0 || newPort)
                _port = FreePort();
            return _port;
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private void OnOutput(string? line)
    {
        if (string.IsNullOrEmpty(line))
            return;
        lock (_recentOutput)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > OutputLinesKept)
                _recentOutput.Dequeue();
        }
        WriteLog(line);
    }

    private string RecentOutput()
    {
        lock (_recentOutput)
            return string.Join("\n", _recentOutput);
    }

    private void OpenLog()
    {
        lock (_recentOutput)
        {
            if (_log != null)
                return;
            try
            {
                Directory.CreateDirectory(AppLog.LogDirectory);
                // One log per app run; the previous run's is kept as .old.
                if (File.Exists(LogFilePath))
                    File.Copy(LogFilePath, LogFilePath + ".old", overwrite: true);
                _log = new StreamWriter(new FileStream(LogFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8)
                {
                    AutoFlush = true
                };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log = null;
            }
        }
    }

    private void Write(string message)
    {
        WriteLog("[app] " + message);
        AppLog.Info("Built-in model: " + message);
        try { Log?.Invoke(message); } catch { }
    }

    private void WriteLog(string line)
    {
        lock (_recentOutput)
        {
            try { _log?.WriteLine($"{DateTime.Now:HH:mm:ss} {line}"); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
    }

    private void SetState(LocalModelState state, string message, CancellationToken ct = default)
    {
        lock (_gate)
        {
            // A start that was replaced by a newer one (or stopped) must not overwrite the status.
            if (ct.IsCancellationRequested)
                return;
            State = state;
            StatusMessage = message;
        }
        try { StateChanged?.Invoke(); } catch (Exception ex) { AppLog.Warn("Built-in model status update failed.", ex); }
    }

    private void StopLocked()
    {
        // Not disposed: the replaced start may still be checking its token.
        _startCts?.Cancel();
        _startCts = null;
        _startTask = null;
        if (_process != null)
        {
            KillProcess(_process);
            _process.Dispose();
            _process = null;
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(3000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    private static string SafeExitCode(Process process)
    {
        try { return process.ExitCode.ToString(); } catch { return "?"; }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            StopLocked();
        }
        _job?.Dispose();
        _http.Dispose();
        lock (_recentOutput)
        {
            try { _log?.Dispose(); } catch { }
            _log = null;
        }
    }

    /// <summary>A Windows job object that ends its processes when the app's handle to it closes (also on a crash).</summary>
    private sealed class KillOnCloseJob : IDisposable
    {
        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JobObjectLimitKillOnJobClose = 0x2000;

        private IntPtr _handle;

        private KillOnCloseJob(IntPtr handle) => _handle = handle;

        public static KillOnCloseJob? TryCreate()
        {
            if (!OperatingSystem.IsWindows())
                return null;
            try
            {
                var handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                    return null;

                var info = new JobObjectExtendedLimit { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
                if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<JobObjectExtendedLimit>()))
                {
                    CloseHandle(handle);
                    return null;
                }
                return new KillOnCloseJob(handle);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        public void Assign(Process process)
        {
            try
            {
                if (_handle != IntPtr.Zero)
                    AssignProcessToJobObject(_handle, process.Handle);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero)
            {
                CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimit
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimit
        {
            public JobObjectBasicLimit BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JobObjectExtendedLimit info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
