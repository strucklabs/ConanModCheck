using System.Buffers.Binary;
using System.Text;

namespace ConanModCheck.IoStore;

/// <summary>
/// Liest einen UE5-IoStore-Container (.utoc + .ucas). Nur das, was für die Mod-Prüfung
/// nötig ist: Verzeichnis der enthaltenen Dateien und das Entpacken einzelner Dateien.
/// </summary>
sealed class IoStoreContainer : IDisposable
{
    const byte FlagCompressed = 1, FlagEncrypted = 2, FlagSigned = 4, FlagIndexed = 8;
    const uint None = 0xFFFFFFFF;

    readonly (long Offset, long Length)[] _chunks;
    readonly (long Offset, int CompressedSize, int UncompressedSize, byte Method)[] _blocks;
    readonly string[] _methods;
    readonly uint _blockSize;
    readonly FileStream _ucas;

    public string TocPath { get; }

    /// <summary>Spielpfad (z.B. "/Game/Mods/X/Y.uasset") → Index des Eintrags.</summary>
    public Dictionary<string, int> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IoStoreContainer(string tocPath)
    {
        TocPath = tocPath;
        var d = File.ReadAllBytes(tocPath);
        if (d.Length < 0x90 || Encoding.ASCII.GetString(d, 0, 16) != "-==--==--==--==-")
            throw new InvalidDataException(L.T("Keine gültige .utoc-Datei."));

        byte version = d[16];
        uint headerSize = U32(d, 0x14), entryCount = U32(d, 0x18);
        uint blockCount = U32(d, 0x1C), blockEntrySize = U32(d, 0x20);
        uint methodCount = U32(d, 0x24), methodLength = U32(d, 0x28);
        _blockSize = U32(d, 0x2C);
        uint dirIndexSize = U32(d, 0x30);
        byte flags = d[0x50];
        uint seedCount = U32(d, 0x54), noHashCount = U32(d, 0x60);

        if ((flags & FlagEncrypted) != 0)
            throw new NotSupportedException(L.T("Der Container ist verschlüsselt."));

        int o = (int)headerSize;
        o += 12 * (int)entryCount; // Chunk-IDs, werden nicht gebraucht

        _chunks = new (long, long)[entryCount];
        for (int i = 0; i < entryCount; i++, o += 10)
            _chunks[i] = (BE40(d, o), BE40(d, o + 5));

        if (version >= 4) o += 4 * (int)seedCount;   // PerfectHash
        if (version >= 5) o += 4 * (int)noHashCount; // PerfectHashWithOverflow

        _blocks = new (long, int, int, byte)[blockCount];
        for (int i = 0; i < blockCount; i++, o += (int)blockEntrySize)
            _blocks[i] = (LE40(d, o), (int)LE24(d, o + 5), (int)LE24(d, o + 8), d[o + 11]);

        _methods = new string[methodCount + 1];
        _methods[0] = "None";
        for (int i = 0; i < methodCount; i++, o += (int)methodLength)
            _methods[i + 1] = Encoding.ASCII.GetString(d, o, (int)methodLength).TrimEnd('\0');

        if ((flags & FlagSigned) != 0)
        {
            // Signaturen: Größe, TOC-Signatur, Block-Signatur, dann ein SHA1 je Block
            int sigSize = (int)U32(d, o);
            o += 4 + 2 * sigSize + 20 * (int)blockCount;
        }

        if ((flags & FlagIndexed) != 0 && dirIndexSize > 0)
            ReadDirectoryIndex(d.AsSpan(o, (int)dirIndexSize));

        _ucas = new FileStream(Path.ChangeExtension(tocPath, ".ucas"), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    void ReadDirectoryIndex(ReadOnlySpan<byte> b)
    {
        int p = 0;
        string mount = FString(b, ref p);
        var dirs = new (uint Name, uint Child, uint Sibling, uint File)[ReadU32(b, ref p)];
        for (int i = 0; i < dirs.Length; i++)
            dirs[i] = (ReadU32(b, ref p), ReadU32(b, ref p), ReadU32(b, ref p), ReadU32(b, ref p));
        var files = new (uint Name, uint Next, uint Entry)[ReadU32(b, ref p)];
        for (int i = 0; i < files.Length; i++)
            files[i] = (ReadU32(b, ref p), ReadU32(b, ref p), ReadU32(b, ref p));
        var strings = new string[ReadU32(b, ref p)];
        for (int i = 0; i < strings.Length; i++)
            strings[i] = FString(b, ref p);

        // Iterativ statt rekursiv, große Spiel-Container haben tiefe Bäume
        var stack = new Stack<(uint Dir, string Prefix)>();
        if (dirs.Length > 0) stack.Push((0, mount));
        while (stack.Count > 0)
        {
            var (di, prefix) = stack.Pop();
            for (; di != None; di = dirs[di].Sibling)
            {
                var dir = dirs[di];
                string path = dir.Name == None ? prefix : prefix + strings[dir.Name] + "/";
                for (uint f = dir.File; f != None; f = files[f].Next)
                    Files[ToGamePath(path + strings[files[f].Name])] = (int)files[f].Entry;
                if (dir.Child != None) stack.Push((dir.Child, path));
            }
        }
    }

    /// <summary>"../../../ConanSandbox/Content/Mods/X.uasset" → "/Game/Mods/X.uasset"</summary>
    static string ToGamePath(string p)
    {
        p = p.Replace('\\', '/');
        while (p.StartsWith("../")) p = p[3..];
        const string content = "ConanSandbox/Content/";
        int i = p.IndexOf(content, StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? "/Game/" + p[(i + content.Length)..] : "/" + p.TrimStart('/');
    }

    public byte[] Read(int entry)
    {
        var (offset, length) = _chunks[entry];
        var result = new byte[length];
        if (length == 0) return result;

        long first = offset / _blockSize, last = (offset + length - 1) / _blockSize;
        long pos = first * _blockSize; // Position im entpackten Datenstrom
        int written = 0;
        for (long bi = first; bi <= last; bi++)
        {
            var blk = _blocks[bi];
            var raw = new byte[blk.CompressedSize];
            _ucas.Position = blk.Offset;
            _ucas.ReadExactly(raw);

            byte[] plain;
            string method = _methods[blk.Method];
            if (method == "None") plain = raw;
            else if (method.Equals("Oodle", StringComparison.OrdinalIgnoreCase)) plain = Oodle.Decompress(raw, blk.UncompressedSize);
            else throw new NotSupportedException(L.F("Kompression „{0}“ wird nicht unterstützt.", method));

            int start = (int)Math.Max(0, offset - pos);
            int count = Math.Min(blk.UncompressedSize - start, (int)length - written);
            Buffer.BlockCopy(plain, start, result, written, count);
            written += count;
            pos += _blockSize;
        }
        return result;
    }

    public byte[] Read(string gamePath) => Read(Files[gamePath]);

    public void Dispose() => _ucas.Dispose();

    static uint U32(byte[] d, int o) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(o));
    static long BE40(byte[] d, int o) { long v = 0; for (int i = 0; i < 5; i++) v = (v << 8) | d[o + i]; return v; }
    static long LE40(byte[] d, int o) { long v = 0; for (int i = 4; i >= 0; i--) v = (v << 8) | d[o + i]; return v; }
    static uint LE24(byte[] d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16);

    static uint ReadU32(ReadOnlySpan<byte> b, ref int p) { uint v = BinaryPrimitives.ReadUInt32LittleEndian(b[p..]); p += 4; return v; }

    static string FString(ReadOnlySpan<byte> b, ref int p)
    {
        int n = BinaryPrimitives.ReadInt32LittleEndian(b[p..]); p += 4;
        if (n == 0) return "";
        string s;
        if (n < 0) { s = Encoding.Unicode.GetString(b.Slice(p, -2 * n)); p += -2 * n; }
        else { s = Encoding.Latin1.GetString(b.Slice(p, n)); p += n; }
        return s.TrimEnd('\0');
    }
}
