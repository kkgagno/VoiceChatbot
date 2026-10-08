using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace VoiceChatbot;

// Which saved conversation memories go into the system prompt ("Memories in prompt" in the
// Conversation expander). Relevant mode ranks the memory summaries against the current message
// with BM25 (Core/MemorySelector + Core/TextRanker); All mode sends every memory as before.
public partial class MainWindow
{
    // What the last built prompt included, shown under the combo (-1 = nothing built yet).
    private int _lastMemoryPromptIncluded = -1;
    private int _lastMemoryPromptTotal;

    private void ApplyMemoryPromptSettings()
    {
        _settings.MemoryMode = MemorySelector.NormalizeMode(_settings.MemoryMode);
        _settings.MemoryMaxItems = MemorySelector.ClampMaxItems(_settings.MemoryMaxItems);
        var maxItems = _settings.MemoryMaxItems;
        SelectMemoryModeCombo(_settings.MemoryMode);
        MemoryMaxItemsSlider.Value = maxItems;
        MemoryMaxItemsValue.Text = maxItems.ToString();
        UpdateMemoryPromptControls();
    }

    private void SaveMemoryPromptSettings()
    {
        _settings.MemoryMode = GetSelectedMemoryMode();
        _settings.MemoryMaxItems = MemorySelector.ClampMaxItems((int)Math.Round(MemoryMaxItemsSlider.Value));
    }

    private void SelectMemoryModeCombo(string mode)
    {
        var normalized = MemorySelector.NormalizeMode(mode);
        var items = MemoryModeCombo.Items.OfType<ComboBoxItem>().ToList();
        MemoryModeCombo.SelectedItem =
            items.FirstOrDefault(i => string.Equals(i.Content?.ToString(), normalized, StringComparison.OrdinalIgnoreCase)) ??
            items.FirstOrDefault();
    }

    private string GetSelectedMemoryMode() =>
        MemorySelector.NormalizeMode((MemoryModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString());

    private void MemoryModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings == null || MemoryModeCombo == null)
            return;

        _settings.MemoryMode = GetSelectedMemoryMode();
        _lastMemoryPromptIncluded = -1;
        UpdateMemoryPromptControls();
    }

    private void MemoryMaxItemsSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var maxItems = MemorySelector.ClampMaxItems((int)Math.Round(e.NewValue));
        if (MemoryMaxItemsValue != null)
            MemoryMaxItemsValue.Text = maxItems.ToString();
        if (_settings == null)
            return;

        _settings.MemoryMaxItems = maxItems;
        UpdateMemoryPromptControls();
    }

    private void UpdateMemoryPromptControls()
    {
        if (MemoryPromptHint == null || MemoryMaxItemsPanel == null || _settings == null)
            return;

        var relevant = MemorySelector.NormalizeMode(_settings.MemoryMode) == MemorySelector.ModeRelevant;
        MemoryMaxItemsPanel.Visibility = relevant ? Visibility.Visible : Visibility.Collapsed;

        var maxItems = MemorySelector.ClampMaxItems(_settings.MemoryMaxItems);
        var hint = relevant
            ? $"Each message gets up to {maxItems} saved {(maxItems == 1 ? "memory" : "memories")} that match it, plus the newest one."
            : "Every saved memory is sent with every message.";
        if (_lastMemoryPromptIncluded >= 0)
            hint += $" Last message used {_lastMemoryPromptIncluded} of {_lastMemoryPromptTotal}.";
        MemoryPromptHint.Text = hint;
    }

    /// <summary>
    /// The memories block for the system prompt (MemoryManager format), limited to the memories
    /// that match <paramref name="queryText"/> when the Relevant mode is selected.
    /// </summary>
    private string BuildMemoryPromptBlock(string? queryText)
    {
        var memories = _loadedMemories ?? new List<ConversationMemory>();
        List<ConversationMemory> selected;
        try
        {
            selected = MemorySelector.Select(
                memories,
                m => m.Summary,
                GetMemoryQueryText(queryText),
                _settings.MemoryMode,
                _settings.MemoryMaxItems);
        }
        catch (Exception ex)
        {
            // Ranking is best effort; never lose memories because of it.
            Debug.WriteLine($"Memory ranking failed, using all memories: {ex.Message}");
            selected = memories.ToList();
        }

        _lastMemoryPromptIncluded = selected.Count;
        _lastMemoryPromptTotal = memories.Count;
        if (Dispatcher.CheckAccess())
            UpdateMemoryPromptControls();
        else
            Dispatcher.BeginInvoke(new Action(UpdateMemoryPromptControls));

        return MemoryManager.FormatForSystemPrompt(selected);
    }

    // "continue" has no topic of its own, so match memories against the request being continued.
    private string? GetMemoryQueryText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !IsManualContinuationRequest(text))
            return text;

        return _history.GetAll()
            .LastOrDefault(m => m.Role.Equals("user", StringComparison.OrdinalIgnoreCase) &&
                                !IsManualContinuationRequest(m.Content))
            ?.Content;
    }
}
