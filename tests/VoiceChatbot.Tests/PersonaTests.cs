using System.Text.Json;
using VoiceChatbot;
using Xunit;

public class PersonaTests
{
    private static Persona P(string name, string prompt = "", string voice = "", int rate = 0, string model = "") =>
        new() { Name = name, SystemPrompt = prompt, VoiceName = voice, SpeechRate = rate, Model = model };

    [Theory]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    [InlineData("  Coach  ", "Coach")]
    [InlineData("Story\n  teller\t mode", "Story teller mode")]
    public void CleanName_TrimsAndCollapsesWhitespace(string? input, string expected) =>
        Assert.Equal(expected, PersonaCatalog.CleanName(input));

    [Fact]
    public void CleanName_CapsLength()
    {
        var name = PersonaCatalog.CleanName(new string('a', 100));
        Assert.Equal(Persona.MaxNameLength, name.Length);
    }

    [Fact]
    public void Normalize_NullListIsEmpty() =>
        Assert.Empty(PersonaCatalog.Normalize(null));

    [Fact]
    public void Normalize_DropsNullBlankAndDuplicateNames_FirstWins()
    {
        var list = PersonaCatalog.Normalize(new Persona?[]
        {
            P(" Coach ", prompt: "first"),
            null,
            P("   "),
            P("coach", prompt: "second"),
            P("Narrator")
        });

        Assert.Equal(new[] { "Coach", "Narrator" }, list.Select(p => p.Name));
        Assert.Equal("first", list[0].SystemPrompt);
    }

    [Fact]
    public void Normalize_FixesNullStringsAndClampsRate()
    {
        var list = PersonaCatalog.Normalize(new[]
        {
            new Persona { Name = "A", SystemPrompt = null!, VoiceName = null!, Model = null!, SpeechRate = 42 },
            new Persona { Name = "B", VoiceName = "  af_bella  ", Model = " llama3 ", SpeechRate = -9 }
        });

        Assert.Equal("", list[0].SystemPrompt);
        Assert.Equal("", list[0].VoiceName);
        Assert.Equal("", list[0].Model);
        Assert.Equal(Persona.MaxSpeechRate, list[0].SpeechRate);
        Assert.Equal("af_bella", list[1].VoiceName);
        Assert.Equal("llama3", list[1].Model);
        Assert.Equal(Persona.MinSpeechRate, list[1].SpeechRate);
    }

    [Fact]
    public void Normalize_ReturnsCopies()
    {
        var original = P("Coach", prompt: "x");
        var list = PersonaCatalog.Normalize(new[] { original });
        list[0].SystemPrompt = "changed";
        Assert.Equal("x", original.SystemPrompt);
    }

    [Fact]
    public void EnsureDefault_CreatesDefaultFromCurrentSettingsWhenEmpty()
    {
        var list = PersonaCatalog.EnsureDefault(null, "Be brief.", "am_onyx (American Male)", 2);

        var persona = Assert.Single(list);
        Assert.Equal(Persona.DefaultName, persona.Name);
        Assert.Equal("Be brief.", persona.SystemPrompt);
        Assert.Equal("am_onyx (American Male)", persona.VoiceName);
        Assert.Equal(2, persona.SpeechRate);
        Assert.Equal("", persona.Model);
    }

    [Fact]
    public void EnsureDefault_UnusableEntriesStillGetDefault()
    {
        var list = PersonaCatalog.EnsureDefault(new Persona?[] { null, P(" ") }, null, null, 99);

        var persona = Assert.Single(list);
        Assert.Equal(Persona.DefaultName, persona.Name);
        Assert.Equal("", persona.SystemPrompt);
        Assert.Equal(Persona.MaxSpeechRate, persona.SpeechRate);
    }

    [Fact]
    public void EnsureDefault_KeepsExistingPersonasWithoutAddingDefault()
    {
        var list = PersonaCatalog.EnsureDefault(new[] { P("Coach"), P("Narrator") }, "prompt", "voice", 0);
        Assert.Equal(new[] { "Coach", "Narrator" }, list.Select(p => p.Name));
    }

    [Fact]
    public void Find_IgnoresCaseAndWhitespace()
    {
        var list = new List<Persona> { P("Story Teller"), P("Coach") };

        Assert.Same(list[0], PersonaCatalog.Find(list, "  story   teller "));
        Assert.Null(PersonaCatalog.Find(list, "Narrator"));
        Assert.Null(PersonaCatalog.Find(list, ""));
        Assert.Null(PersonaCatalog.Find(null, "Coach"));
    }

    [Fact]
    public void ResolveActive_FallsBackToFirstPersona()
    {
        var list = new List<Persona> { P("Default"), P("Coach") };

        Assert.Equal("Coach", PersonaCatalog.ResolveActive(list, "coach")?.Name);
        Assert.Equal("Default", PersonaCatalog.ResolveActive(list, "Deleted one")?.Name);
        Assert.Equal("Default", PersonaCatalog.ResolveActive(list, null)?.Name);
        Assert.Null(PersonaCatalog.ResolveActive(new List<Persona>(), "Coach"));
    }

    [Fact]
    public void ValidateNewName_RejectsBlankAndExistingNames()
    {
        var list = new List<Persona> { P("Coach") };

        Assert.NotNull(PersonaCatalog.ValidateNewName(list, "  "));
        var duplicate = PersonaCatalog.ValidateNewName(list, " COACH ");
        Assert.NotNull(duplicate);
        Assert.Contains("Coach", duplicate);
        Assert.Null(PersonaCatalog.ValidateNewName(list, "Narrator"));
        Assert.Null(PersonaCatalog.ValidateNewName(null, "Narrator"));
    }

    [Fact]
    public void CanDelete_OnlyWhenAnotherPersonaRemains()
    {
        Assert.False(PersonaCatalog.CanDelete(null));
        Assert.False(PersonaCatalog.CanDelete(new List<Persona> { P("Default") }));
        Assert.True(PersonaCatalog.CanDelete(new List<Persona> { P("Default"), P("Coach") }));
    }

    private static readonly string[] Voices =
    {
        "af_bella (American Female)",
        "am_onyx (American Male)",
        "bf_emma (British Female)"
    };

    [Theory]
    [InlineData("am_onyx (American Male)", "am_onyx (American Male)")]
    [InlineData("AM_ONYX (american male)", "am_onyx (American Male)")]
    [InlineData("am_onyx", "am_onyx (American Male)")]
    [InlineData("am_onyx (Old label)", "am_onyx (American Male)")]
    [InlineData(" bf_emma ", "bf_emma (British Female)")]
    [InlineData("zf_xiaobei", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void MatchVoice_ExactThenById(string? wanted, string? expected) =>
        Assert.Equal(expected, PersonaCatalog.MatchVoice(Voices, wanted));

    [Fact]
    public void MatchVoice_HandlesMissingList() =>
        Assert.Null(PersonaCatalog.MatchVoice(null, "am_onyx"));

    [Fact]
    public void Clone_CopiesEveryField()
    {
        var original = P("Coach", "prompt", "voice", 3, "llama3");
        var copy = original.Clone();

        Assert.NotSame(original, copy);
        Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(copy));
    }

    [Fact]
    public void Json_MissingAndNullFieldsLoadSafely()
    {
        // As written by an older or hand-edited settings.json.
        var loaded = JsonSerializer.Deserialize<List<Persona>>(
            """[{"Name":"Coach"},{"Name":"Narrator","Model":null,"SpeechRate":3},null]""");

        var list = PersonaCatalog.Normalize(loaded);

        Assert.Equal(new[] { "Coach", "Narrator" }, list.Select(p => p.Name));
        Assert.Equal("", list[0].VoiceName);
        Assert.Equal(0, list[0].SpeechRate);
        Assert.Equal("", list[1].Model);
        Assert.Equal(3, list[1].SpeechRate);
    }
}
