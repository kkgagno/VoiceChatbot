using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace VoiceChatbot;

/// <summary>Where a prompt id sits in ComfyUI's /queue response.</summary>
public enum ComfyQueueState
{
    /// <summary>The queue could not be read.</summary>
    Unknown,
    Running,
    Pending,
    /// <summary>Neither running nor pending: finished, failed, deleted, or lost in a restart.</summary>
    Absent
}

/// <summary>Reads ComfyUI's /history status and /queue responses.</summary>
public static class ComfyJobStatus
{
    private const int MaxDetailLength = 600;

    /// <summary>
    /// A readable reason when the /history status of a prompt says it failed (status_str "error",
    /// an execution_error or execution_interrupted message), otherwise null.
    /// </summary>
    public static string? DescribeFailure(JsonNode? status)
    {
        if (status is not JsonObject obj)
            return null;

        string? detail = null;
        if (obj["messages"] is JsonArray messages)
        {
            foreach (var message in messages.OfType<JsonArray>())
            {
                if (message.Count < 2)
                    continue;

                var kind = Text(message[0]);
                var data = message[1] as JsonObject;
                if (kind == "execution_error")
                {
                    detail = DescribeExecutionError(data);
                    break;
                }

                if (kind == "execution_interrupted")
                    detail = DescribeInterrupted(data);
            }
        }

        if (detail is not null)
            return detail;

        return string.Equals(Text(obj["status_str"]), "error", StringComparison.OrdinalIgnoreCase)
            ? "ComfyUI reported an error without details. Check the ComfyUI console."
            : null;
    }

    /// <summary>True when the /history status says the prompt finished successfully.</summary>
    public static bool IsCompleted(JsonNode? status) =>
        status?["completed"] is JsonValue value && value.TryGetValue<bool>(out var completed) && completed;

    /// <summary>Whether <paramref name="promptId"/> is running, pending or gone, from a /queue response.</summary>
    public static ComfyQueueState GetQueueState(JsonNode? queue, string promptId)
    {
        if (queue is not JsonObject obj)
            return ComfyQueueState.Unknown;
        if (obj["queue_running"] is not JsonArray running || obj["queue_pending"] is not JsonArray pending)
            return ComfyQueueState.Unknown;

        if (ContainsPrompt(running, promptId))
            return ComfyQueueState.Running;
        if (ContainsPrompt(pending, promptId))
            return ComfyQueueState.Pending;
        return ComfyQueueState.Absent;
    }

    // Queue items are [number, prompt_id, prompt, extra_data, outputs_to_execute, ...].
    private static bool ContainsPrompt(JsonArray items, string promptId) =>
        items.OfType<JsonArray>().Any(item => item.Count > 1 && Text(item[1]) == promptId);

    private static string DescribeExecutionError(JsonObject? data)
    {
        if (data is null)
            return "ComfyUI reported an execution error without details.";

        var exceptionType = Text(data["exception_type"]);
        var exceptionMessage = Collapse(Text(data["exception_message"]));
        var error = (exceptionType, exceptionMessage) switch
        {
            ("", "") => "unknown error",
            ("", _) => exceptionMessage,
            (_, "") => exceptionType,
            _ => $"{exceptionType}: {exceptionMessage}"
        };

        var node = DescribeNode(data);
        var text = node.Length == 0 ? error : $"{node} failed with {error}";
        return text.Length <= MaxDetailLength ? text : text[..MaxDetailLength] + "...";
    }

    private static string DescribeInterrupted(JsonObject? data)
    {
        var node = data is null ? "" : DescribeNode(data);
        return node.Length == 0
            ? "The job was interrupted."
            : $"The job was interrupted at {node}.";
    }

    private static string DescribeNode(JsonObject data)
    {
        var nodeType = Text(data["node_type"]);
        var nodeId = Text(data["node_id"]);
        if (nodeType.Length > 0 && nodeId.Length > 0)
            return $"{nodeType} (node {nodeId})";
        if (nodeType.Length > 0)
            return nodeType;
        return nodeId.Length > 0 ? $"node {nodeId}" : "";
    }

    private static string Text(JsonNode? node)
    {
        if (node is not JsonValue value)
            return "";
        if (value.TryGetValue<string>(out var text))
            return text.Trim();
        return value.ToJsonString().Trim('"').Trim();
    }

    private static string Collapse(string text) =>
        string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
