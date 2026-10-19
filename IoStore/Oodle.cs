using System.Runtime.InteropServices;

namespace ConanModCheck.IoStore;

/// <summary>
/// Anbindung an die Oodle-Bibliothek (oo2core_*_win64.dll). Conan Exiles Enhanced hat sie
/// fest in die Spiel-EXE eingebaut; viele andere Spiele liefern sie aber als eigene DLL mit.
/// </summary>
static unsafe class Oodle
{
    static delegate* unmanaged<byte*, long, byte*, long, int, int, int, nint, long, nint, nint, nint, long, int, long> _decompress;

    public static string? LoadedFrom { get; private set; }

    public static bool IsLoaded => _decompress != null;

    public static void Load(string dllPath)
    {
        var lib = NativeLibrary.Load(dllPath);
        _decompress = (delegate* unmanaged<byte*, long, byte*, long, int, int, int, nint, long, nint, nint, nint, long, int, long>)
            NativeLibrary.GetExport(lib, "OodleLZ_Decompress");
        LoadedFrom = dllPath;
    }

    public static byte[] Decompress(byte[] compressed, int uncompressedSize)
    {
        if (_decompress == null)
            throw new InvalidOperationException(L.T("Oodle wurde nicht geladen."));
        var output = new byte[uncompressedSize];
        long result;
        fixed (byte* src = compressed)
        fixed (byte* dst = output)
            // fuzzSafe=1, checkCRC=0, verbosity=0, threadPhase=3 (alle Phasen)
            result = _decompress(src, compressed.Length, dst, uncompressedSize, 1, 0, 0, 0, 0, 0, 0, 0, 0, 3);
        if (result != uncompressedSize)
            throw new InvalidDataException(L.T("Oodle konnte einen Block nicht entpacken."));
        return output;
    }

    /// <summary>Sucht Oodle-DLLs neben der EXE und in den Steam-Bibliotheken, neueste zuerst.</summary>
    public static List<string> FindCandidates(IEnumerable<string> steamLibraries)
    {
        var local = SafeFiles(AppContext.BaseDirectory, 0);
        var steam = steamLibraries.SelectMany(lib => SafeFiles(Path.Combine(lib, "steamapps", "common"), 4));
        // Eine DLL neben der EXE hat Vorrang, danach oo2core_9 vor oo2core_8 ...
        return local
            .Concat(steam.OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static IEnumerable<string> SafeFiles(string dir, int depth)
    {
        if (!Directory.Exists(dir)) return [];
        try
        {
            return Directory.EnumerateFiles(dir, "oo2core_*_win64.dll", new EnumerationOptions
            {
                RecurseSubdirectories = depth > 0,
                MaxRecursionDepth = depth,
                IgnoreInaccessible = true,
            }).ToList();
        }
        catch (IOException) { return []; }
    }
}
