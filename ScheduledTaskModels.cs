using System;
using System.Collections.Generic;

namespace VoiceChatbot;

public enum ScheduledTaskRecurrence
{
    Once,
    Hourly,
    Daily,
    Weekly,
    Monthly
}

public sealed class ScheduledPromptTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Scheduled prompt";
    public string Prompt { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public DateTime NextRunAt { get; set; } = DateTime.Now.AddHours(1);
    public DateTime? LastRunAt { get; set; }
    public ScheduledTaskRecurrence Recurrence { get; set; } = ScheduledTaskRecurrence.Once;
    public int KeepRuns { get; set; } = 5;
    public string LastStatus { get; set; } = "Pending";
    public List<ScheduledPromptRun> Runs { get; set; } = new();

    public override string ToString()
    {
        var enabled = IsEnabled ? "On" : "Off";
        return $"{Name} - {enabled} - next {NextRunAt:g}";
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
