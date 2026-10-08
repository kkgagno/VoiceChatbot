using System.Globalization;
using System.Text;
using VoiceChatbot;
using Xunit;

public sealed class ConversationStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vc-conversations-" + Guid.NewGuid().ToString("N"));
    private readonly ConversationStore _store;

    public ConversationStoreTests() => _store = new ConversationStore(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private StoredConversation SaveConversation(string firstUserMessage, DateTime updatedUtc, params string[] replies)
    {
        var conversation = ConversationStore.Create(updatedUtc, model: "llama3");
        conversation.AddMessage("user", firstUserMessage, updatedUtc);
        foreach (var reply in replies)
            conversation.AddMessage("assistant", reply, updatedUtc);
        _store.Save(conversation);
        return conversation;
    }

    [Fact]
    public void SaveAndLoad_RoundTripsMessages()
    {
        var conversation = ConversationStore.Create(T0, model: "gemma3", persona: "Coach");
        conversation.AddMessage("user", "Look at this photo", T0, new[] { @"C:\pics\a.png", " " });
        var reply = conversation.AddMessage("assistant", "```python\nprint(1)\n```", T0.AddSeconds(5));
        reply.AudioPath = @"C:\audio\reply.wav";
        _store.Save(conversation);

        var loaded = _store.Load(conversation.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Look at this photo", loaded!.Title);
        Assert.Equal("gemma3", loaded.Model);
        Assert.Equal("Coach", loaded.Persona);
        Assert.Equal(T0, loaded.CreatedUtc);
        Assert.Equal(T0.AddSeconds(5), loaded.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.UpdatedUtc.Kind);
        Assert.Equal(2, loaded.Messages.Count);
        Assert.Equal(new[] { @"C:\pics\a.png" }, loaded.Messages[0].ImagePaths);
        Assert.Null(loaded.Messages[0].AudioPath);
        Assert.Equal("assistant", loaded.Messages[1].Role);
        Assert.Equal("```python\nprint(1)\n```", loaded.Messages[1].Content);
        Assert.Equal(@"C:\audio\reply.wav", loaded.Messages[1].AudioPath);
    }

    [Fact]
    public void Save_OverwritesAtomicallyAndLeavesNoTempFiles()
    {
        var conversation = SaveConversation("hello", T0);
        conversation.AddMessage("assistant", "hi there", T0.AddMinutes(1));
        _store.Save(conversation);

        Assert.Equal(2, _store.Load(conversation.Id)!.Messages.Count);
        Assert.Equal(new[] { conversation.Id + ".json" }, Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void List_ReturnsSummariesNewestFirst()
    {
        var older = SaveConversation("older chat", T0.AddDays(-2), "reply");
        var newest = SaveConversation("newest chat", T0, "a", "b");
        var middle = SaveConversation("middle chat", T0.AddHours(-3));

        var list = _store.List();

        Assert.Equal(new[] { newest.Id, middle.Id, older.Id }, list.Select(s => s.Id));
        Assert.Equal("newest chat", list[0].Title);
        Assert.Equal(3, list[0].MessageCount);
        Assert.Equal(T0, list[0].UpdatedUtc);
        Assert.Equal("llama3", list[0].Model);
    }

    [Fact]
    public void List_ReadsLargeConversationsFromTheHeader()
    {
        var big = new string('x', 50_000);
        var conversation = SaveConversation("big one", T0, big, big);

        var summary = Assert.Single(_store.List());

        Assert.Equal(conversation.Id, summary.Id);
        Assert.Equal(3, summary.MessageCount);
        Assert.Equal("big one", summary.Title);
    }

    [Fact]
    public void List_ReadsTheHeaderWithoutParsingMessages()
    {
        var conversation = SaveConversation("header only", T0, new string('y', 40_000));
        var path = Path.Combine(_folder, conversation.Id + ".json");
        var json = File.ReadAllText(path);
        File.WriteAllText(path, json[..(json.Length - 200)]); // damage the end of the messages

        var summary = Assert.Single(_store.List());

        Assert.Equal("header only", summary.Title);
        Assert.Equal(2, summary.MessageCount);
        Assert.Null(_store.Load(conversation.Id));
    }

    [Fact]
    public void List_FallsBackToFullParseForUnusualLayouts()
    {
        Directory.CreateDirectory(_folder);
        // Messages first, no MessageCount, with a UTF-8 BOM - as a hand-edited file might be.
        var json = "{\"Messages\":[{\"Role\":\"user\",\"Content\":\"hi\",\"TimestampUtc\":\"2026-10-08T10:00:00Z\"}]," +
                   "\"Title\":\"Edited\",\"UpdatedUtc\":\"2026-10-08T10:00:00Z\"}";
        File.WriteAllText(Path.Combine(_folder, "hand-edited.json"), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var summary = Assert.Single(_store.List());

        Assert.Equal("hand-edited", summary.Id);
        Assert.Equal("Edited", summary.Title);
        Assert.Equal(1, summary.MessageCount);
    }

    [Fact]
    public void CorruptAndForeignFilesAreSkipped()
    {
        var good = SaveConversation("good", T0);
        File.WriteAllText(Path.Combine(_folder, "broken.json"), "{ \"Title\": \"half writ");
        File.WriteAllText(Path.Combine(_folder, "garbage.json"), "not json at all");
        File.WriteAllText(Path.Combine(_folder, "bad name!.json"), "{}");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "hello");

        Assert.Equal(new[] { good.Id }, _store.List().Select(s => s.Id));
        Assert.Null(_store.Load("broken"));
        Assert.Null(_store.Load("garbage"));
        Assert.Single(_store.Search("good"));
    }

    [Fact]
    public void List_IsEmptyWhenFolderMissing() => Assert.Empty(_store.List());

    [Theory]
    [InlineData("")]
    [InlineData("missing")]
    [InlineData("../settings")]
    [InlineData(@"..\settings")]
    public void Load_ReturnsNullForUnknownOrUnsafeIds(string id) => Assert.Null(_store.Load(id));

    [Fact]
    public void Delete_RemovesTheFile()
    {
        var conversation = SaveConversation("to delete", T0);

        Assert.True(_store.Delete(conversation.Id));
        Assert.False(_store.Delete(conversation.Id));
        Assert.Null(_store.Load(conversation.Id));
        Assert.Empty(_store.List());
    }

    [Fact]
    public void Rename_ChangesTitleButKeepsUpdatedTime()
    {
        var conversation = SaveConversation("original title", T0);

        Assert.True(_store.Rename(conversation.Id, "  Trip   planning \n"));
        Assert.False(_store.Rename(conversation.Id, "   "));
        Assert.False(_store.Rename("missing", "x"));

        var loaded = _store.Load(conversation.Id)!;
        Assert.Equal("Trip planning", loaded.Title);
        Assert.Equal(T0, loaded.UpdatedUtc);
    }

    [Fact]
    public void Search_MatchesTitlesAndMessageTextCaseInsensitively()
    {
        var weather = SaveConversation("What's the weather in Boston?", T0.AddHours(-1), "It is sunny and 72 degrees.");
        var code = SaveConversation("Write a PowerShell script", T0, "Here is the SCRIPT you asked for.");

        Assert.Equal(new[] { weather.Id }, _store.Search("boston").Select(s => s.Id));
        Assert.Equal(new[] { weather.Id }, _store.Search("SUNNY").Select(s => s.Id));
        Assert.Equal(new[] { code.Id, weather.Id }, _store.Search("  ").Select(s => s.Id));
        Assert.Equal(new[] { code.Id }, _store.Search("script asked").Select(s => s.Id));
        Assert.Empty(_store.Search("script boston"));
        Assert.Empty(_store.Search("tokyo"));
    }

    [Fact]
    public void Search_ReturnsSnippetAroundTheMatch()
    {
        var filler = string.Join(" ", Enumerable.Repeat("lorem", 60));
        SaveConversation("notes", T0, $"{filler} the secret ingredient is cardamom {filler}");

        var result = Assert.Single(_store.Search("cardamom"));

        Assert.Contains("cardamom", result.Snippet);
        Assert.StartsWith("…", result.Snippet);
        Assert.EndsWith("…", result.Snippet);
        Assert.True(result.Snippet.Length < 160);
    }

    [Fact]
    public void Search_HonoursCancellation()
    {
        SaveConversation("hello", T0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => _store.Search("hello", cts.Token));
    }

    [Fact]
    public void AddMessage_TitlesFromFirstUserMessageOnly()
    {
        var conversation = ConversationStore.Create(T0);
        Assert.Equal(StoredConversation.DefaultTitle, conversation.DisplayTitle);

        conversation.AddMessage("assistant", "Good morning!", T0);
        Assert.Equal("", conversation.Title);

        conversation.AddMessage("USER", "  Plan a\n trip to Rome ", T0.AddMinutes(1));
        conversation.AddMessage("user", "Second question", T0.AddMinutes(2));

        Assert.Equal("Plan a trip to Rome", conversation.Title);
        Assert.Equal("user", conversation.Messages[1].Role);
        Assert.Equal(T0, conversation.CreatedUtc);
        Assert.Equal(T0.AddMinutes(2), conversation.UpdatedUtc);
    }

    [Fact]
    public void Clone_IsIndependent()
    {
        var conversation = ConversationStore.Create(T0);
        conversation.AddMessage("user", "hi", T0, new[] { "a.png" });
        var clone = conversation.Clone();

        conversation.AddMessage("assistant", "hello", T0);
        conversation.Messages[0].ImagePaths.Add("b.png");
        conversation.Title = "changed";

        Assert.Single(clone.Messages);
        Assert.Equal(new[] { "a.png" }, clone.Messages[0].ImagePaths);
        Assert.Equal("hi", clone.Title);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("Hello there", "Hello there")]
    [InlineData("line one\r\n\tline two", "line one line two")]
    public void MakeTitle_NormalizesWhitespace(string? input, string expected) =>
        Assert.Equal(expected, ConversationStore.MakeTitle(input));

    [Fact]
    public void MakeTitle_TrimsLongTextAtAWordBoundary()
    {
        var title = ConversationStore.MakeTitle(
            "Can you help me write a detailed packing list for a two week hiking trip in the Alps, please?");

        Assert.Equal("Can you help me write a detailed packing list for a two week…", title);
        Assert.True(title.Length <= ConversationStore.TitleMaxChars + 1);
    }

    [Fact]
    public void MakeTitle_CutsUnbrokenText()
    {
        var title = ConversationStore.MakeTitle(new string('a', 100));

        Assert.Equal(new string('a', 60) + "…", title);
    }

    [Fact]
    public void NewId_IsSortableAndValid()
    {
        var id = ConversationStore.NewId(T0);

        Assert.StartsWith("20261008-120000-", id);
        Assert.True(ConversationStore.IsValidId(id));
        Assert.NotEqual(id, ConversationStore.NewId(T0));
    }

    [Fact]
    public void NewId_IgnoresCurrentCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.StartsWith("20261008-", ConversationStore.NewId(T0));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
