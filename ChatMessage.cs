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

    public override string ToString()
    {
        var imageNote = ImagesBase64.Count > 0 ? $" [{ImagesBase64.Count} photo(s)]" : "";
        return $"[{Timestamp:HH:mm:ss}] {Role}: {Content}{imageNote}";
    }
}

public class ConversationHistory
{
    private readonly List<ChatMessage> _messages = new();
    private const int MaxStoredContentChars = 12000;
    private int _maxMessages = 20;
    public event Action? Changed;

    public int MaxMessages
    {
        get => _maxMessages;
        set
        {
            var next = Math.Max(2, value);
            if (_maxMessages == next)
                return;

            _maxMessages = next;
            Trim();
            Changed?.Invoke();
        }
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
        Changed?.Invoke();
    }

    public void Add(string role, string content, IEnumerable<string>? imagesBase64)
    {
        _messages.Add(new ChatMessage
        {
            Role = role,
            Content = NormalizeStoredContent(content),
            ImagesBase64 = imagesBase64?.Where(i => !string.IsNullOrWhiteSpace(i)).ToList() ?? new List<string>()
        });
        Trim();
        Changed?.Invoke();
    }

    public void Trim()
    {
        while (_messages.Count > _maxMessages)
            _messages.RemoveAt(0);
    }

    public void RemoveWhere(Predicate<ChatMessage> predicate)
    {
        if (_messages.RemoveAll(predicate) > 0)
            Changed?.Invoke();
    }

    public List<ChatMessage> GetAll() => _messages.ToList();

    public void Clear()
    {
        if (_messages.Count == 0)
            return;

        _messages.Clear();
        Changed?.Invoke();
    }

    public void ReplaceAll(IEnumerable<ChatMessage> messages)
    {
        _messages.Clear();
        _messages.AddRange(messages
            .Where(m => IsConversationRole(m.Role) && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => new ChatMessage
            {
                Role = m.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
                Content = NormalizeStoredContent(m.Content),
                ImagesBase64 = new List<string>(),
                Timestamp = m.Timestamp == default ? DateTime.Now : m.Timestamp
            }));
        Trim();
        Changed?.Invoke();
    }

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

    private static bool IsConversationRole(string role)
    {
        return role.Equals("user", StringComparison.OrdinalIgnoreCase) ||
               role.Equals("assistant", StringComparison.OrdinalIgnoreCase);
    }
}
