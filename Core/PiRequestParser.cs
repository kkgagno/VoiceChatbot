using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Decides whether a chat message is addressed to the Pi coding agent, and builds its read-only prompt.
/// Only an explicit address counts: "ask pi ...", "ask the pi agent to ...", "pi agent, ...",
/// "use the pi agent to ..." (and "pie", which is how speech recognition often writes "pi").
/// Ordinary chat such as "Pie recipes for Thanksgiving?", "Agent Smith quotes" or
/// "Use pi to 10 digits to find the area" stays with the chat model.
/// </summary>
public static class PiRequestParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private const string Pi = @"(?:pi|pie)";
    private const string Separator = @"(?:\s*[,:;]\s*|\s+-\s+|\s+)";

    private static readonly Regex[] AddressPatterns =
    {
        // "ask pi to list ...", "ask the pi agent: ...", "please ask pie what's in ..."
        new(@"^(?:please\s+)?ask\s+(?:the\s+)?" + Pi + @"(?:\s+agent)?" + Separator + @"(?:to\s+)?(?:please\s+)?(?<r>.+)$", Options),
        // "pi agent, list ...", "hey pi agent please summarize ..."
        new(@"^(?:(?:hey|ok|okay)[\s,]+)?(?:the\s+)?" + Pi + @"\s+agent" + Separator + @"(?:please\s+)?(?<r>.+)$", Options),
        // "use the pi agent to ...", "have pi agent look at ..."
        new(@"^(?:please\s+)?(?:use|have|let)\s+(?:the\s+)?" + Pi + @"\s+agent" + Separator + @"(?:to\s+)?(?<r>.+)$", Options)
    };

    private static readonly string[] ReadOnlyPhrases =
    {
        "look at ",
        "list ",
        "show ",
        "display ",
        "folders",
        "folder",
        "directories",
        "directory",
        "what is in ",
        "what's in ",
        "whats in ",
        "summarize ",
        "inspect ",
        "check "
    };

    private static readonly string[] MutatingOrExecutionPhrases =
    {
        " run ",
        " execute ",
        " launch ",
        " start ",
        " write ",
        " create ",
        " edit ",
        " modify ",
        " change ",
        " delete ",
        " remove ",
        " install ",
        " update ",
        " script "
    };

    public const string BlockedMutationReason =
        "Pi is connected for read-only inspection in this first version. I blocked this because it sounds like it could write files, edit files, install software, or run code.";

    public const string BlockedNotReadOnlyReason =
        "Pi is connected for read-only file and directory inspection. Start the request with something like \"ask pi to look at\" or \"pi agent, summarize\".";

    /// <summary>The request text after an explicit Pi address, or "" when the message is not for Pi.</summary>
    public static string ExtractRequest(string? userText)
    {
        if (string.IsNullOrWhiteSpace(userText))
            return "";

        var text = userText.Trim();
        foreach (var pattern in AddressPatterns)
        {
            var match = pattern.Match(text);
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups["r"].Value))
                return match.Groups["r"].Value.Trim();
        }

        return "";
    }

    /// <summary>
    /// True when the message is addressed to Pi. Then either <paramref name="prompt"/> holds the
    /// read-only Pi prompt, or <paramref name="blockedReason"/> says why the request was not sent.
    /// </summary>
    public static bool TryCreateReadOnlyPrompt(string? userText, out string prompt, out string blockedReason)
    {
        prompt = "";
        blockedReason = "";

        var request = ExtractRequest(userText);
        if (string.IsNullOrWhiteSpace(request))
            return false;

        var normalized = $" {request.Trim().ToLowerInvariant()} ";
        if (MutatingOrExecutionPhrases.Any(normalized.Contains))
        {
            blockedReason = BlockedMutationReason;
            return true;
        }

        if (!ReadOnlyPhrases.Any(normalized.Contains))
        {
            blockedReason = BlockedNotReadOnlyReason;
            return true;
        }

        prompt = "Read-only task. Do not write files, edit files, install packages, or run destructive commands. " +
                 "You may inspect the current project and answer concisely. User request: " + request.Trim();
        return true;
    }
}
