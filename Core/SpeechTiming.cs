using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VoiceChatbot;

/// <summary>
/// How one reply was spoken, for the "Speech timing" line in the app log (and in the chat with Show
/// diagnostics). <see cref="FirstAudio"/>: from the start (the finished reply, or for speech that starts
/// while the reply is written, the request) to the first audio playing; null when nothing played.
/// <see cref="Synthesis"/>: time spent making the audio; <see cref="Audio"/>: how long the audio made is;
/// <see cref="Pieces"/>: the clips it was made in; <see cref="Voice"/>: what made it, such as
/// "Built-in Kokoro (graphics card)"; <see cref="Stopped"/>: cut short by Stop, Esc, the mic or a replay.
/// </summary>
public sealed record SpeechTiming(TimeSpan? FirstAudio, TimeSpan Synthesis, TimeSpan Audio, int Pieces, string Voice,
    bool WhileWriting = false, bool Stopped = false)
{
    /// <summary>Synthesis time per second of audio (below 1 is faster than speaking); null without audio.</summary>
    public double? RealTimeFactor => Audio > TimeSpan.Zero ? Synthesis.TotalSeconds / Audio.TotalSeconds : null;

    /// <summary>
    /// One line such as "Speech timing: first audio after 0.8 s (4.1 s after your message); synthesis 2.3 s for
    /// 18.5 s of audio in 3 pieces, 0.12 x real time; Built-in Kokoro (graphics card)".
    /// <paramref name="sinceMessage"/>, when given, is the time from the user's message to the first audio.
    /// </summary>
    public string Describe(TimeSpan? sinceMessage = null)
    {
        var parts = new List<string>();

        var start = new StringBuilder();
        if (FirstAudio is { } first)
        {
            start.Append(WhileWriting
                ? $"first audio {Seconds(first)} after the request, while the reply was written"
                : $"first audio after {Seconds(first)}");
            if (sinceMessage is { } message)
                start.Append($" ({Seconds(message)} after your message)");
        }
        else
        {
            start.Append("nothing played");
        }
        parts.Add(start.ToString());

        var synthesis = new StringBuilder($"synthesis {Seconds(Synthesis)} for {Seconds(Audio)} of audio");
        if (Pieces > 1)
            synthesis.Append($" in {Pieces.ToString(CultureInfo.InvariantCulture)} pieces");
        if (RealTimeFactor is { } factor)
            synthesis.Append($", {factor.ToString(factor < 0.1 ? "0.00" : "0.0#", CultureInfo.InvariantCulture)} x real time");
        parts.Add(synthesis.ToString());

        if (!string.IsNullOrWhiteSpace(Voice))
            parts.Add(Voice.Trim());
        if (Stopped)
            parts.Add("stopped early");
        return "Speech timing: " + string.Join("; ", parts);
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
}
