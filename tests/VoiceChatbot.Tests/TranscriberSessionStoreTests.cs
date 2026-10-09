using VoiceChatbot;
using Xunit;

public class TranscriberSessionStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "vc-session-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string StatePath => TranscriberSessionStore.PathIn(_folder);

    [Fact]
    public void SavesAndLoadsTheSession()
    {
        var started = new DateTime(2026, 10, 8, 14, 30, 5);
        var state = new TranscriberSessionState
        {
            Transcript = "[00:00] Hello.\n[00:05] Bye.",
            Notes = "SUMMARY SO FAR\nHi.\n\nNOTES BY TIME\n[00:00–00:05]\n- Hello.",
            NotesStyle = "Key points",
            ElapsedMs = 65_000,
            ProcessedLength = 14,
            SessionStarted = started,
            AutoSaveFileName = LiveTranscriptText.AutoSaveFileName(started),
            SavedAt = started.AddMinutes(2),
        };

        TranscriberSessionStore.Save(StatePath, state);
        var loaded = TranscriberSessionStore.Load(StatePath, out var error);

        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal(state.Transcript, loaded!.Transcript);
        Assert.Equal(state.Notes, loaded.Notes);
        Assert.Equal(TranscriptSummaryStyles.KeyPoints, loaded.NotesStyle);
        Assert.Equal(TimeSpan.FromSeconds(65), loaded.Elapsed);
        Assert.Equal(14, loaded.ProcessedLength);
        Assert.Equal(started, loaded.SessionStarted);
        Assert.Equal("transcript_20261008_143005.md", loaded.AutoSaveFileName);
        Assert.Equal(state.SavedAt, loaded.SavedAt);
        Assert.Equal("current-session.json", Path.GetFileName(StatePath));

        // Saving again replaces the file and leaves no temporary files behind.
        state.Notes = "changed";
        TranscriberSessionStore.Save(StatePath, state);
        Assert.Equal("changed", TranscriberSessionStore.Load(StatePath, out _)!.Notes);
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void MissingCorruptOrEmptyStateIsIgnored()
    {
        Assert.Null(TranscriberSessionStore.Load(StatePath, out var missing));
        Assert.Null(missing);

        Directory.CreateDirectory(_folder);
        File.WriteAllText(StatePath, "{ not json");
        Assert.Null(TranscriberSessionStore.Load(StatePath, out var corrupt));
        Assert.NotNull(corrupt);

        File.WriteAllText(StatePath, "null");
        Assert.Null(TranscriberSessionStore.Load(StatePath, out var nothing));
        Assert.NotNull(nothing);

        TranscriberSessionStore.Save(StatePath, new TranscriberSessionState { Transcript = "  ", Notes = "" });
        Assert.Null(TranscriberSessionStore.Load(StatePath, out var empty));
        Assert.Null(empty);
    }

    [Fact]
    public void UnsafeValuesAreMadeSafe()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(StatePath,
            """{"Transcript":"abc","Notes":null,"NotesStyle":"weird","ElapsedMs":-5,"ProcessedLength":99,"AutoSaveFileName":"..\\..\\evil.md"}""");

        var loaded = TranscriberSessionStore.Load(StatePath, out var error);

        Assert.Null(error);
        Assert.NotNull(loaded);
        Assert.Equal("", loaded!.Notes);
        Assert.Equal(TranscriptSummaryStyles.Summary, loaded.NotesStyle);
        Assert.Equal(TimeSpan.Zero, loaded.Elapsed);
        Assert.Equal(3, loaded.ProcessedLength);
        Assert.Null(loaded.AutoSaveFileName);
    }

    [Theory]
    [InlineData("transcript_20261008_143005.md", true)]
    [InlineData("notes.txt", true)]
    [InlineData("../x.md", false)]
    [InlineData("a/b.md", false)]
    [InlineData("C:x.md", false)]
    [InlineData(".md", false)]
    [InlineData("x.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AcceptsOnlyPlainTranscriptFileNames(string? name, bool expected)
    {
        Assert.Equal(expected, TranscriberSessionStore.IsSafeFileName(name));
    }

    [Fact]
    public void DeleteRemovesTheState()
    {
        TranscriberSessionStore.Save(StatePath, new TranscriberSessionState { Transcript = "x" });
        Assert.True(TranscriberSessionStore.Delete(StatePath, out var error));
        Assert.Null(error);
        Assert.False(File.Exists(StatePath));
        Assert.True(TranscriberSessionStore.Delete(StatePath, out _));
    }
}
