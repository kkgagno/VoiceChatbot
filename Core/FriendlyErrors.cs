using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace VoiceChatbot;

/// <summary>
/// Turns the exceptions that clipboard, file-copy and "open with the default app" calls throw into a
/// short reason for a chat system message, instead of letting them reach the crash dialog.
/// </summary>
public static class FriendlyErrors
{
    /// <summary>CLIPBRD_E_CANT_OPEN: another app has the clipboard open.</summary>
    public const int ClipboardCantOpen = unchecked((int)0x800401D0);

    private const int ErrorFileNotFound = 2;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorHandleDiskFull = 39;
    private const int ErrorDiskFull = 112;
    private const int ErrorNoAssociation = 1155;

    public static string Describe(Exception ex)
    {
        switch (ex)
        {
            case Win32Exception { NativeErrorCode: ErrorNoAssociation }:
                return "no app is set up to open this type of file.";
            case Win32Exception { NativeErrorCode: ErrorFileNotFound }:
            case FileNotFoundException:
                return "the file no longer exists.";
            case DirectoryNotFoundException:
                return "the folder no longer exists.";
            case ExternalException when ex.HResult == ClipboardCantOpen:
                return "the clipboard is in use by another app. Try again in a moment.";
            case UnauthorizedAccessException:
                return "access was denied. Choose a folder you can write to, or close the file if another app has it open.";
            case IOException when Win32Code(ex) is ErrorSharingViolation or ErrorLockViolation:
                return "the file is open in another app. Close it and try again.";
            case IOException when Win32Code(ex) is ErrorDiskFull or ErrorHandleDiskFull:
                return "the disk is full.";
            default:
                return string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
        }
    }

    // IOExceptions from Windows carry HRESULT_FROM_WIN32(code) = 0x8007xxxx.
    private static int Win32Code(Exception ex) =>
        (ex.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? ex.HResult & 0xFFFF : 0;
}
