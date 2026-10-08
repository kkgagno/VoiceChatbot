using System;
using System.Linq;

namespace VoiceChatbot;

public readonly record struct VideoSize(int Width, int Height);

/// <param name="Seconds">Length that is generated: the request clamped to the supported range,
/// shortened to whole seconds that fit in <see cref="LtxVideoSizing.MaxFrames"/>.</param>
/// <param name="Fps">Frame rate after clamping.</param>
/// <param name="Frames">Frames generated: always <paramref name="Seconds"/> x <paramref name="Fps"/>.</param>
public readonly record struct VideoLength(int Seconds, int Fps, int Frames);

/// <summary>Output size and length for the LTX image-to-video workflows.</summary>
public static class LtxVideoSizing
{
    public const int DefaultWidth = 768;
    public const int DefaultHeight = 1344;
    public const int Multiple = 32;
    public const int MaxFrames = 720;
    public const int MaxSeconds = 30;
    public const int MaxFps = 60;

    // Very wide or tall sources are fitted to at most 3:1 so neither side gets too small.
    private const double MinAspect = 1.0 / 3.0;
    private const double MaxAspect = 3.0;

    /// <summary>
    /// A size with the source image's aspect ratio, both sides multiples of 32, using no more pixels
    /// than the default 768x1344 budget. Unknown source sizes keep the default.
    /// </summary>
    public static VideoSize FitToSourceAspect(int sourceWidth, int sourceHeight)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
            return new VideoSize(DefaultWidth, DefaultHeight);

        var budget = (long)DefaultWidth * DefaultHeight;
        var aspect = Math.Clamp(sourceWidth / (double)sourceHeight, MinAspect, MaxAspect);
        var idealWidth = Math.Sqrt(budget * aspect);
        var width = RoundToMultiple(idealWidth);
        var height = RoundToMultiple(idealWidth / aspect);

        // Rounding up can overshoot the budget; step down whichever way keeps the shape closest.
        while ((long)width * height > budget && (width > Multiple || height > Multiple))
        {
            var fitting = new[]
                {
                    (Width: width - Multiple, Height: height),
                    (Width: width, Height: height - Multiple),
                    (Width: width - Multiple, Height: height - Multiple)
                }
                .Where(c => c.Width >= Multiple && c.Height >= Multiple && (long)c.Width * c.Height <= budget)
                .OrderBy(c => AspectError(c.Width, c.Height, aspect))
                .ThenByDescending(c => (long)c.Width * c.Height)
                .ToList();
            if (fitting.Count > 0)
            {
                (width, height) = fitting[0];
                break;
            }

            width = Math.Max(Multiple, width - Multiple);
            height = Math.Max(Multiple, height - Multiple);
        }

        return new VideoSize(width, height);
    }

    /// <summary>
    /// Clamps the requested length and frame rate. When seconds x fps would exceed the frame cap,
    /// the length is cut to the whole seconds that fit, so the seconds/duration and frame-count
    /// inputs of the workflow agree with each other and with the length reported to the user.
    /// </summary>
    public static VideoLength ClampLength(int seconds, int fps)
    {
        fps = Math.Clamp(fps, 1, MaxFps);
        seconds = Math.Clamp(seconds, 1, MaxSeconds);
        seconds = Math.Min(seconds, Math.Max(1, MaxFrames / fps));
        return new VideoLength(seconds, fps, seconds * fps);
    }

    private static int RoundToMultiple(double value) =>
        Math.Max(Multiple, (int)Math.Round(value / Multiple, MidpointRounding.AwayFromZero) * Multiple);

    private static double AspectError(int width, int height, double aspect) =>
        Math.Abs(Math.Log(width / (double)height / aspect));
}
