using System.Globalization;

namespace ConanModCheck;

/// <summary>Welche Items der Baum zeigt.</summary>
enum ItemFilter { All, AllMods, LoadedMods, SelectedMods }

enum OwnKind { Clan, Player, Figure, Container, Buildings, Building, Section, Item, Group }

/// <summary>Ein Knoten im Baum „Wer hat was“.</summary>
sealed class OwnNode(OwnKind kind, string text)
{
    public OwnKind Kind { get; } = kind;
    public string Text { get; set; } = text;
    public List<OwnNode> Children { get; } = [];

    /// <summary>Position in der Welt, falls es ein Objekt ist (für den Teleport-Befehl).</summary>
    public WorldObject? Location { get; init; }

    /// <summary>Bei Items aus Mods: Name der Mod.</summary>
    public string? ModName { get; init; }

    /// <summary>Summe aller Stückzahlen darunter.</summary>
    public long ItemCount { get; set; }

    public string? TeleportCommand => Location is { } o ? $"TeleportPlayer {o.X:0} {o.Y:0} {o.Z:0}" : null;
}

/// <summary>
/// Baut aus dem Spielstand einen Baum: Clan → Spieler → Inventar, Begleiter, Behälter, Gebäude.
/// Mit Suchbegriff bleiben nur Items, deren Name ihn enthält, samt dem Weg dorthin; mit
/// „nur Mods“ nur Items aus Mods. Items aus Mods tragen den Namen der Mod in eckigen Klammern.
/// </summary>
sealed class Ownership
{
    // Tausenderpunkte wie im Deutschen, unabhängig von den Systemeinstellungen
    static NumberFormatInfo Numbers => L.Numbers;

    readonly SaveGame _save;
    readonly ItemNames _names;
    readonly string? _filter;
    readonly ItemOrigin? _origin;
    readonly ItemFilter _mode;
    readonly HashSet<string> _selectedMods;

    /// <summary>Es wird gefiltert: leere Spieler, Begleiter und Gebäude ausblenden.</summary>
    bool Filtering => _filter != null || _mode != ItemFilter.All;

    Ownership(SaveGame save, ItemNames names, string? filter, ItemOrigin? origin, ItemFilter mode, HashSet<string>? selectedMods)
    {
        _save = save;
        _names = names;
        _filter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        _origin = origin;
        _mode = origin == null ? ItemFilter.All : mode;
        _selectedMods = selectedMods ?? [];
    }

    public static List<OwnNode> Build(SaveGame save, ItemNames names, string? filter = null, ItemOrigin? origin = null,
        ItemFilter mode = ItemFilter.All, HashSet<string>? selectedMods = null) =>
        new Ownership(save, names, filter, origin, mode, selectedMods).Build();

    bool PassesFilter(long id, string? mod) => _mode switch
    {
        ItemFilter.AllMods => mod != null,
        ItemFilter.LoadedMods => _origin!.IsFromLoadedMod(id),
        ItemFilter.SelectedMods => mod != null && _selectedMods.Contains(mod),
        _ => true,
    };

    List<OwnNode> Build()
    {
        var roots = new List<OwnNode>();
        var shownFigures = new HashSet<long>();

        foreach (var (guildId, guildName) in _save.Guilds.OrderBy(g => g.Value))
        {
            var clan = new OwnNode(OwnKind.Clan, L.F("Clan „{0}“", guildName));
            foreach (var (charId, _) in _save.Characters.Where(c => _save.CharacterGuild.GetValueOrDefault(c.Key) == guildId).OrderBy(c => c.Value))
                Add(clan, PlayerNode(charId, shownFigures));
            Add(clan, PossessionsNode(L.T("Clan-Besitz"), guildId, shownFigures));
            Finish(clan);
            Add(roots, clan);
        }

        var loners = _save.Characters.Where(c => !_save.CharacterGuild.TryGetValue(c.Key, out var g) || !_save.Guilds.ContainsKey(g)).ToList();
        if (loners.Count > 0)
        {
            var group = new OwnNode(OwnKind.Group, L.T("Spieler ohne Clan"));
            foreach (var (charId, _) in loners.OrderBy(c => c.Value))
                Add(group, PlayerNode(charId, shownFigures));
            Finish(group);
            Add(roots, group);
        }

        // Objekte mit Inhalt, die niemandem gehören (Besitzer 0, -1 oder unbekannt)
        var known = new HashSet<long>(_save.Characters.Keys.Concat(_save.Guilds.Keys));
        var unowned = new OwnNode(OwnKind.Group, L.T("Ohne Besitzer"));
        foreach (var o in Containers(id => !known.Contains(id)))
            Add(unowned, ContainerNode(o));
        Finish(unowned);
        Add(roots, unowned);

        return roots;
    }

    OwnNode PlayerNode(long charId, HashSet<long> shownFigures)
    {
        var info = _save.CharacterInfos.GetValueOrDefault(charId);
        var details = new List<string>();
        if (info?.Level > 0) details.Add(L.F("Stufe {0}", info.Level));
        if (info?.LastOnline is { } last) details.Add(L.F("zuletzt online {0:dd.MM.yyyy}", last));
        var node = new OwnNode(OwnKind.Player, L.F("Spieler „{0}“", _save.Characters[charId]) + (details.Count > 0 ? $" – {string.Join(", ", details)}" : ""));

        foreach (var section in InventorySections(charId, isCharacter: true)) Add(node, section);
        var own = PossessionsNode(L.T("Eigener Besitz"), charId, shownFigures);
        foreach (var c in own.Children) Add(node, c);
        Finish(node);
        return node;
    }

    /// <summary>Begleiter, Behälter und Gebäude eines Spielers oder Clans.</summary>
    OwnNode PossessionsNode(string title, long ownerId, HashSet<long> shownFigures)
    {
        var node = new OwnNode(OwnKind.Group, title);

        var figures = _save.Objects.Values
            .Where(o => (_save.FollowerOwner.TryGetValue(o.Id, out var f) && f == ownerId)
                     || (!_save.FollowerOwner.ContainsKey(o.Id) && _save.FigureOwner.TryGetValue(o.Id, out var g) && g == ownerId))
            .Where(o => shownFigures.Add(o.Id))
            .OrderBy(FigureName)
            .ToList();
        if (figures.Count > 0)
        {
            var group = new OwnNode(OwnKind.Group, L.T("Thralls und Tiere"));
            foreach (var f in figures)
            {
                var fn = new OwnNode(OwnKind.Figure, FigureName(f)) { Location = f };
                foreach (var s in InventorySections(f.Id, isCharacter: true)) Add(fn, s);
                Finish(fn);
                // Auch Begleiter ohne Inventar anzeigen, solange nicht gesucht wird
                if (!Filtering || fn.Children.Count > 0) group.Children.Add(fn);
            }
            Finish(group);
            Add(node, group);
        }

        var containers = new OwnNode(OwnKind.Group, L.T("Behälter und Stationen"));
        foreach (var o in Containers(id => id == ownerId))
            Add(containers, ContainerNode(o));
        containers.Children.Sort((a, b) => b.ItemCount.CompareTo(a.ItemCount));
        Finish(containers);
        Add(node, containers);

        // Items, die direkt dem Clan gehören
        if (_save.Guilds.ContainsKey(ownerId))
            foreach (var s in InventorySections(ownerId, isCharacter: false, clanStore: true)) Add(node, s);

        if (!Filtering)
        {
            var buildings = _save.Pieces.Keys
                .Where(id => _save.BuildingOwner.TryGetValue(id, out var b) && b == ownerId)
                .Select(id => (Id: id, Count: _save.Pieces[id].Count))
                .OrderByDescending(b => b.Count)
                .ToList();
            if (buildings.Count > 0)
            {
                var bn = new OwnNode(OwnKind.Buildings, L.F("Gebäude: {0} mit {1:N0} Bauteilen", buildings.Count, buildings.Sum(b => b.Count)));
                foreach (var b in buildings)
                    bn.Children.Add(new OwnNode(OwnKind.Building, L.F("Gebäude {0} – {1} Bauteile", b.Id, b.Count))
                    { Location = _save.Objects.GetValueOrDefault(b.Id) });
                node.Children.Add(bn);
            }
        }
        Finish(node);
        return node;
    }

    /// <summary>Platzierte Objekte mit Inhalt (keine Gebäude, keine Figuren), deren Besitzer passt.</summary>
    IEnumerable<WorldObject> Containers(Func<long, bool> ownerMatches) =>
        _save.Objects.Values
            .Where(o => !_save.Pieces.ContainsKey(o.Id) && !_save.Characters.ContainsKey(o.Id)
                     && !_save.FollowerOwner.ContainsKey(o.Id) && !_save.FigureOwner.ContainsKey(o.Id)
                     && _save.ItemsByOwner.ContainsKey(o.Id)
                     && ownerMatches(_save.BuildingOwner.TryGetValue(o.Id, out var b) ? b : 0));

    OwnNode ContainerNode(WorldObject o)
    {
        var node = new OwnNode(OwnKind.Container, $"{PrettyClass(o.Class)} (ID {o.Id})") { Location = o };
        foreach (var s in InventorySections(o.Id, isCharacter: false)) Add(node, s);
        Finish(node);
        return node;
    }

    IEnumerable<OwnNode> InventorySections(long ownerId, bool isCharacter, bool clanStore = false)
    {
        if (!_save.ItemsByOwner.TryGetValue(ownerId, out var items)) yield break;
        foreach (var byType in items.GroupBy(i => i.InvType).OrderBy(g => g.Key))
        {
            // Erlernte Rezepte, Gesten u. ä. sind keine Gegenstände
            if (isCharacter && byType.Key is 3 or 6 or 7) continue;
            var section = new OwnNode(OwnKind.Section, SectionName(byType.Key, isCharacter, clanStore));
            foreach (var g in byType.Where(i => i.TemplateId > 0).GroupBy(i => i.TemplateId)
                         .Select(g => (Id: g.Key, Qty: g.Sum(i => (long)i.Quantity)))
                         .OrderBy(g => _names.Name(g.Id) ?? "￿" + g.Id))
            {
                var label = _names.Label(g.Id);
                var mod = _origin?.ModTag(g.Id);
                if (!PassesFilter(g.Id, mod)) continue;
                if (_filter != null && !label.Contains(_filter, StringComparison.CurrentCultureIgnoreCase)
                    && !(mod?.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) ?? false)) continue;
                var text = g.Qty > 1 ? $"{label} × {g.Qty.ToString("N0", Numbers)}" : label;
                if (mod != null) text += $"   [{mod}]";
                section.Children.Add(new OwnNode(OwnKind.Item, text) { ItemCount = g.Qty, ModName = mod });
            }
            Finish(section);
            if (section.Children.Count > 0) yield return section;
        }
    }

    static string SectionName(long invType, bool isCharacter, bool clanStore)
    {
        if (clanStore) return L.T("Clan-Lager");
        if (isCharacter)
            return invType switch { 0 => L.T("Inventar"), 1 => L.T("Ausrüstung"), 2 => L.T("Schnellleiste"), _ => L.F("Fach {0}", invType) };
        return invType switch { 4 => L.T("Inhalt"), 0 => L.T("Inventar"), 1 => L.T("Ausrüstung"), _ => L.F("Fach {0}", invType) };
    }

    string FigureName(WorldObject o)
    {
        bool thrall = o.Class.Contains("Humanoid", StringComparison.OrdinalIgnoreCase);
        var kind = thrall ? L.T("Thrall") : L.T("Tier");
        if (_save.FigureNames.TryGetValue(o.Id, out var n)) return $"{n} ({kind}, ID {o.Id})";
        return thrall ? L.F("Thrall ohne Namen (ID {0})", o.Id) : $"{PrettyClass(o.Class)} ({kind}, ID {o.Id})";
    }

    /// <summary>"/Game/…/BP_PL_Chest_Large.BP_PL_Chest_Large_C" → "Chest Large"</summary>
    public static string PrettyClass(string cls)
    {
        var s = SaveGame.ShortClass(cls);
        foreach (var prefix in new[] { "BP_PL_", "BP_NPC_", "BP_" })
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { s = s[prefix.Length..]; break; }
        return s.Replace('_', ' ').Trim();
    }

    /// <summary>Summiert die Stückzahlen und hängt sie an den Text.</summary>
    static void Finish(OwnNode node)
    {
        node.ItemCount = node.Kind == OwnKind.Item ? node.ItemCount : node.Children.Sum(c => c.ItemCount);
        if (node.Kind is OwnKind.Section or OwnKind.Container or OwnKind.Group or OwnKind.Figure && node.ItemCount > 0)
            node.Text += $"  [{node.ItemCount.ToString("N0", Numbers)}]";
    }

    /// <summary>Fügt einen Knoten nur hinzu, wenn er etwas enthält.</summary>
    void Add(OwnNode parent, OwnNode child) => Add(parent.Children, child);

    void Add(List<OwnNode> list, OwnNode child)
    {
        bool keep = child.Kind switch
        {
            OwnKind.Item or OwnKind.Building => true,
            OwnKind.Player => !Filtering || child.Children.Count > 0,
            _ => child.Children.Count > 0,
        };
        if (keep) list.Add(child);
    }

    /// <summary>Den Baum als eingerückten Text, z. B. für die Konsole oder zum Kopieren.</summary>
    public static void WriteText(TextWriter w, IEnumerable<OwnNode> nodes, int depth = 0)
    {
        foreach (var n in nodes)
        {
            w.WriteLine(new string(' ', depth * 2) + n.Text + (n.Location != null && n.Kind != OwnKind.Figure ? $"  @ {SaveGame.Coords(n.Location)}" : ""));
            WriteText(w, n.Children, depth + 1);
        }
    }
}
