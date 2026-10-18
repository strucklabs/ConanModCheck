using System.Buffers.Binary;
using System.Text;

namespace ConanModCheck.IoStore;

/// <summary>
/// Liest die Namenstabelle eines UE5-Pakets im Zen-Format. Für Datentabellen reicht das:
/// Die Zeilennamen (bei Conan die Item-, Rezept- und Talent-IDs) stehen dort als FNames.
/// </summary>
static class ZenPackage
{
    // Kennung des Hash-Verfahrens, die direkt vor den Namens-Hashes steht (0xC1640000)
    static readonly byte[] HashVersionMarker = [0x00, 0x00, 0x64, 0xC1, 0x00, 0x00, 0x00, 0x00];

    public static List<string> ReadNames(byte[] package)
    {
        var names = new List<string>();
        int headerSize = package.Length >= 8 ? (int)Math.Min(package.Length, BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(4))) : 0;
        // Die Länge des Kopfbereichs davor schwankt je nach Engine-Version, deshalb wird die
        // Namenstabelle über die Hash-Kennung gesucht statt über einen festen Abstand.
        int marker = package.AsSpan(0, headerSize).IndexOf(HashVersionMarker);
        if (marker < 8) return names;

        int o = marker - 8;
        int count = BinaryPrimitives.ReadInt32LittleEndian(package.AsSpan(o));
        if (count <= 0 || count > 1_000_000) return names;
        o += 16 + 8 * count; // Anzahl, Bytes, Hash-Version, Hashes

        int strings = o + 2 * count;
        for (int i = 0; i < count; i++)
        {
            bool utf16 = (package[o + 2 * i] & 0x80) != 0;
            int len = ((package[o + 2 * i] & 0x7F) << 8) | package[o + 2 * i + 1];
            if (utf16) { names.Add(Encoding.Unicode.GetString(package, strings, 2 * len)); strings += 2 * len; }
            else { names.Add(Encoding.Latin1.GetString(package, strings, len)); strings += len; }
        }
        return names;
    }

    /// <summary>Datentabellen haben die Eigenschaft "RowStruct" in ihrer Namenstabelle.</summary>
    public static bool IsDataTable(List<string> names) => names.Contains("RowStruct");

    /// <summary>Zeilennamen, die reine Zahlen sind, also IDs von Items, Rezepten oder Talenten.</summary>
    public static IEnumerable<long> NumericNames(List<string> names)
    {
        foreach (var n in names)
            if (n.Length is > 0 and <= 18 && n.All(char.IsAsciiDigit) && long.TryParse(n, out long id))
                yield return id;
    }
}
