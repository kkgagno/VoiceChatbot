using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace VoiceChatbot;

public static class RecentChatStore
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VoiceChatbot", "recent-chat.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static List<ChatMessage> Load(int maxMessages)
    {
        try
        {
            if (!File.Exists(Path))
                return new List<ChatMessage>();

            var json = File.ReadAllText(Path);
            var messages = JsonSerializer.Deserialize<List<StoredChatMessage>>(json, JsonOptions) ??
                           new List<StoredChatMessage>();

            return messages
                .Where(m => IsConversationRole(m.Role) && !string.IsNullOrWhiteSpace(m.Content))
                .TakeLast(Math.Max(2, maxMessages))
                .Select(m => new ChatMessage
                {
                    Role = m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
                    Content = m.Content,
                    Timestamp = m.Timestamp == default ? DateTime.Now : m.Timestamp,
                    ImagesBase64 = new List<string>()
                })
                .ToList();
        }
        catch
        {
            return new List<ChatMessage>();
        }
    }

    public static void Save(IEnumerable<ChatMessage> messages, int maxMessages)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var stored = messages
                .Where(m => IsConversationRole(m.Role) && !string.IsNullOrWhiteSpace(m.Content))
                .TakeLast(Math.Max(2, maxMessages))
                .Select(m => new StoredChatMessage
                {
                    Role = m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
                    Content = m.Content,
                    Timestamp = m.Timestamp == default ? DateTime.Now : m.Timestamp
                })
                .ToList();

            File.WriteAllText(Path, JsonSerializer.Serialize(stored, JsonOptions));
        }
        catch
        {
            // Recent chat persistence should never break the live assistant.
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
        catch
        {
            // Best effort only.
        }
    }

    private static bool IsConversationRole(string role)
    {
        return role.Equals("user", StringComparison.OrdinalIgnoreCase) ||
               role.Equals("assistant", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StoredChatMessage
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
        public DateTime Timestamp { get; set; } = DateTime.Now;
    }
}
