using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace VoiceChatbot;

/// <summary>
/// The main text of an old Word document (.doc, Word 97 to 2003) read from its compound file: the
/// file information block gives the piece table (in the 0Table or 1Table stream), whose pieces hold
/// the text as UTF-16 or as 8-bit Windows-1252. Field codes are dropped (their results are kept),
/// paragraph, line, page and table-cell marks become line breaks and " | ".
/// Word 6/95 files and encrypted files are refused with <see cref="NotSupportedException"/>.
/// </summary>
public static class WordBinaryText
{
    public const int DefaultMaxChars = 4_000_000;

    /// <summary>True when the compound file holds a WordDocument stream.</summary>
    public static bool IsWordDocument(CompoundFile file) => file.Find(file.Root, "WordDocument") != null;

    public static string Extract(CompoundFile file, int maxChars = DefaultMaxChars)
    {
        var wordEntry = file.Find(file.Root, "WordDocument") ?? throw new InvalidDataException("This is not a Word document.");
        var word = file.ReadStream(wordEntry);
        if (word.Length < 0x60 || BinaryPrimitives.ReadUInt16LittleEndian(word) != 0xA5EC)
            throw new InvalidDataException("The Word document header is damaged.");

        var nFib = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(2));
        if (nFib < 0x00C1)
            throw new NotSupportedException("This is a Word 6 or Word 95 document, which is too old to read here.");

        var flags = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(0x0A));
        if ((flags & 0x0100) != 0)
            throw new NotSupportedException("This Word document is password-protected.");
        var tableName = (flags & 0x0200) != 0 ? "1Table" : "0Table";

        // FibBase (32 bytes), then csw + FibRgW, cslw + FibRgLw, cbRgFcLcb + FibRgFcLcb.
        var position = 32;
        var csw = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(position));
        position += 2 + csw * 2;
        var cslw = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(position));
        var fibRgLw = position + 2;
        position = fibRgLw + cslw * 4;
        var cbRgFcLcb = BinaryPrimitives.ReadUInt16LittleEndian(word.AsSpan(position));
        var fibRgFcLcb = position + 2;
        if (cslw < 4 || cbRgFcLcb < 34 || fibRgFcLcb + 34 * 8 > word.Length)
            throw new InvalidDataException("The Word document header is damaged.");

        var ccpText = BinaryPrimitives.ReadInt32LittleEndian(word.AsSpan(fibRgLw + 12));
        var fcClx = BinaryPrimitives.ReadUInt32LittleEndian(word.AsSpan(fibRgFcLcb + 33 * 8));
        var lcbClx = BinaryPrimitives.ReadUInt32LittleEndian(word.AsSpan(fibRgFcLcb + 33 * 8 + 4));

        var tableEntry = file.Find(file.Root, tableName) ?? throw new InvalidDataException($"The Word document has no {tableName} stream.");
        var table = file.ReadStream(tableEntry);
        if (lcbClx == 0 || fcClx >= table.Length || (long)fcClx + lcbClx > table.Length)
            throw new InvalidDataException("The Word document has no piece table.");

        var clx = table.AsSpan((int)fcClx, (int)lcbClx);
        var pieceTable = FindPieceTable(clx);
        var pieceCount = (pieceTable.Length - 4) / 12;
        if (pieceCount <= 0)
            throw new InvalidDataException("The Word document has no piece table.");

        var raw = new StringBuilder();
        var limit = ccpText > 0 ? ccpText : int.MaxValue;
        for (var i = 0; i < pieceCount && raw.Length < limit && raw.Length < maxChars; i++)
        {
            var cpStart = BinaryPrimitives.ReadInt32LittleEndian(pieceTable[(i * 4)..]);
            var cpEnd = BinaryPrimitives.ReadInt32LittleEndian(pieceTable[((i + 1) * 4)..]);
            var count = Math.Min(cpEnd - cpStart, limit - raw.Length);
            if (count <= 0)
                continue;

            var descriptor = pieceTable.Slice((pieceCount + 1) * 4 + i * 8, 8);
            var fcValue = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[2..]);
            var compressed = (fcValue & 0x40000000) != 0;
            var fc = fcValue & 0x3FFFFFFF;
            if (compressed)
            {
                var offset = fc / 2;
                if (offset >= word.Length)
                    continue;
                var length = (int)Math.Min(count, word.Length - offset);
                raw.Append(TextDecoding.Ansi.GetString(word, (int)offset, length));
            }
            else
            {
                if (fc >= word.Length)
                    continue;
                var length = (int)Math.Min((long)count * 2, word.Length - fc) & ~1;
                raw.Append(Encoding.Unicode.GetString(word, (int)fc, length));
            }
        }

        return Clean(raw.ToString(), maxChars);
    }

    // The CLX is a run of Prc records (0x01, size, data) and then one Pcdt (0x02, size, PlcPcd).
    private static ReadOnlySpan<byte> FindPieceTable(ReadOnlySpan<byte> clx)
    {
        var position = 0;
        while (position < clx.Length)
        {
            var type = clx[position];
            if (type == 0x01)
            {
                if (position + 3 > clx.Length)
                    break;
                var size = BinaryPrimitives.ReadInt16LittleEndian(clx[(position + 1)..]);
                position += 3 + Math.Max((short)0, size);
                continue;
            }

            if (type == 0x02 && position + 5 <= clx.Length)
            {
                var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(clx[(position + 1)..]), (uint)(clx.Length - position - 5));
                return clx.Slice(position + 5, size);
            }
            break;
        }

        return ReadOnlySpan<byte>.Empty;
    }

    /// <summary>Turns Word's special characters into plain text and drops field instructions.</summary>
    public static string Clean(string raw, int maxChars = DefaultMaxChars)
    {
        var output = new StringBuilder(Math.Min(raw.Length, maxChars));
        // Open fields (0x13 instruction 0x14 result 0x15): true once a field reaches its result.
        var fields = new System.Collections.Generic.Stack<bool>();
        var inInstruction = 0; // open fields still in their instruction part
        for (var i = 0; i < raw.Length && output.Length < maxChars; i++)
        {
            var c = raw[i];
            switch (c)
            {
                case '\u0013':
                    fields.Push(false);
                    inInstruction++;
                    continue;
                case '\u0014':
                    if (fields.Count > 0 && !fields.Peek())
                    {
                        fields.Pop();
                        fields.Push(true);
                        inInstruction--;
                    }
                    continue;
                case '\u0015':
                    if (fields.Count > 0 && !fields.Pop())
                        inInstruction--;
                    continue;
            }

            if (inInstruction > 0)
                continue;

            switch (c)
            {
                case '\r':
                    output.Append('\n');
                    break;
                case '\u0007':
                    // Cell end; two in a row end a table row.
                    if (i + 1 < raw.Length && raw[i + 1] == '\u0007')
                    {
                        output.Append('\n');
                        i++;
                    }
                    else
                    {
                        output.Append(" | ");
                    }
                    break;
                case '\u000B':
                    output.Append('\n');
                    break;
                case '\u000C':
                    output.Append("\n\n");
                    break;
                case '\u001E':
                    output.Append('-');
                    break;
                case '\u00A0':
                    output.Append(' ');
                    break;
                case '\t':
                    output.Append('\t');
                    break;
                default:
                    // Pictures (0x01, 0x08), footnote and comment marks (0x02, 0x05), optional hyphens (0x1F).
                    if (c >= ' ')
                        output.Append(c);
                    break;
            }
        }

        // Tidy: trim lines, drop a trailing cell separator, at most one blank line in a row.
        var lines = output.ToString().Split('\n');
        var tidy = new StringBuilder(output.Length);
        var blank = 0;
        foreach (var raw2 in lines)
        {
            var line = raw2.TrimEnd();
            if (line.EndsWith(" |", StringComparison.Ordinal))
                line = line[..^2].TrimEnd();
            if (line.Trim().Length == 0)
            {
                blank++;
                continue;
            }
            if (tidy.Length > 0)
                tidy.Append(blank > 0 ? "\n\n" : "\n");
            tidy.Append(line.Trim());
            blank = 0;
        }

        return tidy.ToString();
    }
}
