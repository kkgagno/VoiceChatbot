using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using OpenCvSharp;

namespace VoiceChatbot;

public sealed class HaarCascadeFaceDetectionService : IFaceDetectionService, IDisposable
{
    private readonly List<CascadeClassifier> _classifiers = new();
    private readonly CascadeClassifier? _eyeClassifier;

    public HaarCascadeFaceDetectionService(FaceModelOptions options)
    {
        foreach (var cascadePath in GetCascadePaths(options))
        {
            if (!File.Exists(cascadePath))
            {
                Debug.WriteLine($"Face cascade not found: {cascadePath}");
                continue;
            }

            var classifier = new CascadeClassifier(cascadePath);
            if (classifier.Empty())
            {
                Debug.WriteLine($"Face cascade could not be loaded: {cascadePath}");
                classifier.Dispose();
                continue;
            }

            _classifiers.Add(classifier);
        }

        var primaryPath = ResolveModelPath(options.HaarCascadePath);
        var modelDirectory = Path.GetDirectoryName(primaryPath);
        var eyePath = string.IsNullOrWhiteSpace(modelDirectory)
            ? ""
            : Path.Combine(modelDirectory, "haarcascade_eye_tree_eyeglasses.xml");
        if (File.Exists(eyePath))
        {
            _eyeClassifier = new CascadeClassifier(eyePath);
            if (_eyeClassifier.Empty())
            {
                _eyeClassifier.Dispose();
                _eyeClassifier = null;
            }
        }
    }

    public FaceDetectionResult DetectFaces(Mat frame)
    {
        if (_classifiers.Count == 0 || frame.Empty())
            return new FaceDetectionResult { State = FacePresenceState.CameraUnavailable };

        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.EqualizeHist(gray, gray);

        var minSide = Math.Max(28, Math.Min(frame.Width, frame.Height) / 12);
        var rawFaces = _classifiers
            .SelectMany(classifier => classifier.DetectMultiScale(
                gray,
                scaleFactor: 1.06,
                minNeighbors: 3,
                flags: HaarDetectionTypes.ScaleImage,
                minSize: new OpenCvSharp.Size(minSide, minSide)))
            .ToArray();

        var plausibleFaces = MergeOverlappingFaces(rawFaces)
            .Where(face => IsPlausibleFace(face, frame.Width, frame.Height))
            .OrderByDescending(face => face.Width * face.Height)
            .ToArray();
        var eyeConfirmedFaces = plausibleFaces
            .Where(face => HasTwoEyes(gray, face))
            .ToArray();
        var faces = eyeConfirmedFaces.Length > 0 ? eyeConfirmedFaces : plausibleFaces;

        return new FaceDetectionResult
        {
            Faces = faces,
            State = faces.Length switch
            {
                0 => FacePresenceState.NoFaceDetected,
                1 => FacePresenceState.FaceDetected,
                _ => FacePresenceState.MultipleFacesDetected
            }
        };
    }

    private static string ResolveModelPath(string path)
    {
        if (Path.IsPathRooted(path)) return path;
        return Path.Combine(AppContext.BaseDirectory, path);
    }

    private static IEnumerable<string> GetCascadePaths(FaceModelOptions options)
    {
        var primary = ResolveModelPath(options.HaarCascadePath);
        yield return primary;

        var directory = Path.GetDirectoryName(primary);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            yield break;

        var alt2 = Path.Combine(directory, "haarcascade_frontalface_alt2.xml");
        if (File.Exists(alt2) && !string.Equals(alt2, primary, StringComparison.OrdinalIgnoreCase))
            yield return alt2;
    }

    private bool HasTwoEyes(Mat grayFrame, Rect face)
    {
        if (_eyeClassifier == null)
            return true;

        var upperFace = new Rect(
            face.X,
            face.Y,
            face.Width,
            Math.Max(1, (int)(face.Height * 0.65)));

        using var faceGray = new Mat(grayFrame, Clamp(upperFace, grayFrame.Width, grayFrame.Height));
        var minEyeWidth = Math.Max(10, face.Width / 9);
        var eyes = _eyeClassifier.DetectMultiScale(
            faceGray,
            scaleFactor: 1.08,
            minNeighbors: 3,
            flags: HaarDetectionTypes.ScaleImage,
            minSize: new Size(minEyeWidth, Math.Max(8, face.Height / 16)));

        if (eyes.Length < 2)
            return false;

        var eyeCenters = eyes
            .Select(eye => new Point2f(eye.X + (eye.Width / 2f), eye.Y + (eye.Height / 2f)))
            .OrderBy(point => point.X)
            .ToArray();

        for (var i = 0; i < eyeCenters.Length; i++)
        {
            for (var j = i + 1; j < eyeCenters.Length; j++)
            {
                var dx = Math.Abs(eyeCenters[j].X - eyeCenters[i].X);
                var dy = Math.Abs(eyeCenters[j].Y - eyeCenters[i].Y);
                if (dx >= face.Width * 0.18 && dx <= face.Width * 0.75 && dy <= face.Height * 0.22)
                    return true;
            }
        }

        return false;
    }

    private static bool IsPlausibleFace(Rect face, int frameWidth, int frameHeight)
    {
        var aspect = face.Width / (double)Math.Max(1, face.Height);
        var frameArea = frameWidth * frameHeight;
        var faceArea = face.Width * face.Height;

        if (aspect < 0.65 || aspect > 1.35)
            return false;

        if (faceArea < frameArea * 0.015 || faceArea > frameArea * 0.75)
            return false;

        if (face.Y > frameHeight * 0.72)
            return false;

        return true;
    }

    private static Rect Clamp(Rect rect, int width, int height)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, width - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, height - 1));
        var right = Math.Clamp(rect.X + rect.Width, x + 1, width);
        var bottom = Math.Clamp(rect.Y + rect.Height, y + 1, height);
        return new Rect(x, y, right - x, bottom - y);
    }

    private static Rect[] MergeOverlappingFaces(IEnumerable<Rect> rawFaces)
    {
        var sorted = rawFaces
            .OrderByDescending(face => face.Width * face.Height)
            .ToList();

        var merged = new List<Rect>();
        foreach (var face in sorted)
        {
            var matchIndex = merged.FindIndex(existing => OverlapRatio(existing, face) >= 0.25);
            if (matchIndex >= 0)
            {
                merged[matchIndex] = Union(merged[matchIndex], face);
                continue;
            }

            merged.Add(face);
        }

        return merged
            .OrderByDescending(face => face.Width * face.Height)
            .ToArray();
    }

    private static double OverlapRatio(Rect left, Rect right)
    {
        var x1 = Math.Max(left.X, right.X);
        var y1 = Math.Max(left.Y, right.Y);
        var x2 = Math.Min(left.X + left.Width, right.X + right.Width);
        var y2 = Math.Min(left.Y + left.Height, right.Y + right.Height);

        var intersectionWidth = Math.Max(0, x2 - x1);
        var intersectionHeight = Math.Max(0, y2 - y1);
        var intersection = intersectionWidth * intersectionHeight;
        if (intersection == 0) return 0;

        var smallerArea = Math.Min(left.Width * left.Height, right.Width * right.Height);
        return smallerArea <= 0 ? 0 : (double)intersection / smallerArea;
    }

    private static Rect Union(Rect left, Rect right)
    {
        var x1 = Math.Min(left.X, right.X);
        var y1 = Math.Min(left.Y, right.Y);
        var x2 = Math.Max(left.X + left.Width, right.X + right.Width);
        var y2 = Math.Max(left.Y + left.Height, right.Y + right.Height);
        return new Rect(x1, y1, x2 - x1, y2 - y1);
    }

    public void Dispose()
    {
        foreach (var classifier in _classifiers)
            classifier.Dispose();
        _classifiers.Clear();
        _eyeClassifier?.Dispose();
    }
}
