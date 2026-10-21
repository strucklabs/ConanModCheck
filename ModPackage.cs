using System.IO.Compression;
using ConanModCheck.IoStore;

namespace ConanModCheck;

/// <summary>Eine geprüfte Datentabelle, die eine Mod mitbringt oder überschreibt.</summary>
sealed record ModTable(string Path, bool IsOverride, HashSet<long> Rows)
{
    /// <summary>Nur bei überschriebenen Spieltabellen: Zeilen, die das aktuelle Spiel hat, die Kopie der Mod aber nicht.</summary>
    public List<long> MissingGameRows { get; init; } = [];
}

/// <summary>Eine von mehreren geladenen Versionen derselben Mod.</summary>
/// <param name="OnlyHere">Eigene IDs, die nur diese Version mitbringt.</param>
sealed record ModVersion(string Source, DateTime Date, int OnlyHere);

/// <summary>
/// Eine Mod-Datei (.pak/.utoc/.ucas, auch als Zip) und das, was sich daraus ablesen lässt.
/// </summary>
sealed class ModPackage : IDisposable
{
    readonly IoStoreContainer? _container;

    public string Name { get; }
    public string Source { get; }

    /// <summary>Ordner unter /Game/Mods/, die zu dieser Mod gehören.</summary>
    public HashSet<string> ModFolders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dateien außerhalb von /Game/Mods/, die Spieldateien ersetzen oder ergänzen.</summary>
    public List<string> OverriddenGameFiles { get; } = [];
    public List<string> AddedOutsideModFolder { get; } = [];

    public List<ModTable> Tables { get; } = [];

    /// <summary>Dateinamen aller Pakete der Mod ohne Endung, für die Herkunft von Blueprint-Klassen.</summary>
    public HashSet<string> AssetNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// IDs, die es nur durch diese Mod gibt: Zeilennummern ihrer Datentabellen, die das Spiel in keiner
    /// eigenen Tabelle verwendet. Ohne Spieldateien zählen Nummern unter <see cref="SmallId"/> nicht,
    /// weil sie zu oft auch im Spiel vorkommen.
    /// </summary>
    public HashSet<long> NewIds { get; } = [];

    /// <summary>Unterhalb dieser Nummer gilt eine Zeile ohne Spieldateien nicht als eigene ID der Mod.</summary>
    public const long SmallId = 1000;

    /// <summary>Eigene ID → Datentabelle der Mod, aus der sie stammt (Dateiname ohne Endung).</summary>
    public Dictionary<long, string> IdTables { get; } = [];

    /// <summary>IDs aus dem Originalspiel, deren Daten die Mod verändert.</summary>
    public HashSet<long> ChangedGameIds { get; } = [];

    /// <summary>ID → Verweis auf den Namen, aus den eigenen Tabellen der Mod.</summary>
    public Dictionary<long, TextRef> Texts { get; } = [];

    /// <summary>Übersetzungen aus dem Pak-Archiv der Mod, falls sie welche mitbringt.</summary>
    public Dictionary<(string, string), string> German { get; } = [];
    public Dictionary<(string, string), string> English { get; } = [];

    /// <summary>Wurden mehrere Versionen dieser Mod geladen: alle, neueste zuerst (sonst leer).</summary>
    public List<ModVersion> Versions { get; } = [];

    ModPackage(string name, string source, IoStoreContainer? container)
    {
        Name = name;
        Source = source;
        _container = container;
    }

    public static ModPackage Open(string utocPath, string source, GameData? game)
    {
        var name = Path.GetFileNameWithoutExtension(utocPath);
        foreach (var suffix in new[] { "-WindowsServer", "-WindowsNoEditor", "-Windows" })
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];

        var mod = new ModPackage(name, source, new IoStoreContainer(utocPath));
        mod.Analyze(game);
        var pak = Path.ChangeExtension(utocPath, ".pak");
        if (File.Exists(pak)) GameData.LoadLocalizationFrom(pak, mod.German, mod.English, _ => { });
        return mod;
    }

    /// <summary>
    /// Nur ein Mod-Ordner, ohne Mod-Datei – etwa für Reste einer Mod, die schon entfernt wurde.
    /// Erkannt werden dann nur Objekte, Bauteile und Effekte, keine Items.
    /// </summary>
    public static ModPackage FromFolder(string folder)
    {
        var mod = new ModPackage(folder, L.T("nur Ordnername, ohne Mod-Datei"), null);
        mod.ModFolders.Add(folder);
        return mod;
    }

    public bool HasFile => _container != null;

    void Analyze(GameData? game)
    {
        if (_container == null) return;
        foreach (var path in _container.Files.Keys)
        {
            if (path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)) AssetNames.Add(Path.GetFileNameWithoutExtension(path));
            if (path.StartsWith("/Game/Mods/", StringComparison.OrdinalIgnoreCase))
            {
                var folder = path["/Game/Mods/".Length..].Split('/')[0];
                if (!folder.Contains('.')) ModFolders.Add(folder);
            }
            else if (path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
            {
                if (game != null && game.Contains(path)) OverriddenGameFiles.Add(path);
                else AddedOutsideModFolder.Add(path);
            }
        }

        var gameIds = game == null ? null : new HashSet<long>(game.ItemIds.Concat(game.FeatIds));
        // Neu ist nur, was das Spiel in keiner Tabelle verwendet; ohne Spieldateien keine kleinen Nummern
        bool IsOwn(long id) => game != null ? !game.RowIds.Contains(id) : id >= SmallId;
        void AddNew(long id, string table)
        {
            if (!IsOwn(id)) return;
            NewIds.Add(id);
            IdTables.TryAdd(id, table);
        }

        foreach (var path in _container.Files.Keys.Where(p => p.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase)).Order())
        {
            List<string> names;
            byte[] package;
            try { package = _container.Read(path); names = ZenPackage.ReadNames(package); }
            catch (Exception) { continue; }
            if (!ZenPackage.IsDataTable(names)) continue;
            if (!Path.GetFileName(path).Contains("tag", StringComparison.OrdinalIgnoreCase))
                foreach (var (id, text) in TableTexts.Read(package, names)) Texts.TryAdd(id, text);

            var rows = ZenPackage.NumericNames(names).ToHashSet();
            if (rows.Count == 0) continue;
            var tableName = Path.GetFileNameWithoutExtension(path);

            bool isOverride = game != null && game.Contains(path);
            var table = new ModTable(path, isOverride, rows);
            if (isOverride)
            {
                var original = ZenPackage.NumericNames(ZenPackage.ReadNames(game!.Read(path))).ToHashSet();
                table = table with { MissingGameRows = original.Except(rows).Order().ToList() };
                // Bei überschriebenen Tabellen zählen nur Zeilen als neu, die es im Original nicht gibt
                foreach (var id in rows.Except(original)) AddNew(id, tableName);
            }
            else
            {
                foreach (var id in rows)
                    if (gameIds != null && gameIds.Contains(id)) ChangedGameIds.Add(id);
                    else AddNew(id, tableName);
            }
            Tables.Add(table);
        }
    }

    public void Dispose() => _container?.Dispose();

    /// <summary>
    /// Fasst mehrfach geladene Versionen derselben Mod zusammen (gleicher Name und gleiche Mod-Ordner,
    /// z. B. ExtractedMods vom Server und der Workshop-Ordner). Sonst gälte jede ID als „gehört auch zu
    /// einer anderen Mod“ und die Bereinigung ließe alles stehen. Zur Mod gehören die IDs aller Versionen,
    /// denn Reste jeder Version können im Spielstand stecken; Namen und Texte kommen bevorzugt aus der
    /// neuesten Datei. Gleicher Name bei verschiedenen Mod-Ordnern bleibt getrennt (mit Hinweis).
    /// </summary>
    public static List<ModPackage> MergeVersions(List<ModPackage> mods, Action<string> log)
    {
        var result = new List<ModPackage>();
        foreach (var byName in mods.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase))
        {
            var groups = byName.GroupBy(m => string.Join("|", m.ModFolders.Order(StringComparer.OrdinalIgnoreCase)), StringComparer.OrdinalIgnoreCase).ToList();
            if (groups.Count > 1)
                log(L.F("Hinweis: Mehrere Mod-Dateien heißen {0}, enthalten aber verschiedene Mod-Ordner; sie werden getrennt geführt.", byName.Key));
            foreach (var g in groups) result.Add(Merge(g.ToList(), log));
        }
        return result;
    }

    static ModPackage Merge(List<ModPackage> versions, Action<string> log)
    {
        if (versions.Count == 1) return versions[0];
        static DateTime DateOf(ModPackage m)
        {
            var file = m.Source.Split(" → ")[0];
            return File.Exists(file) ? File.GetLastWriteTime(file) : DateTime.MinValue;
        }
        var ordered = versions.OrderByDescending(DateOf).ToList();
        var main = ordered[0];
        foreach (var v in ordered)
        {
            var others = ordered.Where(o => o != v).SelectMany(o => o.NewIds).ToHashSet();
            main.Versions.Add(new ModVersion(v.Source, DateOf(v), v.NewIds.Count(id => !others.Contains(id))));
        }
        foreach (var v in ordered.Skip(1))
        {
            main.NewIds.UnionWith(v.NewIds);
            main.ChangedGameIds.UnionWith(v.ChangedGameIds);
            main.AssetNames.UnionWith(v.AssetNames);
            foreach (var (id, table) in v.IdTables) main.IdTables.TryAdd(id, table);
            foreach (var (id, text) in v.Texts) main.Texts.TryAdd(id, text);
            foreach (var (key, text) in v.German) main.German.TryAdd(key, text);
            foreach (var (key, text) in v.English) main.English.TryAdd(key, text);
            foreach (var f in v.OverriddenGameFiles.Where(f => !main.OverriddenGameFiles.Contains(f))) main.OverriddenGameFiles.Add(f);
            foreach (var f in v.AddedOutsideModFolder.Where(f => !main.AddedOutsideModFolder.Contains(f))) main.AddedOutsideModFolder.Add(f);
            foreach (var t in v.Tables.Where(t => !main.Tables.Any(m => m.Path.Equals(t.Path, StringComparison.OrdinalIgnoreCase)))) main.Tables.Add(t);
            v.Dispose();
        }
        log(L.F("{0}: {1} Versionen geladen und zusammengefasst (neueste: {2})", main.Name, ordered.Count, main.Source));
        return main;
    }

    /// <summary>
    /// Sammelt Mod-Container aus Dateien und Ordnern. Zips werden in einen temporären Ordner
    /// entpackt, der beim Beenden wieder gelöscht wird.
    /// </summary>
    public static List<(string Utoc, string Source)> Collect(IEnumerable<string> inputs, string tempDir)
    {
        var result = new List<(string, string)>();
        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                foreach (var toc in Directory.EnumerateFiles(input, "*.utoc", SearchOption.AllDirectories))
                    result.Add((toc, toc));
                foreach (var zip in Directory.EnumerateFiles(input, "*.zip", SearchOption.AllDirectories))
                    result.AddRange(FromZip(zip, tempDir));
                // .pak ohne .utoc daneben: Workshop-Container (z. B. der Ordner steamapps/workshop/content/440900)
                foreach (var pak in Directory.EnumerateFiles(input, "*.pak", SearchOption.AllDirectories))
                    if (!File.Exists(Path.ChangeExtension(pak, ".utoc")) && IsContainer(pak))
                        result.AddRange(FromContainer(pak, tempDir));
            }
            else if (input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) result.AddRange(FromZip(input, tempDir));
            else if (input.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)) result.Add((input, input));
            else if (input.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) && !File.Exists(Path.ChangeExtension(input, ".utoc")))
            {
                var found = FromContainer(input, tempDir);
                if (found.Count == 0) throw new InvalidDataException(L.F("In {0} steckt kein Mod-Paket (.utoc).", input));
                result.AddRange(found);
            }
            else if (input.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || input.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase))
                result.Add((Path.ChangeExtension(input, ".utoc"), input));
            else throw new FileNotFoundException(L.F("Nicht gefunden oder unbekannter Dateityp: {0}", input));
        }
        return result;
    }

    /// <summary>Enthält die .pak Mod-Pakete (.utoc)? Unlesbares gilt als nein.</summary>
    static bool IsContainer(string pakPath)
    {
        try
        {
            using var pak = new PakFile(pakPath);
            return pak.Files.Keys.Any(k => k.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    /// <summary>
    /// Mods aus dem Steam-Workshop sind eine einzelne .pak, in der die Pakete je Plattform liegen
    /// (Name-Windows, Name-WindowsServer, Name-LinuxServer, jeweils .utoc/.ucas/.pak). Der Inhalt ist
    /// derselbe; ausgepackt wird eines, bevorzugt das des Windows-Servers.
    /// </summary>
    static List<(string, string)> FromContainer(string pakPath, string tempDir)
    {
        using var pak = new PakFile(pakPath);
        var tocs = pak.Files.Keys.Where(k => k.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)).ToList();
        var toc = tocs.FirstOrDefault(k => k.EndsWith("-WindowsServer.utoc", StringComparison.OrdinalIgnoreCase))
            ?? tocs.FirstOrDefault(k => k.EndsWith("-Windows.utoc", StringComparison.OrdinalIgnoreCase))
            ?? tocs.FirstOrDefault();
        if (toc == null) return [];

        var target = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        foreach (var ext in new[] { ".utoc", ".ucas", ".pak" })
        {
            var inner = toc[..^".utoc".Length] + ext;
            // Nur den Dateinamen übernehmen, damit Pfade aus dem Archiv nicht aus dem Zielordner herausführen
            if (pak.Files.ContainsKey(inner)) File.WriteAllBytes(Path.Combine(target, Path.GetFileName(inner)), pak.Read(inner));
        }
        var name = Path.GetFileName(toc);
        return [(Path.Combine(target, name), $"{pakPath} → {name}")];
    }

    static IEnumerable<(string, string)> FromZip(string zipPath, string tempDir)
    {
        var target = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        using var zip = ZipFile.OpenRead(zipPath);
        var tocs = new List<(string, string)>();
        foreach (var entry in zip.Entries)
        {
            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (ext is not (".utoc" or ".ucas" or ".pak")) continue;
            // Nur den Dateinamen übernehmen, damit Pfade aus dem Archiv nicht aus dem Zielordner herausführen
            var dest = Path.Combine(target, entry.Name);
            entry.ExtractToFile(dest, overwrite: true);
            if (ext == ".utoc") tocs.Add((dest, $"{zipPath} → {entry.Name}"));
        }
        return tocs;
    }
}
