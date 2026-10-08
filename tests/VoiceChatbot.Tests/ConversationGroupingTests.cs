using System.Globalization;
using VoiceChatbot;
using Xunit;

public class ConversationGroupingTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 9, 30, 0); // Thursday

    [Theory]
    [InlineData("2026-10-08 00:00", ConversationAge.Today)]
    [InlineData("2026-10-08 23:59", ConversationAge.Today)]
    [InlineData("2026-10-09 08:00", ConversationAge.Today)]
    [InlineData("2026-10-07 23:59", ConversationAge.Yesterday)]
    [InlineData("2026-10-07 00:00", ConversationAge.Yesterday)]
    [InlineData("2026-10-06 12:00", ConversationAge.Previous7Days)]
    [InlineData("2026-10-01 00:00", ConversationAge.Previous7Days)]
    [InlineData("2026-09-30 23:59", ConversationAge.Older)]
    [InlineData("2025-01-01 10:00", ConversationAge.Older)]
    public void GetAge(string local, ConversationAge expected) =>
        Assert.Equal(expected, ConversationGrouping.GetAge(DateTime.Parse(local, CultureInfo.InvariantCulture), Now));

    [Theory]
    [InlineData("2026-10-08 14:05", "14:05")]
    [InlineData("2026-10-07 08:15", "08:15")]
    [InlineData("2026-10-05 19:00", "Mon 19:00")]
    [InlineData("2026-09-12 19:00", "Sep 12")]
    [InlineData("2025-12-24 19:00", "Dec 24, 2025")]
    public void FormatTime(string local, string expected) =>
        Assert.Equal(expected, ConversationGrouping.FormatTime(
            DateTime.Parse(local, CultureInfo.InvariantCulture), Now, CultureInfo.InvariantCulture));

    [Fact]
    public void Labels() =>
        Assert.Equal(
            new[] { "Today", "Yesterday", "Previous 7 days", "Older" },
            Enum.GetValues<ConversationAge>().Select(ConversationGrouping.Label));
}
