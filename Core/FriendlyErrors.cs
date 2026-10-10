using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace VoiceChatbot;

/// <summary>
/// Turns the exceptions that clipboard, file-copy and "open with the default app" calls throw into a
/// short reason for a chat system message, instead of letting them reach the crash dialog; and a chat
/// server's "this model cannot see pictures" error into a plain explanation.
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

    /// <summary>The chat text when the built-in model was sent a picture it cannot see.</summary>
    public const string BuiltInModelCannotSeePictures =
        "The built-in model can't see pictures yet: picture support isn't installed. Open Choose AI model... to add it.";

    /// <summary>The chat text when a server's model was sent a picture it cannot see.</summary>
    public const string ModelCannotSeePictures =
        "This model can't see pictures. Pick a model that can (a vision model), or send the message without the picture.";

    // What servers answer when a model gets a picture it cannot take: llama.cpp without --mmproj ("image input
    // is not supported - hint: ... you may need to provide the mmproj"), Ollama ("this model is missing data
    // required for image input"), OpenAI ("image_url is only supported by certain models"), LM Studio ("Model
    // does not support images"), vLLM ("... is not a multimodal model").
    private static readonly Regex PicturesNotSupported = new(
        @"image input is not supported|missing data required for image input|image_url is only supported|" +
        @"not a multimodal model|provide the mmproj|" +
        @"(does not|doesn't|do not|don't|cannot|can't) (support|accept|handle|process) (image|picture|vision)|" +
        @"(image|images|image inputs?|vision) (is |are )?(not supported|unsupported)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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

    /// <summary>True when a chat server's error says the model cannot take pictures (it has no vision support).</summary>
    public static bool IsPicturesNotSupported(string? message) =>
        !string.IsNullOrWhiteSpace(message) && PicturesNotSupported.IsMatch(message);

    /// <summary>
    /// The text for a chat request that failed: a plain explanation when the server said the model cannot see
    /// pictures (<paramref name="builtInModel"/> picks the built-in model's wording), otherwise the error's own
    /// message, as before.
    /// </summary>
    public static string DescribeChatError(Exception ex, bool builtInModel)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (IsPicturesNotSupported(e.Message))
                return builtInModel ? BuiltInModelCannotSeePictures : ModelCannotSeePictures;
        }
        return ex.Message;
    }

    // IOExceptions from Windows carry HRESULT_FROM_WIN32(code) = 0x8007xxxx.
    private static int Win32Code(Exception ex) =>
        (ex.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? ex.HResult & 0xFFFF : 0;
}
