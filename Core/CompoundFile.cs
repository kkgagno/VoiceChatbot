using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VoiceChatbot;

/// <summary>One storage or stream in a <see cref="CompoundFile"/>.</summary>
public sealed record CompoundEntry(int Id, string Name, bool IsStorage, bool IsStream, int Left, int Right, int Child, uint StartSector, long Size);

/// <summary>
/// A read-only reader for OLE compound files ("structured storage"), the container of old Office
/// files (.doc, .xls, .ppt), Outlook .msg files and password-protected Office files. It reads the
/// FAT, mini FAT, directory and streams of version 3 and 4 files held in memory, and checks every
/// sector chain so a damaged file throws <see cref="InvalidDataException"/> instead of looping.
/// </summary>
public sealed class CompoundFile
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const int NoStream = -1;

    private static readonly byte[] Signature = { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };

    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly List<CompoundEntry> _entries;
    private byte[]? _miniStream;

    private CompoundFile(byte[] data, int sectorSize, int miniSectorSize, uint miniCutoff, uint[] fat, uint[] miniFat, List<CompoundEntry> entries)
    {
        _data = data;
        _sectorSize = sectorSize;
        _miniSectorSize = miniSectorSize;
        _miniCutoff = miniCutoff;
        _fat = fat;
        _miniFat = miniFat;
        _entries = entries;
    }

    public CompoundEntry Root => _entries[0];

    /// <summary>True when the bytes start with the compound file signature (D0 CF 11 E0 A1 B1 1A E1).</summary>
    public static bool HasSignature(ReadOnlySpan<byte> data) =>
        data.Length >= Signature.Length && data[..Signature.Length].SequenceEqual(Signature);

    public static CompoundFile Open(byte[] data)
    {
        if (data == null || data.Length < 512 || !HasSignature(data))
            throw new InvalidDataException("This is not an OLE compound file.");

        var header = data.AsSpan(0, 512);
        var majorVersion = BinaryPrimitives.ReadUInt16LittleEndian(header[0x1A..]);
        var sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[0x1E..]);
        var miniSectorShift = BinaryPrimitives.ReadUInt16LittleEndian(header[0x20..]);
        if (sectorShift is not (9 or 12) || miniSectorShift is < 2 or > 12)
            throw new InvalidDataException("The compound file has an unknown sector size.");

        var sectorSize = 1 << sectorShift;
        var miniSectorSize = 1 << miniSectorShift;
        var fatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header[0x2C..]);
        var firstDirectorySector = BinaryPrimitives.ReadUInt32LittleEndian(header[0x30..]);
        var firstMiniFatSector = BinaryPrimitives.ReadUInt32LittleEndian(header[0x3C..]);
        var miniFatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header[0x40..]);
        var firstDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(header[0x44..]);
        var difatSectorCount = BinaryPrimitives.ReadUInt32LittleEndian(header[0x48..]);

        var totalSectors = Math.Max(0, (data.Length - sectorSize) / sectorSize + ((data.Length - sectorSize) % sectorSize > 0 ? 1 : 0));
        if (fatSectorCount > totalSectors || difatSectorCount > totalSectors || miniFatSectorCount > totalSectors)
            throw new InvalidDataException("The compound file header is damaged.");

        // The FAT sectors are listed in the header (first 109) and then in a chain of DIFAT sectors.
        var fatSectors = new List<uint>();
        for (var i = 0; i < 109 && fatSectors.Count < fatSectorCount; i++)
        {
            var sector = BinaryPrimitives.ReadUInt32LittleEndian(header[(0x4C + i * 4)..]);
            if (sector == FreeSector)
                break;
            fatSectors.Add(sector);
        }

        var difat = firstDifatSector;
        var entriesPerSector = sectorSize / 4;
        for (var d = 0; d < difatSectorCount && difat != EndOfChain && difat != FreeSector && fatSectors.Count < fatSectorCount; d++)
        {
            var span = SectorSpan(data, difat, sectorSize);
            for (var i = 0; i < entriesPerSector - 1 && fatSectors.Count < fatSectorCount; i++)
            {
                var sector = BinaryPrimitives.ReadUInt32LittleEndian(span[(i * 4)..]);
                if (sector != FreeSector)
                    fatSectors.Add(sector);
            }
            difat = BinaryPrimitives.ReadUInt32LittleEndian(span[((entriesPerSector - 1) * 4)..]);
        }

        var fat = new uint[fatSectors.Count * entriesPerSector];
        for (var f = 0; f < fatSectors.Count; f++)
        {
            var span = SectorSpan(data, fatSectors[f], sectorSize);
            for (var i = 0; i < entriesPerSector; i++)
                fat[f * entriesPerSector + i] = BinaryPrimitives.ReadUInt32LittleEndian(span[(i * 4)..]);
        }

        var directory = ReadChain(data, fat, firstDirectorySector, sectorSize, long.MaxValue);
        var entries = new List<CompoundEntry>();
        for (var offset = 0; offset + 128 <= directory.Length; offset += 128)
        {
            var entry = directory.AsSpan(offset, 128);
            var nameLength = Math.Clamp((int)BinaryPrimitives.ReadUInt16LittleEndian(entry[0x40..]), 0, 64);
            var name = Encoding.Unicode.GetString(entry[..Math.Max(0, nameLength - 2)]);
            var type = entry[0x42];
            var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(entry[0x78..]);
            if (majorVersion == 3)
                size &= 0xFFFFFFFF; // version 3 files may leave garbage in the high half
            entries.Add(new CompoundEntry(
                entries.Count,
                name,
                IsStorage: type is 1 or 5,
                IsStream: type == 2,
                Left: (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[0x44..]),
                Right: (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[0x48..]),
                Child: (int)BinaryPrimitives.ReadUInt32LittleEndian(entry[0x4C..]),
                StartSector: BinaryPrimitives.ReadUInt32LittleEndian(entry[0x74..]),
                Size: size < 0 ? 0 : size));
        }

        if (entries.Count == 0 || !entries[0].IsStorage)
            throw new InvalidDataException("The compound file has no root storage.");

        var miniFatBytes = miniFatSectorCount == 0 || firstMiniFatSector == EndOfChain
            ? Array.Empty<byte>()
            : ReadChain(data, fat, firstMiniFatSector, sectorSize, long.MaxValue);
        var miniFat = new uint[miniFatBytes.Length / 4];
        for (var i = 0; i < miniFat.Length; i++)
            miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i * 4));

        // The specification fixes the mini stream cutoff at 4096; a different value means a damaged header.
        return new CompoundFile(data, sectorSize, miniSectorSize, 4096, fat, miniFat, entries);
    }

    /// <summary>The storages and streams directly inside <paramref name="storage"/>.</summary>
    public List<CompoundEntry> Children(CompoundEntry storage)
    {
        var children = new List<CompoundEntry>();
        if (!storage.IsStorage)
            return children;

        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(storage.Child);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (id == NoStream || id < 0 || id >= _entries.Count || !visited.Add(id))
                continue;
            var entry = _entries[id];
            children.Add(entry);
            pending.Push(entry.Right);
            pending.Push(entry.Left);
        }

        children.Sort((a, b) => a.Id.CompareTo(b.Id));
        return children;
    }

    /// <summary>The child of <paramref name="storage"/> with this name (any case), or null.</summary>
    public CompoundEntry? Find(CompoundEntry storage, string name)
    {
        foreach (var child in Children(storage))
        {
            if (child.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    /// <summary>A stream's bytes, at most <paramref name="maxBytes"/>.</summary>
    public byte[] ReadStream(CompoundEntry entry, int maxBytes = int.MaxValue)
    {
        if (!entry.IsStream || entry.Size <= 0)
            return Array.Empty<byte>();

        var size = Math.Min(entry.Size, maxBytes);
        if (entry.Size < _miniCutoff)
        {
            var mini = _miniStream ??= ReadChain(_data, _fat, Root.StartSector, _sectorSize, Root.Size);
            return ReadMiniChain(mini, entry.StartSector, size);
        }

        return ReadChain(_data, _fat, entry.StartSector, _sectorSize, size);
    }

    private byte[] ReadMiniChain(byte[] mini, uint start, long size)
    {
        size = Math.Min(size, mini.Length);
        var output = new byte[size];
        var written = 0L;
        var sector = start;
        var steps = 0;
        while (written < size)
        {
            if (sector == EndOfChain || sector >= _miniFat.Length || ++steps > _miniFat.Length + 1)
                throw new InvalidDataException("A compound file stream is cut short.");
            var offset = (long)sector * _miniSectorSize;
            if (offset >= mini.Length)
                throw new InvalidDataException("A compound file stream points outside the file.");
            var count = (int)Math.Min(Math.Min(_miniSectorSize, size - written), mini.Length - offset);
            Array.Copy(mini, offset, output, written, count);
            written += count;
            sector = _miniFat[sector];
        }
        return output;
    }

    private static byte[] ReadChain(byte[] data, uint[] fat, uint start, int sectorSize, long size)
    {
        var output = new MemoryStream();
        var sector = start;
        var steps = 0;
        while (sector != EndOfChain && output.Length < size)
        {
            if (sector >= fat.Length || ++steps > fat.Length + 1)
                throw new InvalidDataException("A compound file sector chain is damaged.");
            var span = SectorSpan(data, sector, sectorSize);
            var count = (int)Math.Min(span.Length, size - output.Length);
            output.Write(span[..count]);
            sector = fat[sector];
        }
        return output.ToArray();
    }

    private static ReadOnlySpan<byte> SectorSpan(byte[] data, uint sector, int sectorSize)
    {
        var offset = ((long)sector + 1) * sectorSize;
        if (sector >= 0xFFFFFFFA || offset >= data.Length)
            throw new InvalidDataException("A compound file sector points outside the file.");
        return data.AsSpan((int)offset, (int)Math.Min(sectorSize, data.Length - offset));
    }
}
