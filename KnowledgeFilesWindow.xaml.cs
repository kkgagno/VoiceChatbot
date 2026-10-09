using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VoiceChatbot;

/// <summary>
/// Lists every file the last knowledge folder scan saw and what happened to it (indexed, could not be
/// read, skipped, too large), sortable by name, type or status. Opened from the Files button; the main
/// window calls <see cref="Refresh"/> when a reindex finishes.
/// </summary>
public partial class KnowledgeFilesWindow : Window
{
    private readonly Func<KnowledgeFileList> _loadFiles;
    private readonly Func<KnowledgeStatus> _getStatus;
    private KnowledgeFileList _list = new("", Array.Empty<KnowledgeFileEntry>());
    private KnowledgeFileSort _sort = KnowledgeFileSort.Status;
    private bool _descending;

    public KnowledgeFilesWindow(Func<KnowledgeFileList> loadFiles, Func<KnowledgeStatus> getStatus)
    {
        InitializeComponent();
        WindowTheme.UseThemedTitleBar(this);
        _loadFiles = loadFiles ?? throw new ArgumentNullException(nameof(loadFiles));
        _getStatus = getStatus ?? throw new ArgumentNullException(nameof(getStatus));
        Refresh();
    }

    /// <summary>Reloads the list from the knowledge service (UI thread).</summary>
    public void Refresh()
    {
        try
        {
            _list = _loadFiles();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Could not load the knowledge file list: {ex.Message}");
            _list = new KnowledgeFileList(_list.Folder, Array.Empty<KnowledgeFileEntry>(), Scanned: false);
        }

        FolderText.Text = _list.Folder.Length == 0 ? "No knowledge folder chosen" : _list.Folder;
        FolderText.ToolTip = _list.Folder.Length == 0 ? null : _list.Folder;
        OpenFolderBtn.IsEnabled = _list.Folder.Length > 0;

        var lines = new List<string> { KnowledgeReport.FormatFileCounts(_list.Entries, _list.UnlistedSkipped) };
        if (_list.UnlistedSkipped > 0)
            lines.Add($"{_list.UnlistedSkipped:N0} more unsupported files are counted but not listed.");
        if (_getStatus().IsIndexing)
            lines.Add("Indexing is running; this list updates when it finishes.");
        else if (!_list.Scanned && _list.Folder.Length > 0)
            lines.Add("The folder has not been checked since the app started, so only indexed files are listed. Click Reindex to check it.");
        SummaryText.Text = string.Join("\n", lines);

        ShowSorted();
    }

    private void ShowSorted()
    {
        var rows = KnowledgeReport.Sort(_list.Entries, _sort, _descending, _list.Folder)
            .Select(e => new KnowledgeFileRow(e, _list.Folder))
            .ToList();
        FileList.ItemsSource = rows;

        EmptyText.Text = _list.Folder.Length == 0
            ? "Choose a knowledge folder first (Browse)."
            : "No files found yet. Click Reindex to scan the folder.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        SortNameBtn.Content = HeaderText("File", KnowledgeFileSort.Name);
        SortTypeBtn.Content = HeaderText("Type", KnowledgeFileSort.Type);
        SortStatusBtn.Content = HeaderText("Status", KnowledgeFileSort.Status);
    }

    private string HeaderText(string label, KnowledgeFileSort sort) =>
        _sort == sort ? $"{label} {(_descending ? "▼" : "▲")}" : label;

    private void SortBy(KnowledgeFileSort sort)
    {
        _descending = _sort == sort && !_descending;
        _sort = sort;
        ShowSorted();
    }

    private void SortName_Click(object sender, RoutedEventArgs e) => SortBy(KnowledgeFileSort.Name);

    private void SortType_Click(object sender, RoutedEventArgs e) => SortBy(KnowledgeFileSort.Type);

    private void SortStatus_Click(object sender, RoutedEventArgs e) => SortBy(KnowledgeFileSort.Status);

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_list.Folder.Length == 0)
            return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_list.Folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not open the folder: {ex.Message}";
        }
    }

    private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not KnowledgeFileRow row || !File.Exists(row.FullPath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.FullPath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SummaryText.Text = $"Could not show the file: {ex.Message}";
        }
    }
}

/// <summary>One row of the Files list. Tone picks the status colour (good, bad, warn, muted).</summary>
public sealed class KnowledgeFileRow
{
    public KnowledgeFileRow(KnowledgeFileEntry entry, string folder)
    {
        FullPath = entry.Path;
        Name = KnowledgeIndex.DisplayName(entry.Path, folder);
        var extension = KnowledgeReport.ExtensionOf(entry.Path);
        Type = extension.Length == 0 ? "none" : extension.TrimStart('.');
        Status = KnowledgeReport.DescribeEntry(entry);
        Detail = string.IsNullOrWhiteSpace(entry.Reason) ? Status : $"{Status}\n\n{entry.Reason}";
        Tone = entry.State switch
        {
            KnowledgeFileState.Indexed => "good",
            KnowledgeFileState.Unreadable => "bad",
            KnowledgeFileState.TooLarge => "warn",
            KnowledgeFileState.Unsupported or KnowledgeFileState.NoText => "muted",
            _ => ""
        };
    }

    public string FullPath { get; }
    public string Name { get; }
    public string Type { get; }
    public string Status { get; }
    public string Detail { get; }
    public string Tone { get; }
}
