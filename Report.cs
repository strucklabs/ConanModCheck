namespace ConanModCheck;

sealed class FolderStat(string folder)
{
    public string Folder { get; } = folder;
    public int Objects { get; set; }
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
        Heading("Reste ohne Besitzer");
        Orphans.Find(save).Write(w, _names.Label);
        WriteUnknownItems();
    }

    // ---------- Übersicht ----------

    void WriteOverview()
    {
        Heading("Übersicht: Mod-Inhalte im Spielstand");
        var folders = FolderStats(save);

        if (folders.Count == 0) { w.WriteLine("Im Spielstand gibt es keine Objekte aus Mods."); w.WriteLine(); return; }

        w.WriteLine("Je Mod-Ordner: platzierte Objekte (inkl. Mod-Steuerung), Bauteile, Objekte mit Mod-Daten, aktive Effekte.");
        w.WriteLine("Items lassen sich nur zuordnen, wenn die Mod-Datei mit angegeben wird.");
        w.WriteLine();
        int width = Math.Max(10, folders.Max(f => f.Folder.Length));
        w.WriteLine($"  {"Ordner".PadRight(width)}  Objekte  Bauteile  Mod-Daten  Effekte  geprüfte Mod");
        foreach (var e in folders)
        {
            var mod = mods.FirstOrDefault(m => m.HasFile && m.ModFolders.Contains(e.Folder))?.Name ?? "";
            w.WriteLine($"  {e.Folder.PadRight(width)}  {e.Objects,7}  {e.Pieces,8}  {e.Props,9}  {e.Buffs,7}  {mod}");
        }
        w.WriteLine();
    }

    /// <summary>Je Mod-Ordner im Spielstand: Objekte, Bauteile, Objekte mit Mod-Daten, aktive Effekte.</summary>
    public static List<FolderStat> FolderStats(SaveGame save)
    {
        var folders = new SortedDictionary<string, FolderStat>(StringComparer.OrdinalIgnoreCase);
        FolderStat Get(string f) => folders.TryGetValue(f, out var e) ? e : folders[f] = new FolderStat(f);
        foreach (var o in save.Objects.Values)
            if (ModPaths.FolderOf(o.Class) is { } f) Get(f).Objects++;
        foreach (var p in save.Pieces.Values.SelectMany(l => l))
            if (ModPaths.FolderOf(p.Class) is { } f) Get(f).Pieces++;
        foreach (var (f, objs) in save.PropertyRefs)
            Get(f).Props = objs.Count;
        foreach (var b in save.Buffs)
            if (ModPaths.FolderOf(b.Path) is { } f) Get(f).Buffs++;
        return folders.Values.ToList();
    }

    // ---------- Einzelne Mod ----------

    void WriteMod(ModPackage mod)
    {
        Heading($"Mod: {mod.Name}");
        w.WriteLine($"Datei: {mod.Source}");
        w.WriteLine($"Mod-Ordner: {(mod.ModFolders.Count > 0 ? string.Join(", ", mod.ModFolders.Order()) : "keiner")}");
        w.WriteLine();

        var findings = new List<string>();  // Was beim Entfernen verloren geht oder sich ändert
        var warnings = new List<string>();  // Wo Vorsicht nötig ist

        WriteModFile(mod, findings, warnings);
        WriteWorldObjects(mod, findings, warnings);
        WriteBuildingPieces(mod, findings, warnings);
        WriteItems(mod, findings);
        WriteOtherReferences(mod, findings);

        Sub("Einschätzung");
        if (warnings.Count == 0 && findings.Count == 0)
            w.WriteLine("  Im Spielstand steckt von dieser Mod höchstens ihre Steuerung. Sie lässt sich gefahrlos entfernen.");
        else
        {
            if (warnings.Count > 0)
            {
                w.WriteLine("  Vorsicht:");
                foreach (var x in warnings) w.WriteLine($"   ! {x}");
            }
            if (findings.Count > 0)
            {
                w.WriteLine("  Beim Entfernen der Mod:");
                foreach (var x in findings) w.WriteLine($"   - {x}");
            }
            if (warnings.Count == 0)
                w.WriteLine("  Nichts davon gefährdet andere Bauten oder Gegenstände. Die Mod lässt sich entfernen, wenn der Verlust in Ordnung ist.");
        }
        w.WriteLine();
    }

    void WriteModFile(ModPackage mod, List<string> findings, List<string> warnings)
    {
        Sub("Inhalt der Mod-Datei");
        if (!mod.HasFile)
        {
            w.WriteLine("  Keine Mod-Datei angegeben, Items dieser Mod lassen sich deshalb nicht erkennen.");
            w.WriteLine();
            return;
        }
        if (mod.Tables.Count == 0) w.WriteLine("  Keine Datentabellen mit Item-, Rezept- oder Talent-IDs.");
        foreach (var t in mod.Tables)
        {
            var kind = t.IsOverride ? "ersetzt Spieltabelle" : "eigene Tabelle";
            w.WriteLine($"  {t.Path} ({kind}, {t.Rows.Count} Einträge)");
            if (t.MissingGameRows.Count > 0)
            {
                var inSave = save.Items.Count(i => t.MissingGameRows.Contains(i.TemplateId));
                w.WriteLine($"    Veraltet: Dem aktuellen Spiel gegenüber fehlen {t.MissingGameRows.Count} Einträge.");
                warnings.Add($"Die Mod ersetzt {Path.GetFileName(t.Path)} durch eine veraltete Kopie. Solange sie aktiv ist, fehlen {t.MissingGameRows.Count} Einträge des aktuellen Spiels"
                    + (inSave > 0 ? $"; {inSave} davon liegen im Spielstand und können verschwinden." : " (keiner davon liegt im Spielstand)."));
            }
        }
        if (game == null)
            w.WriteLine("  Ohne Spieldateien lässt sich nicht trennen, welche IDs neu sind und welche die Mod nur verändert.");
        else
        {
            w.WriteLine($"  Neue IDs durch die Mod: {mod.NewIds.Count}");
            w.WriteLine($"  Veränderte IDs aus dem Spiel: {mod.ChangedGameIds.Count}");
            if (mod.ChangedGameIds.Count > 0)
                findings.Add($"{mod.ChangedGameIds.Count} Items, Rezepte oder Talente des Spiels bekommen wieder ihre ursprünglichen Werte.");
        }

        var otherOverrides = mod.OverriddenGameFiles.Where(p => !mod.Tables.Any(t => t.Path.Equals(p, StringComparison.OrdinalIgnoreCase))).ToList();
        if (otherOverrides.Count > 0)
        {
            w.WriteLine($"  Ersetzt {otherOverrides.Count} weitere Spieldatei(en):");
            foreach (var p in otherOverrides.Take(15)) w.WriteLine($"    {p}");
            if (otherOverrides.Count > 15) w.WriteLine($"    … und {otherOverrides.Count - 15} weitere");
        }
        if (mod.AddedOutsideModFolder.Count > 0)
            w.WriteLine($"  Bringt {mod.AddedOutsideModFolder.Count} Datei(en) außerhalb des Mod-Ordners mit.");
        w.WriteLine();
    }

    void WriteWorldObjects(ModPackage mod, List<string> findings, List<string> warnings)
    {
        Sub("Objekte in der Welt");
        var objects = save.Objects.Values.Where(o => ModPaths.BelongsTo(mod, o.Class) && !save.Pieces.ContainsKey(o.Id)).ToList();
        var controllers = objects.Where(o => ModPaths.IsController(o.Class)).ToList();
        var placed = objects.Except(controllers).ToList();

        if (controllers.Count > 0)
            w.WriteLine($"  Mod-Steuerung: {controllers.Count} ({string.Join(", ", controllers.Select(c => SaveGame.ShortClass(c.Class)))}), wird ohne die Mod nicht mehr geladen.");
        if (placed.Count == 0) { w.WriteLine("  Keine platzierten Objekte."); w.WriteLine(); return; }

        int lostGameItems = 0;
        foreach (var o in placed.OrderBy(o => o.Class))
        {
            string who = save.BuildingOwner.TryGetValue(o.Id, out var b) ? save.OwnerName(b)
                       : save.FollowerOwner.TryGetValue(o.Id, out var f) ? save.OwnerName(f) : "ohne Besitzer";
            w.WriteLine($"  {SaveGame.ShortClass(o.Class)} (ID {o.Id}), {who}, bei {SaveGame.Coords(o)}");
            if (save.ItemsByOwner.TryGetValue(o.Id, out var inv) && inv.Count > 0)
            {
                int fromGame = game == null ? 0 : inv.Count(i => game.ItemIds.Contains(i.TemplateId));
                w.WriteLine($"    Inhalt: {inv.Count} Einträge" + (game != null ? $", davon {fromGame} Items aus dem Spiel" : ""));
                lostGameItems += fromGame;
            }
        }
        findings.Add($"{placed.Count} platzierte Objekte verschwinden.");
        if (lostGameItems > 0)
            warnings.Add($"In Objekten der Mod liegen {lostGameItems} Items aus dem Spiel. Sie gehen mit verloren, wenn sie vorher nicht ausgeräumt werden.");
        w.WriteLine();
    }

    void WriteBuildingPieces(ModPackage mod, List<string> findings, List<string> warnings)
    {
        Sub("Bauteile");
        var buildings = save.Pieces
            .Select(kv => (Id: kv.Key, All: kv.Value, Mod: kv.Value.Where(p => ModPaths.BelongsTo(mod, p.Class)).ToList()))
            .Where(b => b.Mod.Count > 0)
            .OrderBy(b => b.Id)
            .ToList();
        if (buildings.Count == 0) { w.WriteLine("  Keine Bauteile aus dieser Mod."); w.WriteLine(); return; }

        int mixed = 0;
        foreach (var b in buildings)
        {
            string who = save.BuildingOwner.TryGetValue(b.Id, out var owner) ? save.OwnerName(owner) : "ohne Besitzer";
            string where = save.Objects.TryGetValue(b.Id, out var o) ? $" bei {SaveGame.Coords(o)}" : "";
            bool isMixed = b.Mod.Count < b.All.Count;
            if (isMixed) mixed++;
            w.WriteLine($"  Gebäude {b.Id}, {who}{where}: {b.Mod.Count} von {b.All.Count} Bauteilen aus der Mod" + (isMixed ? "  ← gemischt" : ""));
            foreach (var g in b.Mod.GroupBy(p => SaveGame.ShortClass(p.Class)).OrderByDescending(g => g.Count()).Take(6))
                w.WriteLine($"    {g.Count(),4} × {g.Key}");
        }
        findings.Add($"{buildings.Sum(b => b.Mod.Count)} Bauteile in {buildings.Count} Gebäuden verschwinden.");
        if (mixed > 0)
            warnings.Add($"{mixed} Gebäude bestehen teils aus Mod-Bauteilen, teils aus Bauteilen des Spiels. Fehlen die Mod-Teile, kann der Rest an Stabilität verlieren und einstürzen.");
        w.WriteLine();
    }

    void WriteItems(ModPackage mod, List<string> findings)
    {
        Sub("Items, Rezepte und Talente");
        if (!mod.HasFile) { w.WriteLine("  Ohne Mod-Datei nicht prüfbar."); w.WriteLine(); return; }
        if (mod.NewIds.Count == 0)
        {
            w.WriteLine(game == null
                ? "  Ohne Spieldateien nicht prüfbar."
                : "  Die Mod bringt keine eigenen IDs mit, im Spielstand kann also nichts davon liegen.");
            w.WriteLine();
            return;
        }

        var hits = save.Items.Where(i => mod.NewIds.Contains(i.TemplateId)).ToList();
        if (hits.Count == 0) { w.WriteLine("  Keine Einträge im Spielstand."); w.WriteLine(); return; }

        foreach (var group in hits.GroupBy(i => i.OwnerId).OrderBy(g => save.DescribeInventoryOwner(g.Key)))
        {
            w.WriteLine($"  {save.DescribeInventoryOwner(group.Key)}:");
            foreach (var byType in group.GroupBy(i => i.InvType).OrderBy(g => g.Key))
            {
                var ids = byType.GroupBy(i => i.TemplateId).Select(g => g.Count() > 1 ? $"{_names.Label(g.Key)} ×{g.Count()}" : _names.Label(g.Key));
                w.WriteLine($"    {InventoryName(group.Key, byType.Key)}: {string.Join(", ", ids)}");
            }
        }
        int learned = hits.Count(i => save.Characters.ContainsKey(i.OwnerId) && i.InvType == 6);
        int physical = hits.Count - learned;
        if (physical > 0) findings.Add($"{physical} Items aus der Mod verschwinden aus Inventaren und Behältern.");
        if (learned > 0) findings.Add($"{learned} erlernte Rezepte oder Talente der Mod verfallen.");
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

        Sub("Weitere Verweise");
        foreach (var g in buffs.GroupBy(b => b.CharId))
            w.WriteLine($"  Aktive Effekte bei {save.OwnerName(g.Key)}: {string.Join(", ", g.Select(b => SaveGame.ShortClass(b.Path)).Distinct())}");
        foreach (var (id, names) in props.OrderBy(p => p.Key))
        {
            var what = save.Objects.TryGetValue(id, out var o) ? save.DescribeInventoryOwner(id)
                     : save.Characters.ContainsKey(id) ? save.OwnerName(id) : $"Objekt {id}";
            w.WriteLine($"  Mod-Daten in {what}: {string.Join(", ", names.Order())}");
        }
        if (buffs.Count > 0) findings.Add($"{buffs.Count} aktive Effekte der Mod enden.");
        if (props.Count > 0) findings.Add($"{props.Count} Objekte oder Figuren aus dem Spiel tragen Daten der Mod (z. B. Einstellungen), die dann ungenutzt bleiben.");
        w.WriteLine();
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

        Heading("Items ohne bekannte Herkunft");
        w.WriteLine($"{unknown.Count} Items im Spielstand gehören weder zum Spiel noch zu einer der geprüften Mods.");
        w.WriteLine("Sie stammen aus anderen aktiven Mods oder aus Mods, die schon entfernt wurden.");
        w.WriteLine("Wer die übrigen Mod-Dateien mit angibt, sieht hier nur noch die Reste entfernter Mods.");
        w.WriteLine();
        foreach (var g in unknown.GroupBy(i => i.TemplateId).OrderBy(g => g.Key).Take(40))
            w.WriteLine($"  {_names.Label(g.Key)}: {g.Count()}×");
        int rest = unknown.Select(i => i.TemplateId).Distinct().Count() - 40;
        if (rest > 0) w.WriteLine($"  … und {rest} weitere IDs");
        w.WriteLine();
    }

    /// <summary>Gegenstände, keine erlernten Rezepte o. ä.: alles in Behältern, bei Spielfiguren nur Inventar, Ausrüstung und Schnellleiste.</summary>
    bool IsPhysicalItem(InventoryItem i) => !save.Characters.ContainsKey(i.OwnerId) || i.InvType is 0 or 1 or 2;

    string InventoryName(long ownerId, long invType)
    {
        if (!save.Characters.ContainsKey(ownerId)) return invType == 0 ? "Inhalt" : $"Inventar-Typ {invType}";
        return invType switch
        {
            0 => "Inventar",
            1 => "Ausrüstung",
            2 => "Schnellleiste",
            6 => "Erlernt",
            _ => $"Inventar-Typ {invType}",
        };
    }

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
