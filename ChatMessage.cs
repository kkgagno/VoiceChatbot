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
        _messages.Add(new ChatMessage
        {
            Role = role,
            Content = NormalizeStoredContent(content),
            ImagesBase64 = imagesBase64?.Where(i => !string.IsNullOrWhiteSpace(i)).ToList() ?? new List<string>()
        });
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
