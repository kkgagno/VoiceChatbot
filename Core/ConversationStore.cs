using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace VoiceChatbot;

/// <summary>One saved chat, stored as {Id}.json in the conversations folder.</summary>
public sealed class StoredConversation
{
    public const string DefaultTitle = "New conversation";

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string Persona { get; set; } = "";
    public string Model { get; set; } = "";

    // Written ahead of Messages so ConversationStore.List() can read it from the start of the
    // file without parsing every message. Ignored when reading.
    public int MessageCount => Messages.Count;

    [JsonPropertyOrder(1)]
    public List<StoredMessage> Messages { get; set; } = new();

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? DefaultTitle : Title;

    public static bool IsConversationRole(string? role) =>
        string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Appends a user/assistant message, bumps UpdatedUtc and titles the conversation from the
    /// first user message.
    /// </summary>
    public StoredMessage AddMessage(string role, string content, DateTime utcNow, IEnumerable<string>? imagePaths = null)
    {
        var message = new StoredMessage
        {
            Role = string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "assistant",
            Content = content ?? "",
            TimestampUtc = utcNow,
            ImagePaths = imagePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>()
        };
        Messages.Add(message);
        UpdatedUtc = utcNow;
        if (CreatedUtc == default)
            CreatedUtc = utcNow;

        if (message.Role == "user" && string.IsNullOrWhiteSpace(Title))
        {
            var title = ConversationStore.MakeTitle(message.Content);
            if (title.Length > 0)
                Title = title;
        }

        return message;
    }

    /// <summary>Copy that can be serialized on a worker thread while the original keeps changing.</summary>
    public StoredConversation Clone() => new()
    {
        Id = Id,
        Title = Title,
        CreatedUtc = CreatedUtc,
        UpdatedUtc = UpdatedUtc,
        Persona = Persona,
        Model = Model,
        Messages = Messages.Select(m => m.Clone()).ToList()
    };
}

public sealed class StoredMessage
{
    /// <summary>"user" or "assistant".</summary>
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
    public DateTime TimestampUtc { get; set; }
    public List<string> ImagePaths { get; set; } = new();
    public string? AudioPath { get; set; }

    public StoredMessage Clone() => new()
    {
        Role = Role,
        Content = Content,
        TimestampUtc = TimestampUtc,
        ImagePaths = ImagePaths.ToList(),
        AudioPath = AudioPath
    };
}

/// <summary>Lightweight list entry: everything except the messages.</summary>
public sealed class ConversationSummary
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public string Persona { get; init; } = "";
    public string Model { get; init; } = "";
    public int MessageCount { get; init; }

    /// <summary>Search only: the text around the first match, or "" when only the title matched.</summary>
    public string Snippet { get; init; } = "";

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? StoredConversation.DefaultTitle : Title;
}

/// <summary>
/// Saved chat history: one JSON file per conversation in a folder. Writes are atomic (temp file
/// then replace), unreadable files are skipped, and nothing here touches WPF.
/// </summary>
public sealed class ConversationStore
{
    public const int TitleMaxChars = 60;
    private const int HeaderReadBytes = 16 * 1024;
    private const int SnippetChars = 140;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly Regex IdPattern = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly object _writeLock = new();

    public ConversationStore(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            throw new ArgumentException("A folder is required.", nameof(folder));

        Folder = folder;
    }

    public string Folder { get; }

    public static string DefaultFolder => AppPaths.DataPath("conversations");

    /// <summary>Sortable, file-name-safe id such as 20261008-142233-1a2b3c4d.</summary>
    public static string NewId(DateTime utcNow) =>
        utcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8];

    public static StoredConversation Create(DateTime utcNow, string model = "", string persona = "") => new()
    {
        Id = NewId(utcNow),
        CreatedUtc = utcNow,
        UpdatedUtc = utcNow,
        Model = model ?? "",
        Persona = persona ?? ""
    };

    public static bool IsValidId(string? id) => id != null && IdPattern.IsMatch(id);

    /// <summary>Single-line title from a user message, cut at a word boundary to about 60 characters.</summary>
    public static string MakeTitle(string? text)
    {
        var line = Whitespace.Replace(text ?? "", " ").Trim();
        if (line.Length <= TitleMaxChars)
            return line;

        var cut = line.LastIndexOf(' ', TitleMaxChars);
        if (cut < TitleMaxChars / 2)
            cut = TitleMaxChars;

        return line[..cut].TrimEnd(' ', ',', '.', ';', ':', '-') + "…";
    }

    public IReadOnlyList<ConversationSummary> List()
    {
        var summaries = new List<ConversationSummary>();
        foreach (var path in EnumerateConversationFiles())
        {
            var summary = ReadSummary(path);
            if (summary != null)
                summaries.Add(summary);
        }

        return SortNewestFirst(summaries);
    }

    /// <summary>Returns null when the conversation does not exist or the file is damaged.</summary>
    public StoredConversation? Load(string id)
    {
        if (!IsValidId(id))
            return null;

        return ReadConversation(PathFor(id));
    }

    public void Save(StoredConversation conversation)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (string.IsNullOrEmpty(conversation.Id))
            conversation.Id = NewId(DateTime.UtcNow);
        if (!IsValidId(conversation.Id))
            throw new ArgumentException($"Invalid conversation id '{conversation.Id}'.", nameof(conversation));

        var bytes = JsonSerializer.SerializeToUtf8Bytes(conversation, JsonOptions);
        var path = PathFor(conversation.Id);
        lock (_writeLock)
        {
            Directory.CreateDirectory(Folder);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temp, path, null, ignoreMetadataErrors: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
                    {
                        // Replace can fail on some file systems or when another process briefly holds
                        // the file; a rename over the old file is still atomic.
                        File.Move(temp, path, overwrite: true);
                    }
                }
                else
                {
                    File.Move(temp, path, overwrite: true);
                }
            }
            finally
            {
                TryDelete(temp);
            }
        }
    }

    public bool Delete(string id)
    {
        if (!IsValidId(id))
            return false;

        var path = PathFor(id);
        lock (_writeLock)
        {
            if (!File.Exists(path))
                return false;

            File.Delete(path);
            return true;
        }
    }

    /// <summary>Changes only the title; UpdatedUtc is kept so the list order does not jump.</summary>
    public bool Rename(string id, string newTitle)
    {
        var title = Whitespace.Replace(newTitle ?? "", " ").Trim();
        if (title.Length == 0)
            return false;

        var conversation = Load(id);
        if (conversation == null)
            return false;

        conversation.Title = title;
        Save(conversation);
        return true;
    }

    /// <summary>
    /// Case-insensitive search over titles and message text. Every word of the query must appear
    /// somewhere in the conversation. A blank query returns List().
    /// </summary>
    public IReadOnlyList<ConversationSummary> Search(string? query, CancellationToken ct = default)
    {
        var terms = Whitespace.Split(query?.Trim() ?? "").Where(t => t.Length > 0).ToArray();
        if (terms.Length == 0)
            return List();

        var results = new List<ConversationSummary>();
        foreach (var path in EnumerateConversationFiles())
        {
            ct.ThrowIfCancellationRequested();
            var conversation = ReadConversation(path);
            if (conversation == null || !MatchesAll(conversation, terms))
                continue;

            results.Add(ToSummary(conversation, IdFromPath(path), FindSnippet(conversation, terms)));
        }

        return SortNewestFirst(results);
    }

    private string PathFor(string id) => Path.Combine(Folder, id + ".json");

    private static string IdFromPath(string path) => Path.GetFileNameWithoutExtension(path);

    private IEnumerable<string> EnumerateConversationFiles()
    {
        try
        {
            if (!Directory.Exists(Folder))
                return Array.Empty<string>();

            return Directory.GetFiles(Folder, "*.json").Where(p => IsValidId(IdFromPath(p)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static List<ConversationSummary> SortNewestFirst(IEnumerable<ConversationSummary> summaries) =>
        summaries
            .OrderByDescending(s => s.UpdatedUtc)
            .ThenByDescending(s => s.Id, StringComparer.Ordinal)
            .ToList();

    private static StoredConversation? ReadConversation(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var bytes = File.ReadAllBytes(path);
            var json = bytes.AsSpan();
            if (json.StartsWith("\uFEFF"u8))
                json = json[3..];

            var conversation = JsonSerializer.Deserialize<StoredConversation>(json, JsonOptions);
            if (conversation == null)
                return null;

            // The file name is the identity; a copied or hand-edited file may disagree.
            conversation.Id = IdFromPath(path);
            conversation.Title ??= "";
            conversation.Persona ??= "";
            conversation.Model ??= "";
            conversation.CreatedUtc = AsUtc(conversation.CreatedUtc);
            conversation.UpdatedUtc = AsUtc(conversation.UpdatedUtc);
            conversation.Messages = (conversation.Messages ?? new List<StoredMessage>())
                .Where(m => m != null && StoredConversation.IsConversationRole(m.Role))
                .ToList();
            foreach (var message in conversation.Messages)
            {
                message.Role = message.Role.ToLowerInvariant();
                message.Content ??= "";
                message.TimestampUtc = AsUtc(message.TimestampUtc);
                message.ImagePaths = message.ImagePaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList() ?? new List<string>();
            }

            return conversation;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static ConversationSummary? ReadSummary(string path)
    {
        try
        {
            var header = TryReadHeader(path);
            if (header != null)
                return header;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Fall through to a full read, which decides whether the file is usable.
        }

        var conversation = ReadConversation(path);
        return conversation == null ? null : ToSummary(conversation, IdFromPath(path), "");
    }

    /// <summary>
    /// Reads the top-level fields that Save writes before "Messages" from the first few KB of the
    /// file. Returns null when the layout is unexpected so the caller can parse the whole file.
    /// </summary>
    private static ConversationSummary? TryReadHeader(string path)
    {
        var buffer = new byte[HeaderReadBytes];
        int length;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            length = 0;
            int read;
            while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
                length += read;
        }

        var span = buffer.AsSpan(0, length);
        if (span.StartsWith("\uFEFF"u8))
            span = span[3..];

        var reader = new Utf8JsonReader(span, isFinalBlock: length < buffer.Length, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return null;

        string title = "", persona = "", model = "";
        DateTime created = default, updated = default;
        int? count = null;
        var sawMessages = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
                break;
            if (reader.TokenType != JsonTokenType.PropertyName)
                return null;

            var name = reader.GetString() ?? "";
            if (name.Equals("Messages", StringComparison.OrdinalIgnoreCase))
            {
                sawMessages = true;
                break;
            }

            if (!reader.Read())
                return null;

            switch (name.ToLowerInvariant())
            {
                case "title":
                    title = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
                    break;
                case "persona":
                    persona = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
                    break;
                case "model":
                    model = reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : "";
                    break;
                case "createdutc":
                    if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTime(out var c))
                        created = c;
                    break;
                case "updatedutc":
                    if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTime(out var u))
                        updated = u;
                    break;
                case "messagecount":
                    if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n))
                        count = n;
                    break;
                default:
                    if ((reader.TokenType == JsonTokenType.StartObject || reader.TokenType == JsonTokenType.StartArray) &&
                        !reader.TrySkip())
                        return null;
                    break;
            }
        }

        // Without both a count and the start of Messages the header is not trustworthy.
        if (!sawMessages || count == null || updated == default)
            return null;

        return new ConversationSummary
        {
            Id = IdFromPath(path),
            Title = title,
            CreatedUtc = AsUtc(created),
            UpdatedUtc = AsUtc(updated),
            Persona = persona,
            Model = model,
            MessageCount = count.Value
        };
    }

    private static ConversationSummary ToSummary(StoredConversation conversation, string id, string snippet) => new()
    {
        Id = id,
        Title = conversation.Title,
        CreatedUtc = conversation.CreatedUtc,
        UpdatedUtc = conversation.UpdatedUtc,
        Persona = conversation.Persona,
        Model = conversation.Model,
        MessageCount = conversation.Messages.Count,
        Snippet = snippet
    };

    private static bool MatchesAll(StoredConversation conversation, IEnumerable<string> terms) =>
        terms.All(term =>
            conversation.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            conversation.Messages.Any(m => m.Content.Contains(term, StringComparison.OrdinalIgnoreCase)));

    private static string FindSnippet(StoredConversation conversation, IReadOnlyList<string> terms)
    {
        foreach (var term in terms)
        {
            foreach (var message in conversation.Messages)
            {
                var index = message.Content.IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                    return MakeSnippet(message.Content, index);
            }
        }

        return "";
    }

    private static string MakeSnippet(string content, int matchIndex)
    {
        var start = Math.Max(0, matchIndex - SnippetChars / 3);
        var end = Math.Min(content.Length, start + SnippetChars);
        var text = Whitespace.Replace(content[start..end], " ").Trim();
        var builder = new StringBuilder();
        if (start > 0)
            builder.Append('…');
        builder.Append(text);
        if (end < content.Length)
            builder.Append('…');
        return builder.ToString();
    }

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover .tmp file is harmless; List() ignores it.
        }
    }
}
