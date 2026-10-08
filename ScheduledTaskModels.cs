using System;
using System.Collections.Generic;

namespace VoiceChatbot;

public sealed class ScheduledPromptTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Scheduled prompt";
    public string Prompt { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public DateTime NextRunAt { get; set; } = DateTime.Now.AddHours(1);
    public DateTime? LastRunAt { get; set; }
    public ScheduledTaskRecurrence Recurrence { get; set; } = ScheduledTaskRecurrence.Once;
    // First slot of the series (the date and time the user picked). Recurring slots are computed
    // from it, so monthly tasks keep their day of month. Null for tasks saved by older versions.
    public DateTime? ScheduleAnchorAt { get; set; }
    // A failed scheduled run keeps its slot and is retried with back-off instead of being skipped.
    public int ConsecutiveFailures { get; set; }
    public DateTime? RetryAt { get; set; }
    public int KeepRuns { get; set; } = 5;
    public bool ShowInMainChat { get; set; } = false;
    public string LastStatus { get; set; } = "Pending";
    public List<ScheduledPromptRun> Runs { get; set; } = new();

    public override string ToString()
    {
        var enabled = IsEnabled ? "On" : "Off";
        return RetryAt is DateTime retry && retry > NextRunAt
            ? $"{Name} - {enabled} - retry {retry:g}"
            : $"{Name} - {enabled} - next {NextRunAt:g}";
    }
}

public sealed class ScheduledPromptRun
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime CompletedAt { get; set; } = DateTime.Now;
    public string Prompt { get; set; } = "";
    public string ResponseText { get; set; } = "";
    public string AudioPath { get; set; } = "";
    public string Error { get; set; } = "";

    public override string ToString()
    {
        var status = string.IsNullOrWhiteSpace(Error) ? "OK" : "Error";
        return $"{CompletedAt:g} - {status}";
    }
}

public sealed record ScheduledPromptResult(string Text, string AudioPath);
