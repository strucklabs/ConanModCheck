using System.IO.Compression;
using ConanModCheck.IoStore;

namespace ConanModCheck;

/// <summary>Eine geprüfte Datentabelle, die eine Mod mitbringt oder überschreibt.</summary>
sealed record ModTable(string Path, bool IsOverride, HashSet<long> Rows)
{
    /// <summary>Nur bei überschriebenen Spieltabellen: Zeilen, die das aktuelle Spiel hat, die Kopie der Mod aber nicht.</summary>
    public List<long> MissingGameRows { get; init; } = [];
}

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

    /// <summary>IDs, die es nur durch diese Mod gibt (nicht im Originalspiel).</summary>
    public HashSet<long> NewIds { get; } = [];

    /// <summary>IDs aus dem Originalspiel, deren Daten die Mod verändert.</summary>
    public HashSet<long> ChangedGameIds { get; } = [];

    /// <summary>ID → Verweis auf den Namen, aus den eigenen Tabellen der Mod.</summary>
    public Dictionary<long, TextRef> Texts { get; } = [];

    /// <summary>Übersetzungen aus dem Pak-Archiv der Mod, falls sie welche mitbringt.</summary>
    public Dictionary<(string, string), string> German { get; } = [];
    public Dictionary<(string, string), string> English { get; } = [];

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
        var mod = new ModPackage(folder, "nur Ordnername, ohne Mod-Datei", null);
        mod.ModFolders.Add(folder);
        return mod;
    }

    public bool HasFile => _container != null;

    void Analyze(GameData? game)
    {
        if (_container == null) return;
        foreach (var path in _container.Files.Keys)
        {
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

            bool isOverride = game != null && game.Contains(path);
            var table = new ModTable(path, isOverride, rows);
            if (isOverride)
            {
                var original = ZenPackage.NumericNames(ZenPackage.ReadNames(game!.Read(path))).ToHashSet();
                table = table with { MissingGameRows = original.Except(rows).Order().ToList() };
                // Bei überschriebenen Tabellen zählen nur Zeilen als neu, die es im Original nicht gibt
                NewIds.UnionWith(rows.Except(original));
            }
            else
            {
                foreach (var id in rows)
                    if (gameIds != null && gameIds.Contains(id)) ChangedGameIds.Add(id);
                    else NewIds.Add(id);
            }
            Tables.Add(table);
        }
    }

    public void Dispose() => _container?.Dispose();

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
            }
            else if (input.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) result.AddRange(FromZip(input, tempDir));
            else if (input.EndsWith(".utoc", StringComparison.OrdinalIgnoreCase)) result.Add((input, input));
            else if (input.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) || input.EndsWith(".ucas", StringComparison.OrdinalIgnoreCase))
                result.Add((Path.ChangeExtension(input, ".utoc"), input));
            else throw new FileNotFoundException($"Nicht gefunden oder unbekannter Dateityp: {input}");
        }
        return result;
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
