using System.Buffers.Binary;
using System.Text;

namespace ConanModCheck.IoStore;

/// <summary>
/// Liest ein klassisches UE-Pak-Archiv (Version 10 bis 12). Dort liegen bei Conan die Dateien,
/// die keine Pakete sind, zum Beispiel die Sprachdateien (.locres).
/// </summary>
sealed class PakFile : IDisposable
{
    const uint Magic = 0x5A6F12E1;

    readonly FileStream _file;
    readonly byte[] _encodedEntries;
    readonly string[] _methods;

    /// <summary>Pfad im Archiv (z. B. "/ConanSandbox/Content/Localization/…") → Position des Eintrags.</summary>
    public Dictionary<string, int> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public PakFile(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            long size = _file.Length;
            int tailLength = (int)Math.Min(size, 512);
            var tail = ReadAt(size - tailLength, tailLength);
            int m = LastIndexOf(tail, Magic);
            if (m < 0) throw new InvalidDataException(L.T("Kein Pak-Archiv."));

            int version = BinaryPrimitives.ReadInt32LittleEndian(tail.AsSpan(m + 4));
            long indexOffset = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(m + 8));
            long indexSize = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(m + 16));
            if (version < 10) throw new NotSupportedException(L.F("Pak-Version {0} wird nicht unterstützt.", version));
            if (tail[m - 1] != 0) throw new NotSupportedException(L.T("Das Pak-Verzeichnis ist verschlüsselt."));
            if (indexSize <= 0) { _encodedEntries = []; _methods = ["None"]; return; }

            // Namen der Kompressionsverfahren folgen auf Magic, Version, Offset, Größe und SHA1
            var methods = new List<string> { "None" };
            for (int o = m + 4 + 4 + 16 + 20; o + 32 <= tail.Length; o += 32)
            {
                var name = Encoding.ASCII.GetString(tail, o, 32).TrimEnd('\0');
                if (name.Length > 0) methods.Add(name);
            }
            _methods = [.. methods];

            var index = ReadAt(indexOffset, (int)indexSize);
            int p = 0;
            string mount = FString(index, ref p);
            p += 4 + 8; // Anzahl Einträge, PathHashSeed
            if (ReadI32(index, ref p) != 0) p += 8 + 8 + 20; // Pfad-Hash-Index
            if (ReadI32(index, ref p) == 0) throw new NotSupportedException(L.T("Pak ohne vollständiges Verzeichnis."));
            long dirOffset = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(p));
            long dirSize = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(p + 8));
            p += 16 + 20;
            int encodedSize = ReadI32(index, ref p);
            _encodedEntries = index.AsSpan(p, encodedSize).ToArray();

            var dir = ReadAt(dirOffset, (int)dirSize);
            int q = 0;
            int dirCount = ReadI32(dir, ref q);
            for (int d = 0; d < dirCount; d++)
            {
                string dirName = FString(dir, ref q);
                int fileCount = ReadI32(dir, ref q);
                for (int f = 0; f < fileCount; f++)
                {
                    string fileName = FString(dir, ref q);
                    int entry = ReadI32(dir, ref q);
                    Files[Normalize(mount + dirName + fileName)] = entry;
                }
            }
        }
        catch { _file.Dispose(); throw; }
    }

    static string Normalize(string p)
    {
        p = p.Replace('\\', '/');
        while (p.StartsWith("../")) p = p[3..];
        return "/" + p.TrimStart('/');
    }

    public byte[] Read(string path)
    {
        int e = Files[path];
        var b = _encodedEntries;
        uint bits = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(e));
        int p = e + 4;

        uint blockSize = bits & 0x3F;
        if (blockSize == 0x3F) { blockSize = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4; }
        else blockSize <<= 11;
        int blockCount = (int)((bits >> 6) & 0xFFFF);
        bool encrypted = ((bits >> 22) & 1) != 0;
        int method = (int)((bits >> 23) & 0x3F);
        if (encrypted) throw new NotSupportedException(L.T("Verschlüsselte Datei im Pak."));

        long Next(bool fits32)
        {
            long v = fits32 ? BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)) : (long)BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(p));
            p += fits32 ? 4 : 8;
            return v;
        }
        long offset = Next((bits >> 31 & 1) != 0);
        long uncompressed = Next((bits >> 30 & 1) != 0);
        long compressed = method != 0 ? Next((bits >> 29 & 1) != 0) : uncompressed;

        var blockSizes = new List<int>();
        if (blockCount == 1) blockSizes.Add((int)compressed);
        else for (int i = 0; i < blockCount; i++) { blockSizes.Add((int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p))); p += 4; }

        // Vor den Daten steht der Eintrag noch einmal ausführlich (FPakEntry)
        long header = 8 + 8 + 8 + 4 + 20 + (method != 0 ? 4 + 16L * blockCount : 0) + 1 + 4;
        long pos = offset + header;
        if (method == 0) return ReadAt(pos, (int)uncompressed);

        string name = method < _methods.Length ? _methods[method] : "?";
        if (!name.Equals("Oodle", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(L.F("Kompression „{0}“ wird nicht unterstützt.", name));

        var result = new byte[uncompressed];
        int written = 0;
        foreach (int size in blockSizes)
        {
            var raw = ReadAt(pos, size);
            pos += size;
            int expected = (int)Math.Min(blockSize, uncompressed - written);
            var plain = Oodle.Decompress(raw, expected);
            Buffer.BlockCopy(plain, 0, result, written, expected);
            written += expected;
        }
        return result;
    }

    byte[] ReadAt(long offset, int length)
    {
        var buf = new byte[length];
        _file.Position = offset;
        _file.ReadExactly(buf);
        return buf;
    }

    static int LastIndexOf(byte[] data, uint value)
    {
        for (int i = data.Length - 4; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) == value) return i;
        return -1;
    }

    static int ReadI32(byte[] b, ref int p) { int v = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4; return v; }

    static string FString(byte[] b, ref int p)
    {
        int n = ReadI32(b, ref p);
        if (n == 0) return "";
        string s;
        if (n < 0) { s = Encoding.Unicode.GetString(b, p, -2 * n); p += -2 * n; }
        else { s = Encoding.UTF8.GetString(b, p, n); p += n; }
        return s.TrimEnd('\0');
    }

    public void Dispose() => _file.Dispose();
}
