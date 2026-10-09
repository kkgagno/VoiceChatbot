using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// A named preset for the system prompt, voice, speech rate, temperature and, optionally, the model.
/// Stored in AppSettings.Personas.
/// </summary>
public sealed class Persona
{
    public const string DefaultName = "Default";
    public const int MaxNameLength = 40;
    // Same range as the Speech rate slider.
    public const int MinSpeechRate = -5;
    public const int MaxSpeechRate = 5;
    // Same range as the Temperature slider.
    public const double MinTemperature = 0;
    public const double MaxTemperature = 2;

    public string Name { get; set; } = "";
    public string SystemPrompt { get; set; } = "";
    public string VoiceName { get; set; } = "";
    public int SpeechRate { get; set; }
    /// <summary>Blank keeps whatever model is selected when the persona is picked.</summary>
    public string Model { get; set; } = "";
    /// <summary>
    /// The chat temperature. Null (personas saved before temperature was part of them) keeps the
    /// current temperature when the persona is picked.
    /// </summary>
    public double? Temperature { get; set; }

    public Persona Clone() => new()
    {
        Name = Name,
        SystemPrompt = SystemPrompt,
        VoiceName = VoiceName,
        SpeechRate = SpeechRate,
        Model = Model,
        Temperature = Temperature
    };
}

/// <summary>Validation and lookup helpers for the persona list. Nothing here touches WPF.</summary>
public static class PersonaCatalog
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Single-line, trimmed name of at most <see cref="Persona.MaxNameLength"/> characters.</summary>
    public static string CleanName(string? name)
    {
        var clean = Whitespace.Replace(name ?? "", " ").Trim();
        return clean.Length <= Persona.MaxNameLength ? clean : clean[..Persona.MaxNameLength].TrimEnd();
    }

    public static int ClampSpeechRate(int rate) => Math.Clamp(rate, Persona.MinSpeechRate, Persona.MaxSpeechRate);

    /// <summary>The temperature in the slider's range, rounded to two decimals; null stays null and NaN becomes null.</summary>
    public static double? ClampTemperature(double? temperature) =>
        temperature is double t && !double.IsNaN(t)
            ? Math.Round(Math.Clamp(t, Persona.MinTemperature, Persona.MaxTemperature), 2)
            : null;

    /// <summary>
    /// Copy of the list that is safe to use: no null entries, clean names, no blank or duplicate
    /// names (the first one wins, ignoring case), no null strings and a speech rate in range.
    /// </summary>
    public static List<Persona> Normalize(IEnumerable<Persona?>? personas)
    {
        var result = new List<Persona>();
        if (personas == null)
            return result;

        foreach (var persona in personas)
        {
            if (persona == null)
                continue;

            var name = CleanName(persona.Name);
            if (name.Length == 0 || Find(result, name) != null)
                continue;

            result.Add(new Persona
            {
                Name = name,
                SystemPrompt = persona.SystemPrompt ?? "",
                VoiceName = (persona.VoiceName ?? "").Trim(),
                SpeechRate = ClampSpeechRate(persona.SpeechRate),
                Model = (persona.Model ?? "").Trim(),
                Temperature = ClampTemperature(persona.Temperature)
            });
        }

        return result;
    }

    /// <summary>
    /// Normalizes the list and, when it is empty (first run or a damaged settings file), adds
    /// "Default" made from the current prompt, voice, rate and temperature. Never returns an empty list.
    /// </summary>
    public static List<Persona> EnsureDefault(IEnumerable<Persona?>? personas, string? systemPrompt, string? voiceName, int speechRate,
        double? temperature = null)
    {
        var result = Normalize(personas);
        if (result.Count == 0)
        {
            result.Add(new Persona
            {
                Name = Persona.DefaultName,
                SystemPrompt = systemPrompt ?? "",
                VoiceName = (voiceName ?? "").Trim(),
                SpeechRate = ClampSpeechRate(speechRate),
                Temperature = ClampTemperature(temperature)
            });
        }

        return result;
    }

    public static Persona? Find(IEnumerable<Persona>? personas, string? name)
    {
        var clean = CleanName(name);
        if (personas == null || clean.Length == 0)
            return null;

        return personas.FirstOrDefault(p => p != null && string.Equals(CleanName(p.Name), clean, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The persona called <paramref name="activeName"/>, or the first one when it no longer exists.</summary>
    public static Persona? ResolveActive(IReadOnlyList<Persona>? personas, string? activeName)
    {
        if (personas == null || personas.Count == 0)
            return null;

        return Find(personas, activeName) ?? personas[0];
    }

    /// <summary>Null when <paramref name="name"/> can be used for a new persona, otherwise why not.</summary>
    public static string? ValidateNewName(IEnumerable<Persona>? personas, string? name)
    {
        var clean = CleanName(name);
        if (clean.Length == 0)
            return "Type a name for the new persona first.";

        var existing = Find(personas, clean);
        return existing == null
            ? null
            : $"A persona called \"{existing.Name}\" already exists. Pick it and use Update persona to change it.";
    }

    /// <summary>The last persona cannot be deleted, so there is always one to fall back to.</summary>
    public static bool CanDelete(IReadOnlyCollection<Persona>? personas) => personas != null && personas.Count > 1;

    /// <summary>
    /// The entry of <paramref name="available"/> to select for a saved voice name: an exact match
    /// (ignoring case), otherwise the entry with the same voice id, which is the part before "(",
    /// so "am_onyx (American Male)" still matches "am_onyx" after the voice list is reloaded.
    /// Null when there is no match.
    /// </summary>
    public static string? MatchVoice(IEnumerable<string?>? available, string? wanted)
    {
        var target = (wanted ?? "").Trim();
        if (available == null || target.Length == 0)
            return null;

        var voices = available.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
        var exact = voices.FirstOrDefault(v => string.Equals(v.Trim(), target, StringComparison.OrdinalIgnoreCase));
        if (exact != null)
            return exact;

        var id = VoiceId(target);
        return id.Length == 0
            ? null
            : voices.FirstOrDefault(v => string.Equals(VoiceId(v), id, StringComparison.OrdinalIgnoreCase));
    }

    private static string VoiceId(string voice)
    {
        var paren = voice.IndexOf('(');
        return (paren >= 0 ? voice[..paren] : voice).Trim();
    }
}
