using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using VoiceChatbot;
using Xunit;

public class HelpContentTests
{
    [Fact]
    public void EveryTopic_HasATitleSummaryIconGroupAndContent()
    {
        Assert.NotEmpty(HelpContent.Topics);
        foreach (var topic in HelpContent.Topics)
        {
            Assert.False(string.IsNullOrWhiteSpace(topic.Id), "A topic has no id.");
            Assert.False(string.IsNullOrWhiteSpace(topic.Title), $"{topic.Id} has no title.");
            Assert.False(string.IsNullOrWhiteSpace(topic.Summary), $"{topic.Id} has no summary.");
            Assert.False(string.IsNullOrWhiteSpace(topic.Icon), $"{topic.Id} has no icon.");
            Assert.Contains(topic.Group, HelpContent.Groups);
            Assert.Contains(topic.Blocks, b => b.Kind != HelpBlockKind.SeeAlso);

            foreach (var block in topic.Blocks)
            {
                switch (block.Kind)
                {
                    case HelpBlockKind.Bullets:
                    case HelpBlockKind.Steps:
                    case HelpBlockKind.SeeAlso:
                        Assert.NotEmpty(block.Items);
                        Assert.All(block.Items, item => Assert.False(string.IsNullOrWhiteSpace(item), $"{topic.Id} has an empty item."));
                        break;
                    default:
                        Assert.False(string.IsNullOrWhiteSpace(block.Text), $"{topic.Id} has an empty {block.Kind}.");
                        break;
                }
            }
        }
    }

    [Fact]
    public void TopicIdsAndTitles_AreUnique_AndSeeAlsoLinksResolve()
    {
        var topics = HelpContent.Topics;
        Assert.Equal(topics.Count, topics.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(topics.Count, topics.Select(t => t.Title).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var topic in topics)
        {
            foreach (var id in topic.Blocks.Where(b => b.Kind == HelpBlockKind.SeeAlso).SelectMany(b => b.Items))
            {
                Assert.True(HelpContent.Find(id) != null, $"{topic.Id} links to unknown topic '{id}'.");
                Assert.NotEqual(topic.Id, id);
            }
        }

        Assert.NotNull(HelpContent.Find(HelpContent.GettingStartedId));
        Assert.NotNull(HelpContent.Find(HelpContent.LiveTranscriberId));
        Assert.NotNull(HelpContent.Find(HelpContent.SchedulerId));
        Assert.NotNull(HelpContent.Find(HelpContent.KnowledgeFolderId));
        Assert.Equal(HelpContent.GettingStartedId, HelpContent.Topics[0].Id);
    }

    [Fact]
    public void TopicsAreListedGroupByGroup_InTheOrderOfGroups()
    {
        var groupOrder = HelpContent.Topics.Select(t => HelpContent.Groups.ToList().IndexOf(t.Group)).ToList();
        Assert.Equal(groupOrder.OrderBy(i => i), groupOrder);
        Assert.All(HelpContent.Groups, g => Assert.Contains(HelpContent.Topics, t => t.Group == g));
    }

    [Fact]
    public void Markup_IsBalancedEverywhere()
    {
        foreach (var topic in HelpContent.Topics)
        {
            var texts = new[] { topic.Summary }
                .Concat(topic.Blocks.Where(b => b.Kind != HelpBlockKind.SeeAlso).Select(b => b.Text))
                .Concat(topic.Blocks.Where(b => b.Kind != HelpBlockKind.SeeAlso).SelectMany(b => b.Items));
            foreach (var text in texts)
            {
                foreach (var span in HelpMarkup.Parse(text).Where(s => s.Kind == HelpSpanKind.Text))
                {
                    Assert.DoesNotContain("**", span.Text);
                    Assert.DoesNotContain("`", span.Text);
                }
            }
        }
    }

    [Fact]
    public void EverySidebarSection_HasATopic()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "MainWindow.xaml"));
        var headers = Regex.Matches(xaml, "<Expander Header=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(headers);
        foreach (var header in headers)
        {
            var topic = HelpContent.ForSidebarSection(header);
            Assert.True(topic != null, $"No help topic for the sidebar section '{header}'.");
            Assert.Equal(HelpContent.GroupSidebar, topic!.Group);
        }

        // And no topic points at a section that no longer exists.
        foreach (var topic in HelpContent.Topics.Where(t => t.SidebarSection != null))
            Assert.Contains(topic.SidebarSection, headers);
    }

    [Theory]
    [InlineData("Listen")]
    [InlineData("Stop listening")]
    [InlineData("Transcribe")]
    [InlineData("Scheduler")]
    [InlineData("Help")]
    [InlineData("Stop")]
    [InlineData("Conversations")]
    [InlineData("New chat")]
    public void TopBarTopic_ExplainsEveryControl(string label)
    {
        var topBar = HelpContent.Find("top-bar")!;
        Assert.Contains(topBar.Blocks.SelectMany(b => b.Items), item => item.Contains($"**{label}**", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_SplitsBoldCodeAndLinks()
    {
        var spans = HelpMarkup.Parse("Click **Refresh Models**, enter `http://localhost:11434` or see https://ollama.com/download.");

        Assert.Equal(new[]
        {
            new HelpSpan(HelpSpanKind.Text, "Click "),
            new HelpSpan(HelpSpanKind.Bold, "Refresh Models"),
            new HelpSpan(HelpSpanKind.Text, ", enter "),
            new HelpSpan(HelpSpanKind.Code, "http://localhost:11434"),
            new HelpSpan(HelpSpanKind.Text, " or see "),
            new HelpSpan(HelpSpanKind.Link, "https://ollama.com/download"),
            new HelpSpan(HelpSpanKind.Text, ".")
        }, spans);
    }

    [Theory]
    [InlineData("**not closed", "**not closed")]
    [InlineData("a ` alone", "a ` alone")]
    [InlineData("**Bold** and `code`", "Bold and code")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ToPlainText_DropsMarkupAndKeepsUnclosedMarkers(string? markup, string expected) =>
        Assert.Equal(expected, HelpMarkup.ToPlainText(markup));

    [Fact]
    public void Search_EmptyQueryReturnsAllTopicsInOrder()
    {
        Assert.Equal(HelpContent.Topics, HelpContent.Search(""));
        Assert.Equal(HelpContent.Topics, HelpContent.Search("   "));
        Assert.Equal(HelpContent.Topics, HelpContent.Search(null));
    }

    [Theory]
    [InlineData("tavily", "web-search")]
    [InlineData("TAVILY", "web-search")]
    [InlineData("kokoro", "voice-output")]
    [InlineData("wake word", "listening")]
    [InlineData("iphone", "phone-remote")]
    [InlineData("pin", "phone-remote")]
    [InlineData("ollama", "chat-server")]
    [InlineData("whisper", "voice-input")]
    [InlineData("live notes", "live-transcriber")]
    [InlineData("persona", "personas")]
    [InlineData("logs", "troubleshooting")]
    [InlineData("theme", "app")]
    public void Search_PutsTheBestTopicFirst(string query, string expectedId)
    {
        var results = HelpContent.Search(query);
        Assert.NotEmpty(results);
        Assert.Equal(expectedId, results[0].Id);
    }

    [Fact]
    public void Search_NeedsEveryWord_AndIgnoresCommonWords()
    {
        var both = HelpContent.Search("phone certificate");
        Assert.All(both, t => Assert.Contains("certificate", t.SearchText, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(both, t => t.Id == "phone-remote");

        // "how", "do", "I", "my" are ignored, so this still finds the microphone help.
        var question = HelpContent.Search("how do I fix my microphone");
        Assert.NotEmpty(question);
        Assert.Contains(question.Take(3), t => t.Id is "troubleshooting" or "voice-input");
    }

    [Fact]
    public void Search_MatchesTheStartOfWords()
    {
        Assert.Contains(HelpContent.Search("mic"), t => t.Id == "voice-input");
        // "pin" is inside "Typing", but that is not the start of a word.
        Assert.DoesNotContain(HelpContent.Search("pin"), t => t.Id == "chatting");
    }

    [Fact]
    public void Search_FallsBackToTopicsWithMostWords_WhenNoTopicHasThemAll()
    {
        var results = HelpContent.Search("kokoro zzqqxx");
        Assert.NotEmpty(results);
        Assert.Equal("voice-output", results[0].Id);

        Assert.Empty(HelpContent.Search("zzqqxx"));
    }

    [Fact]
    public void ForSidebarSection_IgnoresCaseAndUnknownHeaders()
    {
        Assert.Equal("voice-input", HelpContent.ForSidebarSection(" voice input ")?.Id);
        Assert.Null(HelpContent.ForSidebarSection("Images & Video"));
        Assert.Null(HelpContent.ForSidebarSection(null));
        Assert.Null(HelpContent.Find(""));
        Assert.Equal("app", HelpContent.Find("APP")?.Id);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VoiceChatbot.csproj")))
                return dir.FullName;
        }

        throw new DirectoryNotFoundException("VoiceChatbot.csproj not found above the test output folder.");
    }
}
