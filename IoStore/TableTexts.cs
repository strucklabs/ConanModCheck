using System.Buffers.Binary;
using System.Text;

namespace ConanModCheck.IoStore;

/// <summary>Verweis auf einen Text: Namensraum und Schlüssel für die Übersetzung, dazu der Ausgangstext.</summary>
sealed record TextRef(string Namespace, string Key, string Source);

/// <summary>
/// Holt aus einer Datentabelle zu jeder Zeile den ersten Text, bei Item- und Talent-Tabellen
/// ist das der Name. Die Zeilen werden ohne Kenntnis des Zeilenaufbaus gefunden: Jede Zeile
/// beginnt mit ihrem Namen als FName-Verweis, kurz danach folgt der erste Text.
/// </summary>
static class TableTexts
{
    const int SearchWindow = 96;

    public static Dictionary<long, TextRef> Read(byte[] package, List<string> names)
    {
        var result = new Dictionary<long, TextRef>();
        var rowIndex = new Dictionary<int, long>();
        for (int i = 0; i < names.Count; i++)
            if (names[i].Length is > 0 and <= 18 && names[i].All(char.IsAsciiDigit) && long.TryParse(names[i], out long id))
                rowIndex[i] = id;
        if (rowIndex.Count == 0 || package.Length < 8) return result;

        int body = (int)Math.Min(package.Length, BinaryPrimitives.ReadUInt32LittleEndian(package.AsSpan(4)));
        var span = package.AsSpan();
        for (int p = body; p + 8 <= package.Length; p++)
        {
            int nameIndex = BinaryPrimitives.ReadInt32LittleEndian(span[p..]);
            if (!rowIndex.TryGetValue(nameIndex, out long rowId) || result.ContainsKey(rowId)) continue;
            if (BinaryPrimitives.ReadInt32LittleEndian(span[(p + 4)..]) != 0) continue;
            var text = FindText(package, p + 8, names);
            if (text != null) result[rowId] = text;
        }
        return result;
    }

    /// <summary>Sucht ab <paramref name="start"/> den ersten gültigen FText (Basis- oder Stringtabellen-Text).</summary>
    static TextRef? FindText(byte[] b, int start, List<string> names)
    {
        int end = Math.Min(b.Length - 9, start + SearchWindow);
        for (int p = start; p < end; p++)
        {
            // FText: Flags (4 Byte), Verlaufsart (1 Byte), danach je nach Art die Daten
            byte history = b[p + 4];
            int q = p + 5;
            if (history == 0)
            {
                if (TryFString(b, ref q, 200, out var ns) && TryFString(b, ref q, 200, out var key) && key.Length > 0
                    && IsKey(key) && TryFString(b, ref q, 4000, out var source))
                    return new TextRef(ns, key, source);
            }
            else if (history == 11 && q + 8 <= b.Length)
            {
                // Text aus einer Stringtabelle: FName der Tabelle, dann der Schlüssel
                int tableIndex = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(q));
                int number = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(q + 4));
                if (tableIndex < 0 || tableIndex >= names.Count || number != 0) continue;
                q += 8;
                if (TryFString(b, ref q, 200, out var key) && key.Length > 0 && IsKey(key))
                {
                    // "/Game/…/ST_Foo.ST_Foo" → Namensraum "ST_Foo"
                    var table = names[tableIndex];
                    var ns = table[(table.LastIndexOf('.') + 1)..];
                    return new TextRef(ns, key, "");
                }
            }
        }
        return null;
    }

    static bool IsKey(string s) => s.All(c => c > ' ' && c < 0x7F);

    static bool TryFString(byte[] b, ref int p, int maxChars, out string value)
    {
        value = "";
        if (p + 4 > b.Length) return false;
        int n = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(p));
        if (n == 0) { p += 4; return true; }
        if (n > 0)
        {
            if (n > maxChars || p + 4 + n > b.Length || b[p + 4 + n - 1] != 0) return false;
            value = Encoding.Latin1.GetString(b, p + 4, n - 1);
            p += 4 + n;
        }
        else
        {
            int bytes = -2 * n;
            if (-n > maxChars || p + 4 + bytes > b.Length || b[p + 4 + bytes - 2] != 0 || b[p + 4 + bytes - 1] != 0) return false;
            value = Encoding.Unicode.GetString(b, p + 4, bytes - 2);
            p += 4 + bytes;
        }
        return value.All(c => c is '\n' or '\r' or '\t' || !char.IsControl(c));
    }
}
