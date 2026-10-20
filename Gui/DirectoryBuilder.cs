using System.Windows;

namespace ConanModCheck.Gui;

/// <summary>
/// Baut das Verzeichnis: alle Objekte (auch Behälter, die es nicht mehr gibt) und alle Item-Arten,
/// jeweils mit Fundstellen, Besitzern und Koordinaten. Gegenstück zum Besitz-Baum: dort „wer hat
/// was“, hier „was ist wo“. Geordnet nach Herkunft (Grundspiel, je Mod, Sonderknoten), darunter bei
/// Objekten nach Namen; eine Namensgruppe mit nur einem Eintrag entfällt.
/// </summary>
sealed class DirectoryBuilder(SaveGame save, ItemNames names, ItemOrigin origin, Func<string, bool> folderSelected, Func<string, bool> modSelected)
{
    // Sonderknoten der obersten Ebene (Mod-Ordner können keine Steuerzeichen enthalten)
    const string PlayersKey = "\u0001players", GoneKey = "\u0001gone", UnknownKey = "\u0001unknown";

    // ---------- Objekte ----------

    /// <summary>Alle Objekt-Einträge (unterste Ebene der Gruppierung, noch ohne Baum).</summary>
    public List<DirEntry> Objects()
    {
        var list = new List<DirEntry>();
        foreach (var o in save.Objects.Values) list.AddRange(ObjectEntries(o));
        // Spieler ohne Position und Behälter, die es nicht mehr gibt, aber noch Items haben
        foreach (var (id, name) in save.Characters.Where(c => !save.Objects.ContainsKey(c.Key)))
            list.Add(PlayerEntry(id, null));
        foreach (var id in save.ItemsByOwner.Keys.Where(id => !save.Objects.ContainsKey(id) && !save.Characters.ContainsKey(id)))
        {
            var text = L.F("Behälter {0} – existiert nicht mehr", id);
            list.Add(new DirEntry(text, ItemsStatus(id), null, () => Contents(id))
                { ObjectId = id, SearchText = $"{id}", Origin = GoneKey, Group = text, IsLeaf = true });
        }
        return list;
    }

    /// <summary>
    /// Eintrag eines Objekts, bei Gebäuden aus mehreren Herkünften zusätzlich Zweiteinträge: Ein gemischtes
    /// Gebäude steht beim Grundspiel und bei jeder Mod, deren Teile darin verbaut sind.
    /// </summary>
    IEnumerable<DirEntry> ObjectEntries(WorldObject o)
    {
        if (save.Characters.ContainsKey(o.Id)) { yield return PlayerEntry(o.Id, o); yield break; }
        if (!save.Pieces.TryGetValue(o.Id, out var pieces)) { yield return ObjectEntry(o, ModPaths.FolderOf(o.Class) ?? "", true); yield break; }
        var folders = pieces.Select(p => ModPaths.FolderOf(p.Class)).OfType<string>()
            .GroupBy(f => f, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        var origins = pieces.Any(p => ModPaths.FolderOf(p.Class) == null) ? folders.Prepend("") : folders;
        bool first = true;
        foreach (var origin in origins)
        {
            yield return ObjectEntry(o, origin, first);
            first = false;
        }
    }

    DirEntry ObjectEntry(WorldObject o, string origin, bool primary)
    {
        var folder = ModPaths.FolderOf(o.Class);
        bool building = save.Pieces.TryGetValue(o.Id, out var pieces);
        string name = building ? L.F("Gebäude {0} – {1} Bauteile", o.Id, pieces!.Count)
            : (save.FigureNames.TryGetValue(o.Id, out var fn) ? fn : Ownership.PrettyClass(o.Class)) + $" (ID {o.Id})";
        long? owner = save.BuildingOwner.TryGetValue(o.Id, out var b) ? b
            : save.FollowerOwner.TryGetValue(o.Id, out var f) ? f
            : save.FigureOwner.TryGetValue(o.Id, out var g) ? g : null;
        if (owner <= 0) owner = null; // Besitzer 0 oder -1 heißt: niemand
        string text = name + (owner is { } ow ? " · " + save.OwnerName(ow) : "") + (folder != null ? $" [{folder}]" : "");

        int own = folder != null ? (folderSelected(folder) ? 2 : 1) : 0;
        if (building)
        {
            var modPieces = pieces!.Select(p => ModPaths.FolderOf(p.Class)).OfType<string>().ToList();
            if (modPieces.Count > 0) own = Math.Max(own, modPieces.Any(folderSelected) ? 2 : 1);
        }
        int status = Math.Max(own, ItemsStatus(o.Id));
        return new DirEntry(text, status, o, () => ObjectDetails(o, folder, owner, pieces))
        {
            ObjectId = o.Id,
            OwnStatus = own > 0,
            SearchText = $"{text} {SaveGame.ShortClass(o.Class)}",
            Origin = origin,
            // Gebäude unter „Gebäude“, Figuren mit eigenem Namen unter diesem, sonst nach Art des Objekts
            Group = building ? L.T("Gebäude") : save.FigureNames.TryGetValue(o.Id, out var gn) ? gn : Ownership.PrettyClass(o.Class),
            Primary = primary,
            IsLeaf = true,
        };
    }

    DirEntry PlayerEntry(long id, WorldObject? o)
    {
        var text = save.OwnerName(id) + $" (ID {id})";
        return new DirEntry(text, ItemsStatus(id), o, () => Contents(id))
            { ObjectId = id, Weight = FontWeights.SemiBold, SearchText = text, Origin = PlayersKey, Group = text, IsLeaf = true };
    }

    IEnumerable<DirEntry> ObjectDetails(WorldObject o, string? folder, long? owner, List<BuildingPiece>? pieces)
    {
        if (owner is { } ow) yield return Info(L.F("Besitzer: {0}", save.OwnerName(ow)));
        if (folder != null) yield return Info(L.F("Aus der Mod: {0}", folder), folderSelected(folder) ? 2 : 1);
        yield return Info(L.F("Klasse: {0}", SaveGame.ShortClass(o.Class)));
        if (pieces != null)
        {
            var risk = Support.AtRisk(pieces, p => ModPaths.FolderOf(p.Class) is { } f && folderSelected(f));
            if (risk.Count > 0) yield return Info(L.F("{0:N0} Teile stehen nur auf Teilen der angehakten Mods und könnten einstürzen (Näherung).", risk.Count), 2);
            foreach (var grp in pieces.GroupBy(p => p.Class).OrderByDescending(g => ModPaths.FolderOf(g.Key) != null).ThenByDescending(g => g.Count()))
            {
                var f = ModPaths.FolderOf(grp.Key);
                yield return new DirEntry($"{grp.Count(),4} × {Ownership.PrettyClass(grp.Key)}" + (f != null ? $" [{f}]" : ""), f == null ? 0 : folderSelected(f) ? 2 : 1)
                    { OwnStatus = true };
            }
        }
        foreach (var e in Contents(o.Id)) yield return e;
    }

    /// <summary>Items einer Figur oder eines Behälters, gruppiert nach Inventarart.</summary>
    IEnumerable<DirEntry> Contents(long ownerId)
    {
        if (!save.ItemsByOwner.TryGetValue(ownerId, out var items)) yield break;
        bool character = save.Characters.ContainsKey(ownerId);
        foreach (var byType in items.GroupBy(i => i.InvType).OrderBy(g => g.Key))
        {
            var list = byType.ToList();
            int status = list.Select(i => ItemStatus(i.TemplateId)).DefaultIfEmpty(0).Max();
            var label = list.Count == 1 ? L.F("{0}: 1 Eintrag", InventoryName(character, byType.Key)) : L.F("{0}: {1:N0} Einträge", InventoryName(character, byType.Key), list.Count);
            yield return new DirEntry(label, status, null,
                () => list.OrderByDescending(i => ItemStatus(i.TemplateId)).ThenBy(i => names.Label(i.TemplateId), StringComparer.CurrentCultureIgnoreCase)
                    .Select(i => new DirEntry(names.Label(i.TemplateId) + (i.Quantity > 1 ? $" ×{i.Quantity}" : "") + ModTag(i.TemplateId), ItemStatus(i.TemplateId))
                        { ItemId = i.TemplateId, OwnStatus = true }))
            { Weight = FontWeights.SemiBold };
        }
    }

    static string InventoryName(bool character, long invType) => InventoryKinds.Name(character, invType);

    // ---------- Items ----------

    /// <summary>Alle Item-Arten (unterste Ebene, noch ohne Baum); Herkunft Grundspiel, geladene Mod oder unbekannt.</summary>
    public List<DirEntry> Items()
    {
        var list = new List<DirEntry>();
        foreach (var g in save.Items.GroupBy(i => i.TemplateId))
        {
            var places = g.ToList();
            int total = places.Sum(i => i.Quantity);
            int count = places.Select(i => i.OwnerId).Distinct().Count();
            string text = names.Label(g.Key) + " – " + (count == 1 ? L.F("{0:N0} Stück an einer Stelle", total) : L.F("{0:N0} Stück an {1:N0} Stellen", total, count)) + ModTag(g.Key);
            string from = origin.IsFromLoadedMod(g.Key) ? origin.ModTag(g.Key)! : origin.ModTag(g.Key) != null || !origin.CanTellGameItems ? UnknownKey : "";
            list.Add(new DirEntry(text, ItemStatus(g.Key), null, () => ItemPlaces(places))
                { ItemId = g.Key, OwnStatus = true, SearchText = text, Origin = from, IsLeaf = true });
        }
        return list;
    }

    // ---------- Baum ----------

    /// <summary>
    /// Ordnet (schon gefilterte) Einträge zum Baum: oben Grundspiel, die Mods alphabetisch, dann Spieler,
    /// verschwundene Behälter und unbekannte Herkunft; darunter bei Objekten Namensgruppen.
    /// </summary>
    public static List<DirEntry> Tree(IEnumerable<DirEntry> leaves, bool items, bool canTellGameItems)
    {
        var tree = new List<DirEntry>();
        foreach (var top in leaves.GroupBy(e => e.Origin, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => OriginOrder(g.Key)).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            var entries = top.ToList();
            var members = items ? Sorted(entries) : NameGroups(entries);
            tree.Add(GroupNode($"{OriginLabel(top.Key, canTellGameItems)} ({entries.Count:N0})", members, FontWeights.SemiBold));
        }
        return tree;
    }

    static List<DirEntry> NameGroups(List<DirEntry> entries)
    {
        var list = new List<DirEntry>();
        foreach (var g in entries.GroupBy(e => e.Group, StringComparer.CurrentCultureIgnoreCase))
        {
            // Gleichnamige Einträge unterscheiden sich nur durch ID und Besitzer: nach ID ordnen (als Zahl)
            var members = g.OrderByDescending(e => e.Status).ThenBy(e => e.ObjectId ?? e.ItemId ?? 0).ThenBy(e => e.Text, StringComparer.CurrentCultureIgnoreCase).ToList();
            list.Add(members.Count == 1 || g.Key.Length == 0 ? members[0] : GroupNode($"{g.Key} ({members.Count:N0})", members, FontWeights.Normal));
            if (g.Key.Length == 0 && members.Count > 1) list.AddRange(members.Skip(1));
        }
        return Sorted(list);
    }

    static DirEntry GroupNode(string text, List<DirEntry> members, FontWeight weight) =>
        new(text, members.Select(m => m.Status).DefaultIfEmpty(0).Max(), null, () => members) { Members = members, Weight = weight };

    static int OriginOrder(string key) => key switch { "" => 0, PlayersKey => 2, GoneKey => 3, UnknownKey => 4, _ => 1 };

    static string OriginLabel(string key, bool canTellGameItems) => key switch
    {
        "" => L.T("Grundspiel"),
        PlayersKey => L.T("Spieler"),
        GoneKey => L.T("Behälter, die es nicht mehr gibt"),
        UnknownKey => canTellGameItems ? L.T("Herkunft unbekannt (Mod-Datei nicht geladen)") : L.T("Spiel oder Mod ohne geladene Datei (ohne Spieldateien nicht unterscheidbar)"),
        _ => key,
    };

    IEnumerable<DirEntry> ItemPlaces(List<InventoryItem> places)
    {
        foreach (var byOwner in places.GroupBy(i => i.OwnerId).OrderBy(g => Where(g.Key), StringComparer.CurrentCultureIgnoreCase))
        {
            int qty = byOwner.Sum(i => i.Quantity);
            bool character = save.Characters.ContainsKey(byOwner.Key);
            var kinds = string.Join(", ", byOwner.Select(i => InventoryName(character, i.InvType)).Distinct());
            yield return new DirEntry($"{Where(byOwner.Key)} – {kinds}" + (qty > 1 ? $" ×{qty}" : ""), ItemsStatus(byOwner.Key), save.Objects.GetValueOrDefault(byOwner.Key))
                { ObjectId = byOwner.Key };
        }
    }

    /// <summary>Wo ein Item liegt: Spieler, Objekt mit Besitzer oder ein Behälter, den es nicht mehr gibt.</summary>
    string Where(long ownerId)
    {
        if (save.Characters.ContainsKey(ownerId)) return save.OwnerName(ownerId);
        if (save.Objects.TryGetValue(ownerId, out var o))
        {
            var name = save.FigureNames.TryGetValue(o.Id, out var fn) ? fn : Ownership.PrettyClass(o.Class);
            long? owner = save.BuildingOwner.TryGetValue(o.Id, out var b) ? b : save.FollowerOwner.TryGetValue(o.Id, out var f) ? f : null;
            return $"{name} (ID {o.Id})" + (owner is { } ow and > 0 ? " · " + save.OwnerName(ow) : "");
        }
        return L.F("Behälter {0} – existiert nicht mehr", ownerId);
    }

    // ---------- Hilfen ----------

    string ModTag(long itemId) => origin.ModTag(itemId) is { } m ? $" [{m}]" : "";

    int ItemStatus(long itemId) => origin.ModTag(itemId) is not { } m ? 0 : origin.IsFromLoadedMod(itemId) && modSelected(m) ? 2 : 1;

    /// <summary>Wichtigste Markierung der Items eines Besitzers.</summary>
    int ItemsStatus(long ownerId) => save.ItemsByOwner.TryGetValue(ownerId, out var items)
        ? items.Select(i => ItemStatus(i.TemplateId)).DefaultIfEmpty(0).Max() : 0;

    static DirEntry Info(string text, int status = 0) => new(text, status) { OwnStatus = status > 0 };

    /// <summary>Mod-Inhalte zuerst (angehakte vor übrigen), dann alphabetisch.</summary>
    static List<DirEntry> Sorted(List<DirEntry> list) =>
        list.OrderByDescending(e => e.Status).ThenBy(e => e.Text, StringComparer.CurrentCultureIgnoreCase).ToList();
}
