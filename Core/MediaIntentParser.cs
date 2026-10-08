using System;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Recognizes explicit ComfyUI media requests in a chat message: create an image, edit an image,
/// or make a video from an image. Each one needs generation phrasing plus an image/picture/photo or
/// video/movie/clip noun that is followed by the description, so ordinary requests such as
/// "Edit the code above to use async", "Create a movie review of Dune", "Create an image classifier"
/// or "Summarize this video https://youtu.be/..." stay with the chat model.
/// </summary>
public static class MediaIntentParser
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private const string Please = @"^(?:please\s+)?";
    private const string Article = @"(?:(?:an?|another)\s+)?(?:new\s+)?";
    private const string Determiners = @"(?:(?:this|these|the|that|those|my|our|last|latest|attached|generated|previous|current|uploaded|new)\s+){0,3}";
    private const string ImageNoun = @"(?:image|picture|photo)s?";
    private const string EditNoun = @"(?:image|picture|photo|pic)s?";
    private const string VideoNoun = @"(?:video|movie|clip)s?";

    // "5 second", "10-second", "3 sec", "8 seconds long", "5s"; the bare "s" form only right before
    // the video noun, so "a 90s arcade" is not read as 90 seconds.
    private const string Duration = @"\d{1,2}(?:[\s-]*(?:seconds?|secs?)\b|[\s-]*s(?=[\s-]+(?:long[\s-]+)?(?:video|movie|clip)))(?:[\s-]+long)?";

    // Joins the noun to the description; the joining word is not part of the prompt. A dash only
    // counts after a space, so "picture-perfect" or "picture-in-picture" is not a joiner.
    private const string Punctuation = @"(?:\s*[:,]|\s+[\u2013\u2014-])";
    private const string Joiner = @"(?:" + Punctuation + @"|\s+(?:of|showing|depicting|with)\b)\s*";
    private const string VideoJoiner = @"(?:" + Punctuation + @"|\s+(?:of|showing|depicting|with|where|that\s+shows)\b)\s*";

    // Instruction verbs that may follow the noun directly: "edit this image make the sky purple".
    private const string EditVerbs = @"(?:so|by|with|into|using|make|add|remove|replace|put|turn|give|swap|erase|delete|blur|crop|recolou?r|colou?r|paint|convert|place|insert|set|fix|brighten|darken|move|zoom|change|keep|show)";

    private static readonly Regex CreateImageRegex = new(
        Please + @"(?:create|generate|make|draw)\s+" + Article + ImageNoun + Joiner + @"(?<p>.+)$",
        Options);

    private static readonly Regex EditImageRegex = new(
        Please + @"(?:edit|change|modify)\s+" + Determiners + EditNoun +
        @"(?:(?:" + Punctuation + @"|\s+(?:and|to)\b)\s*(?<p>.+)|\s+(?<p>" + EditVerbs + @"\b.+))$",
        Options);

    private static readonly Regex CreateVideoRegex = new(
        Please + @"(?:create|generate|make)\s+" + Article + @"(?:" + Duration + @"\s+)?" + VideoNoun +
        @"(?:" + VideoJoiner + @"(?<p>.+)|\s+(?<p>from\s+" + Determiners + @"(?:image|picture|photo)\b.*))$",
        Options);

    private static readonly Regex ImageToVideoRegex = new(
        Please + @"(?:turn|make|convert)\s+" + Determiners + @"(?:image|picture|photo)\s+into\s+(?:an?\s+)?(?:" + Duration + @"\s+)?" +
        @"(?:video|movie|clip)(?:(?:" + Punctuation + @"|\s+(?:where|that|of|showing|with)\b)\s*(?<p>.+)|\s*[.!?]?)$",
        Options);

    // "5 second video of ...", or "can you make me a 5 second clip of ..." anywhere in the message.
    private static readonly Regex DurationVideoRegex = new(
        @"(?:" + Please + @"(?:an?\s+)?|\b(?:create|generate|make)\s+(?:(?:me|us|a|an|another|new|quick|short)\s+){0,3})" +
        Duration + @"\s+" + VideoNoun + VideoJoiner + @"(?<p>.+)$",
        Options);

    private static readonly Regex SecondsRegex = new(
        @"\b(?<n>\d{1,2})(?:[\s-]*(?:seconds?|secs?)\b|[\s-]*s(?=[\s-]+(?:long[\s-]+)?(?:video|movie|clip)\b))",
        Options);

    // Requests for code, markup or diagrams that happen to say "image" or "picture" belong to the chat model.
    private static readonly Regex CodeRequestRegex = new(
        @"\b(?:svg|html|css|ascii|matplotlib|pillow|opencv|tikz|mermaid|graphviz|plantuml|dockerfile|docker)\b|" +
        @"\b(?:in|using|with)\s+(?:python|javascript|typescript|java|c#|c\+\+|rust|golang|powershell|bash|js|react)(?![\w#+])",
        Options);

    public static bool TryGetImageCreatePrompt(string? text, out string prompt)
    {
        prompt = "";
        if (!IsCandidate(text))
            return false;

        return TryMatchPrompt(CreateImageRegex, text!.Trim(), out prompt);
    }

    public static bool TryGetImageEditPrompt(string? text, out string prompt)
    {
        prompt = "";
        if (!IsCandidate(text))
            return false;

        return TryMatchPrompt(EditImageRegex, text!.Trim(), out prompt);
    }

    public static bool TryGetVideoPrompt(string? text, out string prompt, out int? seconds)
    {
        prompt = "";
        seconds = null;
        if (!IsCandidate(text))
            return false;

        var trimmed = text!.Trim();
        foreach (var regex in new[] { CreateVideoRegex, ImageToVideoRegex, DurationVideoRegex })
        {
            var match = regex.Match(trimmed);
            if (!match.Success)
                continue;

            seconds = TryParseVideoSeconds(trimmed);
            prompt = match.Groups["p"].Value.Trim();
            if (string.IsNullOrWhiteSpace(prompt))
                prompt = RemoveVideoCommandWords(trimmed);
            return true;
        }

        return false;
    }

    public static int? TryParseVideoSeconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = SecondsRegex.Match(text);
        if (match.Success && int.TryParse(match.Groups["n"].Value, out var seconds))
            return Math.Clamp(seconds, 1, 30);

        return null;
    }

    public static string RemoveVideoCommandWords(string text)
    {
        var cleaned = Regex.Replace(text, @"^(?:please\s+)?(?:create|generate|make|turn)\s+", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\b(?:an?\s+)?\d*\s*(?:second|seconds|sec|s)?\s*(?:video|movie|clip)\b", "", RegexOptions.IgnoreCase).Trim();
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ");
        return string.IsNullOrWhiteSpace(cleaned) ? text.Trim() : cleaned;
    }

    // A YouTube link means "talk about this video", never "generate media".
    private static bool IsCandidate(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && !YouTubeUrl.ContainsYouTubeUrl(text)
        && !CodeRequestRegex.IsMatch(text);

    private static bool TryMatchPrompt(Regex regex, string text, out string prompt)
    {
        prompt = "";
        var match = regex.Match(text);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups["p"].Value))
            return false;

        prompt = match.Groups["p"].Value.Trim();
        return true;
    }
}
