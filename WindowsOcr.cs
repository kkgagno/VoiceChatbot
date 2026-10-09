using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace VoiceChatbot;

/// <summary>
/// Text recognition with the OCR engine built into Windows 10 and 11 (Windows.Media.Ocr), for
/// pictures and for scanned PDF pages rendered with Windows.Data.Pdf. Nothing has to be installed
/// except an OCR language, which normally comes with the Windows display language.
/// </summary>
internal static class WindowsOcr
{
    private const int ErrorWrongPassword = unchecked((int)0x8007052B);

    /// <summary>The text of a picture (multi-page TIFFs give "Page n" blocks), or why there is none.</summary>
    public sealed record ImageText(string Text, string Error, DocumentReadProblem Problem);

    /// <summary>
    /// An OCR engine for the user's Windows languages, else English, else any installed OCR
    /// language; null (with the reason) when none is installed or OCR is not available.
    /// </summary>
    public static OcrEngine? TryCreateEngine(out string error)
    {
        error = "";
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine != null)
                return engine;

            foreach (var tag in new[] { "en-US", "en-GB", "en" })
            {
                var language = new Language(tag);
                if (OcrEngine.IsLanguageSupported(language) && OcrEngine.TryCreateFromLanguage(language) is { } english)
                    return english;
            }

            foreach (var language in OcrEngine.AvailableRecognizerLanguages)
            {
                if (OcrEngine.TryCreateFromLanguage(language) is { } any)
                    return any;
            }

            error = DocumentFileTypes.OcrLanguageMissingMessage;
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or PlatformNotSupportedException or InvalidCastException or ArgumentException)
        {
            AppLog.Warn("Windows OCR is not available.", ex);
            error = "Windows text recognition (OCR) is not available on this PC: " + ex.Message;
        }

        return null;
    }

    /// <summary>Reads the text in a picture. Up to <paramref name="maxPages"/> frames of a multi-page TIFF are read.</summary>
    public static async Task<ImageText> RecognizeImageAsync(OcrEngine engine, string path, int maxPages, IProgress<string>? progress, CancellationToken ct)
    {
        var extension = Path.GetExtension(path);
        using var stream = await OpenReadAsync(path, ct);

        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No codec for the format (HEIC without HEIF Image Extensions, WebP without its extension) or a damaged file.
            AppLog.Warn($"Windows could not decode {Path.GetFileName(path)}.", ex);
            var problem = extension.ToLowerInvariant() is ".heic" or ".heif" or ".webp"
                ? DocumentReadProblem.NeedsImageExtension
                : DocumentReadProblem.Failed;
            return new ImageText("", DocumentFileTypes.GetImageDecodeMessage(extension, $"0x{ex.HResult:X8}"), problem);
        }

        // An animated GIF is one picture; a TIFF fax or scan can hold several pages.
        var isGif = extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
        var frameCount = isGif ? 1 : (int)Math.Min(decoder.FrameCount, (uint)Math.Max(1, maxPages));
        var pages = new List<(int Page, string Text)>();
        for (var i = 0; i < frameCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (frameCount > 1)
                progress?.Report($"OCR page {i + 1} of {frameCount}...");
            else
                progress?.Report("reading the text in the picture...");

            var frame = i == 0 ? null : await decoder.GetFrameAsync((uint)i).AsTask(ct);
            using var bitmap = await DecodeForOcrAsync(decoder, frame, ct);
            pages.Add((i + 1, await RecognizeAsync(engine, bitmap, ct)));
        }

        var text = frameCount > 1 ? OcrText.JoinPages(pages) : pages.FirstOrDefault().Text ?? "";
        if (!OcrText.HasWords(text))
            return new ImageText("", DocumentFileTypes.NoTextInImageMessage, DocumentReadProblem.NoTextInImage);

        if (decoder.FrameCount > frameCount && !isGif)
            text = $"[OCR read the first {frameCount} of {decoder.FrameCount} pages of this image (OCR is limited to {maxPages} pages).]\n\n{text}";
        return new ImageText(text, "", DocumentReadProblem.None);
    }

    /// <summary>
    /// Renders the given PDF pages (1-based) at about 200 DPI and reads them. Throws when Windows
    /// cannot open the PDF; <see cref="IsPasswordError"/> tells a password-protected file apart.
    /// </summary>
    public static async Task<Dictionary<int, string>> RecognizePdfPagesAsync(
        OcrEngine engine,
        string path,
        IReadOnlyList<int> pageNumbers,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var texts = new Dictionary<int, string>();
        using var stream = await OpenReadAsync(path, ct);
        var pdf = await PdfDocument.LoadFromStreamAsync(stream).AsTask(ct);
        var max = OcrEngine.MaxImageDimension;
        for (var i = 0; i < pageNumbers.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var number = pageNumbers[i];
            if (number < 1 || number > pdf.PageCount)
                continue;

            progress?.Report($"OCR page {i + 1} of {pageNumbers.Count}...");
            using var page = pdf.GetPage((uint)(number - 1));
            var (width, height) = OcrText.PdfRenderSize(page.Size.Width, page.Size.Height, OcrText.PdfRenderDpi, max);
            using var image = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(image, new PdfPageRenderOptions
            {
                DestinationWidth = width,
                DestinationHeight = height,
                IsIgnoringHighContrast = true
            }).AsTask(ct);

            image.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(image).AsTask(ct);
            using var bitmap = await DecodeForOcrAsync(decoder, null, ct);
            texts[number] = await RecognizeAsync(engine, bitmap, ct);
        }

        return texts;
    }

    public static bool IsPasswordError(Exception ex) => ex.HResult == ErrorWrongPassword;

    private static async Task<SoftwareBitmap> DecodeForOcrAsync(BitmapDecoder decoder, BitmapFrame? frame, CancellationToken ct)
    {
        var width = frame?.PixelWidth ?? decoder.PixelWidth;
        var height = frame?.PixelHeight ?? decoder.PixelHeight;

        // The OCR engine refuses images larger than MaxImageDimension: scale big photos down.
        var (scaledWidth, scaledHeight) = OcrText.FitWithin(width, height, OcrEngine.MaxImageDimension);
        var transform = new BitmapTransform();
        if (scaledWidth != width || scaledHeight != height)
        {
            transform.ScaledWidth = scaledWidth;
            transform.ScaledHeight = scaledHeight;
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        var operation = frame != null
            ? frame.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage)
            : decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        return await operation.AsTask(ct);
    }

    private static async Task<string> RecognizeAsync(OcrEngine engine, SoftwareBitmap bitmap, CancellationToken ct)
    {
        var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
        return string.Join("\n", result.Lines.Select(line => line.Text));
    }

    /// <summary>Opens a file for the WinRT decoders: through StorageFile, else a plain file stream.</summary>
    private static async Task<IRandomAccessStream> OpenReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
            return await file.OpenReadAsync().AsTask(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Some paths (long paths, some network shares) are refused by StorageFile.
            AppLog.Info($"StorageFile could not open {Path.GetFileName(path)} ({ex.Message}); using a file stream.");
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
            return stream.AsRandomAccessStream();
        }
    }
}
