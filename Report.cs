using System.Globalization;

namespace ConanModCheck;

sealed class FolderStat(string folder)
{
    public string Folder { get; } = folder;
    public int Objects { get; set; }

    /// <summary>Mod-Controller (die Steuerung, die der Server für jede aktive Mod anlegt); zählen nicht zu den Objekten.</summary>
    public int Controllers { get; set; }
    public int Pieces { get; set; }
    public int Props { get; set; }
    public int Buffs { get; set; }
}

/// <summary>Wertet den Spielstand gegen die Mods aus und schreibt den Bericht als Text.</summary>
/// <param name="knownMods">Alle geladenen Mods, auch nicht ausgewählte; nur für die Herkunft von Items.</param>
sealed class Report(SaveGame save, List<ModPackage> mods, GameData? game, TextWriter w, IEnumerable<ModPackage>? knownMods = null)
{
    readonly ItemNames _names = ItemNames.Build(game, knownMods ?? mods);

    public void Write()
    {
        WriteOverview();
        foreach (var mod in mods) WriteMod(mod);
        Heading(L.T("Reste ohne Besitzer"));
        Orphans.Find(save).Write(w, _names.Label);
        WritePlayerAttributes();
        WriteSpecialInventories();
        WriteUnknownItems();
    }

    // ---------- Übersicht ----------

    void WriteOverview()
    {
        Heading(L.T("Übersicht: Mod-Inhalte im Spielstand"));
        var folders = FolderStats(save);

        if (folders.Count == 0) { w.WriteLine(L.T("Im Spielstand gibt es keine Objekte aus Mods.")); w.WriteLine(); return; }

        w.WriteLine(L.T("Je Mod-Ordner: Mod-Controller, platzierte Objekte, Bauteile, Items in Inventaren und Behältern, erlernte Rezepte und Talente, Objekte mit Mod-Daten, aktive Effekte, Warnungen."));
        w.WriteLine(L.T("Hinweis: Items lassen sich nur zuordnen, wenn die Mod-Datei mit angegeben wird; sonst steht dort „–“."));
        w.WriteLine();
        var known = knownMods?.ToList() ?? mods;
        var warnings = new List<(string Folder, List<string> Texts)>();
        int width = Math.Max(10, folders.Max(f => f.Folder.Length));
        w.WriteLine($"  {L.T("Ordner").PadRight(width)}  {L.T("Controller"),10}  {L.T("Objekte"),7}  {L.T("Bauteile"),8}  {L.T("Items"),6}  {L.T("Erlernt"),7}  {L.T("Mod-Daten"),9}  {L.T("Effekte"),7}  {L.T("Warnungen"),9}  {L.T("Mod-Datei")}");
        foreach (var e in folders)
        {
            var mod = known.FirstOrDefault(m => m.HasFile && m.ModFolders.Contains(e.Folder));
            var (items, learned) = mod != null ? ItemCounts(save, mod) : (-1, -1);
            var found = DescribeMod(mod ?? ModPackage.FromFolder(e.Folder)).Warnings;
            if (found.Count > 0 && !mods.Any(m => m.ModFolders.Contains(e.Folder))) warnings.Add((e.Folder, found));
            string Count(int n) => n < 0 ? "–" : n.ToString(CultureInfo.InvariantCulture);
            w.WriteLine($"  {e.Folder.PadRight(width)}  {ControllerMark(e.Controllers),10}  {e.Objects,7}  {e.Pieces,8}  {Count(items),6}  {Count(learned),7}  {e.Props,9}  {e.Buffs,7}  {(found.Count > 0 ? "⚠ " + found.Count : ""),9}  {mod?.Name ?? "–"}");
        }
        w.WriteLine();

        // Ausgewählte Mods bekommen einen eigenen Abschnitt; die Warnungen der übrigen sollen nicht untergehen
        if (warnings.Count > 0)
        {
            Sub(L.T("Warnungen bei Mods, die nicht zum Entfernen ausgewählt sind"));
            foreach (var (folder, texts) in warnings)
            {
                w.WriteLine("  " + folder + ":");
                foreach (var t in texts) w.WriteLine($"   ! {t}");
            }
            w.WriteLine();
        }
    }

    /// <summary>
    /// Items der Mod in Inventaren und Behältern (Einträge) und erlernte Rezepte und Talente bei
    /// Spielerfiguren. Nur mit Mod-Datei möglich, da die IDs aus ihren Tabellen stammen.
    /// </summary>
    public static (int Items, int Learned) ItemCounts(SaveGame save, ModPackage mod)
    {
        var hits = save.Items.Where(i => mod.NewIds.Contains(i.TemplateId)).ToList();
        int learned = hits.Count(i => save.Characters.ContainsKey(i.OwnerId) && InventoryKinds.IsLearned(i.InvType));
        int physical = hits.Count(i => !save.Characters.ContainsKey(i.OwnerId) || InventoryKinds.IsItems(i.InvType));
        return (physical, learned);
    }

    /// <summary>"✓", "2 ✓" bei mehreren Controllern, leer ohne. Rechtsbündig gesetzt stehen alle Häkchen untereinander.</summary>
    public static string ControllerMark(int count) => count switch { 0 => "", 1 => "✓", _ => $"{count} ✓" };

    /// <summary>Je Mod-Ordner im Spielstand: Controller, Objekte, Bauteile, Objekte mit Mod-Daten, aktive Effekte.</summary>
    public static List<FolderStat> FolderStats(SaveGame save)
    {
        var folders = new SortedDictionary<string, FolderStat>(StringComparer.OrdinalIgnoreCase);
        FolderStat Get(string f) => folders.TryGetValue(f, out var e) ? e : folders[f] = new FolderStat(f);
        foreach (var o in save.Objects.Values)
            if (ModPaths.FolderOf(o.Class) is { } f)
            {
                if (ModPaths.IsController(o.Class)) Get(f).Controllers++;
                else Get(f).Objects++;
            }
        foreach (var p in save.Pieces.Values.SelectMany(l => l))
            if (ModPaths.FolderOf(p.Class) is { } f) Get(f).Pieces++;
        foreach (var (f, objs) in save.PropertyRefs)
            Get(f).Props = objs.Count;
        foreach (var b in save.Buffs)
            if (ModPaths.FolderOf(b.Path) is { } f) Get(f).Buffs++;
        return folders.Values.ToList();
    }

    // ---------- Einzelne Mod ----------

    /// <summary>
    /// Ein einzelner Mod-Abschnitt wie im Bericht, als Text, dazu Warnungen und Folgen beim Entfernen
    /// (für die Detailansicht der Übersicht).
    /// </summary>
    public (string Text, List<string> Warnings, List<string> Findings) DescribeMod(ModPackage mod)
    {
        var previous = w;
        var text = new StringWriter();
        w = text;
        try
        {
            var (warnings, findings) = WriteMod(mod);
            return (text.ToString(), warnings, findings);
        }
        finally { w = previous; }
    }

    (List<string> Warnings, List<string> Findings) WriteMod(ModPackage mod)
    {
        Heading(L.F("Mod: {0}", mod.Name));
        w.WriteLine(L.F("Datei: {0}", mod.Source));
        w.WriteLine(L.F("Mod-Ordner: {0}", mod.ModFolders.Count > 0 ? string.Join(", ", mod.ModFolders.Order()) : L.T("keiner")));
        w.WriteLine();

        var findings = new List<string>();  // Was beim Entfernen verloren geht oder sich ändert
        var warnings = new List<string>();  // Wo Vorsicht nötig ist

        WriteModFile(mod, findings, warnings);
        WriteWorldObjects(mod, findings, warnings);
        WriteBuildingPieces(mod, findings, warnings);
        WriteItems(mod, findings);
        WriteOtherReferences(mod, findings);

        Sub(L.T("Einschätzung"));
        if (warnings.Count == 0 && findings.Count == 0)
            w.WriteLine("  " + L.T("Im Spielstand steckt von dieser Mod höchstens ihre Steuerung. Sie lässt sich gefahrlos entfernen."));
        else
        {
            if (warnings.Count > 0)
            {
                w.WriteLine("  " + L.T("Vorsicht:"));
                foreach (var x in warnings) w.WriteLine($"   ! {x}");
            }
            if (findings.Count > 0)
            {
                w.WriteLine("  " + L.T("Beim Entfernen der Mod:"));
                foreach (var x in findings) w.WriteLine($"   - {x}");
            }
            if (warnings.Count == 0)
                w.WriteLine("  " + L.T("Nichts davon gefährdet andere Bauten oder Gegenstände. Die Mod lässt sich entfernen, wenn der Verlust in Ordnung ist."));
        }
        w.WriteLine();
        return (warnings, findings);
    }

    void WriteModFile(ModPackage mod, List<string> findings, List<string> warnings)
    {
        Sub(L.T("Inhalt der Mod-Datei"));
        if (!mod.HasFile)
        {
            w.WriteLine("  " + L.T("Hinweis: Keine Mod-Datei angegeben, Items dieser Mod lassen sich deshalb nicht erkennen."));
            w.WriteLine();
            return;
        }
        if (mod.Tables.Count == 0) w.WriteLine("  " + L.T("Keine Datentabellen mit Item-, Rezept- oder Talent-IDs."));
        foreach (var t in mod.Tables)
        {
            var kind = t.IsOverride ? L.T("ersetzt Spieltabelle") : L.T("eigene Tabelle");
            w.WriteLine("  " + L.F("{0} ({1}, {2} Einträge)", t.Path, kind, t.Rows.Count));
            if (t.MissingGameRows.Count > 0)
            {
                var inSave = save.Items.Count(i => t.MissingGameRows.Contains(i.TemplateId));
                w.WriteLine("    ! " + L.F("Veraltet: Dem aktuellen Spiel gegenüber fehlen {0} Einträge.", t.MissingGameRows.Count));
                warnings.Add(L.F("Die Mod ersetzt {0} durch eine veraltete Kopie. Solange sie aktiv ist, fehlen {1} Einträge des aktuellen Spiels", Path.GetFileName(t.Path), t.MissingGameRows.Count)
                    + (inSave > 0 ? L.F("; {0} davon liegen im Spielstand und können verschwinden.", inSave) : L.T(" (keiner davon liegt im Spielstand).")));
            }
        }
        if (game == null)
            w.WriteLine("  " + L.T("Hinweis: Ohne Spieldateien lässt sich nicht trennen, welche IDs neu sind und welche die Mod nur verändert."));
        else
        {
            w.WriteLine("  " + L.F("Neue IDs durch die Mod: {0}", mod.NewIds.Count));
            w.WriteLine("  " + L.F("Veränderte IDs aus dem Spiel: {0}", mod.ChangedGameIds.Count));
            if (mod.ChangedGameIds.Count > 0)
                findings.Add(L.F("{0} Items, Rezepte oder Talente des Spiels bekommen wieder ihre ursprünglichen Werte.", mod.ChangedGameIds.Count));
        }

        var otherOverrides = mod.OverriddenGameFiles.Where(p => !mod.Tables.Any(t => t.Path.Equals(p, StringComparison.OrdinalIgnoreCase))).ToList();
        if (otherOverrides.Count > 0)
        {
            w.WriteLine("  " + L.F("Ersetzt {0} weitere Spieldatei(en):", otherOverrides.Count));
            foreach (var p in otherOverrides.Take(15)) w.WriteLine($"    {p}");
            if (otherOverrides.Count > 15) w.WriteLine("    " + L.F("… und {0} weitere", otherOverrides.Count - 15));
        }
        if (mod.AddedOutsideModFolder.Count > 0)
            w.WriteLine("  " + L.F("Bringt {0} Datei(en) außerhalb des Mod-Ordners mit.", mod.AddedOutsideModFolder.Count));
        w.WriteLine();
    }

    void WriteWorldObjects(ModPackage mod, List<string> findings, List<string> warnings)
    {
        Sub(L.T("Objekte in der Welt"));
        var objects = save.Objects.Values.Where(o => ModPaths.BelongsTo(mod, o.Class) && !save.Pieces.ContainsKey(o.Id)).ToList();
        var controllers = objects.Where(o => ModPaths.IsController(o.Class)).ToList();
        var placed = objects.Except(controllers).ToList();

        if (controllers.Count > 0)
            w.WriteLine("  " + L.F("Mod-Steuerung: {0} ({1}), wird ohne die Mod nicht mehr geladen.", controllers.Count, string.Join(", ", controllers.Select(c => SaveGame.ShortClass(c.Class)))));
        if (placed.Count == 0) { w.WriteLine("  " + L.T("Keine platzierten Objekte.")); w.WriteLine(); return; }

        int lostGameItems = 0;
        foreach (var o in placed.OrderBy(o => o.Class))
        {
            string who = save.BuildingOwner.TryGetValue(o.Id, out var b) ? save.OwnerName(b)
                       : save.FollowerOwner.TryGetValue(o.Id, out var f) ? save.OwnerName(f) : L.T("ohne Besitzer");
            w.WriteLine("  " + L.F("{0} (ID {1}), {2}, bei {3}", SaveGame.ShortClass(o.Class), o.Id, who, SaveGame.Coords(o)));
            if (save.ItemsByOwner.TryGetValue(o.Id, out var inv) && inv.Count > 0)
            {
                int fromGame = game == null ? 0 : inv.Count(i => game.ItemIds.Contains(i.TemplateId));
                w.WriteLine("    " + (game != null
                    ? L.F("Inhalt: {0} Einträge, davon {1} Items aus dem Spiel", inv.Count, fromGame)
                    : L.F("Inhalt: {0} Einträge", inv.Count)));
                lostGameItems += fromGame;
            }
        }
        findings.Add(L.F("{0} platzierte Objekte verschwinden.", placed.Count));
        if (lostGameItems > 0)
            warnings.Add(L.F("In Objekten der Mod liegen {0} Items aus dem Spiel. Sie gehen mit verloren, wenn sie vorher nicht ausgeräumt werden.", lostGameItems));
        w.WriteLine();
    }

    void WriteBuildingPieces(ModPackage mod, List<string> findings, List<string> warnings)
    {
        Sub(L.T("Bauteile"));
        var buildings = save.Pieces
            .Select(kv => (Id: kv.Key, All: kv.Value, Mod: kv.Value.Where(p => ModPaths.BelongsTo(mod, p.Class)).ToList()))
            .Where(b => b.Mod.Count > 0)
            .OrderBy(b => b.Id)
            .ToList();
        if (buildings.Count == 0) { w.WriteLine("  " + L.T("Keine Bauteile aus dieser Mod.")); w.WriteLine(); return; }

        int mixed = 0, atRisk = 0;
        foreach (var b in buildings)
        {
            string who = save.BuildingOwner.TryGetValue(b.Id, out var owner) ? save.OwnerName(owner) : L.T("ohne Besitzer");
            string where = save.Objects.TryGetValue(b.Id, out var o) ? " " + L.F("bei {0}", SaveGame.Coords(o)) : "";
            bool isMixed = b.Mod.Count < b.All.Count;
            if (isMixed) mixed++;
            int risk = isMixed ? Support.AtRisk(b.All, p => ModPaths.BelongsTo(mod, p.Class)).Count : 0;
            atRisk += risk;
            w.WriteLine("  " + L.F("Gebäude {0}, {1}{2}: {3} von {4} Bauteilen aus der Mod", b.Id, who, where, b.Mod.Count, b.All.Count)
                + (isMixed ? "  ← " + L.T("gemischt") : "")
                + (risk > 0 ? ", " + L.F("{0} Teile stehen nur auf Mod-Teilen", risk) : ""));
            foreach (var g in b.Mod.GroupBy(p => SaveGame.ShortClass(p.Class)).OrderByDescending(g => g.Count()).Take(6))
                w.WriteLine($"    {g.Count(),4} × {g.Key}");
        }
        findings.Add(L.F("{0} Bauteile in {1} Gebäuden verschwinden.", buildings.Sum(b => b.Mod.Count), buildings.Count));
        if (mixed > 0)
            warnings.Add(L.F("{0} Gebäude bestehen teils aus Mod-Bauteilen, teils aus Bauteilen des Spiels. Fehlen die Mod-Teile, kann der Rest an Stabilität verlieren und einstürzen.", mixed));
        if (atRisk > 0)
            warnings.Add(L.F("{0} Bauteile des Spiels stehen nur auf Teilen dieser Mod und könnten nach dem Entfernen einstürzen (Näherung; auf der Karte orange).", atRisk));
        w.WriteLine();
    }

    void WriteItems(ModPackage mod, List<string> findings)
    {
        Sub(L.T("Items, Rezepte und Talente"));
        if (!mod.HasFile) { w.WriteLine("  " + L.T("Ohne Mod-Datei nicht prüfbar.")); w.WriteLine(); return; }
        if (mod.NewIds.Count == 0)
        {
            w.WriteLine("  " + (game == null
                ? L.T("Ohne Spieldateien nicht prüfbar.")
                : L.T("Die Mod bringt keine eigenen IDs mit, im Spielstand kann also nichts davon liegen.")));
            w.WriteLine();
            return;
        }

        var hits = save.Items.Where(i => mod.NewIds.Contains(i.TemplateId)).ToList();
        if (hits.Count == 0) { w.WriteLine("  " + L.T("Keine Einträge im Spielstand.")); w.WriteLine(); return; }

        foreach (var group in hits.GroupBy(i => i.OwnerId).OrderBy(g => save.DescribeInventoryOwner(g.Key)))
        {
            w.WriteLine($"  {save.DescribeInventoryOwner(group.Key)}:");
            foreach (var byType in group.GroupBy(i => i.InvType).OrderBy(g => g.Key))
            {
                var ids = byType.GroupBy(i => i.TemplateId).Select(g => g.Count() > 1 ? $"{_names.Label(g.Key)} ×{g.Count()}" : _names.Label(g.Key));
                w.WriteLine($"    {InventoryName(group.Key, byType.Key)}: {string.Join(", ", ids)}");
            }
        }
        bool character(InventoryItem i) => save.Characters.ContainsKey(i.OwnerId);
        int learned = hits.Count(i => character(i) && InventoryKinds.IsLearned(i.InvType));
        int unknown = hits.Count(i => character(i) && !InventoryKinds.IsKnown(i.InvType));
        int physical = hits.Count - learned - unknown;
        if (physical > 0) findings.Add(L.F("{0} Items aus der Mod verschwinden aus Inventaren und Behältern.", physical));
        if (learned > 0) findings.Add(L.F("{0} erlernte Rezepte, Talente, Zauber oder Lore-Einträge der Mod verfallen.", learned));
        if (unknown > 0) findings.Add(L.F("{0} Einträge in Sonderinventaren unbekannter Bedeutung passen zur Mod; die Bereinigung lässt sie stehen.", unknown));
        w.WriteLine();
    }

    void WriteOtherReferences(ModPackage mod, List<string> findings)
    {
        var buffs = save.Buffs.Where(b => ModPaths.BelongsTo(mod, b.Path)).ToList();
        var known = new HashSet<long>(save.Objects.Values.Where(o => ModPaths.BelongsTo(mod, o.Class)).Select(o => o.Id));
        var props = mod.ModFolders
            .SelectMany(f => save.PropertyRefs.TryGetValue(f, out var d) ? d : [])
            .Where(kv => !known.Contains(kv.Key))
            .ToList();
        if (buffs.Count == 0 && props.Count == 0) return;

        Sub(L.T("Weitere Verweise"));
        foreach (var g in buffs.GroupBy(b => b.CharId))
            w.WriteLine("  " + L.F("Aktive Effekte bei {0}: {1}", save.OwnerName(g.Key), string.Join(", ", g.Select(b => SaveGame.ShortClass(b.Path)).Distinct())));
        foreach (var (id, names) in props.OrderBy(p => p.Key))
        {
            var what = save.Objects.TryGetValue(id, out _) ? save.DescribeInventoryOwner(id)
                     : save.Characters.ContainsKey(id) ? save.OwnerName(id) : L.F("Objekt {0}", id);
            w.WriteLine("  " + L.F("Mod-Daten in {0}: {1}", what, string.Join(", ", names.Order())));
        }
        if (buffs.Count > 0) findings.Add(L.F("{0} aktive Effekte der Mod enden.", buffs.Count));
        if (props.Count > 0) findings.Add(L.F("{0} Objekte oder Figuren aus dem Spiel tragen Daten der Mod (z. B. Einstellungen), die dann ungenutzt bleiben.", props.Count));
        w.WriteLine();
    }

    // ---------- Spielerattribute ----------

    const int GameMaxLevel = 60;

    /// <summary>
    /// Herkunft einer Komponente an einer Spielerfigur. null = Grundspiel. Klassen ohne „_C“ sind
    /// fest im Spielprogramm eingebaut (Mods können keine hinzufügen); Blueprint-Klassen („_C“)
    /// liegen als Paket gleichen Namens in den Spiel- oder Mod-Dateien.
    /// </summary>
    string? ComponentOrigin(string component)
    {
        if (!component.EndsWith("_C", StringComparison.Ordinal)) return null;
        var asset = component[..^2];
        if (game != null && game.AssetNames.Contains(asset)) return null;
        if ((knownMods ?? mods).FirstOrDefault(m => m.AssetNames.Contains(asset)) is { } mod) return mod.Name;
        // Ohne Spieldateien lässt sich ein Blueprint keiner Seite zuordnen; dann nicht aufführen
        return game != null ? L.T("Mod, Datei nicht geladen") : null;
    }

    /// <summary>
    /// Je Spieler: Stufe, Attributpunkte, erlernte Talente und Rezepte nach Herkunft und Daten von
    /// Mod-Komponenten. Nur zur Anzeige – die Bereinigung verändert an Spielerfiguren nichts.
    /// </summary>
    void WritePlayerAttributes()
    {
        if (save.Characters.Count == 0) return;
        Heading(L.T("Spielerattribute"));
        w.WriteLine(L.T("Nur zur Anzeige: Die Bereinigung verändert an Spielerfiguren nichts."));
        w.WriteLine(L.T("Hinweis: Fallen Talente einer Mod weg, gibt das Spiel die dafür ausgegebenen Wissenspunkte nicht unbedingt von selbst zurück; dann hilft ein Zurücksetzen der Talente im Spiel."));
        if (game == null)
            w.WriteLine(L.T("Hinweis: Ohne Spieldateien lässt sich nicht prüfen, welche Talente und gespeicherten Daten der Figuren aus Mods stammen."));
        w.WriteLine();

        var origin = ItemOrigin.Build(game, knownMods ?? mods);
        foreach (var (id, name) in save.Characters.OrderBy(c => c.Value, StringComparer.CurrentCultureIgnoreCase))
        {
            int level = save.CharacterInfos.TryGetValue(id, out var info) ? info.Level : 0;
            w.WriteLine(L.F("{0}, Stufe {1}", name, level));
            if (level > GameMaxLevel)
                w.WriteLine("  " + L.F("Hinweis: Über der Höchststufe des Grundspiels ({0}). Vermutlich hebt eine Mod oder eine Servereinstellung die Grenze an; ohne sie kann sich die Stufe ändern.", GameMaxLevel));

            if (save.AttributePoints.TryGetValue(id, out var p))
                w.WriteLine("  " + L.F("Attributpunkte: {0} gesamt, {1} verteilt, {2} frei",
                    p.GetValueOrDefault("AttributePointsTotal"), p.GetValueOrDefault("AttributePointsDistributed"), p.GetValueOrDefault("AttributePointsUndistributed")));

            var learned = save.ItemsByOwner.TryGetValue(id, out var inv) ? inv.Where(i => i.InvType == 6).ToList() : [];
            w.WriteLine("  " + L.F("Erlernte Talente und Rezepte: {0}", learned.Count));
            if (origin.CanTellGameItems)
                foreach (var g in learned.Select(i => origin.ModTag(i.TemplateId)).OfType<string>().GroupBy(m => m).OrderByDescending(g => g.Count()))
                    w.WriteLine("    " + L.F("aus {0}: {1}", g.Key, g.Count()));

            var foreign = save.CharacterComponents.TryGetValue(id, out var comps)
                ? comps.Select(c => (Component: c, Origin: ComponentOrigin(c))).Where(c => c.Origin != null).OrderBy(c => c.Component).ToList()
                : [];
            if (foreign.Count > 0)
                w.WriteLine("  " + L.F("Daten von Komponenten, die nicht zum Grundspiel gehören: {0}",
                    string.Join(", ", foreign.Select(c => $"{c.Component} ({c.Origin})"))));
            w.WriteLine();
        }
    }

    // ---------- Sonderinventare ----------

    /// <summary>
    /// Sonderinventare von Spielerfiguren (alles außer Inventar, Ausrüstung, Schnellleiste und Erlerntem):
    /// je Typ, woher die Einträge stammen – aus dem Spiel, aus einer geprüften Mod (mit Tabelle) oder
    /// unbekannt. Unbekannte Typen fasst die Bereinigung nie an.
    /// </summary>
    void WriteSpecialInventories()
    {
        var special = save.Items.Where(i => save.Characters.ContainsKey(i.OwnerId) && !InventoryKinds.IsItems(i.InvType) && i.InvType != 6).ToList();
        if (special.Count == 0) return;
        var modOf = new Dictionary<long, ModPackage>();
        foreach (var m in knownMods ?? mods)
            foreach (var id in m.NewIds) modOf.TryAdd(id, m);

        Heading(L.T("Sonderinventare von Spielerfiguren"));
        w.WriteLine(L.T("Neben Inventar, Ausrüstung, Schnellleiste und Erlerntem führt das Spiel weitere Listen je Spielerfigur. Zugeordnet wird über die Datentabellen von Spiel und geprüften Mods."));
        w.WriteLine(L.T("Hinweis: Einträge in Inventaren unbekannter Bedeutung entfernt die Bereinigung nie, auch wenn sie zu einer entfernten Mod gehören."));
        w.WriteLine();
        foreach (var type in special.GroupBy(i => i.InvType).OrderBy(g => g.Key))
        {
            w.WriteLine(L.F("{0}: {1} Einträge, {2} verschiedene IDs, bei {3} Figuren", InventoryKinds.Name(true, type.Key), type.Count(),
                type.Select(i => i.TemplateId).Distinct().Count(), type.Select(i => i.OwnerId).Distinct().Count()));
            int fromGame = type.Count(i => game != null && game.RowIds.Contains(i.TemplateId));
            if (fromGame > 0) w.WriteLine("  " + L.F("aus dem Spiel: {0}", fromGame));
            foreach (var byMod in type.Where(i => modOf.ContainsKey(i.TemplateId)).GroupBy(i => modOf[i.TemplateId]).OrderBy(g => g.Key.Name))
            {
                var tables = byMod.Select(i => byMod.Key.IdTables.GetValueOrDefault(i.TemplateId)).Where(t => t != null).Distinct().Take(3);
                w.WriteLine("  " + L.F("aus der Mod {0}: {1} (Tabelle {2})", byMod.Key.Name, byMod.Count(), string.Join(", ", tables)));
            }
            var unknown = type.Where(i => !modOf.ContainsKey(i.TemplateId) && !(game != null && game.RowIds.Contains(i.TemplateId))).ToList();
            if (unknown.Count > 0)
            {
                w.WriteLine("  " + L.F("Herkunft unbekannt: {0}", unknown.Count) + (game == null ? " " + L.T("(ohne Spieldateien nicht prüfbar)") : ""));
                var ids = unknown.GroupBy(i => i.TemplateId).OrderBy(g => g.Key).ToList();
                foreach (var g in ids.Take(20))
                    w.WriteLine("    " + L.F("{0}: {1}× bei {2}", _names.Label(g.Key), g.Count(),
                        string.Join(", ", g.Select(i => save.Characters.TryGetValue(i.OwnerId, out var n) ? n : save.DescribeInventoryOwner(i.OwnerId)).Distinct().Take(4))));
                if (ids.Count > 20) w.WriteLine("    " + L.F("… und {0} weitere IDs", ids.Count - 20));
            }
            w.WriteLine();
        }
    }

    // ---------- Items ohne bekannte Herkunft ----------

    void WriteUnknownItems()
    {
        if (game == null) return;
        var modIds = new HashSet<long>((knownMods ?? mods).SelectMany(m => m.NewIds));
        var unknown = save.Items
            .Where(IsPhysicalItem)
            .Where(i => i.TemplateId > 0 && !game.ItemIds.Contains(i.TemplateId) && !modIds.Contains(i.TemplateId))
            .ToList();
        if (unknown.Count == 0) return;

        Heading(L.T("Items ohne bekannte Herkunft"));
        w.WriteLine(L.F("{0} Items im Spielstand gehören weder zum Spiel noch zu einer der geprüften Mods.", unknown.Count));
        w.WriteLine(L.T("Hinweis: Sie stammen aus anderen aktiven Mods oder aus Mods, die schon entfernt wurden."));
        w.WriteLine(L.T("Wer die übrigen Mod-Dateien mit angibt, sieht hier nur noch die Reste entfernter Mods."));
        w.WriteLine();
        foreach (var g in unknown.GroupBy(i => i.TemplateId).OrderBy(g => g.Key).Take(40))
            w.WriteLine($"  {_names.Label(g.Key)}: {g.Count()}×");
        int rest = unknown.Select(i => i.TemplateId).Distinct().Count() - 40;
        if (rest > 0) w.WriteLine("  " + L.F("… und {0} weitere IDs", rest));
        w.WriteLine();
    }

    /// <summary>Gegenstände, keine erlernten Rezepte o. ä.: alles in Behältern, bei Spielfiguren nur Inventar, Ausrüstung und Schnellleiste.</summary>
    bool IsPhysicalItem(InventoryItem i) => !save.Characters.ContainsKey(i.OwnerId) || InventoryKinds.IsItems(i.InvType);

    string InventoryName(long ownerId, long invType) => InventoryKinds.Name(save.Characters.ContainsKey(ownerId), invType);

    // ---------- Hilfen ----------

    void Heading(string text)
    {
        w.WriteLine(new string('=', Math.Max(40, text.Length)));
        w.WriteLine(text);
        w.WriteLine(new string('=', Math.Max(40, text.Length)));
        w.WriteLine();
    }

    void Sub(string text)
    {
        w.WriteLine(text);
        w.WriteLine(new string('-', text.Length));
    }
}
