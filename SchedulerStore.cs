using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceChatbot;

public sealed class SchedulerStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceChatbot",
        "scheduler.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly string BackupPath = StorePath + ".bak";

    public List<ScheduledPromptTask> Tasks { get; set; } = new();

    /// <summary>Set when scheduler.json existed but could not be read; shown to the user once.</summary>
    [JsonIgnore]
    public string LoadError { get; private set; } = "";

    /// <summary>True when the unreadable file could not be backed up, so nothing is written over it.</summary>
    [JsonIgnore]
    public bool SaveBlocked => _saveBlocked;

    // The file on disk could not be read. Never replace it with an empty store; once the user adds
    // tasks it may be overwritten, because a copy is in scheduler.json.bak.
    private bool _keepUnreadableFile;
    private bool _saveBlocked;
    private readonly List<string> _startupNotices = new();

    public static SchedulerStore Load()
    {
        if (!File.Exists(StorePath))
            return new SchedulerStore();

        SchedulerStore store;
        try
        {
            var json = File.ReadAllText(StorePath);
            store = JsonSerializer.Deserialize<SchedulerStore>(json, JsonOptions)
                ?? throw new JsonException("The file does not contain a scheduler store.");
            store.Tasks ??= new List<ScheduledPromptTask>();
            foreach (var task in store.Tasks)
            {
                Normalize(task);
                if (task.LastStatus == "Running")
                    task.LastStatus = "Interrupted: the app closed while this task was running.";
            }
        }
        catch (Exception ex)
        {
            var backedUp = TryBackupUnreadableFile();
            return new SchedulerStore
            {
                _keepUnreadableFile = true,
                _saveBlocked = !backedUp,
                LoadError = backedUp
                    ? $"Scheduler: scheduler.json could not be read ({ex.Message}). A copy was saved to {BackupPath} and the scheduler started with no tasks."
                    : $"Scheduler: scheduler.json could not be read ({ex.Message}) or backed up. Scheduled tasks will not be saved this session so the file is not overwritten: {StorePath}"
            };
        }

        try
        {
            store.ApplyStartupCatchUp(DateTime.Now);
        }
        catch (Exception ex)
        {
            store._startupNotices.Add($"Could not check for runs missed while the app was closed: {ex.Message}");
        }

        return store;
    }

    public void Save()
    {
        if (_saveBlocked)
        {
            if (!TryBackupUnreadableFile())
                return;
            _saveBlocked = false;
        }

        if (_keepUnreadableFile && Tasks.Count == 0)
            return;

        var dir = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        foreach (var task in Tasks)
            Normalize(task);

        // Write a temp file and swap it in, so a crash mid-write cannot leave a truncated store.
        var tempPath = StorePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tempPath, StorePath, overwrite: true);
        _keepUnreadableFile = false;
    }

    /// <summary>Messages from the startup catch-up policy, returned once.</summary>
    public IReadOnlyList<string> TakeStartupNotices()
    {
        var notices = _startupNotices.ToList();
        _startupNotices.Clear();
        return notices;
    }

    public void AddRun(ScheduledPromptTask task, ScheduledPromptRun run)
    {
        task.Runs.Insert(0, run);
        PruneRuns(task);
    }

    public static void AdvanceAfterRun(ScheduledPromptTask task, DateTime now)
    {
        task.LastRunAt = now;
        task.ConsecutiveFailures = 0;
        task.RetryAt = null;

        if (task.Recurrence == ScheduledTaskRecurrence.Once)
        {
            task.IsEnabled = false;
            return;
        }

        var anchor = GetAnchor(task, now);
        task.ScheduleAnchorAt = anchor;
        var after = task.NextRunAt > now ? task.NextRunAt : now;
        task.NextRunAt = ScheduleMath.NextOccurrenceAfter(task.Recurrence, anchor, after);
    }

    /// <summary>
    /// A failed scheduled run keeps its slot (a Once task stays enabled) and is retried after a
    /// back-off. A streak of failures keeps a single error entry in the run history, so retries
    /// do not push earlier successful results out of it.
    /// </summary>
    public void RecordFailedAttempt(ScheduledPromptTask task, ScheduledPromptRun run, DateTime now)
    {
        if (task.ConsecutiveFailures > 0
            && task.Runs.Count > 0
            && !string.IsNullOrWhiteSpace(task.Runs[0].Error))
        {
            TryDeleteFile(task.Runs[0].AudioPath);
            task.Runs[0] = run;
        }
        else
        {
            AddRun(task, run);
        }

        task.ConsecutiveFailures++;
        task.RetryAt = now + ScheduleMath.RetryDelay(task.ConsecutiveFailures);
        task.LastStatus = $"Error: {run.Error.TrimEnd('.', ' ')}. Retrying at {task.RetryAt:t} (attempt {task.ConsecutiveFailures + 1}).";
    }

    /// <summary>
    /// Catch-up policy for slots that passed while the app was closed: if the most recent missed
    /// slot is less than <see cref="ScheduleMath.CatchUpWindow"/> old, the task runs once at startup;
    /// otherwise the run is skipped and a recurring task moves to its next slot (a Once task is
    /// turned off).
    /// </summary>
    public void ApplyStartupCatchUp(DateTime now)
    {
        foreach (var task in Tasks)
        {
            if (!task.IsEnabled || string.IsNullOrWhiteSpace(task.Prompt))
                continue;

            var anchor = GetAnchor(task, now);
            var plan = ScheduleMath.PlanStartupCatchUp(task.Recurrence, anchor, task.NextRunAt, now, ScheduleMath.CatchUpWindow);
            switch (plan.Action)
            {
                case MissedRunAction.RunNow:
                    task.NextRunAt = plan.NextRunAt;
                    task.RetryAt = null;
                    _startupNotices.Add($"'{task.Name}' was due at {plan.MissedAt:g} while the app was closed; running it once now.");
                    break;

                case MissedRunAction.Skip:
                    var hours = ScheduleMath.CatchUpWindow.TotalHours;
                    task.ConsecutiveFailures = 0;
                    task.RetryAt = null;
                    if (task.Recurrence == ScheduledTaskRecurrence.Once)
                    {
                        task.IsEnabled = false;
                        task.LastStatus = $"Missed: due {plan.MissedAt:g} while the app was closed. More than {hours:0} hours late, so it was not run.";
                        _startupNotices.Add($"'{task.Name}' was due {plan.MissedAt:g} while the app was closed. It is more than {hours:0} hours late, so it was not run and has been turned off.");
                    }
                    else
                    {
                        task.ScheduleAnchorAt = anchor;
                        task.NextRunAt = plan.NextRunAt;
                        task.LastStatus = $"Skipped the {plan.MissedAt:g} run (the app was closed for more than {hours:0} hours after it).";
                        _startupNotices.Add($"Skipped '{task.Name}' due {plan.MissedAt:g}: the app was closed for more than {hours:0} hours after it. Next run {plan.NextRunAt:g}.");
                    }

                    break;
            }
        }
    }

    public static void PruneRuns(ScheduledPromptTask task)
    {
        var keep = Math.Clamp(task.KeepRuns, 1, 100);
        task.KeepRuns = keep;
        if (task.Runs.Count <= keep)
            return;

        var kept = task.Runs
            .OrderByDescending(r => r.CompletedAt)
            .Take(keep)
            .ToList();
        var keptIds = kept.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var removed in task.Runs.Where(r => !keptIds.Contains(r.Id)))
        {
            TryDeleteFile(removed.AudioPath);
        }

        task.Runs = kept;
    }

    private static void Normalize(ScheduledPromptTask task)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
            task.Id = Guid.NewGuid().ToString("N");
        if (string.IsNullOrWhiteSpace(task.Name))
            task.Name = "Scheduled prompt";
        task.KeepRuns = Math.Clamp(task.KeepRuns <= 0 ? 5 : task.KeepRuns, 1, 100);
        task.ConsecutiveFailures = Math.Max(0, task.ConsecutiveFailures);
        task.Runs ??= new List<ScheduledPromptRun>();
        PruneRuns(task);
    }

    private static DateTime GetAnchor(ScheduledPromptTask task, DateTime now)
    {
        var anchor = task.ScheduleAnchorAt ?? task.NextRunAt;
        return anchor <= DateTime.MinValue.AddDays(1) ? now : anchor;
    }

    private static bool TryBackupUnreadableFile()
    {
        try
        {
            File.Copy(StorePath, BackupPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Retention cleanup is best effort.
        }
    }
}
