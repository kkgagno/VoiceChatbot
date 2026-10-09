using System.Buffers.Binary;
using System.Text;
using VoiceChatbot;
using Xunit;

/// <summary>Writes small version 3 compound files (512-byte sectors, mini stream for small streams) for tests.</summary>
internal static class CompoundFileBuilder
{
    private const uint EndOfChain = 0xFFFFFFFE;
    private const uint FreeSector = 0xFFFFFFFF;
    private const uint FatSector = 0xFFFFFFFD;
    private const int SectorSize = 512;
    private const int MiniSectorSize = 64;
    private const int MiniCutoff = 4096;

    private sealed class Node
    {
        public string Name = "";
        public bool IsStorage;
        public byte[] Data = Array.Empty<byte>();
        public List<Node> Children = new();
        public int Id;
        public uint Start = EndOfChain;
    }

    /// <summary>Builds a file from "name" or "storage/name" paths and their bytes.</summary>
    public static byte[] Build(params (string Path, byte[] Data)[] streams)
    {
        var root = new Node { Name = "Root Entry", IsStorage = true };
        foreach (var (path, data) in streams)
        {
            var parts = path.Split('/');
            var parent = root;
            foreach (var storage in parts[..^1])
            {
                var existing = parent.Children.FirstOrDefault(c => c.IsStorage && c.Name == storage);
                if (existing == null)
                {
                    existing = new Node { Name = storage, IsStorage = true };
                    parent.Children.Add(existing);
                }
                parent = existing;
            }
            parent.Children.Add(new Node { Name = parts[^1], Data = data });
        }

        var nodes = new List<Node>();
        void Number(Node node)
        {
            node.Id = nodes.Count;
            nodes.Add(node);
            foreach (var child in node.Children)
                Number(child);
        }
        Number(root);

        var sectors = new List<byte[]>();
        var fat = new List<uint>();

        uint AddChain(byte[] data, int sectorSize, List<byte[]> into, List<uint> table)
        {
            if (data.Length == 0)
                return EndOfChain;
            var first = (uint)into.Count;
            var count = (data.Length + sectorSize - 1) / sectorSize;
            for (var i = 0; i < count; i++)
            {
                var sector = new byte[sectorSize];
                Array.Copy(data, i * sectorSize, sector, 0, Math.Min(sectorSize, data.Length - i * sectorSize));
                into.Add(sector);
                table.Add(i == count - 1 ? EndOfChain : (uint)into.Count);
            }
            return first;
        }

        // Small streams go in the mini stream, large ones in regular sectors.
        var miniSectors = new List<byte[]>();
        var miniFat = new List<uint>();
        foreach (var node in nodes.Where(n => !n.IsStorage))
        {
            node.Start = node.Data.Length < MiniCutoff
                ? AddChain(node.Data, MiniSectorSize, miniSectors, miniFat)
                : AddChain(node.Data, SectorSize, sectors, fat);
        }

        var miniStream = miniSectors.SelectMany(s => s).ToArray();
        root.Start = AddChain(miniStream, SectorSize, sectors, fat);
        root.Data = miniStream;

        var miniFatBytes = new byte[Math.Max(1, (miniFat.Count * 4 + SectorSize - 1) / SectorSize) * SectorSize];
        for (var i = 0; i < miniFatBytes.Length / 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(miniFatBytes.AsSpan(i * 4), i < miniFat.Count ? miniFat[i] : FreeSector);
        var miniFatStart = miniFat.Count == 0 ? EndOfChain : AddChain(miniFatBytes, SectorSize, sectors, fat);

        // Directory: siblings are chained through their right pointers (a valid, if lopsided, tree).
        var directory = new byte[((nodes.Count + 3) / 4) * 4 * 128];
        foreach (var node in nodes)
        {
            var entry = directory.AsSpan(node.Id * 128, 128);
            var name = Encoding.Unicode.GetBytes(node.Name);
            name.CopyTo(entry);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[0x40..], (ushort)(name.Length + 2));
            entry[0x42] = (byte)(node == root ? 5 : node.IsStorage ? 1 : 2);
            entry[0x43] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x44..], FreeSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x48..], FreeSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x4C..], node.Children.Count > 0 ? (uint)node.Children[0].Id : FreeSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x74..], node.IsStorage && node != root ? 0 : node.Start);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[0x78..], (ulong)(node.IsStorage && node != root ? 0 : node.Data.Length));
        }
        foreach (var node in nodes)
        {
            for (var i = 0; i + 1 < node.Children.Count; i++)
                BinaryPrimitives.WriteUInt32LittleEndian(directory.AsSpan(node.Children[i].Id * 128 + 0x48), (uint)node.Children[i + 1].Id);
        }
        for (var unused = nodes.Count; unused < directory.Length / 128; unused++)
        {
            var entry = directory.AsSpan(unused * 128, 128);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x44..], FreeSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x48..], FreeSector);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[0x4C..], FreeSector);
        }
        var directoryStart = AddChain(directory, SectorSize, sectors, fat);

        // FAT sectors go last; they mark themselves.
        var fatSectorCount = 1;
        while ((sectors.Count + fatSectorCount) > fatSectorCount * (SectorSize / 4))
            fatSectorCount++;
        var fatStart = sectors.Count;
        for (var i = 0; i < fatSectorCount; i++)
            fat.Add(FatSector);
        var fatBytes = new byte[fatSectorCount * SectorSize];
        for (var i = 0; i < fatBytes.Length / 4; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(fatBytes.AsSpan(i * 4), i < fat.Count ? fat[i] : FreeSector);
        for (var i = 0; i < fatSectorCount; i++)
            sectors.Add(fatBytes.AsSpan(i * SectorSize, SectorSize).ToArray());

        var header = new byte[SectorSize];
        new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x18), 0x3E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1A), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1C), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x1E), 9);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(0x20), 6);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x2C), (uint)fatSectorCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x30), directoryStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x38), MiniCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x3C), miniFatStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x40), miniFat.Count == 0 ? 0u : (uint)(miniFatBytes.Length / SectorSize));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x44), EndOfChain);
        for (var i = 0; i < 109; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x4C + i * 4), i < fatSectorCount ? (uint)(fatStart + i) : FreeSector);

        return header.Concat(sectors.SelectMany(s => s)).ToArray();
    }
}

public class CompoundFileTests
{
    private static byte[] Utf16(string text) => Encoding.Unicode.GetBytes(text);

    [Fact]
    public void ReadsSmallAndLargeStreamsAndStorages()
    {
        var large = Enumerable.Range(0, 10_000).Select(i => (byte)(i % 251)).ToArray();
        var data = CompoundFileBuilder.Build(
            ("small", Encoding.ASCII.GetBytes("hello mini stream")),
            ("large", large),
            ("folder/inner", Encoding.ASCII.GetBytes("inside")));

        var file = CompoundFile.Open(data);
        var names = file.Children(file.Root).Select(c => c.Name).ToList();

        Assert.Equal(new[] { "small", "large", "folder" }, names);
        Assert.Equal("hello mini stream", Encoding.ASCII.GetString(file.ReadStream(file.Find(file.Root, "SMALL")!)));
        Assert.Equal(large, file.ReadStream(file.Find(file.Root, "large")!));
        var folder = file.Find(file.Root, "folder")!;
        Assert.True(folder.IsStorage);
        Assert.Equal("inside", Encoding.ASCII.GetString(file.ReadStream(file.Find(folder, "inner")!)));
        Assert.Null(file.Find(file.Root, "missing"));
        Assert.Equal(5, file.ReadStream(file.Find(file.Root, "large")!, maxBytes: 5).Length);
    }

    [Fact]
    public void RejectsFilesThatAreNotCompoundFiles()
    {
        Assert.Throws<InvalidDataException>(() => CompoundFile.Open(Encoding.ASCII.GetBytes(new string('x', 600))));
        var data = CompoundFileBuilder.Build(("s", new byte[] { 1 }));
        data[0x30] = 0xF0; // directory sector far outside the file
        data[0x31] = 0xFF;
        Assert.Throws<InvalidDataException>(() => CompoundFile.Open(data));
    }

    [Fact]
    public void OutlookMessageGivesHeadersAttachmentsAndBody()
    {
        var properties = new byte[32 + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(properties.AsSpan(32), 0x00390040); // PR_CLIENT_SUBMIT_TIME
        BinaryPrimitives.WriteInt64LittleEndian(properties.AsSpan(40), new DateTime(2024, 5, 6, 14, 30, 0, DateTimeKind.Utc).ToFileTimeUtc());

        var data = CompoundFileBuilder.Build(
            ("__properties_version1.0", properties),
            ("__substg1.0_0037001F", Utf16("Inspection results")),
            ("__substg1.0_0C1A001F", Utf16("Sam Inspector")),
            ("__substg1.0_5D01001F", Utf16("sam@example.com")),
            ("__substg1.0_0E04001F", Utf16("Keith")),
            ("__substg1.0_1000001F", Utf16("The furnace is 15 years old.\r\nReplace the filter.")),
            ("__attach_version1.0_#00000000/__substg1.0_3707001F", Utf16("report.pdf")),
            ("__recip_version1.0_#00000000/__substg1.0_3001001F", Utf16("Keith")));

        var file = CompoundFile.Open(data);

        Assert.True(OutlookMsgText.IsOutlookMessage(file));
        Assert.False(WordBinaryText.IsWordDocument(file));
        Assert.Equal(
            "From: Sam Inspector <sam@example.com>\n" +
            "To: Keith\n" +
            "Date: 2024-05-06 14:30 UTC\n" +
            "Subject: Inspection results\n" +
            "Attachments: report.pdf\n\n" +
            "The furnace is 15 years old.\nReplace the filter.",
            OutlookMsgText.Extract(file));
    }

    [Fact]
    public void OutlookMessageFallsBackToHtmlThenCompressedRtf()
    {
        var html = CompoundFileBuilder.Build(
            ("__substg1.0_0037001E", Encoding.ASCII.GetBytes("Ansi subject")),
            ("__substg1.0_10130102", Encoding.UTF8.GetBytes("<html><body><p>Hello <b>there</b></p></body></html>")));
        Assert.Equal("Subject: Ansi subject\n\nHello there", OutlookMsgText.Extract(CompoundFile.Open(html)));

        var rtfBody = Encoding.ASCII.GetBytes(@"{\rtf1\ansi Paid in full.\par}");
        var stored = new byte[16 + rtfBody.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(0), (uint)(rtfBody.Length + 12));
        BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(4), (uint)rtfBody.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(stored.AsSpan(8), 0x414C454D); // "MELA": not compressed
        rtfBody.CopyTo(stored, 16);
        var rtf = CompoundFileBuilder.Build(("__substg1.0_10090102", stored));
        Assert.Equal("Paid in full.", OutlookMsgText.Extract(CompoundFile.Open(rtf)));
    }

    [Fact]
    public void CompressedRtfDecompresses()
    {
        // The example from the compressed RTF specification (MS-OXRTFCP 3.1.1).
        var compressed = new byte[]
        {
            0x2d, 0x00, 0x00, 0x00, 0x2b, 0x00, 0x00, 0x00, 0x4c, 0x5a, 0x46, 0x75, 0xf1, 0xc5, 0xc7, 0xa7,
            0x03, 0x00, 0x0a, 0x00, 0x72, 0x63, 0x70, 0x67, 0x31, 0x32, 0x35, 0x42, 0x32, 0x0a, 0xf3, 0x20,
            0x68, 0x65, 0x6c, 0x09, 0x00, 0x20, 0x62, 0x77, 0x05, 0xb0, 0x6c, 0x64, 0x7d, 0x0a, 0x80, 0x0f, 0xa0
        };

        Assert.Equal("{\\rtf1\\ansi\\ansicpg1252\\pard hello world}\r\n", Encoding.ASCII.GetString(OutlookMsgText.DecompressRtf(compressed)));
        Assert.Empty(OutlookMsgText.DecompressRtf(new byte[] { 1, 2, 3 }));
    }

    private static byte[] WordDocumentStream(string text, bool compressed, bool encrypted = false)
    {
        // FibBase (32) + csw (2) + 14 shorts + cslw (2) + 22 longs + cbRgFcLcb (2) + 93 pairs, then the text.
        const int fibRgLw = 32 + 2 + 28 + 2;
        const int fibRgFcLcb = fibRgLw + 88 + 2;
        const int textOffset = 1024;
        var textBytes = compressed ? Encoding.Latin1.GetBytes(text) : Encoding.Unicode.GetBytes(text);
        var stream = new byte[textOffset + textBytes.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(0), 0xA5EC);
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(2), 0x00C1);
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(0x0A), (ushort)(0x0200 | (encrypted ? 0x0100 : 0)));
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(32), 14);
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(fibRgLw - 2), 22);
        BinaryPrimitives.WriteInt32LittleEndian(stream.AsSpan(fibRgLw + 12), text.Length); // ccpText
        BinaryPrimitives.WriteUInt16LittleEndian(stream.AsSpan(fibRgFcLcb - 2), 93);
        BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(fibRgFcLcb + 33 * 8), 7);       // fcClx
        BinaryPrimitives.WriteUInt32LittleEndian(stream.AsSpan(fibRgFcLcb + 33 * 8 + 4), 1 + 4 + 8 + 8 + 3); // lcbClx
        textBytes.CopyTo(stream, textOffset);
        return stream;
    }

    private static byte[] TableStream(int charCount, bool compressed)
    {
        // 7 junk bytes, a Prc (0x01, size 0), then the Pcdt with one piece.
        var table = new List<byte>(new byte[7]);
        table.AddRange(new byte[] { 0x01, 0x00, 0x00 });
        table.Add(0x02);
        table.AddRange(BitConverter.GetBytes(4 + 4 + 8)); // lcb of PlcPcd: two CPs and one PCD
        table.AddRange(BitConverter.GetBytes(0));
        table.AddRange(BitConverter.GetBytes(charCount));
        table.AddRange(new byte[] { 0, 0 });
        var fc = compressed ? (uint)(1024 * 2) | 0x40000000 : 1024u;
        table.AddRange(BitConverter.GetBytes(fc));
        table.AddRange(new byte[] { 0, 0 });
        return table.ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldWordDocumentTextIsRead(bool compressed)
    {
        var text = "Seller disclosure\rRoof\u0007Good\u0007\u0007\u0013 HYPERLINK \"http://x\" \u0014See site\u0015 now\u000Bnext\r";
        var data = CompoundFileBuilder.Build(
            ("WordDocument", WordDocumentStream(text, compressed)),
            ("1Table", TableStream(text.Length, compressed)));

        var file = CompoundFile.Open(data);

        Assert.True(WordBinaryText.IsWordDocument(file));
        Assert.Equal("Seller disclosure\nRoof | Good\nSee site now\nnext", WordBinaryText.Extract(file));
    }

    [Fact]
    public void EncryptedWordDocumentsAreRefused()
    {
        var data = CompoundFileBuilder.Build(
            ("WordDocument", WordDocumentStream("secret", compressed: false, encrypted: true)),
            ("1Table", TableStream(6, compressed: false)));

        Assert.Throws<NotSupportedException>(() => WordBinaryText.Extract(CompoundFile.Open(data)));
    }

    [Fact]
    public void WordFieldInstructionsAreDroppedEvenWhenNested()
    {
        Assert.Equal("res", WordBinaryText.Clean("\u0013 IF \u0013 MERGEFIELD x \u0014val\u0015 = 1 \u0014res\u0015"));
        Assert.Equal("x y", WordBinaryText.Clean("\u0013A\u0014x \u0013PAGE\u0015y\u0015"));
        Assert.Equal("a-b", WordBinaryText.Clean("a\u001Eb\u0001"));
    }
}
