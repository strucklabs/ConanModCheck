using System.Buffers.Binary;
using System.Text;

namespace ConanModCheck.IoStore;

/// <summary>Liest eine UE-Sprachdatei (.locres): (Namensraum, Schlüssel) → übersetzter Text.</summary>
static class Locres
{
    public static void ReadInto(byte[] b, Dictionary<(string Ns, string Key), string> target)
    {
        if (b.Length < 25) return;
        int p = 16;          // Kennung (GUID)
        byte version = b[p++];
        long stringsOffset = BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(p)); p += 8;
        if (version >= 2) p += 4; // Anzahl Einträge

        var keys = new List<(string, string, int)>();
        uint nsCount = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
        for (uint n = 0; n < nsCount; n++)
        {
            if (version >= 2) p += 4; // Hash
            string ns = FString(b, ref p);
            uint keyCount = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p)); p += 4;
            for (uint k = 0; k < keyCount; k++)
            {
                if (version >= 2) p += 4; // Hash
                string key = FString(b, ref p);
                p += 4; // Hash des Ausgangstexts
                int index = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4;
                keys.Add((ns, key, index));
            }
        }

        int q = (int)stringsOffset;
        int count = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(q)); q += 4;
        var strings = new string[count];
        for (int i = 0; i < count; i++)
        {
            strings[i] = FString(b, ref q);
            if (version >= 2) q += 4; // Referenzzähler
        }

        foreach (var (ns, key, index) in keys)
            if (index >= 0 && index < strings.Length) target[(ns, key)] = strings[index];
    }

    static string FString(byte[] b, ref int p)
    {
        int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p)); p += 4;
        if (n == 0) return "";
        string s;
        if (n < 0) { s = Encoding.Unicode.GetString(b, p, -2 * n); p += -2 * n; }
        else { s = Encoding.Latin1.GetString(b, p, n); p += n; }
        return s.TrimEnd('\0');
    }
}
