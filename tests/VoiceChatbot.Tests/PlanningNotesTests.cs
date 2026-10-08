using VoiceChatbot;
using Xunit;

public class PlanningNotesTests
{
    private const string NotesOnlyGreeting =
        "The user said \"hi there\". This is a short greeting.\n\n" +
        "Wait, they might want a longer chat. Actually, a brief friendly reply is best.\n\n" +
        "Let's keep it simple and conversational.\n\n" +
        "Let's try: \"Hi! What can I help you with?\"";

    [Fact]
    public void KeepsTheQuotedReplyFromNotesThatHaveNoOtherAnswer()
    {
        Assert.True(PlanningNotes.LooksLikeNotes(NotesOnlyGreeting));
        Assert.True(PlanningNotes.TryExtractAnswer(NotesOnlyGreeting, out var answer, out var notes));
        Assert.Equal("Hi! What can I help you with?", answer);
        Assert.Contains("The user said", notes);
    }

    [Fact]
    public void KeepsTheParagraphsWrittenAfterTheNotes()
    {
        var reply =
            "Okay, the user is asking about the capital of France. That's simple. I should answer directly and keep it short.\n\n" +
            "The capital of France is Paris.\n\nIt is also the largest city in the country.";

        Assert.True(PlanningNotes.TryExtractAnswer(reply, out var answer, out var notes));
        Assert.Equal("The capital of France is Paris.\n\nIt is also the largest city in the country.", answer);
        Assert.StartsWith("Okay, the user is asking", notes);
    }

    [Fact]
    public void KeepsTheTextAfterAFinalAnswerLabel()
    {
        var reply =
            "We need to answer the greeting. The user said hello. Let's be friendly, and I should keep it short.\n" +
            "Final answer: Hello! How can I help you today?";

        Assert.True(PlanningNotes.TryExtractAnswer(reply, out var answer, out _));
        Assert.Equal("Hello! How can I help you today?", answer);
    }

    [Theory]
    [InlineData("Response:")]
    [InlineData("Reply:")]
    public void KeepsTheTextAfterOtherLabels(string label)
    {
        var reply = "The user said thanks. They might be done. I should reply warmly.\n" +
                    $"{label} \"You're welcome! Anything else I can do?\"";

        Assert.True(PlanningNotes.TryExtractAnswer(reply, out var answer, out _));
        Assert.Equal("You're welcome! Anything else I can do?", answer);
    }

    [Fact]
    public void UnquotesAnAnswerParagraph()
    {
        var reply = "The user said thanks. They might be wrapping up. I'll respond warmly.\n\n\"You're welcome! Anything else?\"";

        Assert.True(PlanningNotes.TryExtractAnswer(reply, out var answer, out _));
        Assert.Equal("You're welcome! Anything else?", answer);
    }

    [Fact]
    public void HandlesCurlyQuotesAndApostrophes()
    {
        var reply = "Okay, so the user greeted me. Let’s keep it light. I’ll say “Hey! Good to hear from you.”";

        Assert.True(PlanningNotes.TryExtractAnswer(reply, out var answer, out _));
        Assert.Equal("Hey! Good to hear from you.", answer);
    }

    [Fact]
    public void NotesWithoutAnAnswerGiveNothing()
    {
        var reply = "The user is asking about the weather. They might want details for tomorrow. I should check first.\n\n" +
                    "Wait, there is no forecast in the context. Actually, maybe ask where they are.";

        Assert.True(PlanningNotes.TryExtractAnswer(reply, out var answer, out var notes));
        Assert.Equal("", answer);
        Assert.Contains("Wait, there is no forecast", notes);
    }

    [Fact]
    public void KeepsACodeBlockAfterTheNotesInOnePiece()
    {
        var code = "```python\nfor i in range(3):\n\n    print(i)\n```";
        var reply = "We need to write a Python script. The user wants to print numbers. Let's keep it short.\n\n" +
                    code + "\n\nRun it with python.";

        Assert.True(PlanningNotes.TryExtractAnswer(reply, out var answer, out _));
        Assert.Equal(code + "\n\nRun it with python.", answer);
    }

    [Theory]
    [InlineData("The user manual says to hold the power button for ten seconds. Actually, let's try a soft reset first, which is safer.")]
    [InlineData("The user is asking a fair question.")]
    [InlineData("The user wants to export data, so open Settings and choose Export. Let's walk through it step by step.")]
    [InlineData("When the user clicks Save, the app writes the file. Let's look at the code first. I should mention it needs admin rights.")]
    [InlineData("Context matters here: the treaty was signed in 1648 after thirty years of war.")]
    [InlineData("Context: you asked about Python. Let's look at lists. Actually, tuples work too, and they might be faster.")]
    [InlineData("Correction: the capital of Australia is Canberra, not Sydney. I should have said that earlier. Let's continue.")]
    [InlineData("Sure! Here's a quick recipe. Wait, do you have eggs? Actually, let's check that first.")]
    [InlineData("It sounds like you've had a long day. Let's take it slow. I should mention that rest helps.")]
    [InlineData("")]
    public void OrdinaryRepliesAreUnchanged(string reply)
    {
        Assert.False(PlanningNotes.LooksLikeNotes(reply));
        Assert.False(PlanningNotes.TryExtractAnswer(reply, out var answer, out var notes));
        Assert.Equal(reply, answer);
        Assert.Equal("", notes);
    }

    [Theory]
    [InlineData("The user said")]
    [InlineData("The user is asking about")]
    [InlineData("Okay, the user")]
    [InlineData("Okay so the user wants")]
    [InlineData("**The user said** hi")]
    [InlineData("We need to answer")]
    [InlineData("I need to figure out")]
    public void RecognizesHowNotesStart(string start)
    {
        Assert.True(PlanningNotes.LooksLikeStart(start));
    }

    [Theory]
    [InlineData("")]
    [InlineData("The")]
    [InlineData("The user manual")]
    [InlineData("Hello! The user said")]
    [InlineData("Context: the user")]
    [InlineData("We need to talk about your budget")]
    public void OrdinaryStartsAreNotNotes(string start)
    {
        Assert.False(PlanningNotes.LooksLikeStart(start));
    }

    [Fact]
    public void StreamedNotesAreRecognizedFromTheFirstWords()
    {
        var shown = new List<bool>();
        var text = "";
        foreach (var token in new[] { "The", " user", " said", " \"hi\"." })
        {
            text += token;
            shown.Add(PlanningNotes.LooksLikeStart(text));
        }

        Assert.Equal(new[] { false, false, true, true }, shown);
    }
}
