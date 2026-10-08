using System.Text.Json;
using VoiceChatbot;
using Xunit;

public class FaceEmbeddingFormatTests
{
    [Fact]
    public void OldSFaceSamplesAreOutdated()
    {
        // Saved before the Model field existed, with the [0,1]-scaled BGR input.
        var old = new FaceEmbedding { Version = 1, Values = new float[128] };

        Assert.Equal(FaceEmbeddingFormats.SFaceModel, FaceEmbeddingFormats.ModelOf(old));
        Assert.True(FaceEmbeddingFormats.IsOutdated(old));
    }

    [Fact]
    public void LegacyVectorsAreNotOutdated()
    {
        var legacy = new FaceEmbedding { Version = 1, Values = new float[32 * 32] };

        Assert.Equal(FaceEmbeddingFormats.LegacyModel, FaceEmbeddingFormats.ModelOf(legacy));
        Assert.False(FaceEmbeddingFormats.IsOutdated(legacy));
    }

    [Fact]
    public void NewSFaceSamplesUseCurrentVersion()
    {
        var created = FaceEmbeddingFormats.Create(FaceEmbeddingFormats.SFaceModel, new float[128]);

        Assert.Equal(FaceEmbeddingFormats.CurrentSFaceVersion, created.Version);
        Assert.False(FaceEmbeddingFormats.IsOutdated(created));
    }

    [Fact]
    public void CountsUsableAndOutdatedSamples()
    {
        var profile = new LocalFaceProfile
        {
            Embeddings =
            {
                new FaceEmbedding { Version = 1, Values = new float[128] },
                FaceEmbeddingFormats.Create(FaceEmbeddingFormats.SFaceModel, new float[128]),
                FaceEmbeddingFormats.Create(FaceEmbeddingFormats.LegacyModel, new float[1024])
            }
        };

        Assert.Equal(1, FaceEmbeddingFormats.CountOutdated(profile));
        Assert.Equal(2, FaceEmbeddingFormats.CountUsable(profile, activeModel: null));
        Assert.Equal(1, FaceEmbeddingFormats.CountUsable(profile, FaceEmbeddingFormats.SFaceModel));
        Assert.Equal(1, FaceEmbeddingFormats.CountUsable(profile, FaceEmbeddingFormats.LegacyModel));
    }
}

public class FaceMatchTests
{
    private const float Threshold = 0.36f;

    // Unit vector whose cosine similarity with Probe() is exactly `cosine`.
    private static float[] At(double cosine)
    {
        var values = new float[FaceEmbeddingFormats.SFaceEmbeddingLength];
        values[0] = (float)cosine;
        values[1] = (float)Math.Sqrt(1 - (cosine * cosine));
        return values;
    }

    private static FaceEmbedding Probe() => FaceEmbeddingFormats.Create(FaceEmbeddingFormats.SFaceModel, At(1));

    private static LocalFaceProfile Profile(string name, FaceProfileRole role, double cosine, int version = FaceEmbeddingFormats.CurrentSFaceVersion) => new()
    {
        DisplayName = name,
        Role = role,
        Embeddings =
        {
            new FaceEmbedding { Model = FaceEmbeddingFormats.SFaceModel, Version = version, Values = At(cosine) }
        }
    };

    [Fact]
    public void ClearLeadIsRecognizedWithTheProfileRole()
    {
        var result = FaceEmbeddingMath.Match(Probe(), new[]
        {
            Profile("Keith", FaceProfileRole.Owner, 0.30),
            Profile("Emma", FaceProfileRole.Child, 0.62)
        }, Threshold);

        Assert.True(result.IsRecognized);
        Assert.False(result.IsAmbiguous);
        Assert.Equal("Emma", result.DisplayName);
        Assert.Equal(FaceProfileRole.Child, result.Role);
        Assert.Equal(FaceIdentity.Child1, result.Identity);
        Assert.Equal("Keith", result.RunnerUpName);
        Assert.Equal(0.30f, result.RunnerUpSimilarity, 3);
    }

    [Fact]
    public void CloseCallBetweenProfilesIsAmbiguous()
    {
        var result = FaceEmbeddingMath.Match(Probe(), new[]
        {
            Profile("Keith", FaceProfileRole.Owner, 0.41),
            Profile("Emma", FaceProfileRole.Child, 0.40)
        }, Threshold);

        Assert.False(result.IsRecognized);
        Assert.True(result.IsAmbiguous);
        Assert.Equal("Keith", result.DisplayName);
        Assert.Equal("Emma", result.RunnerUpName);
    }

    [Fact]
    public void RunnerUpIsTrackedWhicheverOrderProfilesLoad()
    {
        var result = FaceEmbeddingMath.Match(Probe(), new[]
        {
            Profile("Emma", FaceProfileRole.Child, 0.40),
            Profile("Liam", FaceProfileRole.Child, 0.10),
            Profile("Keith", FaceProfileRole.Owner, 0.43)
        }, Threshold);

        Assert.True(result.IsAmbiguous);
        Assert.Equal("Keith", result.DisplayName);
        Assert.Equal("Emma", result.RunnerUpName);
    }

    [Fact]
    public void SingleProfileAboveThresholdIsRecognized()
    {
        var result = FaceEmbeddingMath.Match(Probe(), new[] { Profile("Keith", FaceProfileRole.Owner, 0.40) }, Threshold);

        Assert.True(result.IsRecognized);
        Assert.Equal(FaceIdentity.Keith, result.Identity);
    }

    [Fact]
    public void BelowThresholdIsNotRecognizedOrAmbiguous()
    {
        var result = FaceEmbeddingMath.Match(Probe(), new[]
        {
            Profile("Keith", FaceProfileRole.Owner, 0.30),
            Profile("Emma", FaceProfileRole.Child, 0.29)
        }, Threshold);

        Assert.False(result.IsRecognized);
        Assert.False(result.IsAmbiguous);
        Assert.Equal("Keith", result.DisplayName);
    }

    [Fact]
    public void OutdatedSamplesNeverMatch()
    {
        var result = FaceEmbeddingMath.Match(Probe(), new[] { Profile("Keith", FaceProfileRole.Owner, 1.0, version: 1) }, Threshold);

        Assert.False(result.IsRecognized);
        Assert.Equal("", result.DisplayName);
        Assert.Equal(FaceIdentity.Unknown, result.Identity);
    }

    [Fact]
    public void GuestRoleMapsToUnknownPolicyIdentity()
    {
        var result = FaceEmbeddingMath.Match(Probe(), new[] { Profile("Grandma", FaceProfileRole.Guest, 0.9) }, Threshold);

        Assert.True(result.IsRecognized);
        Assert.Equal(FaceIdentity.Unknown, result.Identity);
    }
}

public class FaceProfileRoleTests
{
    [Theory]
    [InlineData(FaceIdentity.Keith, FaceProfileRole.Owner)]
    [InlineData(FaceIdentity.Child1, FaceProfileRole.Child)]
    [InlineData(FaceIdentity.Child2, FaceProfileRole.Child)]
    [InlineData(FaceIdentity.Unknown, FaceProfileRole.Guest)]
    public void MigratesProfilesSavedBeforeRoles(FaceIdentity legacyIdentity, FaceProfileRole expected)
    {
        var profile = new LocalFaceProfile { DisplayName = "Someone", Identity = legacyIdentity };

        Assert.True(FaceProfileRoles.Migrate(profile));
        Assert.Equal(expected, profile.Role);
        Assert.True(profile.RoleInferred);
        Assert.Equal(FaceProfileRoles.ToPolicyIdentity(expected), profile.Identity);
    }

    [Fact]
    public void ExplicitRoleWinsOverNameAndOldIdentity()
    {
        var profile = new LocalFaceProfile { DisplayName = "Emma", Identity = FaceIdentity.Unknown, Role = FaceProfileRole.Child };

        Assert.False(FaceProfileRoles.Migrate(profile));
        Assert.False(profile.RoleInferred);
        Assert.Equal(FaceIdentity.Child1, profile.Identity);
    }

    [Fact]
    public void RoleIsStoredAsTextAndOldFilesHaveNone()
    {
        var json = JsonSerializer.Serialize(new LocalFaceProfile { DisplayName = "Emma", Role = FaceProfileRole.Child, RoleInferred = true });
        Assert.Contains("\"Role\":\"Child\"", json);
        Assert.DoesNotContain("RoleInferred", json);
        Assert.DoesNotContain("EffectiveRole", json);
        Assert.Equal(FaceProfileRole.Child, JsonSerializer.Deserialize<LocalFaceProfile>(json)!.Role);

        var legacy = JsonSerializer.Deserialize<LocalFaceProfile>("{\"DisplayName\":\"Keith\",\"Identity\":0,\"Embeddings\":[{\"Version\":1,\"Values\":[0.5]}]}")!;
        Assert.Null(legacy.Role);
        Assert.Equal(FaceProfileRole.Owner, legacy.EffectiveRole);
        Assert.Equal("", legacy.Embeddings[0].Model);
    }

    [Theory]
    [InlineData("Keith", false, FaceProfileRole.Owner)]
    [InlineData("Emma", true, FaceProfileRole.Guest)]
    [InlineData("Child 2", true, FaceProfileRole.Child)]
    public void SuggestsARoleForNewProfiles(string name, bool ownerExists, FaceProfileRole expected) =>
        Assert.Equal(expected, FaceProfileRoles.SuggestForNewProfile(name, ownerExists));
}

public class FaceScanFreshnessTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoScanIsNeverFresh() =>
        Assert.False(FaceScanFreshness.IsFresh(DateTime.MinValue, Now, 300));

    [Theory]
    [InlineData(0, true)]
    [InlineData(299, true)]
    [InlineData(300, false)]
    [InlineData(3600, false)]
    [InlineData(-5, false)] // clock moved back past the scan
    public void ExpiresAfterTheConfiguredTime(int ageSeconds, bool fresh) =>
        Assert.Equal(fresh, FaceScanFreshness.IsFresh(Now.AddSeconds(-ageSeconds), Now, 300));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTimeoutUsesTheDefault(int timeoutSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(FaceScanFreshness.DefaultTimeoutSeconds), FaceScanFreshness.EffectiveTimeout(timeoutSeconds));
        Assert.True(FaceScanFreshness.IsFresh(Now.AddSeconds(-60), Now, timeoutSeconds));
    }

    [Fact]
    public void UnverifiedWithCameraOnIsTreatedAsNoFace()
    {
        Assert.Equal(FacePresenceState.NoFaceDetected, FaceScanFreshness.UnverifiedState(cameraFeaturesEnabled: true));
        Assert.Equal(FacePresenceState.CameraUnavailable, FaceScanFreshness.UnverifiedState(cameraFeaturesEnabled: false));
    }
}
