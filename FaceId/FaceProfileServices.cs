using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp.Dnn;
using OpenCvSharp;

namespace VoiceChatbot;

public interface IFaceProfileStore
{
    Task<IReadOnlyList<LocalFaceProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default);
    Task SaveProfileAsync(LocalFaceProfile profile, CancellationToken cancellationToken = default);
}

public sealed class JsonFaceProfileStore : IFaceProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly string _profileDirectory;

    public JsonFaceProfileStore(string profileDirectory)
    {
        _profileDirectory = profileDirectory;
    }

    public async Task<IReadOnlyList<LocalFaceProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_profileDirectory)) return Array.Empty<LocalFaceProfile>();

        var profiles = new List<LocalFaceProfile>();
        foreach (var file in Directory.EnumerateFiles(_profileDirectory, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                var profile = JsonSerializer.Deserialize<LocalFaceProfile>(json, JsonOptions);
                if (profile != null)
                {
                    if (string.IsNullOrWhiteSpace(profile.DisplayName))
                        profile.DisplayName = profile.Identity == FaceIdentity.Unknown ? Path.GetFileNameWithoutExtension(file) : profile.Identity.ToString();
                    if (string.IsNullOrWhiteSpace(profile.ProfileId))
                        profile.ProfileId = FaceProfileNames.ToProfileId(profile.DisplayName);
                    profiles.Add(profile);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Face profile load failed for {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return profiles;
    }

    public async Task SaveProfileAsync(LocalFaceProfile profile, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_profileDirectory);
        profile.UpdatedUtc = DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(profile.DisplayName))
            profile.DisplayName = profile.Identity == FaceIdentity.Unknown ? "Unknown" : profile.Identity.ToString();
        profile.ProfileId = FaceProfileNames.ToProfileId(profile.DisplayName);
        var path = Path.Combine(_profileDirectory, $"{profile.ProfileId}.json");
        var json = JsonSerializer.Serialize(profile, JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
    }
}

public static class FaceProfileNames
{
    public static string NormalizeDisplayName(string displayName)
    {
        var trimmed = (displayName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new InvalidOperationException("Enter a profile name before saving a face.");

        return trimmed.Length > 40 ? trimmed[..40].Trim() : trimmed;
    }

    public static string ToProfileId(string displayName)
    {
        var normalized = NormalizeDisplayName(displayName);
        var safe = new string(normalized
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray());
        safe = string.Join("_", safe.Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(safe) ? "profile" : safe.ToLowerInvariant();
    }

    public static FaceIdentity ToPolicyIdentity(string displayName)
    {
        var normalized = NormalizeDisplayName(displayName);
        if (string.Equals(normalized, "Keith", StringComparison.OrdinalIgnoreCase))
            return FaceIdentity.Keith;

        return normalized.StartsWith("child", StringComparison.OrdinalIgnoreCase)
            ? FaceIdentity.Child1
            : FaceIdentity.Unknown;
    }
}

public interface IFaceEnrollmentService
{
    Task CaptureSamplesAsync(FaceIdentity identity, int sampleCount, CancellationToken cancellationToken = default);
    Task SaveEnrollmentAsync(FaceIdentity identity, CancellationToken cancellationToken = default);
}

public interface IFaceRecognitionService
{
    Task<FaceRecognitionResult> RecognizeAsync(Mat currentFace, CancellationToken cancellationToken = default);
}

public sealed class FaceEnrollmentService : IFaceEnrollmentService
{
    public Task CaptureSamplesAsync(FaceIdentity identity, int sampleCount, CancellationToken cancellationToken = default)
    {
        // TODO: Capture several cropped face samples, generate embeddings, and keep them local until saved.
        Debug.WriteLine($"Face enrollment capture requested for {identity}; implementation pending.");
        return Task.CompletedTask;
    }

    public Task SaveEnrollmentAsync(FaceIdentity identity, CancellationToken cancellationToken = default)
    {
        // TODO: Save generated embeddings through IFaceProfileStore. Do not save raw face photos as secrets.
        Debug.WriteLine($"Face enrollment save requested for {identity}; implementation pending.");
        return Task.CompletedTask;
    }
}

public sealed class FaceIdentityManager
{
    private readonly IFaceProfileStore _profileStore;
    private readonly FaceModelOptions _options;
    private readonly SFaceEmbeddingGenerator _embeddingGenerator;

    public FaceIdentityManager(IFaceProfileStore profileStore, FaceModelOptions options)
    {
        _profileStore = profileStore;
        _options = options;
        _embeddingGenerator = new SFaceEmbeddingGenerator(options);
    }

    public async Task<int> EnrollSampleAsync(string displayName, Mat frame, Rect faceBounds, CancellationToken cancellationToken = default)
    {
        displayName = FaceProfileNames.NormalizeDisplayName(displayName);
        var profileId = FaceProfileNames.ToProfileId(displayName);
        var identity = FaceProfileNames.ToPolicyIdentity(displayName);

        var embedding = _embeddingGenerator.CreateEmbedding(frame, faceBounds);
        var profiles = (await _profileStore.LoadProfilesAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var profile = profiles.FirstOrDefault(p => string.Equals(p.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))
            ?? new LocalFaceProfile
        {
            ProfileId = profileId,
            Identity = identity,
            DisplayName = displayName
        };

        profile.DisplayName = displayName;
        profile.Identity = identity;
        profile.Embeddings.Add(new FaceEmbedding { Values = embedding });
        await _profileStore.SaveProfileAsync(profile, cancellationToken).ConfigureAwait(false);
        return profile.Embeddings.Count;
    }

    public async Task<int> CountSamplesAsync(string displayName, CancellationToken cancellationToken = default)
    {
        displayName = FaceProfileNames.NormalizeDisplayName(displayName);
        var profileId = FaceProfileNames.ToProfileId(displayName);
        var profiles = await _profileStore.LoadProfilesAsync(cancellationToken).ConfigureAwait(false);
        return profiles.FirstOrDefault(profile => string.Equals(profile.ProfileId, profileId, StringComparison.OrdinalIgnoreCase))?.Embeddings.Count ?? 0;
    }

    public async Task<IReadOnlyList<LocalFaceProfile>> LoadProfilesAsync(CancellationToken cancellationToken = default)
    {
        return await _profileStore.LoadProfilesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ClearAllProfilesAsync(CancellationToken cancellationToken = default)
    {
        var directory = _options.ProfileDirectory;
        if (!Directory.Exists(directory))
            return Task.CompletedTask;

        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            File.Delete(file);

        return Task.CompletedTask;
    }

    public async Task<FaceRecognitionResult> RecognizeAsync(Mat frame, Rect faceBounds, CancellationToken cancellationToken = default)
    {
        var embedding = _embeddingGenerator.CreateEmbedding(frame, faceBounds);
        var profiles = await _profileStore.LoadProfilesAsync(cancellationToken).ConfigureAwait(false);
        return FaceEmbeddingMath.Match(embedding, profiles, _options.RecognitionThreshold);
    }
}

public sealed class SFaceEmbeddingGenerator
{
    private readonly FaceModelOptions _options;
    private readonly Lazy<Net?> _model;
    private readonly Lazy<CascadeClassifier?> _eyeClassifier;

    public SFaceEmbeddingGenerator(FaceModelOptions options)
    {
        _options = options;
        _model = new Lazy<Net?>(LoadModel);
        _eyeClassifier = new Lazy<CascadeClassifier?>(LoadEyeClassifier);
    }

    public float[] CreateEmbedding(Mat frame, Rect faceBounds)
    {
        var model = _model.Value;
        if (model == null)
            return LegacyFaceEmbeddingGenerator.CreateEmbedding(frame, faceBounds);

        var safeBounds = LegacyFaceEmbeddingGenerator.Clamp(faceBounds, frame.Width, frame.Height, expandRatio: 0.18);
        if (safeBounds.Width <= 0 || safeBounds.Height <= 0)
            throw new InvalidOperationException("No valid face crop is available.");

        using var face = new Mat(frame, safeBounds);
        using var aligned = AlignFace(face);
        using var resized = Letterbox(aligned, 112, 112);
        using var blob = CvDnn.BlobFromImage(
            resized,
            1.0 / 255.0,
            new Size(112, 112),
            new Scalar(0, 0, 0),
            swapRB: false,
            crop: false);

        model.SetInput(blob);
        using var output = model.Forward();
        output.GetArray(out float[] values);
        return Normalize(values);
    }

    private Mat AlignFace(Mat face)
    {
        var classifier = _eyeClassifier.Value;
        if (classifier == null)
            return face.Clone();

        try
        {
            using var gray = new Mat();
            Cv2.CvtColor(face, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.EqualizeHist(gray, gray);

            var upperFace = new Rect(0, 0, gray.Width, Math.Max(1, (int)(gray.Height * 0.68)));
            using var upperGray = new Mat(gray, upperFace);
            var eyes = classifier.DetectMultiScale(
                    upperGray,
                    scaleFactor: 1.08,
                    minNeighbors: 3,
                    flags: HaarDetectionTypes.ScaleImage,
                    minSize: new Size(Math.Max(12, gray.Width / 12), Math.Max(8, gray.Height / 18)))
                .Select(rect => new Rect(rect.X, rect.Y, rect.Width, rect.Height))
                .OrderByDescending(rect => rect.Width * rect.Height)
                .Take(8)
                .ToArray();

            if (eyes.Length < 2)
                return face.Clone();

            var pair = SelectEyePair(eyes);
            if (pair == null)
                return face.Clone();

            var (leftEye, rightEye) = pair.Value;
            var leftCenter = new Point2f(leftEye.X + (leftEye.Width / 2f), leftEye.Y + (leftEye.Height / 2f));
            var rightCenter = new Point2f(rightEye.X + (rightEye.Width / 2f), rightEye.Y + (rightEye.Height / 2f));
            var midpoint = new Point2f((leftCenter.X + rightCenter.X) / 2f, (leftCenter.Y + rightCenter.Y) / 2f);
            var angle = Math.Atan2(rightCenter.Y - leftCenter.Y, rightCenter.X - leftCenter.X) * 180.0 / Math.PI;

            using var rotation = Cv2.GetRotationMatrix2D(midpoint, angle, 1.0);
            var aligned = new Mat();
            Cv2.WarpAffine(face, aligned, rotation, face.Size(), InterpolationFlags.Linear, BorderTypes.Replicate);
            return aligned;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Face alignment failed, using unaligned crop: {ex.Message}");
            return face.Clone();
        }
    }

    private static (Rect Left, Rect Right)? SelectEyePair(IReadOnlyList<Rect> eyes)
    {
        (Rect Left, Rect Right)? best = null;
        var bestScore = 0.0;

        for (var i = 0; i < eyes.Count; i++)
        {
            for (var j = i + 1; j < eyes.Count; j++)
            {
                var first = eyes[i];
                var second = eyes[j];
                var firstCenter = new Point2f(first.X + (first.Width / 2f), first.Y + (first.Height / 2f));
                var secondCenter = new Point2f(second.X + (second.Width / 2f), second.Y + (second.Height / 2f));
                var horizontal = Math.Abs(firstCenter.X - secondCenter.X);
                var vertical = Math.Abs(firstCenter.Y - secondCenter.Y);

                if (horizontal < 24 || vertical > horizontal * 0.45)
                    continue;

                var score = horizontal - vertical;
                if (score <= bestScore)
                    continue;

                bestScore = score;
                best = firstCenter.X <= secondCenter.X
                    ? (first, second)
                    : (second, first);
            }
        }

        return best;
    }

    private Net? LoadModel()
    {
        var modelPath = ResolveModelPath(_options.FaceEmbeddingModelPath);
        if (!File.Exists(modelPath))
        {
            Debug.WriteLine($"SFace model not found, using legacy face vectors: {modelPath}");
            return null;
        }

        try
        {
            var model = CvDnn.ReadNetFromOnnx(modelPath);
            if (model == null || model.Empty())
                return null;

            model.SetPreferableBackend(Backend.OPENCV);
            model.SetPreferableTarget(Target.CPU);
            return model;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"SFace model load failed, using legacy face vectors: {ex.Message}");
            return null;
        }
    }

    private static Mat Letterbox(Mat source, int width, int height)
    {
        var scale = Math.Min(width / (double)source.Width, height / (double)source.Height);
        var resizedWidth = Math.Max(1, (int)Math.Round(source.Width * scale));
        var resizedHeight = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(resizedWidth, resizedHeight));

        var output = new Mat(new Size(width, height), MatType.CV_8UC3, Scalar.Black);
        var x = (width - resizedWidth) / 2;
        var y = (height - resizedHeight) / 2;
        using var roi = new Mat(output, new Rect(x, y, resizedWidth, resizedHeight));
        resized.CopyTo(roi);
        return output;
    }

    private static float[] Normalize(float[] values)
    {
        double magnitude = 0;
        foreach (var value in values)
            magnitude += value * value;

        magnitude = Math.Sqrt(magnitude);
        if (magnitude <= 0.000001)
            return values;

        for (var i = 0; i < values.Length; i++)
            values[i] = (float)(values[i] / magnitude);

        return values;
    }

    private static string ResolveModelPath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(AppContext.BaseDirectory, path);
    }

    private static CascadeClassifier? LoadEyeClassifier()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "Models", "haarcascade_eye_tree_eyeglasses.xml");
        if (!File.Exists(path))
            return null;

        var classifier = new CascadeClassifier(path);
        if (!classifier.Empty())
            return classifier;

        classifier.Dispose();
        return null;
    }
}

public static class LegacyFaceEmbeddingGenerator
{
    private const int EmbeddingSize = 32;

    public static float[] CreateEmbedding(Mat frame, Rect faceBounds)
    {
        var safeBounds = Clamp(faceBounds, frame.Width, frame.Height);
        if (safeBounds.Width <= 0 || safeBounds.Height <= 0)
            throw new InvalidOperationException("No valid face crop is available.");

        using var face = new Mat(frame, safeBounds);
        using var gray = new Mat();
        Cv2.CvtColor(face, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray);

        using var resized = new Mat();
        Cv2.Resize(gray, resized, new Size(EmbeddingSize, EmbeddingSize));

        var values = new float[EmbeddingSize * EmbeddingSize];
        double sum = 0;
        for (var y = 0; y < EmbeddingSize; y++)
        {
            for (var x = 0; x < EmbeddingSize; x++)
            {
                var value = resized.At<byte>(y, x) / 255f;
                values[(y * EmbeddingSize) + x] = value;
                sum += value;
            }
        }

        var mean = sum / values.Length;
        double variance = 0;
        for (var i = 0; i < values.Length; i++)
            variance += Math.Pow(values[i] - mean, 2);

        var stdDev = Math.Sqrt(variance / values.Length);
        if (stdDev < 0.0001) stdDev = 1;

        for (var i = 0; i < values.Length; i++)
            values[i] = (float)((values[i] - mean) / stdDev);

        return values;
    }

    public static Rect Clamp(Rect rect, int width, int height, double expandRatio = 0)
    {
        var expandX = (int)Math.Round(rect.Width * expandRatio);
        var expandY = (int)Math.Round(rect.Height * expandRatio);
        var x = Math.Clamp(rect.X - expandX, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y - expandY, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(rect.X + rect.Width + expandX, x + 1, width);
        var bottom = Math.Clamp(rect.Y + rect.Height + expandY, y + 1, height);
        return new Rect(x, y, right - x, bottom - y);
    }
}

public sealed class FaceRecognitionService : IFaceRecognitionService
{
    private readonly IFaceProfileStore _profileStore;
    private readonly FaceModelOptions _options;

    public FaceRecognitionService(IFaceProfileStore profileStore, FaceModelOptions options)
    {
        _profileStore = profileStore;
        _options = options;
    }

    public async Task<FaceRecognitionResult> RecognizeAsync(Mat currentFace, CancellationToken cancellationToken = default)
    {
        // TODO: Replace this crop-vector matcher with IOnnxInferenceBackend once a local face embedding model is added.
        var profiles = await _profileStore.LoadProfilesAsync(cancellationToken).ConfigureAwait(false);
        _ = profiles;
        _ = _options;
        return new FaceRecognitionResult { Identity = FaceIdentity.Unknown, Similarity = 0f, IsRecognized = false };
    }
}

public static class FaceEmbeddingMath
{
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

    public static FaceRecognitionResult Match(float[] currentEmbedding, IEnumerable<LocalFaceProfile> profiles, float threshold)
    {
        var best = new FaceRecognitionResult();

        foreach (var profile in profiles)
        {
            var similarities = profile.Embeddings
                .Select(stored => CosineSimilarity(currentEmbedding, stored.Values))
                .OrderByDescending(score => score)
                .Take(5)
                .ToArray();

            if (similarities.Length == 0)
                continue;

            var strongest = similarities[0];
            var topAverage = similarities.Average();
            var stableScore = (strongest * 0.65f) + (topAverage * 0.35f);

            if (stableScore > best.Similarity)
                best = new FaceRecognitionResult
                {
                    Identity = profile.Identity,
                    DisplayName = string.IsNullOrWhiteSpace(profile.DisplayName) ? profile.Identity.ToString() : profile.DisplayName,
                    Similarity = stableScore,
                    IsRecognized = stableScore >= threshold
                };
        }

        return best;
    }
}
