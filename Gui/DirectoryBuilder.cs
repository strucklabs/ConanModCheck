using System.Windows;

namespace ConanModCheck.Gui;

/// <summary>
/// Baut das Verzeichnis: alle Objekte (auch Behälter, die es nicht mehr gibt) und alle Item-Arten,
/// jeweils mit Fundstellen, Besitzern und Koordinaten. Gegenstück zum Besitz-Baum: dort „wer hat
/// was“, hier „was ist wo“.
/// </summary>
sealed class DirectoryBuilder(SaveGame save, ItemNames names, ItemOrigin origin, Func<string, bool> folderSelected, Func<string, bool> modSelected)
{
    // ---------- Objekte ----------

    public List<DirEntry> Objects()
    {
        var list = new List<DirEntry>();
        foreach (var o in save.Objects.Values) list.Add(ObjectEntry(o));
        // Spieler ohne Position und Behälter, die es nicht mehr gibt, aber noch Items haben
        foreach (var (id, name) in save.Characters.Where(c => !save.Objects.ContainsKey(c.Key)))
            list.Add(PlayerEntry(id, null));
        foreach (var id in save.ItemsByOwner.Keys.Where(id => !save.Objects.ContainsKey(id) && !save.Characters.ContainsKey(id)))
            list.Add(new DirEntry(L.F("Behälter {0} – existiert nicht mehr", id), ItemsStatus(id), null, () => Contents(id))
                { ObjectId = id, SearchText = $"{id}" });
        return Sorted(list);
    }

    DirEntry ObjectEntry(WorldObject o)
    {
        if (save.Characters.ContainsKey(o.Id)) return PlayerEntry(o.Id, o);
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
        };
    }

    DirEntry PlayerEntry(long id, WorldObject? o)
    {
        var text = save.OwnerName(id) + $" (ID {id})";
        return new DirEntry(text, ItemsStatus(id), o, () => Contents(id))
            { ObjectId = id, Weight = FontWeights.SemiBold, SearchText = text };
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

    static string InventoryName(bool character, long invType) => !character
        ? (invType == 4 || invType == 0 ? L.T("Inhalt") : L.F("Inventar-Typ {0}", invType))
        : invType switch
        {
            0 => L.T("Inventar"),
            1 => L.T("Ausrüstung"),
            2 => L.T("Schnellleiste"),
            6 => L.T("Erlernt"),
            _ => L.F("Inventar-Typ {0}", invType),
        };

    // ---------- Items ----------

    public List<DirEntry> Items()
    {
        var list = new List<DirEntry>();
        foreach (var g in save.Items.GroupBy(i => i.TemplateId))
        {
            var places = g.ToList();
            int total = places.Sum(i => i.Quantity);
            int count = places.Select(i => i.OwnerId).Distinct().Count();
            string text = names.Label(g.Key) + " – " + (count == 1 ? L.F("{0:N0} Stück an einer Stelle", total) : L.F("{0:N0} Stück an {1:N0} Stellen", total, count)) + ModTag(g.Key);
            list.Add(new DirEntry(text, ItemStatus(g.Key), null, () => ItemPlaces(places))
                { ItemId = g.Key, OwnStatus = true, SearchText = text });
        }
        return Sorted(list);
    }

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
