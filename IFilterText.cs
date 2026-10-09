using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace VoiceChatbot;

/// <summary>
/// Best-effort text from any file type that has a Windows text filter (IFilter, the add-ins Windows
/// Search uses): old Office files (.doc, .xls, .ppt through the Office filter that ships with
/// Windows or the Office filter pack), Publisher, Visio, OneNote, WordPerfect and others. Each read
/// runs on its own STA thread with a time limit and a size cap, and every filter error, including a
/// misbehaving filter, comes back as an error result rather than an exception.
/// </summary>
internal static class IFilterText
{
    public const int DefaultMaxChars = 4_000_000;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private const string IFilterIid = "{89BCB740-6119-101A-BCB7-00DD010655AF}";
    // Persistent handlers that mean "no text": the null filter. (The plain-text one is fine.)
    private const string NullPersistentHandler = "{098F2470-BAE0-11CD-B579-08002B30BFEB}";

    private const int FilterEndOfChunks = unchecked((int)0x80041700);
    private const int FilterNoMoreText = unchecked((int)0x80041701);
    private const int FilterAccess = unchecked((int)0x80041703);
    private const int FilterNoText = unchecked((int)0x80041705);
    private const int FilterEmbeddingUnavailable = unchecked((int)0x80041707);
    private const int FilterLinkUnavailable = unchecked((int)0x80041708);
    private const int FilterPassword = unchecked((int)0x8004170B);
    private const int FilterUnknownFormat = unchecked((int)0x8004170C);
    private const int FilterLastText = 0x00041709;

    public sealed record FilterResult(string Text, string Error, DocumentReadProblem Problem);

    /// <summary>True when Windows has a (non-null) text filter registered for the extension.</summary>
    public static bool HasFilter(string extension)
    {
        try
        {
            var handler = FindPersistentHandler(extension);
            return handler != null &&
                   !handler.Equals(NullPersistentHandler, StringComparison.OrdinalIgnoreCase) &&
                   ReadDefault($@"CLSID\{handler}\PersistentAddinsRegistered\{IFilterIid}") != null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the file's text through its Windows text filter. Cancelling throws
    /// OperationCanceledException; a filter that hangs is abandoned after the time limit.
    /// </summary>
    public static async Task<FilterResult> ExtractAsync(string path, CancellationToken ct, int maxChars = DefaultMaxChars, TimeSpan? timeout = null)
    {
        var extension = Path.GetExtension(path);
        if (!OperatingSystem.IsWindows())
            return new FilterResult("", DocumentFileTypes.GetNoFilterMessage(extension), DocumentReadProblem.NeedsFilter);
        if (!HasFilter(extension))
            return new FilterResult("", DocumentFileTypes.GetNoFilterMessage(extension), DocumentReadProblem.NeedsFilter);

        var done = new TaskCompletionSource<FilterResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var stopToken = stop.Token;
        var thread = new Thread(() =>
        {
            try
            {
                done.TrySetResult(ReadWithFilter(path, maxChars, stopToken));
            }
            catch (OperationCanceledException)
            {
                done.TrySetCanceled();
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Windows text filter failed on {Path.GetFileName(path)}.", ex);
                done.TrySetResult(new FilterResult("", $"The Windows text filter for {extension} files failed: {ex.Message}", DocumentReadProblem.Failed));
            }
        })
        {
            IsBackground = true,
            Name = "IFilter text"
        };
        // Most filters are apartment-threaded; an STA thread lets them run here without marshaling.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        var limit = Task.Delay(timeout ?? DefaultTimeout, ct);
        var finished = await Task.WhenAny(done.Task, limit).ConfigureAwait(false);
        if (finished == done.Task)
            return await done.Task.ConfigureAwait(false);

        // Cancelled or too slow: ask the filter loop to stop. A filter stuck inside a call keeps its
        // background thread until it returns; it cannot be interrupted safely.
        stop.Cancel();
        ct.ThrowIfCancellationRequested();
        AppLog.Warn($"Windows text filter timed out on {Path.GetFileName(path)}.");
        return new FilterResult("", $"The Windows text filter for {extension} files took too long and was stopped.", DocumentReadProblem.Failed);
    }

    private static FilterResult ReadWithFilter(string path, int maxChars, CancellationToken stop)
    {
        var extension = Path.GetExtension(path);
        object? unknown = null;
        IFilter? filter = null;
        var buffer = IntPtr.Zero;
        try
        {
            var hr = NativeMethods.LoadIFilter(path, IntPtr.Zero, out unknown);
            if (hr != 0 || unknown == null)
            {
                // A filter is registered (HasFilter), so this is the file or the filter failing.
                return hr == FilterAccess
                    ? new FilterResult("", "Windows could not open the file to read it (it may be in use or not downloaded).", DocumentReadProblem.Failed)
                    : new FilterResult("", $"The Windows text filter for {extension} files could not open this file (0x{hr:X8}).", DocumentReadProblem.Failed);
            }

            filter = (IFilter)unknown;
            const IFILTER_INIT flags = IFILTER_INIT.CANON_PARAGRAPHS | IFILTER_INIT.HARD_LINE_BREAKS |
                                       IFILTER_INIT.CANON_HYPHENS | IFILTER_INIT.CANON_SPACES |
                                       IFILTER_INIT.APPLY_INDEX_ATTRIBUTES;
            hr = filter.Init(flags, 0, IntPtr.Zero, out _);
            if (hr < 0)
            {
                return hr switch
                {
                    FilterPassword => new FilterResult("", DocumentFileTypes.GetPasswordMessage(path), DocumentReadProblem.PasswordProtected),
                    FilterUnknownFormat => new FilterResult("", $"The Windows text filter does not recognize this {extension} file; it may be damaged or in a different format.", DocumentReadProblem.Failed),
                    FilterAccess => new FilterResult("", "Windows could not open the file to read it (it may be in use or not downloaded).", DocumentReadProblem.Failed),
                    _ => new FilterResult("", $"The Windows text filter could not open this {extension} file (0x{hr:X8}).", DocumentReadProblem.Failed)
                };
            }

            const int bufferChars = 8192;
            buffer = Marshal.AllocHGlobal(bufferChars * 2);
            var output = new StringBuilder();
            var chunks = 0;
            while (output.Length < maxChars && chunks++ < 1_000_000)
            {
                stop.ThrowIfCancellationRequested();
                hr = filter.GetChunk(out var chunk);
                if (hr == FilterEndOfChunks)
                    break;
                if (hr is FilterEmbeddingUnavailable or FilterLinkUnavailable)
                    continue; // an embedded object or link it cannot read: skip that chunk
                if (hr < 0)
                    break;    // access denied, password, or a filter error: keep what was read

                if ((chunk.flags & CHUNKSTATE.CHUNK_TEXT) == 0)
                    continue; // property values (title, author...) are not body text

                if (output.Length > 0)
                {
                    switch (chunk.breakType)
                    {
                        case CHUNK_BREAKTYPE.CHUNK_EOP or CHUNK_BREAKTYPE.CHUNK_EOC or CHUNK_BREAKTYPE.CHUNK_EOS when output[^1] != '\n':
                            output.Append(chunk.breakType == CHUNK_BREAKTYPE.CHUNK_EOS ? ' ' : '\n');
                            break;
                        case CHUNK_BREAKTYPE.CHUNK_EOW when !char.IsWhiteSpace(output[^1]):
                            output.Append(' ');
                            break;
                    }
                }

                while (output.Length < maxChars)
                {
                    stop.ThrowIfCancellationRequested();
                    var count = (uint)bufferChars;
                    hr = filter.GetText(ref count, buffer);
                    if (hr is FilterNoMoreText or FilterNoText)
                        break;
                    if (hr < 0)
                        break;
                    if (count > 0)
                        output.Append(Marshal.PtrToStringUni(buffer, (int)Math.Min(count, (uint)bufferChars)).Replace('\0', ' '));
                    if (hr == FilterLastText)
                        break;
                }
            }

            var text = output.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
            if (text.Length > maxChars)
                text = text[..maxChars];
            return string.IsNullOrWhiteSpace(text)
                ? new FilterResult("", $"No readable text was found in this {DocumentFileTypes.GetTypeName(path)} file.", DocumentReadProblem.NoText)
                : new FilterResult(text, "", DocumentReadProblem.None);
        }
        catch (Exception ex) when (ex is COMException or SEHException or InvalidCastException or DllNotFoundException or EntryPointNotFoundException or ArgumentException)
        {
            AppLog.Warn($"Windows text filter failed on {Path.GetFileName(path)}.", ex);
            return new FilterResult("", $"The Windows text filter for {extension} files failed (0x{ex.HResult:X8}).", DocumentReadProblem.Failed);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
            ReleaseComObject(filter);
            if (!ReferenceEquals(unknown, filter))
                ReleaseComObject(unknown);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        try
        {
            if (value != null && Marshal.IsComObject(value))
                Marshal.FinalReleaseComObject(value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidComObjectException)
        {
        }
    }

    // ==================== Registry lookup ====================

    // HKCR\.ext\PersistentHandler, or HKCR\.ext -> ProgID -> CLSID -> PersistentHandler.
    private static string? FindPersistentHandler(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return null;

        var ext = extension.StartsWith('.') ? extension : "." + extension;
        var handler = ReadDefault($@"{ext}\PersistentHandler");
        if (handler != null)
            return handler;

        var progId = ReadDefault(ext);
        if (progId == null)
            return null;
        var clsid = ReadDefault($@"{progId}\CLSID");
        return clsid == null ? null : ReadDefault($@"CLSID\{clsid}\PersistentHandler");
    }

    private static string? ReadDefault(string subKey)
    {
        using var key = Registry.ClassesRoot.OpenSubKey(subKey);
        var value = key?.GetValue(null) as string;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // ==================== COM interop ====================

    [Flags]
    private enum IFILTER_INIT : uint
    {
        CANON_PARAGRAPHS = 1,
        HARD_LINE_BREAKS = 2,
        CANON_HYPHENS = 4,
        CANON_SPACES = 8,
        APPLY_INDEX_ATTRIBUTES = 16,
        APPLY_OTHER_ATTRIBUTES = 32,
        INDEXING_ONLY = 64,
        SEARCH_LINKS = 128,
        APPLY_CRAWL_ATTRIBUTES = 256,
        FILTER_OWNED_VALUE_OK = 512,
    }

    [Flags]
    private enum CHUNKSTATE : uint
    {
        CHUNK_TEXT = 0x1,
        CHUNK_VALUE = 0x2,
        CHUNK_FILTER_OWNED_VALUE = 0x4,
    }

    private enum CHUNK_BREAKTYPE : uint
    {
        CHUNK_NO_BREAK = 0,
        CHUNK_EOW = 1,
        CHUNK_EOS = 2,
        CHUNK_EOP = 3,
        CHUNK_EOC = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPSPEC
    {
        public uint ulKind;
        public IntPtr data; // PROPID or LPWSTR (owned by the filter)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FULLPROPSPEC
    {
        public Guid guidPropSet;
        public PROPSPEC psProperty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STAT_CHUNK
    {
        public uint idChunk;
        public CHUNK_BREAKTYPE breakType;
        public CHUNKSTATE flags;
        public uint locale;
        public FULLPROPSPEC attribute;
        public uint idChunkSource;
        public uint cwcStartSource;
        public uint cwcLenSource;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILTERREGION
    {
        public uint idChunk;
        public uint cwcStart;
        public uint cwcExtent;
    }

    [ComImport]
    [Guid("89BCB740-6119-101A-BCB7-00DD010655AF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFilter
    {
        [PreserveSig]
        int Init(IFILTER_INIT grfFlags, uint cAttributes, IntPtr aAttributes, out uint pdwFlags);

        [PreserveSig]
        int GetChunk(out STAT_CHUNK pStat);

        [PreserveSig]
        int GetText(ref uint pcwcBuffer, IntPtr awcBuffer);

        [PreserveSig]
        int GetValue(out IntPtr ppPropValue);

        [PreserveSig]
        int BindRegion(FILTERREGION origPos, ref Guid riid, out IntPtr ppunk);
    }

    private static class NativeMethods
    {
        [DllImport("query.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int LoadIFilter(string pwcsPath, IntPtr pUnkOuter, [MarshalAs(UnmanagedType.IUnknown)] out object ppIUnk);
    }
}
