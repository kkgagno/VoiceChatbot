using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.AcroForms.Fields;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using UglyToad.PdfPig.Tokens;

namespace VoiceChatbot;

/// <summary>
/// Reads the text layer of a PDF's pages as lines in reading order: PdfPig gives the words and their
/// boxes, <see cref="PdfLayoutText"/> lays them out by position, so a form's label and its amount end
/// up on one line even when the PDF draws them far apart. Values typed into a fillable form (text,
/// checkbox, radio and choice fields) are placed where their fields are. One instance per open
/// document; not thread-safe.
/// </summary>
internal sealed class PdfPageText
{
    // Groups letters into words by distance, whatever order the PDF draws them in. One thread: the
    // reader already runs on a worker thread, one page at a time.
    private static readonly NearestNeighbourWordExtractor WordExtractor = new(
        new NearestNeighbourWordExtractor.NearestNeighbourWordExtractorOptions { MaxDegreeOfParallelism = 1 });

    private const int MaxParentDepth = 32;

    private readonly PdfDocument _document;
    private readonly string _fileName;
    private readonly bool _hasForm;
    private bool _loggedWordProblem;
    private bool _loggedFormProblem;

    public PdfPageText(PdfDocument document, string path)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _fileName = Path.GetFileName(path);
        _hasForm = HasFormFields();
    }

    /// <summary>
    /// The page's text, and whether its printed text (without form values) is too thin to be anything
    /// but a scan, so OCR should read it.
    /// </summary>
    public (string Text, bool NeedsOcr) Read(Page page)
    {
        var words = GetWords(page, out var fallbackText);
        if (fallbackText != null)
            return (fallbackText, DocumentFileTypes.PdfPageNeedsOcr(fallbackText));

        var needsOcr = DocumentFileTypes.PdfPageNeedsOcr(string.Concat(words.Select(w => w.Text)));
        var fields = _hasForm ? GetFormValues(page) : Array.Empty<PdfLayoutWord>();
        if (fields.Count > 0)
            words = PdfLayoutText.WithFormValues(words, fields);
        return (PdfLayoutText.BuildText(words), needsOcr);
    }

    // ==================== Words ====================

    private List<PdfLayoutWord> GetWords(Page page, out string? fallbackText)
    {
        fallbackText = null;
        IEnumerable<Word> words;
        try
        {
            // Spaces only separate words, which the extractor finds by distance; leaving them out is faster.
            var letters = page.Letters.Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToList();
            words = WordExtractor.GetWords(letters).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOnce(ref _loggedWordProblem, $"PdfPig could not group the letters of page {page.Number} of {_fileName} into words ({ex.Message}); using its simpler word reader.");
            try
            {
                words = page.GetWords().ToList();
            }
            catch (Exception inner) when (inner is not OperationCanceledException)
            {
                LogOnce(ref _loggedWordProblem, $"PdfPig could not find the words of page {page.Number} of {_fileName}: {inner.Message}");
                fallbackText = page.Text ?? "";
                return new List<PdfLayoutWord>();
            }
        }

        var result = new List<PdfLayoutWord>();
        foreach (var word in words)
        {
            var text = WordText(word);
            if (string.IsNullOrWhiteSpace(text))
                continue;
            var (left, bottom, right, top) = Bounds(word.BoundingBox);
            result.Add(new PdfLayoutWord(text, left, bottom, right, top, Orientation(word.TextOrientation)));
        }

        return result;
    }

    // The nearest-neighbour extractor does not sort a word's letters, so a word whose letters the PDF
    // draws out of order would read scrambled: put them in reading order along the word.
    private static string WordText(Word word)
    {
        var letters = word.Letters;
        if (letters == null || letters.Count < 2)
            return word.Text ?? "";

        IEnumerable<Letter> ordered = word.TextOrientation switch
        {
            TextOrientation.Horizontal => letters.OrderBy(l => l.StartBaseLine.X),
            TextOrientation.Rotate180 => letters.OrderByDescending(l => l.StartBaseLine.X),
            TextOrientation.Rotate90 => letters.OrderByDescending(l => l.StartBaseLine.Y),
            TextOrientation.Rotate270 => letters.OrderBy(l => l.StartBaseLine.Y),
            _ => letters
        };
        return string.Concat(ordered.Select(l => l.Value));
    }

    private static PdfWordOrientation Orientation(TextOrientation orientation) => orientation switch
    {
        TextOrientation.Horizontal => PdfWordOrientation.Horizontal,
        TextOrientation.Rotate90 => PdfWordOrientation.Rotate90,
        TextOrientation.Rotate180 => PdfWordOrientation.Rotate180,
        TextOrientation.Rotate270 => PdfWordOrientation.Rotate270,
        _ => PdfWordOrientation.Other
    };

    // An upright box around a possibly turned rectangle (Left/Top/... are only meaningful when it is not turned).
    private static (double Left, double Bottom, double Right, double Top) Bounds(PdfRectangle box)
    {
        var xs = new[] { box.TopLeft.X, box.TopRight.X, box.BottomLeft.X, box.BottomRight.X };
        var ys = new[] { box.TopLeft.Y, box.TopRight.Y, box.BottomLeft.Y, box.BottomRight.Y };
        return (xs.Min(), ys.Min(), xs.Max(), ys.Max());
    }

    // ==================== Form fields ====================

    private bool HasFormFields()
    {
        try
        {
            return _document.TryGetForm(out var form) && form?.Fields.Count > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOnce(ref _loggedFormProblem, $"Could not read the form fields of {_fileName}; reading its printed text only. {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The filled-in values on a page, one per visible field widget, with the widget's box. Page
    /// annotations give each widget's box in the same coordinates as the words (page rotation and crop
    /// box applied) and work whether or not the field records its page; the value is the widget's own
    /// or inherited from its parent fields.
    /// </summary>
    private IReadOnlyList<PdfLayoutWord> GetFormValues(Page page)
    {
        var values = new List<PdfLayoutWord>();
        // A turned page turns its fields' text with it, like its own text.
        var orientation = page.Rotation.Value switch
        {
            90 => PdfWordOrientation.Rotate90,
            180 => PdfWordOrientation.Rotate180,
            270 => PdfWordOrientation.Rotate270,
            _ => PdfWordOrientation.Horizontal
        };
        try
        {
            foreach (var annotation in page.GetAnnotations())
            {
                if (annotation.Type != AnnotationType.Widget ||
                    (annotation.Flags & (AnnotationFlags.Hidden | AnnotationFlags.NoView)) != 0)
                    continue;

                var value = WidgetValue(annotation.AnnotationDictionary);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var (left, bottom, right, top) = Bounds(annotation.Rectangle);
                values.Add(new PdfLayoutWord(value, left, bottom, right, top, orientation));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOnce(ref _loggedFormProblem, $"Could not read the form values on page {page.Number} of {_fileName}: {ex.GetType().Name}: {ex.Message}");
        }

        return values;
    }

    private string? WidgetValue(DictionaryToken widget)
    {
        // Field type, flags, value and options can sit on the widget or on any parent field.
        NameToken? type = null;
        long? flags = null;
        IToken? value = null;
        IToken? options = null;
        var dictionary = widget;
        for (var depth = 0; dictionary != null && depth < MaxParentDepth; depth++)
        {
            if (type == null && dictionary.TryGet(NameToken.Ft, out var ft))
                type = Resolve(ft) as NameToken;
            if (flags == null && dictionary.TryGet(NameToken.Ff, out var ff) && Resolve(ff) is NumericToken number)
                flags = number.Long;
            if (value == null && dictionary.TryGet(NameToken.V, out var v))
                value = Resolve(v);
            if (options == null && dictionary.TryGet(NameToken.Opt, out var opt))
                options = Resolve(opt);
            dictionary = dictionary.TryGet(NameToken.Parent, out var parent) ? Resolve(parent) as DictionaryToken : null;
        }

        switch (type?.Data)
        {
            case "Tx":
                return ((AcroTextFieldFlags)(flags ?? 0)).HasFlag(AcroTextFieldFlags.Password) ? null : TextOf(value);
            case "Btn":
                var buttonFlags = (AcroButtonFieldFlags)(flags ?? 0);
                if (buttonFlags.HasFlag(AcroButtonFieldFlags.PushButton))
                    return null;
                // A checkbox or radio button is on when its widget shows an "on" state (its /AS), else when the field's value is not Off.
                var state = widget.TryGet(NameToken.As, out var appearance) ? (Resolve(appearance) as NameToken)?.Data : (value as NameToken)?.Data;
                return !string.IsNullOrEmpty(state) && !string.Equals(state, "Off", StringComparison.OrdinalIgnoreCase) ? "[X]" : null;
            case "Ch":
                var chosen = value is ArrayToken array
                    ? array.Data.Select(t => TextOf(Resolve(t))).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => DisplayName(s!, options)).ToList()
                    : new List<string> { DisplayName(TextOf(value) ?? "", options) };
                return string.Join(", ", chosen.Where(s => !string.IsNullOrWhiteSpace(s)));
            default:
                return null;
        }
    }

    // A choice field's value is the option's export value; show the option's name when it has one.
    private string DisplayName(string exportValue, IToken? options)
    {
        if (exportValue.Length == 0 || options is not ArrayToken list)
            return exportValue;

        foreach (var option in list.Data.Select(Resolve))
        {
            if (option is ArrayToken { Length: 2 } pair && TextOf(Resolve(pair[0])) == exportValue)
                return TextOf(Resolve(pair[1])) ?? exportValue;
        }

        return exportValue;
    }

    private static string? TextOf(IToken? token) => token switch
    {
        StringToken s => s.Data,
        HexToken h => h.Data,
        NameToken n => n.Data,
        NumericToken n => n.Data.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null
    };

    private IToken? Resolve(IToken? token)
    {
        for (var i = 0; token is IndirectReferenceToken reference && i < 8; i++)
            token = _document.Structure.GetObject(reference.Data)?.Data;
        return token;
    }

    // One log line per document and kind of problem, not one per page.
    private static void LogOnce(ref bool logged, string message)
    {
        if (logged)
            return;
        logged = true;
        AppLog.Info(message);
    }
}
