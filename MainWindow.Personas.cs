using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace VoiceChatbot;

// Personas: named presets for the system prompt, voice, speech rate and (optionally) model
// (Core/Persona). Picked from the top-bar combo; saved, updated and deleted from the Personas
// section of the Chat Backend expander. Saved conversations remember the persona they used.
public partial class MainWindow
{
    private const string DeletePersonaLabel = "Delete persona";
    private static readonly TimeSpan PersonaDeleteConfirmWindow = TimeSpan.FromSeconds(4);

    // True while code fills or reselects PersonaCombo, so that does not re-apply a persona.
    private bool _personaComboUpdating;
    private bool _personaDeletePending;
    private DispatcherTimer? _personaDeleteTimer;

    // ==================== Settings ====================

    private void ApplyPersonaSettings()
    {
        // Startup only shows the active persona. The prompt, voice and rate already hold the values
        // saved last time, which may include edits not yet stored in the persona.
        _settings.Personas = PersonaCatalog.EnsureDefault(
            _settings.Personas, _settings.SystemPrompt, _settings.VoiceName, _settings.SpeechRate);
        var active = PersonaCatalog.ResolveActive(_settings.Personas, _settings.ActivePersona);
        _settings.ActivePersona = active?.Name ?? "";
        PersonaModelToggle.IsChecked = !string.IsNullOrWhiteSpace(active?.Model);
        RefreshPersonaCombo();
    }

    private void SavePersonaSettings()
    {
        // The list itself is kept up to date by the Save/Update/Delete handlers.
        if (PersonaCombo.SelectedItem is string name && PersonaCatalog.Find(_settings.Personas, name) != null)
            _settings.ActivePersona = name;
    }

    private Persona? CurrentPersona => PersonaCatalog.Find(_settings.Personas, _settings.ActivePersona);

    // ==================== Switching ====================

    private void PersonaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_personaComboUpdating || _settings == null || PersonaCombo.SelectedItem is not string name)
            return;

        try
        {
            SwitchPersona(name);
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not switch persona: {ex.Message}");
        }
    }

    /// <summary>Applies the persona's prompt, voice, rate and model (if set) and makes it active.</summary>
    private bool SwitchPersona(string? name)
    {
        var persona = PersonaCatalog.Find(_settings.Personas, name);
        if (persona == null)
            return false;

        ApplyPersonaToControls(persona);
        _settings.ActivePersona = persona.Name;
        PersonaModelToggle.IsChecked = persona.Model.Length > 0;
        ResetPersonaDeleteConfirm();
        SelectPersonaInCombo();
        UpdatePersonaControls();
        SetPersonaStatus($"Switched to \"{persona.Name}\".", "TextMutedBrush");
        SaveSettings();
        return true;
    }

    private void ApplyPersonaToControls(Persona persona)
    {
        SystemPromptBox.Text = persona.SystemPrompt;
        SelectPersonaVoice(persona.VoiceName);
        // The slider's ValueChanged handler passes the rate on to the speech engine.
        RateSlider.Value = PersonaCatalog.ClampSpeechRate(persona.SpeechRate);
        if (persona.Model.Length > 0)
            SelectPersonaModel(persona);
    }

    private void SelectPersonaVoice(string voiceName)
    {
        if (string.IsNullOrWhiteSpace(voiceName))
            return;

        var match = PersonaCatalog.MatchVoice(VoiceCombo.Items.OfType<string>(), voiceName);
        if (match == null)
        {
            // Same as startup: keep a saved voice selectable even if the current list lacks it.
            match = voiceName.Trim();
            VoiceCombo.Items.Add(match);
        }

        VoiceCombo.SelectedItem = match;
        _speech.VoiceName = match;
    }

    private void SelectPersonaModel(Persona persona)
    {
        var listed = ModelCombo.Items.OfType<string>()
            .FirstOrDefault(m => string.Equals(m, persona.Model, StringComparison.OrdinalIgnoreCase));
        if (listed != null)
        {
            ModelCombo.SelectedItem = listed;
            return;
        }

        ModelCombo.Text = persona.Model;
        if (ModelCombo.Items.Count > 0)
            AddSystemMessage($"Persona \"{persona.Name}\" uses the model {persona.Model}, which is not in the current model list. Start it or pick another model if replies fail.");
    }

    /// <summary>
    /// Called after a saved conversation is reopened: switches to the persona it was recorded with,
    /// if that persona still exists.
    /// </summary>
    private void SwitchToConversationPersona(string? personaName)
    {
        try
        {
            var name = PersonaCatalog.CleanName(personaName);
            if (name.Length == 0 || string.Equals(name, _settings.ActivePersona, StringComparison.OrdinalIgnoreCase))
                return;

            if (SwitchPersona(name))
                AddSystemMessage($"Switched to the persona this chat used: {CurrentPersona?.Name ?? name}.");
            else
                AddSystemMessage($"This chat used the persona \"{name}\", which no longer exists. Keeping \"{_settings.ActivePersona}\".");
        }
        catch (Exception ex)
        {
            AddSystemMessage($"Could not switch persona: {ex.Message}");
        }
    }

    // ==================== Save / update / delete ====================

    private void PersonaNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return))
            return;

        e.Handled = true;
        SavePersona_Click(sender, e);
    }

    private void SavePersona_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var error = PersonaCatalog.ValidateNewName(_settings.Personas, PersonaNameBox.Text);
            if (error != null)
            {
                SetPersonaStatus(error, "WarningBrush");
                PersonaNameBox.Focus();
                return;
            }

            var persona = new Persona { Name = PersonaCatalog.CleanName(PersonaNameBox.Text) };
            CapturePersonaFromControls(persona);
            _settings.Personas.Add(persona);
            _settings.ActivePersona = persona.Name;
            PersonaNameBox.Text = "";
            ResetPersonaDeleteConfirm();
            RefreshPersonaCombo();
            SetPersonaStatus($"Saved \"{persona.Name}\" and made it active.", "SuccessBrush");
            SaveSettings();
        }
        catch (Exception ex)
        {
            SetPersonaStatus($"Could not save the persona: {ex.Message}", "ErrorBrush");
        }
    }

    private void UpdatePersona_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var persona = CurrentPersona;
            if (persona == null)
            {
                SetPersonaStatus("Pick a persona in the top bar first.", "WarningBrush");
                return;
            }

            CapturePersonaFromControls(persona);
            ResetPersonaDeleteConfirm();
            UpdatePersonaControls();
            SetPersonaStatus($"Updated \"{persona.Name}\" with the current settings.", "SuccessBrush");
            SaveSettings();
        }
        catch (Exception ex)
        {
            SetPersonaStatus($"Could not update the persona: {ex.Message}", "ErrorBrush");
        }
    }

    private void DeletePersona_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var persona = CurrentPersona;
            if (persona == null || !PersonaCatalog.CanDelete(_settings.Personas))
            {
                ResetPersonaDeleteConfirm();
                SetPersonaStatus("The last persona cannot be deleted.", "WarningBrush");
                return;
            }

            // First click arms the button; a second click within a few seconds deletes.
            if (!_personaDeletePending)
            {
                _personaDeletePending = true;
                DeletePersonaBtn.Content = "Click to confirm";
                _personaDeleteTimer ??= CreatePersonaDeleteTimer();
                _personaDeleteTimer.Stop();
                _personaDeleteTimer.Start();
                return;
            }

            ResetPersonaDeleteConfirm();
            _settings.Personas.Remove(persona);
            var next = _settings.Personas[0];
            // The prompt, voice and rate now follow the persona that takes over.
            SwitchPersona(next.Name);
            RefreshPersonaCombo();
            SetPersonaStatus($"Deleted \"{persona.Name}\". Now using \"{next.Name}\".", "TextMutedBrush");
        }
        catch (Exception ex)
        {
            SetPersonaStatus($"Could not delete the persona: {ex.Message}", "ErrorBrush");
        }
    }

    private DispatcherTimer CreatePersonaDeleteTimer()
    {
        var timer = new DispatcherTimer { Interval = PersonaDeleteConfirmWindow };
        timer.Tick += (_, _) => ResetPersonaDeleteConfirm();
        return timer;
    }

    private void ResetPersonaDeleteConfirm()
    {
        _personaDeleteTimer?.Stop();
        _personaDeletePending = false;
        DeletePersonaBtn.Content = DeletePersonaLabel;
    }

    private void CapturePersonaFromControls(Persona persona)
    {
        persona.SystemPrompt = SystemPromptBox.Text ?? "";
        var voice = VoiceCombo.SelectedItem?.ToString() ?? VoiceCombo.Text;
        persona.VoiceName = string.IsNullOrWhiteSpace(voice) ? _settings.VoiceName ?? "" : voice.Trim();
        persona.SpeechRate = PersonaCatalog.ClampSpeechRate((int)Math.Round(RateSlider.Value));
        persona.Model = PersonaModelToggle.IsChecked == true ? (ModelCombo.Text ?? "").Trim() : "";
    }

    // ==================== UI ====================

    /// <summary>Rebuilds the combo after the list changed and selects the active persona.</summary>
    private void RefreshPersonaCombo()
    {
        _personaComboUpdating = true;
        try
        {
            PersonaCombo.Items.Clear();
            foreach (var persona in _settings.Personas)
                PersonaCombo.Items.Add(persona.Name);
        }
        finally
        {
            _personaComboUpdating = false;
        }

        SelectPersonaInCombo();
        UpdatePersonaControls();
    }

    private void SelectPersonaInCombo()
    {
        var name = CurrentPersona?.Name;
        if (Equals(PersonaCombo.SelectedItem, name))
            return;

        _personaComboUpdating = true;
        try
        {
            PersonaCombo.SelectedItem = name;
        }
        finally
        {
            _personaComboUpdating = false;
        }
    }

    private void UpdatePersonaControls()
    {
        var active = CurrentPersona;
        PersonaActiveText.Text = active == null
            ? "No persona selected."
            : active.Model.Length > 0
                ? $"Active persona: {active.Name}  ·  model {active.Model}"
                : $"Active persona: {active.Name}";
        UpdatePersonaBtn.IsEnabled = active != null;
        DeletePersonaBtn.IsEnabled = active != null && PersonaCatalog.CanDelete(_settings.Personas);
    }

    private void SetPersonaStatus(string text, string brushKey)
    {
        PersonaStatusText.Text = text;
        PersonaStatusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }
}
