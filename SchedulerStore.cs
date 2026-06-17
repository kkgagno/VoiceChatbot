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

    public List<ScheduledPromptTask> Tasks { get; set; } = new();

    public static SchedulerStore Load()
    {
        try
        {
            if (!File.Exists(StorePath))
                return new SchedulerStore();

            var json = File.ReadAllText(StorePath);
            var store = JsonSerializer.Deserialize<SchedulerStore>(json, JsonOptions) ?? new SchedulerStore();
            foreach (var task in store.Tasks)
                Normalize(task);
            return store;
        }
        catch
        {
            return new SchedulerStore();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(StorePath);
        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        foreach (var task in Tasks)
            Normalize(task);

        File.WriteAllText(StorePath, JsonSerializer.Serialize(this, JsonOptions));
    }

    public void AddRun(ScheduledPromptTask task, ScheduledPromptRun run)
    {
        task.Runs.Insert(0, run);
        PruneRuns(task);
    }

    public static void AdvanceAfterRun(ScheduledPromptTask task, DateTime now)
    {
        task.LastRunAt = now;

        if (task.Recurrence == ScheduledTaskRecurrence.Once)
        {
            task.IsEnabled = false;
            return;
        }

        var next = task.NextRunAt;
        if (next <= DateTime.MinValue.AddDays(1))
            next = now;

        do
        {
            next = task.Recurrence switch
            {
                ScheduledTaskRecurrence.Hourly => next.AddHours(1),
                ScheduledTaskRecurrence.Daily => next.AddDays(1),
                ScheduledTaskRecurrence.Weekly => next.AddDays(7),
                ScheduledTaskRecurrence.Monthly => next.AddMonths(1),
                _ => now
            };
        } while (next <= now);

        task.NextRunAt = next;
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
        task.Runs ??= new List<ScheduledPromptRun>();
        PruneRuns(task);
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
