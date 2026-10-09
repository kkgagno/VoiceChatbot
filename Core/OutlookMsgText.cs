using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VoiceChatbot;

/// <summary>
/// Readable text from an Outlook message saved as .msg (an OLE compound file of MAPI properties),
/// without Outlook: From, To, Cc, Date and Subject, the attachment names, then the plain-text body,
/// or the HTML body as text, or the compressed RTF body as text.
/// </summary>
public static class OutlookMsgText
{
    public const int DefaultMaxChars = 2_000_000;
    private const int MaxBodyBytes = 16 * 1024 * 1024;

    // MAPI property ids (the first four hex digits of a "__substg1.0_IIIITTTT" stream name).
    private const string Subject = "0037";
    private const string SenderName = "0C1A";
    private const string SenderEmail = "0C1F";
    private const string SentRepresentingName = "0042";
    private const string SentRepresentingEmail = "0065";
    private const string SenderSmtpAddress = "5D01";
    private const string DisplayTo = "0E04";
    private const string DisplayCc = "0E03";
    private const string Body = "1000";
    private const string BodyHtml = "1013";
    private const string RtfCompressed = "1009";
    private const string AttachLongFilename = "3707";
    private const string AttachFilename = "3704";
    private const string DisplayName = "3001";
    private const string TransportHeaders = "007D";

    /// <summary>True when the compound file looks like an Outlook item (it has MAPI property streams).</summary>
    public static bool IsOutlookMessage(CompoundFile file) =>
        file.Children(file.Root).Any(e => e.Name.StartsWith("__substg1.0_", StringComparison.OrdinalIgnoreCase) ||
                                          e.Name.StartsWith("__properties_version1.0", StringComparison.OrdinalIgnoreCase));

    public static string Extract(CompoundFile file, int maxChars = DefaultMaxChars)
    {
        var root = file.Root;
        var codePage = ReadCodePage(file, root);
        var output = new StringBuilder();

        var fromName = FirstNonEmpty(ReadString(file, root, SenderName, codePage), ReadString(file, root, SentRepresentingName, codePage));
        var fromEmail = FirstNonEmpty(ReadString(file, root, SenderSmtpAddress, codePage), ReadString(file, root, SenderEmail, codePage), ReadString(file, root, SentRepresentingEmail, codePage));
        var from = fromEmail.Length > 0 && fromEmail.Contains('@') && !fromName.Contains(fromEmail, StringComparison.OrdinalIgnoreCase)
            ? (fromName.Length > 0 ? $"{fromName} <{fromEmail}>" : fromEmail)
            : fromName;
        AppendHeader(output, "From", from);
        AppendHeader(output, "To", ReadString(file, root, DisplayTo, codePage));
        AppendHeader(output, "Cc", ReadString(file, root, DisplayCc, codePage));
        AppendHeader(output, "Date", ReadDate(file, root, ReadString(file, root, TransportHeaders, codePage)));
        AppendHeader(output, "Subject", ReadString(file, root, Subject, codePage));

        var attachments = file.Children(root)
            .Where(e => e.IsStorage && e.Name.StartsWith("__attach_version1.0_", StringComparison.OrdinalIgnoreCase))
            .Select(a => FirstNonEmpty(ReadString(file, a, AttachLongFilename, codePage), ReadString(file, a, AttachFilename, codePage), ReadString(file, a, DisplayName, codePage)))
            .Where(n => n.Length > 0)
            .ToList();
        if (attachments.Count > 0)
            output.Append("Attachments: ").Append(string.Join(", ", attachments)).Append('\n');

        var body = ReadString(file, root, Body, codePage);
        if (body.Trim().Length == 0)
        {
            var html = Bytes(file, root, BodyHtml + "0102");
            var htmlText = html.Length > 0
                ? TextDecoding.Decode(html, HtmlDocumentText.DetectCharset(html))
                : ReadString(file, root, BodyHtml, codePage);
            body = HtmlDocumentText.ToPlainText(htmlText);
        }
        if (body.Trim().Length == 0)
        {
            var rtf = DecompressRtf(Bytes(file, root, RtfCompressed + "0102"));
            if (rtf.Length > 0)
                body = RtfText.ToPlainText(Encoding.Latin1.GetString(rtf));
        }

        if (output.Length > 0)
            output.Append('\n');
        output.Append(body.Replace("\r\n", "\n").Trim());

        var text = output.ToString().Trim();
        return text.Length > maxChars ? text[..maxChars] : text;
    }

    private static void AppendHeader(StringBuilder output, string name, string value)
    {
        value = value.Replace("\r", " ").Replace("\n", " ").Trim();
        if (value.Length > 0)
            output.Append(name).Append(": ").Append(value).Append('\n');
    }

    private static string FirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    /// <summary>A string property: Unicode (001F) or 8-bit (001E) in the message's code page.</summary>
    private static string ReadString(CompoundFile file, CompoundEntry storage, string id, int codePage)
    {
        var unicode = Bytes(file, storage, id + "001F");
        if (unicode.Length > 0)
            return Encoding.Unicode.GetString(unicode).TrimEnd('\0');

        var ansi = Bytes(file, storage, id + "001E");
        if (ansi.Length > 0)
            return ((codePage > 0 ? TextDecoding.GetEncoding(codePage) : null) ?? TextDecoding.Ansi).GetString(ansi).TrimEnd('\0');
        return "";
    }

    private static byte[] Bytes(CompoundFile file, CompoundEntry storage, string idAndType)
    {
        var entry = file.Find(storage, "__substg1.0_" + idAndType);
        return entry == null ? Array.Empty<byte>() : file.ReadStream(entry, MaxBodyBytes);
    }

    // Fixed-size properties live in "__properties_version1.0": a 32-byte header for the message,
    // then 16-byte entries (property tag, flags, 8-byte value).
    private static IEnumerable<(ushort Id, ushort Type, ulong Value)> FixedProperties(CompoundFile file, CompoundEntry storage, int headerSize)
    {
        var entry = file.Find(storage, "__properties_version1.0");
        if (entry == null)
            yield break;

        var data = file.ReadStream(entry, 1024 * 1024);
        for (var offset = headerSize; offset + 16 <= data.Length; offset += 16)
        {
            var tag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            yield return ((ushort)(tag >> 16), (ushort)(tag & 0xFFFF), BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + 8)));
        }
    }

    private static int ReadCodePage(CompoundFile file, CompoundEntry root)
    {
        foreach (var (id, type, value) in FixedProperties(file, root, 32))
        {
            // PR_INTERNET_CPID (3FDE) or PR_MESSAGE_CODEPAGE (3FFD), both PT_LONG (0003).
            if (type == 0x0003 && id is 0x3FDE or 0x3FFD)
                return (int)(value & 0xFFFFFFFF);
        }
        return 0;
    }

    private static string ReadDate(CompoundFile file, CompoundEntry root, string transportHeaders)
    {
        foreach (var (id, type, value) in FixedProperties(file, root, 32))
        {
            // PR_CLIENT_SUBMIT_TIME (0039) or PR_MESSAGE_DELIVERY_TIME (0E06), PT_SYSTIME (0040).
            if (type == 0x0040 && id is 0x0039 or 0x0E06 && value > 0)
            {
                try
                {
                    return DateTime.FromFileTimeUtc((long)value).ToString("yyyy-MM-dd HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (ArgumentOutOfRangeException)
                {
                }
            }
        }

        // A received message also keeps its internet headers.
        foreach (var line in transportHeaders.Split('\n'))
        {
            if (line.StartsWith("Date:", StringComparison.OrdinalIgnoreCase))
                return line[5..].Trim();
        }
        return "";
    }

    private const string RtfPrebuf =
        "{\\rtf1\\ansi\\mac\\deff0\\deftab720{\\fonttbl;}{\\f0\\fnil \\froman \\fswiss \\fmodern \\fscript " +
        "\\fdecor MS Sans SerifSymbolArialTimes New RomanCourier{\\colortbl\\red0\\green0\\blue0\r\n\\par " +
        "\\pard\\plain\\f0\\fs20\\b\\i\\u\\tab\\tx";

    /// <summary>Decompresses an Outlook compressed RTF body (LZFu, or MELA for uncompressed); empty when invalid.</summary>
    public static byte[] DecompressRtf(byte[] data)
    {
        if (data == null || data.Length < 16)
            return Array.Empty<byte>();

        var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0));
        var rawSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        var type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        var end = (int)Math.Min(data.Length, (long)compressedSize + 4);
        if (rawSize > MaxBodyBytes)
            return Array.Empty<byte>();

        if (type == 0x414C454D) // "MELA": stored uncompressed
            return data.AsSpan(16, Math.Max(0, Math.Min((int)rawSize, data.Length - 16))).ToArray();
        if (type != 0x75465A4C) // "LZFu"
            return Array.Empty<byte>();

        var dictionary = new byte[4096];
        var prebuf = Encoding.ASCII.GetBytes(RtfPrebuf);
        Array.Copy(prebuf, dictionary, prebuf.Length);
        var write = prebuf.Length;
        var output = new List<byte>((int)Math.Min(rawSize, 1 << 20));
        var position = 16;
        while (position < end && output.Count < rawSize)
        {
            var control = data[position++];
            for (var bit = 0; bit < 8 && position < end; bit++)
            {
                if ((control & (1 << bit)) == 0)
                {
                    var literal = data[position++];
                    output.Add(literal);
                    dictionary[write] = literal;
                    write = (write + 1) & 0xFFF;
                    continue;
                }

                if (position + 1 >= end)
                    return output.ToArray();
                var reference = (data[position] << 8) | data[position + 1];
                position += 2;
                var offset = reference >> 4;
                var length = (reference & 0xF) + 2;
                if (offset == write)
                    return output.ToArray(); // the end marker
                for (var i = 0; i < length; i++)
                {
                    var value = dictionary[(offset + i) & 0xFFF];
                    output.Add(value);
                    dictionary[write] = value;
                    write = (write + 1) & 0xFFF;
                }
            }
        }

        return output.ToArray();
    }
}
