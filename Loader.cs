using ConanModCheck.IoStore;

namespace ConanModCheck;

/// <summary>Gemeinsames Laden von Oodle, Spieldaten und Mods für Konsole und Oberfläche.</summary>
static class Loader
{
    /// <summary>Lädt die angegebene oder die erste gefundene Oodle-DLL. Schon geladen → sofort true.</summary>
    public static bool LoadOodle(string? path, List<string> steamLibraries, Action<string> log)
    {
        if (Oodle.IsLoaded) return true;
        List<string> candidates = path != null ? [path] : Oodle.FindCandidates(steamLibraries);
        foreach (var c in candidates)
        {
            try { Oodle.Load(c); break; }
            catch (Exception) { /* nächste versuchen */ }
        }
        if (Oodle.IsLoaded) log($"Oodle: {Oodle.LoadedFrom}");
        else log("Keine Oodle-Bibliothek (oo2core_*_win64.dll) gefunden. Mod- und Spieldateien lassen sich ohne sie nicht lesen. "
               + "Viele Spiele bringen sie mit; sie lässt sich angeben oder neben ConanModCheck.exe legen.");
        return Oodle.IsLoaded;
    }

    public static GameData? LoadGame(string? gameDir, List<string> steamLibraries, Action<string> log)
    {
        if (!Oodle.IsLoaded) return null;
        gameDir ??= GameData.FindGame(steamLibraries);
        if (gameDir == null || !GameData.IsGameDir(gameDir))
        {
            log("Conan Exiles wurde nicht gefunden. Ohne Spieldateien sind die Item-Angaben ungenauer.");
            return null;
        }
        log($"Spiel: {gameDir}");
        var game = GameData.Load(gameDir, log);
        log($"  {game.ItemIds.Count} Item- und {game.FeatIds.Count} Talent-IDs im Originalspiel");
        return game;
    }

    /// <summary>Öffnet Mod-Dateien und ergänzt Mods, die nur über ihren Ordnernamen angegeben sind.</summary>
    public static List<ModPackage> LoadMods(IEnumerable<string> inputs, IEnumerable<string> folders, GameData? game, string tempDir, Action<string> log)
    {
        var mods = new List<ModPackage>();
        var inputList = inputs.ToList();
        if (inputList.Count > 0 && !Oodle.IsLoaded)
            log("Mod-Dateien werden übersprungen, weil Oodle fehlt.");
        else
        {
            foreach (var (utoc, source) in ModPackage.Collect(inputList, tempDir))
            {
                try
                {
                    var mod = ModPackage.Open(utoc, source, game);
                    mods.Add(mod);
                    log($"Mod: {mod.Name}");
                }
                catch (Exception ex) { log($"Mod konnte nicht gelesen werden: {source} ({ex.Message})"); }
            }
        }
        foreach (var folder in folders)
            if (!mods.Any(m => m.ModFolders.Contains(folder))) mods.Add(ModPackage.FromFolder(folder));
        return mods;
    }

    public static string NewTempDir() => Path.Combine(Path.GetTempPath(), "ConanModCheck", Guid.NewGuid().ToString("N"));

    public static void DeleteTempDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* Temp-Ordner darf liegen bleiben */ }
    }
}
