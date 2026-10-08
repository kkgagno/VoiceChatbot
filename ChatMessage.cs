using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceChatbot;

public class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public List<string> ImagesBase64 { get; set; } = new();
    public DateTime Timestamp { get; set; } = DateTime.Now;

    // Tool calling. All null for ordinary messages, which then serialize exactly as before.
    /// <summary>Calls the model asked for (role "assistant").</summary>
    public List<ToolCall>? ToolCalls { get; set; }
    /// <summary>The call this result answers (role "tool"; OpenAI-compatible servers match on it).</summary>
    public string? ToolCallId { get; set; }
    /// <summary>The tool that produced this result (role "tool"; Ollama matches on it).</summary>
    public string? ToolName { get; set; }

    public bool HasToolData() =>
        ToolCalls is { Count: > 0 } || !string.IsNullOrEmpty(ToolCallId) || !string.IsNullOrEmpty(ToolName);

    public override string ToString()
    {
        var imageNote = ImagesBase64.Count > 0 ? $" [{ImagesBase64.Count} photo(s)]" : "";
        return $"[{Timestamp:HH:mm:ss}] {Role}: {Content}{imageNote}";
    }
}

public sealed class ChatMessageAddedEventArgs : EventArgs
{
    public ChatMessageAddedEventArgs(string role, string content, ChatMessage? message = null)
    {
        Role = role;
        Content = content;
        Message = message;
    }

    public string Role { get; }

    /// <summary>The full text, before the history's own truncation of very large messages.</summary>
    public string Content { get; }

    /// <summary>The entry that was added to the history (so a handler can remove it again).</summary>
    public ChatMessage? Message { get; }
}

public class ConversationHistory
{
    private readonly List<ChatMessage> _messages = new();
    private const int MaxStoredContentChars = 12000;
    private int _maxMessages = 20;

    /// <summary>
    /// Raised after a user or assistant message is added. System/context messages,
    /// InsertBeforeLast and Seed do not raise it.
    /// </summary>
    public event EventHandler<ChatMessageAddedEventArgs>? MessageAdded;

    public int MaxMessages
    {
        get => _maxMessages;
        set => _maxMessages = Math.Max(2, value);
    }

    public void Add(string role, string content)
    {
        Add(role, content, null);
    }

    public void InsertBeforeLast(string role, string content)
    {
        var message = new ChatMessage
        {
            Role = role,
            Content = NormalizeStoredContent(content)
        };

        var index = Math.Max(0, _messages.Count - 1);
        _messages.Insert(index, message);
        Trim();
    }

    public void Add(string role, string content, IEnumerable<string>? imagesBase64)
    {
        var message = new ChatMessage
        {
            Role = role,
            Content = NormalizeStoredContent(content),
            ImagesBase64 = imagesBase64?.Where(i => !string.IsNullOrWhiteSpace(i)).ToList() ?? new List<string>()
        };
        _messages.Add(message);
        Trim();

        if (role.Equals("user", StringComparison.OrdinalIgnoreCase) ||
            role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
        {
            MessageAdded?.Invoke(this, new ChatMessageAddedEventArgs(role, content ?? "", message));
        }
    }

    /// <summary>Replaces the context with earlier messages (e.g. a reopened conversation) without raising MessageAdded.</summary>
    public void Seed(IEnumerable<ChatMessage> messages)
    {
        _messages.Clear();
        foreach (var message in messages)
        {
            _messages.Add(new ChatMessage
            {
                Role = message.Role,
                Content = NormalizeStoredContent(message.Content),
                Timestamp = message.Timestamp
            });
        }
        Trim();
    }

    public void Trim()
    {
        while (_messages.Count > _maxMessages)
            _messages.RemoveAt(0);
    }

    public void RemoveWhere(Predicate<ChatMessage> predicate)
    {
        _messages.RemoveAll(predicate);
    }

    public List<ChatMessage> GetAll() => _messages.ToList();

    public void Clear() => _messages.Clear();

    public string Export()
    {
        return string.Join("\n", _messages.Select(m => m.ToString()));
    }

    private static string NormalizeStoredContent(string content)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= MaxStoredContentChars)
            return content;

        var headLength = MaxStoredContentChars / 2;
        var tailLength = MaxStoredContentChars - headLength;
        return content[..headLength] +
               $"\n\n[Large message truncated in conversation history: original length {content.Length:N0} characters.]\n\n" +
               content[^tailLength..];
    }
}
