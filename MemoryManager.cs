using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoiceChatbot;

public class ConversationMemory
{
    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("summary")]
    public string Summary { get; set; } = "";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messageCount")]
    public int MessageCount { get; set; }

    [JsonPropertyName("durationMinutes")]
    public double DurationMinutes { get; set; }

    public override string ToString() => $"[{Timestamp}] {Summary[..Math.Min(Summary.Length, 80)]}...";
}

public static class MemoryManager
{
    private static readonly string MemoryDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceChatbot", "memory");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string GetMemoryDir() => MemoryDir;

    /// <summary>
    /// Save a conversation summary to disk. Auto-prunes to keep only the most recent MaxMemories.
    /// </summary>
    public static void Save(ConversationMemory memory, int maxMemories = 10)
    {
        Directory.CreateDirectory(MemoryDir);

        var filename = $"mem_{DateTime.Now:yyyyMMdd_HHmmss}.json";
        var path = Path.Combine(MemoryDir, filename);
        var json = JsonSerializer.Serialize(memory, JsonOpts);
        File.WriteAllText(path, json);

        // Prune old ones
        var files = GetMemoryFiles();
        if (files.Count > maxMemories)
        {
            // Delete oldest (files are sorted by name which = sorted by time)
            for (int i = 0; i < files.Count - maxMemories; i++)
            {
                try { File.Delete(files[i]); } catch { }
            }
        }
    }

    /// <summary>
    /// Load all stored conversation memories, oldest first.
    /// </summary>
    public static List<ConversationMemory> LoadAll()
    {
        var result = new List<ConversationMemory>();
        if (!Directory.Exists(MemoryDir)) return result;

        foreach (var file in GetMemoryFiles())
        {
            try
            {
                var json = File.ReadAllText(file);
                var mem = JsonSerializer.Deserialize<ConversationMemory>(json);
                if (mem != null) result.Add(mem);
            }
            catch { }
        }
        return result;
    }

    /// <summary>
    /// Delete a specific memory by timestamp.
    /// </summary>
    public static void Delete(string timestamp)
    {
        if (!Directory.Exists(MemoryDir)) return;
        foreach (var file in GetMemoryFiles())
        {
            try
            {
                var json = File.ReadAllText(file);
                var mem = JsonSerializer.Deserialize<ConversationMemory>(json);
                if (mem?.Timestamp == timestamp)
                {
                    File.Delete(file);
                    return;
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Update the summary text for a specific memory by timestamp.
    /// </summary>
    public static bool UpdateSummary(string timestamp, string summary)
    {
        if (!Directory.Exists(MemoryDir)) return false;
        foreach (var file in GetMemoryFiles())
        {
            try
            {
                var json = File.ReadAllText(file);
                var mem = JsonSerializer.Deserialize<ConversationMemory>(json);
                if (mem?.Timestamp != timestamp)
                    continue;

                mem.Summary = summary.Trim();
                File.WriteAllText(file, JsonSerializer.Serialize(mem, JsonOpts));
                return true;
            }
            catch { }
        }

        return false;
    }

    /// <summary>
    /// Delete all memories.
    /// </summary>
    public static void ClearAll()
    {
        if (!Directory.Exists(MemoryDir)) return;
        foreach (var file in GetMemoryFiles())
        {
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>
    /// Format all memories into a block suitable for injection into the system prompt.
    /// </summary>
    public static string FormatForSystemPrompt(List<ConversationMemory> memories)
    {
        if (memories.Count == 0) return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("[Previous conversations - summarized memories]");
        foreach (var mem in memories)
        {
            var date = mem.Timestamp.Length >= 10 ? mem.Timestamp[..10] : mem.Timestamp;
            sb.AppendLine($"- {date}: {mem.Summary}");
        }
        sb.AppendLine("[End of previous conversations]");
        return sb.ToString();
    }

    private static List<string> GetMemoryFiles()
    {
        if (!Directory.Exists(MemoryDir)) return new List<string>();
        return Directory.GetFiles(MemoryDir, "mem_*.json")
            .OrderBy(f => f)
            .ToList();
    }
}
