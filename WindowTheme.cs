using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace VoiceChatbot;

/// <summary>
/// Gives windows a dark title bar that matches the app theme (Windows 10 20H1+ / Windows 11).
/// Silently does nothing on older systems.
/// </summary>
public static class WindowTheme
{
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void UseDarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) => Apply(window);
    }

    private static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var enabled = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));

            // Windows 11: tint the caption to the window background (COLORREF is 0x00BBGGRR).
            var caption = 0x00130D0B;
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
