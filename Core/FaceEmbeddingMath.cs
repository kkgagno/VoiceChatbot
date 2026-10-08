using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceChatbot;

public static class FaceEmbeddingMath
{
    /// <summary>Default gap required between the best and second-best profile before a match counts.</summary>
    public const float DefaultAmbiguityMargin = 0.05f;

    public static float CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count) return 0f;

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;

        for (var i = 0; i < left.Count; i++)
        {
            dot += left[i] * right[i];
            leftMagnitude += left[i] * left[i];
            rightMagnitude += right[i] * right[i];
        }

        var denominator = Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude);
        return denominator <= 0 ? 0f : (float)(dot / denominator);
    }

    /// <summary>
    /// Finds the closest profile. Only stored samples made by the same model and preprocessing as
    /// <paramref name="current"/> are compared. A match needs the threshold and a lead of at least
    /// <paramref name="margin"/> over the next profile; otherwise it is reported as ambiguous.
    /// </summary>
    public static FaceRecognitionResult Match(
        FaceEmbedding current,
        IEnumerable<LocalFaceProfile> profiles,
        float threshold,
        float margin = DefaultAmbiguityMargin)
    {
        FaceRecognitionResult? best = null;
        string runnerUpName = "";
        float? runnerUpScore = null;

        foreach (var profile in profiles)
        {
            var similarities = profile.Embeddings
                .Where(stored => FaceEmbeddingFormats.IsCompatible(stored, current))
                .Select(stored => CosineSimilarity(current.Values, stored.Values))
                .OrderByDescending(score => score)
                .Take(5)
                .ToArray();

            if (similarities.Length == 0)
                continue;

            var strongest = similarities[0];
            var topAverage = similarities.Average();
            var stableScore = (strongest * 0.65f) + (topAverage * 0.35f);
            var role = profile.EffectiveRole;
            var displayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Identity.ToString() : profile.DisplayName;

            if (best == null || stableScore > best.Similarity)
            {
                if (best != null)
                {
                    runnerUpName = best.DisplayName;
                    runnerUpScore = best.Similarity;
                }

                best = new FaceRecognitionResult
                {
                    Identity = FaceProfileRoles.ToPolicyIdentity(role),
                    Role = role,
                    DisplayName = displayName,
                    Similarity = stableScore
                };
            }
            else if (runnerUpScore == null || stableScore > runnerUpScore.Value)
            {
                runnerUpName = displayName;
                runnerUpScore = stableScore;
            }
        }

        if (best == null)
            return new FaceRecognitionResult();

        var clearsThreshold = best.Similarity >= threshold;
        var ambiguous = clearsThreshold
            && runnerUpScore.HasValue
            && best.Similarity - runnerUpScore.Value < Math.Max(0f, margin);

        best.RunnerUpName = runnerUpName;
        best.RunnerUpSimilarity = runnerUpScore ?? 0f;
        best.IsAmbiguous = ambiguous;
        best.IsRecognized = clearsThreshold && !ambiguous;
        return best;
    }
}
