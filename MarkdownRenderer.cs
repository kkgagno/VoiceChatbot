using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using Markdig;
using Markdig.Extensions.AutoLinks;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using MdTableColumnAlign = Markdig.Extensions.Tables.TableColumnAlign;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using WpfBlock = System.Windows.Documents.Block;
using WpfInline = System.Windows.Documents.Inline;
using WpfTable = System.Windows.Documents.Table;
using WpfTableCell = System.Windows.Documents.TableCell;
using WpfTableRow = System.Windows.Documents.TableRow;

namespace VoiceChatbot;

/// <summary>
/// Turns the Markdown prose of an assistant reply into a read-only, selectable RichTextBox styled
/// with the theme brushes: headings, paragraphs, bold/italic/strikethrough, inline code, bullet,
/// numbered, nested and task lists, block quotes, rules, tables and links (http, https and mailto
/// open in the default browser). Fenced code is not handled here: the chat splits it out first and
/// shows it with the code block UI and its Copy button.
/// </summary>
internal sealed class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
        .UseTaskLists()
        .UseAutoLinks(new AutoLinkOptions { UseHttpsForWWWLinks = true })
        .DisableHtml()
        .Build();

    private const double BodySize = 14;
    private const double CodeSize = 12.5;
    private const double ListIndent = 22;
    private const double QuoteIndent = 15;
    private static readonly double[] HeadingSizes = { 20, 17.5, 15.5, 14.5, 14, 14 };

    private readonly FrameworkElement _theme;
    private readonly FontFamily _uiFont;
    private readonly FontFamily _codeFont;
    private readonly double _pixelsPerDip;

    // Widest line seen while rendering, so short replies keep a snug bubble instead of a full-width one.
    private double _naturalWidth;
    private bool _needsFullWidth;
    private int _listDepth;

    public MarkdownRenderer(FrameworkElement theme)
    {
        _theme = theme;
        _uiFont = theme.TryFindResource("UiFont") as FontFamily ?? SystemFonts.MessageFontFamily;
        _codeFont = theme.TryFindResource("CodeFont") as FontFamily ?? new FontFamily("Consolas");
        _pixelsPerDip = VisualTreeHelper.GetDpi(theme).PixelsPerDip;
    }

    /// <summary>
    /// Renders Markdown prose. Returns null when there is nothing to format (only paragraphs and line
    /// breaks); <paramref name="plainText"/> then holds the text for an ordinary text box, with
    /// escapes and entities resolved.
    /// </summary>
    public RichTextBox? Render(string markdown, out string plainText)
    {
        var document = Markdig.Markdown.Parse(markdown ?? "", Pipeline);
        if (IsPlain(document))
        {
            plainText = BuildPlainText(document);
            return null;
        }

        plainText = "";
        _naturalWidth = 0;
        _needsFullWidth = false;
        _listDepth = 0;

        var flow = new FlowDocument
        {
            FontFamily = _uiFont,
            FontSize = BodySize,
            Foreground = ThemeBrush("TextPrimaryBrush"),
            PagePadding = new Thickness(2, 0, 2, 0),
            TextAlignment = TextAlignment.Left
        };
        AddBlocks(flow.Blocks, document, 0);
        TrimOuterMargins(flow.Blocks);

        var box = new RichTextBox
        {
            Document = flow,
            IsReadOnly = true,
            IsDocumentEnabled = true,
            IsUndoEnabled = false,
            IsTabStop = false,
            FocusVisualStyle = null,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Foreground = ThemeBrush("TextPrimaryBrush"),
            FontFamily = _uiFont,
            FontSize = BodySize,
            SelectionBrush = ThemeBrush("PrimaryBrush"),
            SelectionOpacity = 0.45,
            Cursor = Cursors.IBeam,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // A little slack over the measured width: bold text and font fallback run wider.
            MaxWidth = _needsFullWidth || _naturalWidth <= 0
                ? double.PositiveInfinity
                : Math.Ceiling(_naturalWidth * 1.06 + 14)
        };
        // The reply never scrolls by itself, so the wheel always scrolls the chat.
        box.PreviewMouseWheel += ForwardMouseWheel;
        return box;
    }

    // ---------- Blocks ----------

    private void AddBlocks(BlockCollection target, ContainerBlock container, double indent)
    {
        foreach (var block in container)
        {
            var rendered = RenderBlock(block, indent);
            if (rendered is not null)
                target.Add(rendered);
        }
    }

    private WpfBlock? RenderBlock(MdBlock block, double indent)
    {
        switch (block)
        {
            case HeadingBlock heading:
                return RenderHeading(heading, indent);
            case ParagraphBlock paragraph:
                return RenderParagraph(paragraph, indent);
            case ListBlock list:
                return RenderList(list, indent);
            case QuoteBlock quote:
                return RenderQuote(quote, indent);
            case ThematicBreakBlock:
                return new Paragraph
                {
                    Margin = new Thickness(0, 4, 0, 12),
                    BorderBrush = ThemeBrush("BorderStrongBrush"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    FontSize = 1,
                    LineHeight = 1
                };
            case MdTable table:
                return RenderTable(table);
            case CodeBlock code:
                return RenderCode(code, indent);
            case LinkReferenceDefinitionGroup:
                return null;
            case ContainerBlock container:
                var section = new Section();
                AddBlocks(section.Blocks, container, indent);
                return section;
            case LeafBlock leaf when leaf.Lines.Count > 0:
                var text = leaf.Lines.ToString();
                TrackLines(text, _uiFont, BodySize, FontWeights.Normal, indent);
                return new Paragraph(new Run(text)) { Margin = new Thickness(0, 0, 0, 8) };
            default:
                return null;
        }
    }

    private Paragraph RenderHeading(HeadingBlock block, double indent)
    {
        var level = Math.Clamp(block.Level, 1, HeadingSizes.Length);
        var size = HeadingSizes[level - 1];
        var weight = level <= 2 ? FontWeights.Bold : FontWeights.SemiBold;
        var paragraph = new Paragraph
        {
            FontSize = size,
            FontWeight = weight,
            Margin = new Thickness(0, level <= 2 ? 12 : 8, 0, level == 1 ? 8 : 4)
        };
        if (level == 1)
        {
            paragraph.BorderBrush = ThemeBrush("BorderBrush");
            paragraph.BorderThickness = new Thickness(0, 0, 0, 1);
            paragraph.Padding = new Thickness(0, 0, 0, 4);
        }

        AddInlines(paragraph.Inlines, block.Inline);
        TrackInlines(block.Inline, size, weight, indent);
        return paragraph;
    }

    private Paragraph RenderParagraph(ParagraphBlock block, double indent)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        AddInlines(paragraph.Inlines, block.Inline);
        TrackInlines(block.Inline, BodySize, FontWeights.Normal, indent);
        return paragraph;
    }

    private List RenderList(ListBlock block, double indent)
    {
        var items = block.OfType<ListItemBlock>().ToList();
        var allTasks = items.Count > 0 && items.All(StartsWithTask);
        var list = new List
        {
            MarkerStyle = allTasks
                ? TextMarkerStyle.None
                : block.IsOrdered
                    ? TextMarkerStyle.Decimal
                    : (_listDepth % 3) switch { 0 => TextMarkerStyle.Disc, 1 => TextMarkerStyle.Circle, _ => TextMarkerStyle.Square },
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(allTasks ? 4 : ListIndent, 0, 0, 0)
        };

        // A list split by a code block continues its numbering ("2." after the code).
        if (block.IsOrdered &&
            int.TryParse(block.OrderedStart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var start) &&
            start > 1)
        {
            list.StartIndex = start;
        }

        var itemIndent = indent + (allTasks ? 4 : ListIndent);
        _listDepth++;
        try
        {
            foreach (var item in items)
            {
                var listItem = new ListItem();
                AddBlocks(listItem.Blocks, item, itemIndent);
                if (listItem.Blocks.Count == 0)
                    listItem.Blocks.Add(new Paragraph());

                foreach (var inner in listItem.Blocks)
                    inner.Margin = new Thickness(0, 0, 0, block.IsLoose ? 6 : 2);
                list.ListItems.Add(listItem);
            }
        }
        finally
        {
            _listDepth--;
        }

        return list;
    }

    private Section RenderQuote(QuoteBlock block, double indent)
    {
        var section = new Section
        {
            BorderBrush = ThemeBrush("PrimaryBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(12, 2, 0, 2),
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = ThemeBrush("TextSecondaryBrush")
        };
        AddBlocks(section.Blocks, block, indent + QuoteIndent);
        TrimOuterMargins(section.Blocks);
        return section;
    }

    private WpfTable RenderTable(MdTable block)
    {
        // Tables use the full bubble width; columns share it evenly.
        _needsFullWidth = true;

        var rows = block.OfType<MdTableRow>().ToList();
        var columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.OfType<MdTableCell>().Sum(c => Math.Max(1, c.ColumnSpan)));
        var border = ThemeBrush("BorderBrush");
        var table = new WpfTable
        {
            CellSpacing = 0,
            Margin = new Thickness(0, 2, 0, 10),
            BorderBrush = border,
            BorderThickness = new Thickness(1, 1, 0, 0)
        };
        for (var i = 0; i < columnCount; i++)
            table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });

        var group = new TableRowGroup();
        foreach (var row in rows)
        {
            var tableRow = new WpfTableRow();
            if (row.IsHeader)
            {
                tableRow.Background = ThemeBrush("SurfaceBrush");
                tableRow.FontWeight = FontWeights.SemiBold;
            }

            var column = 0;
            foreach (var cellBlock in row.OfType<MdTableCell>())
            {
                var cell = CreateTableCell(border);
                var span = Math.Max(1, cellBlock.ColumnSpan);
                if (span > 1)
                    cell.ColumnSpan = span;

                AddBlocks(cell.Blocks, cellBlock, 0);
                if (cell.Blocks.Count == 0)
                    cell.Blocks.Add(new Paragraph());

                var alignment = GetColumnAlignment(block, column);
                foreach (var inner in cell.Blocks)
                {
                    inner.Margin = new Thickness(0);
                    inner.TextAlignment = alignment;
                }

                tableRow.Cells.Add(cell);
                column += span;
            }

            // Short rows still get their grid lines.
            for (; column < columnCount; column++)
            {
                var filler = CreateTableCell(border);
                filler.Blocks.Add(new Paragraph());
                tableRow.Cells.Add(filler);
            }

            group.Rows.Add(tableRow);
        }

        table.RowGroups.Add(group);
        return table;
    }

    private static WpfTableCell CreateTableCell(Brush border) => new()
    {
        Padding = new Thickness(8, 4, 8, 4),
        BorderBrush = border,
        BorderThickness = new Thickness(0, 0, 1, 1)
    };

    private static TextAlignment GetColumnAlignment(MdTable table, int column)
    {
        if (column < 0 || column >= table.ColumnDefinitions.Count)
            return TextAlignment.Left;

        return table.ColumnDefinitions[column].Alignment switch
        {
            MdTableColumnAlign.Center => TextAlignment.Center,
            MdTableColumnAlign.Right => TextAlignment.Right,
            _ => TextAlignment.Left
        };
    }

    /// <summary>Indented code, or a ~~~ fence, inside the prose (``` fences are split out before rendering).</summary>
    private Paragraph RenderCode(CodeBlock block, double indent)
    {
        var text = block.Lines.ToString().TrimEnd();
        var paragraph = new Paragraph
        {
            FontFamily = _codeFont,
            FontSize = CodeSize,
            Background = ThemeBrush("InputBrush"),
            BorderBrush = ThemeBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 8),
            Margin = new Thickness(0, 2, 0, 8)
        };

        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
                paragraph.Inlines.Add(new LineBreak());
            paragraph.Inlines.Add(new Run(lines[i]));
        }

        TrackLines(text, _codeFont, CodeSize, FontWeights.Normal, indent + 22);
        return paragraph;
    }

    private static void TrimOuterMargins(BlockCollection blocks)
    {
        if (blocks.FirstBlock is { } first)
            first.Margin = new Thickness(first.Margin.Left, 0, first.Margin.Right, first.Margin.Bottom);
        if (blocks.LastBlock is { } last)
            last.Margin = new Thickness(last.Margin.Left, last.Margin.Top, last.Margin.Right, 0);
    }

    // ---------- Inlines ----------

    private void AddInlines(InlineCollection target, ContainerInline? container)
    {
        if (container is null)
            return;

        foreach (var inline in container)
        {
            var rendered = RenderInline(inline);
            if (rendered is not null)
                target.Add(rendered);
        }
    }

    private WpfInline? RenderInline(MdInline inline)
    {
        switch (inline)
        {
            case LiteralInline literal:
                var text = literal.Content.ToString();
                return text.Length == 0 ? null : new Run(text);
            case LineBreakInline:
                // Soft breaks too: chat replies have always kept their line breaks.
                return new LineBreak();
            case HtmlEntityInline entity:
                return new Run(entity.Transcoded.ToString());
            case HtmlInline html:
                return new Run(html.Tag);
            case CodeInline code:
                return new Run(code.Content)
                {
                    FontFamily = _codeFont,
                    FontSize = CodeSize,
                    Background = ThemeBrush("BorderBrush")
                };
            case TaskList task:
                if (!IsLeadingTask(task))
                    return new Run(task.Checked ? "[x]" : "[ ]");
                return new Run(task.Checked ? "☑ " : "☐ ")
                {
                    Foreground = ThemeBrush(task.Checked ? "SuccessBrush" : "TextMutedBrush")
                };
            case EmphasisInline emphasis:
                return RenderEmphasis(emphasis);
            case LinkInline link:
                return RenderLink(link);
            case AutolinkInline autolink:
                return CreateLink(autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url, new Run(autolink.Url));
            case ContainerInline container:
                var span = new Span();
                AddInlines(span.Inlines, container);
                return span;
            default:
                return null;
        }
    }

    private WpfInline RenderEmphasis(EmphasisInline emphasis)
    {
        Span span;
        if (emphasis.DelimiterChar == '~')
        {
            span = new Span
            {
                TextDecorations = TextDecorations.Strikethrough,
                Foreground = ThemeBrush("TextSecondaryBrush")
            };
        }
        else if (emphasis.DelimiterCount >= 2)
        {
            span = new Bold();
        }
        else
        {
            span = new Italic();
        }

        AddInlines(span.Inlines, emphasis);
        return span;
    }

    private WpfInline RenderLink(LinkInline link)
    {
        var content = new Span();
        AddInlines(content.Inlines, link);
        if (content.Inlines.Count == 0)
            content.Inlines.Add(new Run(link.Url ?? ""));

        // Images are not downloaded; the alt text links to the picture instead.
        if (link.IsImage)
            content.Inlines.InsertBefore(content.Inlines.FirstInline, new Run("Image: "));

        return CreateLink(link.GetDynamicUrl?.Invoke() ?? link.Url, content);
    }

    private WpfInline CreateLink(string? url, WpfInline content)
    {
        // Anything other than http, https or mailto is shown as text, not as a link.
        if (!MarkdownText.TryGetSafeLinkUri(url, out var uri))
            return content;

        var hyperlink = new Hyperlink(content)
        {
            NavigateUri = uri,
            Foreground = ThemeBrush("AccentBrush"),
            Cursor = Cursors.Hand,
            ToolTip = uri.AbsoluteUri
        };
        hyperlink.RequestNavigate += OpenLink;
        return hyperlink;
    }

    private static void OpenLink(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        if (!MarkdownText.TryGetSafeLinkUri(e.Uri?.OriginalString, out var uri))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not open link {uri}: {ex.Message}");
        }
    }

    private static void ForwardMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement { Parent: UIElement parent })
            return;

        e.Handled = true;
        parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = sender
        });
    }

    // ---------- Plain detection ----------

    /// <summary>True when the reply is only paragraphs of text: an ordinary text box shows it as well.</summary>
    private static bool IsPlain(MarkdownDocument document) =>
        document.All(block => block is ParagraphBlock paragraph &&
                              (paragraph.Inline is null ||
                               paragraph.Inline.All(inline => inline is LiteralInline or LineBreakInline or HtmlEntityInline)));

    private static string BuildPlainText(MarkdownDocument document)
    {
        var paragraphs = new List<string>();
        foreach (var paragraph in document.OfType<ParagraphBlock>())
        {
            var lines = paragraph.Inline is null ? new List<string>() : InlineLines(paragraph.Inline);
            var text = string.Join("\n", lines).Trim();
            if (text.Length > 0)
                paragraphs.Add(text);
        }

        return string.Join("\n\n", paragraphs);
    }

    private static bool StartsWithTask(ListItemBlock item) =>
        item.FirstOrDefault() is ParagraphBlock { Inline: { } inline } &&
        inline.FirstOrDefault(i => i is not LiteralInline { Content.Length: 0 }) is TaskList;

    /// <summary>Markdig also matches [x] mid-sentence; only a box at the start of a list item is a task.</summary>
    private static bool IsLeadingTask(TaskList task)
    {
        if (task.Parent is not { Parent: null })
            return false;

        for (var previous = task.PreviousSibling; previous is not null; previous = previous.PreviousSibling)
        {
            if (previous is not LiteralInline { Content.Length: 0 })
                return false;
        }

        return true;
    }

    // ---------- Natural width ----------

    private void TrackInlines(ContainerInline? inlines, double fontSize, FontWeight weight, double indent)
    {
        if (_needsFullWidth || inlines is null)
            return;

        foreach (var line in InlineLines(inlines))
            TrackLine(line, _uiFont, fontSize, weight, indent);
    }

    private void TrackLines(string text, FontFamily font, double fontSize, FontWeight weight, double indent)
    {
        if (_needsFullWidth)
            return;

        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            TrackLine(line, font, fontSize, weight, indent);
    }

    private void TrackLine(string line, FontFamily font, double fontSize, FontWeight weight, double indent)
    {
        if (string.IsNullOrEmpty(line))
            return;

        var formatted = new FormattedText(
            line,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(font, FontStyles.Normal, weight, FontStretches.Normal),
            fontSize,
            Brushes.White,
            _pixelsPerDip);
        _naturalWidth = Math.Max(_naturalWidth, indent + formatted.WidthIncludingTrailingWhitespace);
    }

    /// <summary>The visible text of an inline tree, one string per line.</summary>
    private static List<string> InlineLines(ContainerInline root)
    {
        var lines = new List<string>();
        var current = new StringBuilder();

        void Walk(ContainerInline container)
        {
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LineBreakInline:
                        lines.Add(current.ToString());
                        current.Clear();
                        break;
                    case LiteralInline literal:
                        current.Append(literal.Content.ToString());
                        break;
                    case HtmlEntityInline entity:
                        current.Append(entity.Transcoded.ToString());
                        break;
                    case HtmlInline html:
                        current.Append(html.Tag);
                        break;
                    case CodeInline code:
                        current.Append(code.Content);
                        break;
                    case AutolinkInline autolink:
                        current.Append(autolink.Url);
                        break;
                    case TaskList:
                        current.Append("☐ ");
                        break;
                    case LinkInline { IsImage: true }:
                        current.Append("Image: ");
                        Walk((ContainerInline)inline);
                        break;
                    case ContainerInline inner:
                        Walk(inner);
                        break;
                }
            }
        }

        Walk(root);
        lines.Add(current.ToString());
        return lines;
    }

    private Brush ThemeBrush(string key) => _theme.TryFindResource(key) as Brush ?? Brushes.Gray;
}
