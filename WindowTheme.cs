using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VoiceChatbot;

/// <summary>
/// Gives windows a dark or light title bar that matches the app theme (Windows 10 20H1+ / Windows 11)
/// and updates it when the theme changes. Silently does nothing on older systems.
/// </summary>
public static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Themes the title bar of <paramref name="window"/> once it has a handle.</summary>
    public static void UseThemedTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
    }

    /// <summary>Re-themes the title bar of every open window (after a theme change). UI thread only.</summary>
    public static void ApplyToOpenWindows()
    {
        if (Application.Current == null)
            return;

        foreach (Window window in Application.Current.Windows)
            Apply(window);
    }

    private static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var light = ThemeManager.IsLight;
            var dark = light ? 0 : 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref dark, sizeof(int));

            // Windows 11: tint the caption to the window background (COLORREF is 0x00BBGGRR).
            var rgb = Convert.ToInt32(ThemePalette.WindowBackground(light).TrimStart('#'), 16);
            var caption = ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
