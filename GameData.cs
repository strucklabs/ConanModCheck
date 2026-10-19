using System.Text.RegularExpressions;
using ConanModCheck.IoStore;
using Microsoft.Win32;

namespace ConanModCheck;

/// <summary>
/// Die originalen Spieldaten: welche Item- und Talent-IDs es ohne Mods gibt und welche
/// Dateien das Spiel selbst mitbringt (um zu erkennen, was eine Mod überschreibt).
/// </summary>
sealed class GameData : IDisposable
{
    readonly List<IoStoreContainer> _containers = [];
    readonly Dictionary<string, IoStoreContainer> _fileIndex = new(StringComparer.OrdinalIgnoreCase);

    public string GameDir { get; }
    public HashSet<long> ItemIds { get; } = [];
    public HashSet<long> FeatIds { get; } = [];

    /// <summary>ID → Verweis auf den Namen (aus den Item- und Talent-Tabellen).</summary>
    public Dictionary<long, TextRef> Texts { get; } = [];

    /// <summary>Übersetzungen aus den Sprachdateien des Spiels.</summary>
    public Dictionary<(string, string), string> German { get; } = [];
    public Dictionary<(string, string), string> English { get; } = [];

    GameData(string gameDir) => GameDir = gameDir;

    public bool Contains(string gamePath) => _fileIndex.ContainsKey(gamePath);

    public byte[] Read(string gamePath) => _fileIndex[gamePath].Read(gamePath);

    public static GameData Load(string gameDir, Action<string> log)
    {
        var game = new GameData(gameDir);
        var paks = Path.Combine(gameDir, "ConanSandbox", "Content", "Paks");
        foreach (var toc in Directory.EnumerateFiles(paks, "*.utoc").Order())
        {
            try
            {
                var c = new IoStoreContainer(toc);
                game._containers.Add(c);
                foreach (var f in c.Files.Keys) game._fileIndex.TryAdd(f, c);
            }
            catch (Exception ex) { log("  " + L.F("Übersprungen: {0} ({1})", Path.GetFileName(toc), ex.Message)); }
        }

        foreach (var path in game._fileIndex.Keys.Where(p => p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)))
        {
            var kind = TableKind(path);
            if (kind == null) continue;
            var package = game.Read(path);
            var names = ZenPackage.ReadNames(package);
            var target = kind == "feat" ? game.FeatIds : game.ItemIds;
            target.UnionWith(ZenPackage.NumericNames(names));

            // Tag-Tabellen haben zwar Item-IDs als Zeilen, aber keine Namen
            if (Path.GetFileName(path).Contains("tag", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var (id, text) in TableTexts.Read(package, names)) game.Texts.TryAdd(id, text);
        }

        LoadLocalization(paks, game.German, game.English, log);
        return game;
    }

    /// <summary>Liest die deutschen und englischen Sprachdateien aus allen Pak-Archiven eines Ordners.</summary>
    public static void LoadLocalization(string paksDir, Dictionary<(string, string), string> german,
        Dictionary<(string, string), string> english, Action<string> log)
    {
        foreach (var pakPath in Directory.EnumerateFiles(paksDir, "*.pak").Order())
            LoadLocalizationFrom(pakPath, german, english, log);
    }

    public static void LoadLocalizationFrom(string pakPath, Dictionary<(string, string), string> german,
        Dictionary<(string, string), string> english, Action<string> log)
    {
        try
        {
            using var pak = new PakFile(pakPath);
            foreach (var file in pak.Files.Keys.Where(f => f.EndsWith(".locres", StringComparison.OrdinalIgnoreCase)))
            {
                var target = file.Contains("/de/", StringComparison.OrdinalIgnoreCase) ? german
                           : file.Contains("/en/", StringComparison.OrdinalIgnoreCase) ? english : null;
                if (target == null) continue;
                try { Locres.ReadInto(pak.Read(file), target); }
                catch (Exception) { /* einzelne kaputte Sprachdatei überspringen */ }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            // Leere oder unbekannte Pak-Archive enthalten keine Sprachdateien
        }
    }

    /// <summary>Ordnet eine Spieltabelle anhand ihres Namens zu: "item", "feat" oder null.</summary>
    static string? TableKind(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (name.Contains("itemtable")) return "item";
        if (name.Contains("feattable")) return "feat";
        return null;
    }

    public void Dispose()
    {
        foreach (var c in _containers) c.Dispose();
    }

    // ---------- Spiel und Steam finden ----------

    public static List<string> SteamLibraries()
    {
        var libs = new List<string>();
        string? steam = null;
        try
        {
            steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                 ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        }
        catch { /* ohne Registry geht es auch mit --game/--oodle */ }
        if (steam == null) return libs;

        steam = Path.GetFullPath(steam);
        libs.Add(steam);
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(.+?)\""))
            {
                var p = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(p)) libs.Add(Path.GetFullPath(p));
            }
        }
        return libs.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string? FindGame(IEnumerable<string> steamLibraries)
    {
        foreach (var lib in steamLibraries)
            foreach (var name in new[] { "Conan Exiles", "Conan Exiles Dedicated Server" })
            {
                var dir = Path.Combine(lib, "steamapps", "common", name);
                if (IsGameDir(dir)) return dir;
            }
        return null;
    }

    public static bool IsGameDir(string dir) =>
        Directory.Exists(Path.Combine(dir, "ConanSandbox", "Content", "Paks"));
}
