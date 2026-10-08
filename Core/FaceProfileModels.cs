using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace VoiceChatbot;

/// <summary>
/// What a recognized profile is allowed to do. Stored on the profile and chosen at enrollment,
/// so the policy no longer depends on the profile's name.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FaceProfileRole
{
    Guest,
    Owner,
    Child
}

public sealed class FaceEmbedding
{
    /// <summary>Preprocessing version for <see cref="Model"/>; see <see cref="FaceEmbeddingFormats"/>.</summary>
    public int Version { get; set; } = 1;

    /// <summary>"SFace" or "Legacy". Empty in files saved before this field existed.</summary>
    public string Model { get; set; } = "";

    public float[] Values { get; set; } = Array.Empty<float>();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class LocalFaceProfile
{
    public string ProfileId { get; set; } = "";

    /// <summary>
    /// Policy identity derived from <see cref="Role"/>. Still written so older builds can read the
    /// file, and read once to migrate profiles saved before roles existed.
    /// </summary>
    public FaceIdentity Identity { get; set; } = FaceIdentity.Unknown;

    /// <summary>Explicit role. Null in profiles saved before roles existed.</summary>
    public FaceProfileRole? Role { get; set; }

    public string DisplayName { get; set; } = "";
    public List<FaceEmbedding> Embeddings { get; set; } = new();
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>True when <see cref="Role"/> was filled in by migration and the user has not confirmed it yet.</summary>
    [JsonIgnore]
    public bool RoleInferred { get; set; }

    [JsonIgnore]
    public FaceProfileRole EffectiveRole => Role ?? FaceProfileRoles.FromLegacyIdentity(Identity);
}

public sealed class FaceRecognitionResult
{
    public FaceIdentity Identity { get; set; } = FaceIdentity.Unknown;
    public FaceProfileRole Role { get; set; } = FaceProfileRole.Guest;
    public string DisplayName { get; set; } = "";
    public float Similarity { get; set; }
    public bool IsRecognized { get; set; }

    /// <summary>True when the best profile cleared the threshold but the runner-up was too close to call.</summary>
    public bool IsAmbiguous { get; set; }

    public string RunnerUpName { get; set; } = "";
    public float RunnerUpSimilarity { get; set; }
}

public static class FaceProfileRoles
{
    /// <summary>Maps a role onto the policy identity slots the gate and prompt understand.</summary>
    public static FaceIdentity ToPolicyIdentity(FaceProfileRole role) => role switch
    {
        FaceProfileRole.Owner => FaceIdentity.Keith,
        FaceProfileRole.Child => FaceIdentity.Child1,
        _ => FaceIdentity.Unknown
    };

    /// <summary>
    /// Role for a profile saved before roles existed. Its Identity field holds what the old
    /// name-based rule produced, so this keeps that profile's behaviour until the user confirms a role.
    /// </summary>
    public static FaceProfileRole FromLegacyIdentity(FaceIdentity identity) => identity switch
    {
        FaceIdentity.Keith => FaceProfileRole.Owner,
        FaceIdentity.Child1 or FaceIdentity.Child2 => FaceProfileRole.Child,
        _ => FaceProfileRole.Guest
    };

    /// <summary>Fills in a missing role and keeps Identity in step with the role. Returns true if it migrated.</summary>
    public static bool Migrate(LocalFaceProfile profile)
    {
        var migrated = false;
        if (profile.Role is null)
        {
            profile.Role = FromLegacyIdentity(profile.Identity);
            profile.RoleInferred = true;
            migrated = true;
        }

        profile.Identity = ToPolicyIdentity(profile.Role.Value);
        return migrated;
    }

    /// <summary>Default choice offered when a new profile is enrolled. The user always confirms it.</summary>
    public static FaceProfileRole SuggestForNewProfile(string displayName, bool ownerProfileExists)
    {
        if ((displayName ?? "").Trim().StartsWith("child", StringComparison.OrdinalIgnoreCase))
            return FaceProfileRole.Child;

        return ownerProfileExists ? FaceProfileRole.Guest : FaceProfileRole.Owner;
    }

    public static string Describe(FaceProfileRole role) => role switch
    {
        FaceProfileRole.Owner => "owner",
        FaceProfileRole.Child => "child",
        _ => "guest"
    };
}

/// <summary>
/// Tracks which model and preprocessing produced an embedding, so vectors made with a different
/// pipeline are never compared against each other.
/// </summary>
public static class FaceEmbeddingFormats
{
    public const string SFaceModel = "SFace";
    public const string LegacyModel = "Legacy";
    public const int SFaceEmbeddingLength = 128;

    /// <summary>
    /// v1 fed SFace a [0,1]-scaled BGR image (almost no identity signal). v2 feeds raw 0-255 RGB,
    /// which is what the model's built-in normalization expects.
    /// </summary>
    public const int CurrentSFaceVersion = 2;
    public const int CurrentLegacyVersion = 1;

    public static string ModelOf(FaceEmbedding embedding)
    {
        if (!string.IsNullOrWhiteSpace(embedding.Model))
            return embedding.Model;

        // Files from before the Model field: SFace vectors are 128 floats, legacy ones 32x32.
        return embedding.Values.Length == SFaceEmbeddingLength ? SFaceModel : LegacyModel;
    }

    public static int CurrentVersionFor(string model) =>
        string.Equals(model, SFaceModel, StringComparison.Ordinal) ? CurrentSFaceVersion : CurrentLegacyVersion;

    public static FaceEmbedding Create(string model, float[] values) => new()
    {
        Model = model,
        Version = CurrentVersionFor(model),
        Values = values
    };

    public static bool IsOutdated(FaceEmbedding embedding) =>
        embedding.Version < CurrentVersionFor(ModelOf(embedding));

    public static bool IsCompatible(FaceEmbedding stored, FaceEmbedding current) =>
        string.Equals(ModelOf(stored), ModelOf(current), StringComparison.Ordinal)
        && stored.Version == current.Version
        && stored.Values.Length == current.Values.Length;

    /// <summary>
    /// Samples the active model can match against. <paramref name="activeModel"/> is null while the
    /// model is still loading; then every sample that is not outdated counts.
    /// </summary>
    public static int CountUsable(LocalFaceProfile profile, string? activeModel) =>
        profile.Embeddings.Count(embedding =>
            !IsOutdated(embedding)
            && (activeModel == null || string.Equals(ModelOf(embedding), activeModel, StringComparison.Ordinal)));

    public static int CountOutdated(LocalFaceProfile profile) => profile.Embeddings.Count(IsOutdated);
}
