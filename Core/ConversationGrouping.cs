using System;
using System.Globalization;

namespace VoiceChatbot;

public enum ConversationAge
{
    Today,
    Yesterday,
    Previous7Days,
    Older
}

/// <summary>Date buckets and short time labels for the conversation history list.</summary>
public static class ConversationGrouping
{
    /// <summary>Both times are local. Anything in the future (clock changes) counts as today.</summary>
    public static ConversationAge GetAge(DateTime localTime, DateTime localNow)
    {
        var days = (localNow.Date - localTime.Date).Days;
        return days switch
        {
            <= 0 => ConversationAge.Today,
            1 => ConversationAge.Yesterday,
            <= 7 => ConversationAge.Previous7Days,
            _ => ConversationAge.Older
        };
    }

    public static string Label(ConversationAge age) => age switch
    {
        ConversationAge.Today => "Today",
        ConversationAge.Yesterday => "Yesterday",
        ConversationAge.Previous7Days => "Previous 7 days",
        _ => "Older"
    };

    /// <summary>"3:04 PM" for today and yesterday, "Mon 3:04 PM" within a week, then "Sep 12" or "Sep 12, 2025".</summary>
    public static string FormatTime(DateTime localTime, DateTime localNow, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        return GetAge(localTime, localNow) switch
        {
            ConversationAge.Today or ConversationAge.Yesterday => localTime.ToString("t", culture),
            ConversationAge.Previous7Days => localTime.ToString("ddd ", culture) + localTime.ToString("t", culture),
            _ when localTime.Year == localNow.Year => localTime.ToString("MMM d", culture),
            _ => localTime.ToString("MMM d, yyyy", culture)
        };
    }
}
