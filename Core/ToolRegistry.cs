using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace VoiceChatbot;

/// <summary>Runs one tool call. Receives the raw arguments JSON and returns text for the model.</summary>
public delegate Task<string> ToolExecutor(string argumentsJson, CancellationToken ct);

/// <summary>
/// The tools offered to the model. Features register a spec plus an executor (and optionally an
/// availability check and a status-note formatter). Execution never throws for tool failures:
/// errors and timeouts come back as text so the model can explain or try something else.
/// Only cancellation of the caller's token propagates.
/// </summary>
public sealed class ToolRegistry
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public const int MaxResultChars = 12000;

    private sealed record Entry(
        ToolSpec Spec,
        ToolExecutor Execute,
        Func<bool>? IsAvailable,
        Func<ToolCall, string>? Describe,
        TimeSpan Timeout);

    private readonly object _gate = new();
    private readonly List<Entry> _entries = new();

    /// <summary>Adds a tool, replacing any tool with the same name.</summary>
    public void Register(
        ToolSpec spec,
        ToolExecutor execute,
        Func<bool>? isAvailable = null,
        Func<ToolCall, string>? describeCall = null,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(execute);

        var entry = new Entry(spec, execute, isAvailable, describeCall,
            timeout is { } t && t > TimeSpan.Zero ? t : DefaultTimeout);
        lock (_gate)
        {
            var index = _entries.FindIndex(e => SameName(e.Spec.Name, spec.Name));
            if (index >= 0)
                _entries[index] = entry;
            else
                _entries.Add(entry);
        }
    }

    public bool Unregister(string name)
    {
        lock (_gate)
            return _entries.RemoveAll(e => SameName(e.Spec.Name, name)) > 0;
    }

    public bool IsRegistered(string name)
    {
        lock (_gate)
            return _entries.Any(e => SameName(e.Spec.Name, name));
    }

    /// <summary>Tools whose availability check passes right now, in registration order.</summary>
    public IReadOnlyList<ToolSpec> GetAvailableTools(Func<ToolSpec, bool>? filter = null)
    {
        List<Entry> snapshot;
        lock (_gate)
            snapshot = _entries.ToList();

        return snapshot
            .Where(IsAvailable)
            .Select(e => e.Spec)
            .Where(spec => filter?.Invoke(spec) ?? true)
            .ToList();
    }

    public string DescribeCall(ToolCall call)
    {
        var entry = Find(call.Name);
        if (entry?.Describe is not null)
        {
            try
            {
                var note = entry.Describe(call);
                if (!string.IsNullOrWhiteSpace(note))
                    return note;
            }
            catch
            {
                // Fall back to the generic note.
            }
        }

        return BuiltInTools.DescribeCall(call);
    }

    /// <summary>Runs the call with its timeout. Returns the tool's text, or an "Error: ..." text.</summary>
    public async Task<string> ExecuteAsync(ToolCall call, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var name = call.Name?.Trim() ?? "";
        var entry = Find(name);
        if (entry is null)
        {
            var known = string.Join(", ", GetAvailableTools().Select(t => t.Name));
            return $"Error: there is no tool named \"{name}\". Available tools: {(known.Length == 0 ? "none" : known)}.";
        }

        if (!IsAvailable(entry))
            return $"Error: the {name} tool is not available right now.";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(entry.Timeout);

        Task<string> work;
        try
        {
            work = entry.Execute(call.ArgumentsJson ?? "{}", timeoutCts.Token) ?? Task.FromResult("");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"Error: {name} failed: {ex.Message}";
        }

        try
        {
            // WhenAny also bounds executors that ignore the token.
            var finished = await Task.WhenAny(work, Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);
            if (finished != work)
            {
                ObserveFault(work);
                ct.ThrowIfCancellationRequested();
                return TimedOut(name, entry.Timeout);
            }

            var result = await work.ConfigureAwait(false);
            return Cap(string.IsNullOrWhiteSpace(result) ? "(The tool returned no result.)" : result.Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return TimedOut(name, entry.Timeout);
        }
        catch (Exception ex)
        {
            return $"Error: {name} failed: {ex.Message}";
        }
    }

    private Entry? Find(string? name)
    {
        lock (_gate)
            return _entries.FirstOrDefault(e => SameName(e.Spec.Name, name));
    }

    private static bool IsAvailable(Entry entry)
    {
        try
        {
            return entry.IsAvailable?.Invoke() ?? true;
        }
        catch
        {
            return false;
        }
    }

    private static bool SameName(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string TimedOut(string name, TimeSpan timeout) =>
        $"Error: {name} timed out after {timeout.TotalSeconds:0} seconds.";

    private static string Cap(string text) =>
        text.Length <= MaxResultChars
            ? text
            : text[..MaxResultChars] + "\n[Tool result truncated.]";

    private static void ObserveFault(Task task) =>
        task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
}
