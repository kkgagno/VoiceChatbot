using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace VoiceChatbot;

/// <summary>
/// The Help button and F1 (see HelpWindow and Core/HelpContent.cs). F1 inside a settings section opens
/// that section's topic, and Help's "Show this section" link opens the section here.
/// </summary>
public partial class MainWindow
{
    private void Help_Click(object sender, RoutedEventArgs e) => OpenHelp(null);

    private void OpenHelp(string? topicId) => HelpWindow.Open(topicId, this);

    /// <summary>The help topic for the sidebar section that contains <paramref name="source"/>, or null.</summary>
    private static string? ContextHelpTopicId(DependencyObject? source)
    {
        for (var node = source; node != null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is Expander { Header: string header })
                return HelpContent.ForSidebarSection(header)?.Id;
        }

        return null;
    }

    /// <summary>Shows the window and the sidebar, opens the section called <paramref name="header"/> and scrolls to it.</summary>
    private void RevealSidebarSection(string header)
    {
        if (FindSidebarSection(SidebarPanel, header) is not { } section)
            return;

        if (!IsVisible || WindowState == WindowState.Minimized)
            ShowFromTray();
        else
            Activate();

        SetSidebarVisible(true);
        section.IsExpanded = true;
        // After the sidebar and the section have been laid out.
        Dispatcher.BeginInvoke(() =>
        {
            section.BringIntoView();
            section.Focus();
        }, DispatcherPriority.Loaded);
    }

    private static Expander? FindSidebarSection(DependencyObject parent, string header)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is not DependencyObject node)
                continue;
            if (node is Expander { Header: string text } expander && string.Equals(text, header, System.StringComparison.OrdinalIgnoreCase))
                return expander;
            if (FindSidebarSection(node, header) is { } found)
                return found;
        }

        return null;
    }
}
