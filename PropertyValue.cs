using System.Buffers.Binary;
using System.Text;

namespace ConanModCheck;

/// <summary>
/// Liest Eigenschaftswerte aus <c>properties.value</c> (Unreal Engine 5.4+, Tagged-Format mit Typnamen-Baum).
/// Aufbau: Kopf (int32, int32, int32 Flag; bei Flag 1 folgen 16 Byte Kennung und ein int32 – Blueprint-Struktur),
/// danach der rohe Wert der Eigenschaft. Ist der Wert eine Struktur, ist er eine Liste von Tags bis „None“:
/// Name, Typ (Name + Anzahl Untertypen, rekursiv), Größe (int32), Flags (u8: 1 Index, 2 Guid, 4 Erweiterungen,
/// 16 Bool wahr), Wert. Ergebnis: Dictionary (Struktur), List (Array), string, long, double, bool, DateTime.
/// Nur lesen – geschrieben wird nichts.
/// </summary>
sealed class PropertyReader
{
    readonly byte[] _b;
    int _o;

    PropertyReader(byte[] b) { _b = b; _o = 8; if (I32() != 0) _o += 16 + 4; }

    /// <summary>Hat der Leser den Wert genau bis zum Ende gelesen? (Plausibilitätsprüfung)</summary>
    public bool AtEnd => _o == _b.Length;

    /// <summary>Wert ist eine Struktur.</summary>
    public static Dictionary<string, object?> ReadStruct(byte[] value, out bool complete)
    {
        var r = new PropertyReader(value);
        var result = r.Props(value.Length);
        complete = r.AtEnd;
        return result;
    }

    /// <summary>Wert ist ein Text (Name, String).</summary>
    public static string ReadString(byte[] value, out bool complete)
    {
        var r = new PropertyReader(value);
        var s = r.Str();
        complete = r.AtEnd;
        return s;
    }

    /// <summary>Wert ist ein Array aus Texten (Namen, Enum-Werte).</summary>
    public static List<string> ReadStringArray(byte[] value, out bool complete)
    {
        var r = new PropertyReader(value);
        int n = r.I32();
        var list = new List<string>(n);
        for (int i = 0; i < n; i++) list.Add(r.Str());
        complete = r.AtEnd;
        return list;
    }

    /// <summary>Wert ist ein Array aus Strukturen.</summary>
    public static List<Dictionary<string, object?>> ReadStructArray(byte[] value, out bool complete)
    {
        var r = new PropertyReader(value);
        int n = r.I32();
        var list = new List<Dictionary<string, object?>>(n);
        for (int i = 0; i < n; i++) list.Add(r.Props(value.Length));
        complete = r.AtEnd;
        return list;
    }

    int I32() { int v = BinaryPrimitives.ReadInt32LittleEndian(_b.AsSpan(_o)); _o += 4; return v; }
    long I64() { long v = BinaryPrimitives.ReadInt64LittleEndian(_b.AsSpan(_o)); _o += 8; return v; }

    string Str()
    {
        int n = I32();
        if (n == 0) return "";
        if (n < 0)
        {
            var s = Encoding.Unicode.GetString(_b, _o, -2 * n - 2);
            _o += -2 * n;
            return s;
        }
        var t = Encoding.Latin1.GetString(_b, _o, n - 1);
        _o += n;
        return t;
    }

    sealed record TypeName(string Name, List<TypeName> Inner);

    TypeName Type()
    {
        var name = Str();
        int n = I32();
        var inner = new List<TypeName>(n);
        for (int i = 0; i < n; i++) inner.Add(Type());
        return new TypeName(name, inner);
    }

    Dictionary<string, object?> Props(int end)
    {
        var result = new Dictionary<string, object?>();
        while (_o < end)
        {
            var name = Str();
            if (name is "None" or "") break;
            var type = Type();
            int size = I32();
            byte flags = _b[_o++];
            int index = (flags & 1) != 0 ? I32() : 0;
            if ((flags & 2) != 0) _o += 16;
            if ((flags & 4) != 0) throw new InvalidDataException("Eigenschafts-Erweiterungen werden nicht unterstützt.");
            int start = _o;
            object? value = type.Name == "BoolProperty" ? (flags & 16) != 0 : Value(type, size);
            _o = start + size;
            result[index == 0 ? name : $"{name}[{index}]"] = value;
        }
        return result;
    }

    object? Value(TypeName type, int size)
    {
        switch (type.Name)
        {
            case "IntProperty": return (long)I32();
            case "Int64Property" or "UInt64Property": return I64();
            case "FloatProperty": { float f = BinaryPrimitives.ReadSingleLittleEndian(_b.AsSpan(_o)); _o += 4; return (double)f; }
            case "NameProperty" or "StrProperty": return Str();
            case "EnumProperty": return Str();
            case "ByteProperty": return size == 1 ? (long)_b[_o++] : Str();
            case "StructProperty":
            {
                var structName = type.Inner.Count > 0 ? type.Inner[0].Name : "";
                if (structName == "DateTime") return Ticks(I64());
                return structName is "Guid" or "Vector" or "Rotator" or "Quat" or "Vector2D" or "LinearColor" or "Color" or "Timespan"
                    ? null : Props(_o + size);
            }
            case "ArrayProperty" or "SetProperty":
            {
                var inner = type.Inner.Count > 0 ? type.Inner[0] : new TypeName("", []);
                int n = I32();
                var list = new List<object?>(n);
                for (int i = 0; i < n; i++)
                {
                    if (inner.Name == "StructProperty")
                    {
                        var structName = inner.Inner.Count > 0 ? inner.Inner[0].Name : "";
                        if (structName == "DateTime") list.Add(Ticks(I64()));
                        else list.Add(Props(_b.Length));
                    }
                    else if (inner.Name is "NameProperty" or "StrProperty" or "EnumProperty") list.Add(Str());
                    else if (inner.Name == "IntProperty") list.Add((long)I32());
                    else return null; // unbekannter Inhalt: Größe der Eigenschaft überspringt ihn
                }
                return list;
            }
            default: return null;
        }
    }

    /// <summary>Zeitstempel (Ticks seit 0001, UTC) in Ortszeit; 0 = keiner.</summary>
    static DateTime? Ticks(long ticks) =>
        ticks <= 0 || ticks > DateTime.MaxValue.Ticks ? null : new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();
}
