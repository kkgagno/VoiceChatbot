using System;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Finds a YouTube video link in a chat message and turns it into a canonical watch URL.
/// Accepts www., m. and music.youtube.com, youtube-nocookie.com, youtu.be, and the /watch?v=,
/// /shorts/, /live/, /embed/ and /v/ forms, with or without the scheme. The canonical form
/// (https://www.youtube.com/watch?v=ID) drops playlist and timestamp parameters, so yt-dlp
/// only ever sees the one video.
/// </summary>
public static class YouTubeUrl
{
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // A YouTube host at a word start (so "notyoutube.com" does not count), then the rest of the URL.
    private static readonly Regex CandidateRegex = new(
        @"(?<![\w.-])(?:https?://)?(?:(?:www|m|music)\.)?(?<host>youtube\.com|youtube-nocookie\.com|youtu\.be)(?<rest>/[^\s<>""'`]*)",
        Options);

    // Video ids are exactly 11 characters from [A-Za-z0-9_-].
    private const string Id = @"(?<id>[A-Za-z0-9_-]{11})(?![A-Za-z0-9_-])";

    private static readonly Regex ShortHostPath = new(@"^/" + Id, Options);
    private static readonly Regex PathForms = new(@"^/(?:shorts|live|embed|v|e)/" + Id, Options);
    private static readonly Regex WatchQuery = new(@"^/watch/?\?(?:[^#\s]*?&)?v=" + Id, Options);

    public static bool TryExtract(string? text, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (Match candidate in CandidateRegex.Matches(text))
        {
            if (TryGetVideoId(candidate.Groups["host"].Value, candidate.Groups["rest"].Value, out var id))
            {
                url = $"https://www.youtube.com/watch?v={id}";
                return true;
            }
        }

        return false;
    }

    public static bool ContainsYouTubeUrl(string? text) => TryExtract(text, out _);

    private static bool TryGetVideoId(string host, string rest, out string id)
    {
        id = "";
        Match match;
        if (host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase))
            match = ShortHostPath.Match(rest);
        else
        {
            match = WatchQuery.Match(rest);
            if (!match.Success)
                match = PathForms.Match(rest);
        }

        if (!match.Success)
            return false;

        id = match.Groups["id"].Value;
        return true;
    }
}
