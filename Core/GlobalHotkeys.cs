using System;
using System.Collections.Generic;
using System.Linq;

namespace VoiceChatbot;

/// <param name="Name">How the combination is shown and saved, for example "Ctrl+Alt+Space".</param>
/// <param name="Modifiers">RegisterHotKey MOD_* flags, without MOD_NOREPEAT.</param>
/// <param name="VirtualKey">The VK_* code of the key.</param>
public readonly record struct HotkeyKeys(string Name, uint Modifiers, uint VirtualKey);

/// <summary>
/// The choices for the global listen hotkey (Settings > App) and the Win32 RegisterHotKey arguments
/// for each. MainWindow.Tray.cs does the registering.
/// </summary>
public static class GlobalHotkeys
{
    public const string Off = "Off";
    public const string CtrlAltSpace = "Ctrl+Alt+Space";
    public const string CtrlShiftSpace = "Ctrl+Shift+Space";
    public const string CtrlAltL = "Ctrl+Alt+L";
    public const string Default = CtrlAltSpace;

    // RegisterHotKey fsModifiers flags and virtual-key codes (WinUser.h).
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModNoRepeat = 0x4000;
    public const uint VkSpace = 0x20;
    public const uint VkL = 0x4C;

    /// <summary>GetLastError after RegisterHotKey when another app (or another copy of this one) owns the keys.</summary>
    public const int ErrorHotkeyAlreadyRegistered = 1409;

    /// <summary>The choices in the order the settings list shows them.</summary>
    public static IReadOnlyList<string> Choices { get; } = new[] { Off, CtrlAltSpace, CtrlShiftSpace, CtrlAltL };

    private static readonly HotkeyKeys[] KeysByChoice =
    {
        new(CtrlAltSpace, ModControl | ModAlt, VkSpace),
        new(CtrlShiftSpace, ModControl | ModShift, VkSpace),
        new(CtrlAltL, ModControl | ModAlt, VkL),
    };

    /// <summary>
    /// The saved choice as one of <see cref="Choices"/>. Case and spaces are ignored ("ctrl + alt + space");
    /// blank or unknown text gives <see cref="Default"/>.
    /// </summary>
    public static string Normalize(string? choice)
    {
        var compact = new string((choice ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray());
        return Choices.FirstOrDefault(c => string.Equals(c, compact, StringComparison.OrdinalIgnoreCase)) ?? Default;
    }

    /// <summary>The keys to register for a choice. False for "Off".</summary>
    public static bool TryGetKeys(string? choice, out HotkeyKeys keys)
    {
        var name = Normalize(choice);
        foreach (var candidate in KeysByChoice)
        {
            if (candidate.Name == name)
            {
                keys = candidate;
                return true;
            }
        }

        keys = default;
        return false;
    }

    /// <summary>fsModifiers for RegisterHotKey: MOD_NOREPEAT is added so holding the keys fires once.</summary>
    public static uint RegisterModifiers(HotkeyKeys keys) => keys.Modifiers | ModNoRepeat;

    /// <summary>The note shown when RegisterHotKey fails with <paramref name="win32Error"/>.</summary>
    public static string DescribeRegisterFailure(string? choice, int win32Error)
    {
        var name = Normalize(choice);
        if (win32Error == ErrorHotkeyAlreadyRegistered)
            return $"The {name} hotkey is already used by another app (for example the full Voice Chatbot app, or another copy of {AppPaths.ProductName}), so it is not active. " +
                   "Close that app or pick a different hotkey in Settings > App.";

        var detail = win32Error == 0 ? "" : $" (Windows error {win32Error})";
        return $"Could not set the {name} hotkey{detail}. Pick a different hotkey in Settings > App.";
    }
}

/// <summary>The tray icon's hover text. NotifyIcon.Text throws above 127 characters.</summary>
public static class TrayTooltip
{
    public const string AppName = AppPaths.ProductName;
    public const int MaxLength = 127;

    /// <summary>
    /// "Voice Chatbot Mini - Listening... (Ctrl+Alt+Space to talk)". The status and the hotkey hint are left out
    /// when blank; pass no hotkey (or "Off") while none is registered. Whitespace runs become one space and
    /// the result is cut to <see cref="MaxLength"/> characters.
    /// </summary>
    public static string Format(string? status, string? hotkey)
    {
        var text = AppName;
        var trimmedStatus = CollapseWhitespace(status);
        if (trimmedStatus.Length > 0)
            text += " - " + trimmedStatus;

        var trimmedHotkey = CollapseWhitespace(hotkey);
        if (trimmedHotkey.Length > 0 && !string.Equals(trimmedHotkey, GlobalHotkeys.Off, StringComparison.OrdinalIgnoreCase))
            text += $" ({trimmedHotkey} to talk)";

        if (text.Length <= MaxLength)
            return text;

        var cut = MaxLength - 3;
        if (char.IsHighSurrogate(text[cut - 1]))
            cut--;
        return text[..cut] + "...";
    }

    private static string CollapseWhitespace(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
