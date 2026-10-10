using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace VoiceChatbot;

// System tray icon and the global listen hotkey (App expander). Both keep working while the window is
// minimized or hidden in the tray. Window_Closing calls DisposeTrayAndHotkey; the choices and their
// RegisterHotKey arguments live in Core/GlobalHotkeys.cs.
public partial class MainWindow
{
    private const int WmHotkey = 0x0312;
    // Any id in 0x0000-0xBFFF that is unique among this window's hotkeys.
    private const int ListenHotkeyId = 0x5643;

    private Forms.NotifyIcon? _trayIcon;
    private Drawing.Icon? _trayIconImage;
    private Forms.ToolStripMenuItem? _trayListenItem;
    private Forms.ToolStripMenuItem? _traySpeechItem;
    private HwndSource? _hotkeySource;
    private IntPtr _hotkeyHwnd;          // non-zero while the listen hotkey is registered
    private string _activeHotkey = "";   // the registered choice, "" when none
    private bool _loadingHotkeyChoices;
    private bool _trayHintShown;
    private bool _trayDisposed;
    private WindowState _stateBeforeTray = WindowState.Normal;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ==================== Settings ====================

    /// <summary>Called once from ApplySettings: fills the App expander, shows the tray icon and registers the hotkey.</summary>
    private void ApplyTrayAndHotkeySettings()
    {
        _settings.GlobalListenHotkey = GlobalHotkeys.Normalize(_settings.GlobalListenHotkey);
        MinimizeToTrayToggle.IsChecked = _settings.MinimizeToTray;

        _loadingHotkeyChoices = true;
        try
        {
            GlobalHotkeyCombo.ItemsSource = GlobalHotkeys.Choices;
            GlobalHotkeyCombo.SelectedItem = _settings.GlobalListenHotkey;
        }
        finally
        {
            _loadingHotkeyChoices = false;
        }

        EnsureTrayIcon();
        RegisterListenHotkey(_settings.GlobalListenHotkey);
    }

    private void SaveTrayAndHotkeySettings()
    {
        _settings.MinimizeToTray = MinimizeToTrayToggle.IsChecked == true;
        _settings.GlobalListenHotkey = GlobalHotkeys.Normalize(GlobalHotkeyCombo.SelectedItem as string ?? _settings.GlobalListenHotkey);
    }

    private void MinimizeToTrayToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.MinimizeToTray = MinimizeToTrayToggle.IsChecked == true;
        SaveSettings();
    }

    private void GlobalHotkeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingHotkeyChoices || _settings == null || GlobalHotkeyCombo.SelectedItem is not string choice)
            return;

        _settings.GlobalListenHotkey = GlobalHotkeys.Normalize(choice);
        RegisterListenHotkey(_settings.GlobalListenHotkey);
        SaveSettings();
    }

    // ==================== Global hotkey ====================

    /// <summary>
    /// Makes <paramref name="choice"/> the global listen hotkey in place of the current one and shows the
    /// result in the App expander. Returns false when Windows refused it, usually because another app has it.
    /// </summary>
    private bool RegisterListenHotkey(string choice)
    {
        UnregisterListenHotkey();
        if (_trayDisposed)
            return false;

        if (!GlobalHotkeys.TryGetKeys(choice, out var keys))
        {
            SetHotkeyStatus("Off. Use the Listen / Stop listening button, or Ctrl+L while this window is active.", "", "TextMutedBrush");
            UpdateTrayText();
            return true;
        }

        try
        {
            var hwnd = EnsureHotkeyHook();
            if (hwnd != IntPtr.Zero &&
                RegisterHotKey(hwnd, ListenHotkeyId, GlobalHotkeys.RegisterModifiers(keys), keys.VirtualKey))
            {
                _hotkeyHwnd = hwnd;
                _activeHotkey = keys.Name;
                SetHotkeyStatus($"Press {keys.Name} in any app to ask a question; press it again to stop listening.",
                    "", "SuccessBrush");
                AppLog.Info($"Global listen hotkey {keys.Name} registered.");
                UpdateTrayText();
                return true;
            }

            var error = hwnd == IntPtr.Zero ? 0 : Marshal.GetLastWin32Error();
            ReportHotkeyFailure(GlobalHotkeys.DescribeRegisterFailure(keys.Name, error));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Could not register the {keys.Name} hotkey.", ex);
            ReportHotkeyFailure(GlobalHotkeys.DescribeRegisterFailure(keys.Name, 0));
        }

        UpdateTrayText();
        return false;
    }

    private void UnregisterListenHotkey()
    {
        if (_hotkeyHwnd == IntPtr.Zero)
            return;

        try
        {
            UnregisterHotKey(_hotkeyHwnd, ListenHotkeyId);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not unregister the listen hotkey.", ex);
        }

        _hotkeyHwnd = IntPtr.Zero;
        _activeHotkey = "";
    }

    /// <summary>The window handle WM_HOTKEY is posted to; hooks this window's message loop the first time.</summary>
    private IntPtr EnsureHotkeyHook()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        if (_hotkeySource == null && hwnd != IntPtr.Zero)
        {
            _hotkeySource = HwndSource.FromHwnd(hwnd);
            _hotkeySource?.AddHook(HotkeyWndProc);
        }

        return hwnd;
    }

    private IntPtr HotkeyWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && (long)wParam == ListenHotkeyId)
        {
            handled = true;
            // Leave the window procedure before touching the microphone.
            Dispatcher.BeginInvoke(ToggleListeningFromShortcut);
        }

        return IntPtr.Zero;
    }

    private void ReportHotkeyFailure(string message)
    {
        SetHotkeyStatus(message, "", "WarningBrush");
        AddSystemMessage(message);
    }

    private void SetHotkeyStatus(string text, string glyph, string brushKey)
    {
        HotkeyStatusText.Text = text;
        HotkeyStatusIcon.Text = glyph;
        HotkeyStatusIcon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
    }

    /// <summary>
    /// The global hotkey and the tray's Listen item: work like the Listen / Stop listening switch. Works while
    /// hidden.
    /// </summary>
    private void ToggleListeningFromShortcut()
    {
        try
        {
            if (_shutdownStarted || _speech == null)
                return;

            if (IsListeningOn)
            {
                ToggleListening();
                return;
            }

            ToggleListening();
            if (AlwaysListenToggle.IsChecked != true)
                NotifyWhenInBackground("Could not start listening. Open Voice Chatbot to see why.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Listen shortcut failed", ex);
            AddSystemMessage($"Could not start or stop listening: {ex.Message}");
        }
    }

    // ==================== Tray icon ====================

    private void EnsureTrayIcon()
    {
        if (_trayIcon != null || _trayDisposed)
            return;

        Forms.ContextMenuStrip? menu = null;
        try
        {
            _trayIconImage ??= LoadTrayIconImage();

            menu = new Forms.ContextMenuStrip();
            var openItem = new Forms.ToolStripMenuItem("Open Voice Chatbot", null, (_, _) => RunTrayCommand(ShowFromTray));
            _trayListenItem = new Forms.ToolStripMenuItem("Listen", null, (_, _) => RunTrayCommand(ToggleListeningFromShortcut));
            _traySpeechItem = new Forms.ToolStripMenuItem("Speak responses", null, (_, _) => RunTrayCommand(ToggleSpeechFromTray));
            // Close after the menu finishes its click: closing disposes this menu (DisposeTrayAndHotkey).
            var exitItem = new Forms.ToolStripMenuItem("Exit", null,
                (_, _) => RunTrayCommand(() => Dispatcher.BeginInvoke(new Action(Close))));
            menu.Items.AddRange(new Forms.ToolStripItem[]
            {
                openItem, _trayListenItem, _traySpeechItem, new Forms.ToolStripSeparator(), exitItem
            });
            menu.Opening += (_, _) => RunTrayCommand(UpdateTrayMenu);

            _trayIcon = new Forms.NotifyIcon
            {
                Icon = _trayIconImage,
                ContextMenuStrip = menu,
                Text = TrayTooltip.Format(null, null),
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => RunTrayCommand(ShowFromTray);
            _trayIcon.BalloonTipClicked += (_, _) => RunTrayCommand(ShowFromTray);

            _speech.StateChanged += OnVoiceStateChangedForTray;
            // Backstop: never leave a ghost icon behind, even if the window closes some other way.
            Closed += (_, _) => DisposeTrayAndHotkey();
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not create the tray icon", ex);
            AddSystemMessage($"The tray icon is not available: {ex.Message}");
            try { _trayIcon?.Dispose(); } catch { }
            try { menu?.Dispose(); } catch { }
            _trayIcon = null;
            _trayListenItem = null;
            _traySpeechItem = null;
        }
    }

    /// <summary>The app icon at the tray's size, falling back to the exe's icon and then the Windows default.</summary>
    private static Drawing.Icon LoadTrayIconImage()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Resources/AppIcon.ico"));
            if (resource?.Stream != null)
            {
                using var stream = resource.Stream;
                return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not load the tray icon from resources.", ex);
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && Drawing.Icon.ExtractAssociatedIcon(exe) is { } icon)
                return icon;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not read the icon from the exe.", ex);
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    /// <summary>WinForms shows its own error dialog for exceptions in its event handlers, so catch them here.</summary>
    private void RunTrayCommand(Action command)
    {
        try
        {
            command();
        }
        catch (Exception ex)
        {
            AppLog.Error("Tray command failed", ex);
        }
    }

    private void UpdateTrayMenu()
    {
        if (_trayListenItem == null || _traySpeechItem == null)
            return;

        _trayListenItem.Text = _speech != null && IsListeningOn ? "Stop listening" : "Listen";
        _trayListenItem.ShortcutKeyDisplayString = _activeHotkey;
        _traySpeechItem.Checked = TtsToggle.IsChecked == true;
    }

    private void OnVoiceStateChangedForTray(VoiceState state) =>
        Dispatcher.BeginInvoke(() => RunTrayCommand(UpdateTrayText));

    private void UpdateTrayText()
    {
        if (_trayIcon == null)
            return;

        var status = _speech?.CurrentState switch
        {
            VoiceState.Listening => "Listening...",
            VoiceState.Processing => "Processing...",
            VoiceState.Speaking => "Speaking...",
            _ => ""
        };
        _trayIcon.Text = TrayTooltip.Format(status, _activeHotkey);
    }

    private void ToggleSpeechFromTray()
    {
        // Same as flipping "Speak responses" in Voice Output.
        TtsToggle.IsChecked = TtsToggle.IsChecked != true;
        TtsToggle_Click(TtsToggle, new RoutedEventArgs());
    }

    /// <summary>Shows a tray balloon unless the window is active, where the chat already shows what happened.</summary>
    private void NotifyWhenInBackground(string message)
    {
        if (_trayIcon == null || IsActive)
            return;

        try
        {
            _trayIcon.ShowBalloonTip(4000, TrayTooltip.AppName, message, Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not show a tray notification.", ex);
        }
    }

    // ==================== Window ====================

    /// <summary>From Window_StateChanged: with "Minimize to tray" on, minimizing hides the window to the tray.</summary>
    private void HideToTrayIfMinimized()
    {
        if (WindowState != WindowState.Minimized)
        {
            _stateBeforeTray = WindowState;
            return;
        }

        // Never hide without a tray icon to bring the window back.
        if (_settings == null || !_settings.MinimizeToTray || _trayIcon == null || _shutdownStarted)
            return;

        Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            var hotkey = _activeHotkey.Length > 0 ? $" Press {_activeHotkey} to talk." : "";
            NotifyWhenInBackground($"Still running in the tray. Double-click the icon to open it.{hotkey}");
        }
    }

    private void ShowFromTray()
    {
        if (_shutdownStarted)
            return;

        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = _stateBeforeTray;
        Activate();
        // Activate alone can leave the window behind the app that has focus; a Topmost flip brings it forward.
        Topmost = true;
        Topmost = false;
        // Replies may have arrived while hidden; size their bubbles to the visible chat.
        Dispatcher.BeginInvoke(UpdateChatBubbleWidths, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Removes the tray icon and the hotkey. Called first in Window_Closing; safe to call again.</summary>
    private void DisposeTrayAndHotkey()
    {
        _trayDisposed = true;
        UnregisterListenHotkey();
        try
        {
            _hotkeySource?.RemoveHook(HotkeyWndProc);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Could not remove the hotkey hook.", ex);
        }

        _hotkeySource = null;
        if (_speech != null)
            _speech.StateChanged -= OnVoiceStateChangedForTray;

        if (_trayIcon != null)
        {
            try
            {
                _trayIcon.Visible = false;
                _trayIcon.ContextMenuStrip?.Dispose();
                _trayIcon.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Could not remove the tray icon.", ex);
            }

            _trayIcon = null;
        }

        _trayListenItem = null;
        _traySpeechItem = null;
        _trayIconImage?.Dispose();
        _trayIconImage = null;
    }
}
