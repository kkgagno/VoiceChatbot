using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace VoiceChatbot;

/// <summary>
/// The Help window (Help in the top bar, or F1): the topics from <see cref="HelpContent"/> in a
/// searchable list, and the selected topic as a readable, selectable document with links to the web,
/// to other topics and to the sidebar section it explains. One window is shared by every caller.
/// It is not owned by the main window, so it can stay open beside the app while you follow it.
/// </summary>
public partial class HelpWindow : Window
{
    private static HelpWindow? _current;

    private HelpTopic? _shown;
    private bool _updatingList;

    private HelpWindow()
    {
        InitializeComponent();
        WindowTheme.UseThemedTitleBar(this);
        Title = $"{AppPaths.ProductName} Help";
        ShowTopicList(HelpContent.Topics, grouped: true);
    }

    /// <summary>
    /// Opens a sidebar section in the main window, for the "Show this section" link of a sidebar
    /// topic. Set by the main window; without it the link is left out.
    /// </summary>
    public static Action<string>? RevealSidebarSection { get; set; }

    /// <summary>
    /// Opens Help, or brings the open Help window forward, at <paramref name="topicId"/>. Without a
    /// topic a new window starts at Getting started and an open one keeps its page.
    /// <paramref name="near"/> is the window to center a new Help window on.
    /// </summary>
    public static void Open(string? topicId, Window? near)
    {
        var topic = HelpContent.Find(topicId);
        if (_current == null)
        {
            var window = new HelpWindow();
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_current, window))
                    _current = null;
            };
            _current = window;
            window.PlaceNear(near);
            window.ShowTopic(topic ?? HelpContent.Find(HelpContent.GettingStartedId));
            window.Show();
            // The page has focus, so the keys scroll it and the search box keeps its hint (Ctrl+F).
            window.TopicView.Focus();
            return;
        }

        if (topic != null)
            _current.ShowTopic(topic);
        _current.BringForward();
    }

    private void PlaceNear(Window? near)
    {
        if (near is not { IsVisible: true } || near.WindowState == WindowState.Minimized)
            return;

        // Centered over the window that asked (a maximized one by its normal size, which is on the same
        // screen), kept inside the desktop.
        var bounds = near.WindowState == WindowState.Normal
            ? new Rect(near.Left, near.Top, near.ActualWidth, near.ActualHeight)
            : near.RestoreBounds;
        if (bounds.IsEmpty || double.IsNaN(bounds.Left) || bounds.Width <= 0)
            return;

        var width = Math.Min(Width, SystemParameters.VirtualScreenWidth);
        var height = Math.Min(Height, SystemParameters.VirtualScreenHeight);
        var left = bounds.Left + (bounds.Width - width) / 2;
        var top = bounds.Top + (bounds.Height - height) / 2;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - width);
        Top = Math.Clamp(top, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - height);
    }

    private void BringForward()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        // Activate alone can leave the window behind the app that has focus.
        Topmost = true;
        Topmost = false;
    }

    // ==================== Topic list and search ====================

    private void ShowTopicList(IReadOnlyList<HelpTopic> topics, bool grouped)
    {
        _updatingList = true;
        try
        {
            var view = new ListCollectionView(topics.ToList());
            if (grouped)
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(HelpTopic.Group)));
            TopicList.ItemsSource = view;
            TopicList.SelectedItem = _shown != null && topics.Contains(_shown) ? _shown : null;
        }
        finally
        {
            _updatingList = false;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();

    private void ApplySearch()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            SearchResultText.Visibility = Visibility.Collapsed;
            ShowTopicList(HelpContent.Topics, grouped: true);
            if (_shown != null)
                TopicList.ScrollIntoView(_shown);
            return;
        }

        var results = HelpContent.Search(query);
        ShowTopicList(results, grouped: false);
        SearchResultText.Visibility = Visibility.Visible;
        SearchResultText.Text = results.Count switch
        {
            0 => "No topic matches. Try one word, such as microphone, Kokoro, PIN or Ollama.",
            1 => "1 topic matches.",
            _ => $"{results.Count} topics match, best first."
        };

        // Show the best match straight away, unless the page already shown is one of the matches.
        if (results.Count > 0 && (_shown == null || !results.Contains(_shown)))
            ShowTopic(results[0]);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when TopicList.Items.Count > 0:
                ShowTopic(TopicList.Items[0] as HelpTopic);
                TopicView.Focus();
                e.Handled = true;
                break;
            case Key.Down when TopicList.Items.Count > 0:
                if (TopicList.SelectedItem == null)
                    TopicList.SelectedIndex = 0;
                if (TopicList.ItemContainerGenerator.ContainerFromItem(TopicList.SelectedItem) is ListBoxItem item)
                    item.Focus();
                else
                    TopicList.Focus();
                e.Handled = true;
                break;
        }
    }

    private void TopicList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingList || TopicList.SelectedItem is not HelpTopic topic || ReferenceEquals(topic, _shown))
            return;

        Render(topic);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // Esc in a search box with text clears the search; otherwise it closes Help.
            if (SearchBox.IsKeyboardFocusWithin && SearchBox.Text.Length > 0)
                SearchBox.Clear();
            else
                Close();
            e.Handled = true;
        }
        else if (e.Key == Key.F1)
        {
            // Already in Help: F1 goes back to the start.
            ShowTopic(HelpContent.Find(HelpContent.GettingStartedId));
            e.Handled = true;
        }
    }

    // ==================== Showing a topic ====================

    private void ShowTopic(HelpTopic? topic)
    {
        if (topic == null)
            return;

        // A topic the current search hides: clear the search so the list shows where it is.
        if (!TopicList.Items.OfType<HelpTopic>().Contains(topic))
        {
            SearchBox.TextChanged -= SearchBox_TextChanged;
            SearchBox.Clear();
            SearchBox.TextChanged += SearchBox_TextChanged;
            SearchResultText.Visibility = Visibility.Collapsed;
            ShowTopicList(HelpContent.Topics, grouped: true);
        }

        Render(topic);
    }

    private void Render(HelpTopic topic)
    {
        _shown = topic;
        _updatingList = true;
        try
        {
            TopicList.SelectedItem = topic;
            TopicList.ScrollIntoView(topic);
        }
        finally
        {
            _updatingList = false;
        }

        TopicView.Document = BuildDocument(topic);
        TopicView.ScrollToHome();

        var index = IndexOf(topic);
        var count = HelpContent.Topics.Count;
        PreviousBtn.IsEnabled = index > 0;
        PreviousBtn.ToolTip = index > 0 ? HelpContent.Topics[index - 1].Title : null;
        NextBtn.IsEnabled = index < count - 1;
        NextBtn.Content = index < count - 1 ? $"Next: {HelpContent.Topics[index + 1].Title}" : "Next";
        PositionText.Text = $"{index + 1} of {count}";
    }

    private static int IndexOf(HelpTopic topic)
    {
        for (var i = 0; i < HelpContent.Topics.Count; i++)
        {
            if (ReferenceEquals(HelpContent.Topics[i], topic))
                return i;
        }

        return 0;
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => Step(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Step(int by)
    {
        if (_shown == null)
            return;

        var index = IndexOf(_shown) + by;
        if (index >= 0 && index < HelpContent.Topics.Count)
            ShowTopic(HelpContent.Topics[index]);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ==================== The document ====================

    private FlowDocument BuildDocument(HelpTopic topic)
    {
        var document = new FlowDocument
        {
            FontFamily = TryFindResource("UiFont") as FontFamily ?? SystemFonts.MessageFontFamily,
            FontSize = 14,
            LineHeight = 21,
            PagePadding = new Thickness(34, 26, 34, 34),
            MaxPageWidth = 900,
            TextAlignment = TextAlignment.Left
        };
        document.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");

        var title = new Paragraph { Margin = new Thickness(0, 0, 0, 6), FontSize = 24, FontWeight = FontWeights.SemiBold, LineHeight = 30 };
        var icon = new Run(topic.Icon + "  ")
        {
            FontFamily = TryFindResource("IconFont") as FontFamily ?? new FontFamily("Segoe MDL2 Assets"),
            FontSize = 20,
            FontWeight = FontWeights.Normal,
            BaselineAlignment = BaselineAlignment.Center
        };
        icon.SetResourceReference(TextElement.ForegroundProperty, "PrimaryLightBrush");
        title.Inlines.Add(icon);
        title.Inlines.Add(new Run(topic.Title));
        document.Blocks.Add(title);

        var summary = new Paragraph { Margin = new Thickness(0, 0, 0, 16), FontSize = 15, LineHeight = 22 };
        summary.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
        AddInlines(summary.Inlines, topic.Summary);
        document.Blocks.Add(summary);

        if (topic.SidebarSection is { } section)
            document.Blocks.Add(SidebarLocation(section));

        foreach (var block in topic.Blocks)
        {
            if (Render(block) is { } rendered)
                document.Blocks.Add(rendered);
        }

        return document;
    }

    private Paragraph SidebarLocation(string section)
    {
        var where = new Paragraph { Margin = new Thickness(0, 0, 0, 16), FontSize = 12.5 };
        where.SetResourceReference(TextElement.ForegroundProperty, "TextMutedBrush");
        where.Inlines.Add(new Run($"In the app: settings sidebar (Ctrl+B), section {section}"));
        if (RevealSidebarSection is { } reveal)
        {
            where.Inlines.Add(new Run("   ·   "));
            var link = CreateLink("Show this section", () => reveal(section));
            link.ToolTip = $"Opens the sidebar and the {section} section in the main window";
            where.Inlines.Add(link);
        }

        return where;
    }

    private Block? Render(HelpBlock block)
    {
        switch (block.Kind)
        {
            case HelpBlockKind.Heading:
            {
                var heading = new Paragraph { Margin = new Thickness(0, 20, 0, 8), FontSize = 17, FontWeight = FontWeights.SemiBold, LineHeight = 24 };
                AddInlines(heading.Inlines, block.Text);
                return heading;
            }
            case HelpBlockKind.Paragraph:
            {
                var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
                AddInlines(paragraph.Inlines, block.Text);
                return paragraph;
            }
            case HelpBlockKind.Bullets:
            case HelpBlockKind.Steps:
            {
                var list = new List
                {
                    MarkerStyle = block.Kind == HelpBlockKind.Steps ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 0, 0, 12),
                    Padding = new Thickness(24, 0, 0, 0)
                };
                // The markers in the accent color, the text in the normal one.
                list.SetResourceReference(TextElement.ForegroundProperty, "PrimaryLightBrush");
                foreach (var item in block.Items)
                {
                    var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 7) };
                    paragraph.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
                    AddInlines(paragraph.Inlines, item);
                    list.ListItems.Add(new ListItem(paragraph));
                }

                return list;
            }
            case HelpBlockKind.Tip:
            {
                var paragraph = new Paragraph { Margin = new Thickness(0) };
                paragraph.Inlines.Add(new Bold(new Run("Tip: ")));
                AddInlines(paragraph.Inlines, block.Text);
                var tip = new Section(paragraph)
                {
                    Margin = new Thickness(0, 4, 0, 16),
                    Padding = new Thickness(14, 10, 14, 10),
                    BorderThickness = new Thickness(3, 0, 0, 0)
                };
                tip.SetResourceReference(TextElement.BackgroundProperty, "PrimarySoftBrush");
                tip.SetResourceReference(Block.BorderBrushProperty, "PrimaryBrush");
                return tip;
            }
            case HelpBlockKind.SeeAlso:
            {
                var links = block.Items.Select(HelpContent.Find).OfType<HelpTopic>().ToList();
                if (links.Count == 0)
                    return null;

                var paragraph = new Paragraph { Margin = new Thickness(0, 20, 0, 0) };
                var label = new Run("See also:  ") { FontWeight = FontWeights.SemiBold };
                label.SetResourceReference(TextElement.ForegroundProperty, "TextSecondaryBrush");
                paragraph.Inlines.Add(label);
                for (var i = 0; i < links.Count; i++)
                {
                    if (i > 0)
                    {
                        var dot = new Run("   ·   ");
                        dot.SetResourceReference(TextElement.ForegroundProperty, "TextMutedBrush");
                        paragraph.Inlines.Add(dot);
                    }

                    var target = links[i];
                    paragraph.Inlines.Add(CreateLink(target.Title, () => ShowTopic(target)));
                }

                return paragraph;
            }
            default:
                return null;
        }
    }

    private void AddInlines(InlineCollection inlines, string text)
    {
        foreach (var span in HelpMarkup.Parse(text))
        {
            switch (span.Kind)
            {
                case HelpSpanKind.Bold:
                    inlines.Add(new Bold(new Run(span.Text)));
                    break;
                case HelpSpanKind.Code:
                {
                    var code = new Run(span.Text)
                    {
                        FontFamily = TryFindResource("CodeFont") as FontFamily ?? new FontFamily("Consolas"),
                        FontSize = 13
                    };
                    code.SetResourceReference(TextElement.BackgroundProperty, "BorderBrush");
                    inlines.Add(code);
                    break;
                }
                case HelpSpanKind.Link when Uri.TryCreate(span.Text, UriKind.Absolute, out var uri)
                                            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp):
                {
                    var link = CreateLink(span.Text, () => OpenInBrowser(uri));
                    link.ToolTip = uri.AbsoluteUri;
                    inlines.Add(link);
                    break;
                }
                default:
                    inlines.Add(new Run(span.Text));
                    break;
            }
        }
    }

    private static Hyperlink CreateLink(string text, Action onClick)
    {
        var link = new Hyperlink(new Run(text)) { Cursor = Cursors.Hand };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush");
        link.Click += (_, _) => onClick();
        return link;
    }

    private static void OpenInBrowser(Uri uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Help: could not open {uri.AbsoluteUri}.", ex);
        }
    }
}
