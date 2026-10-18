namespace ConanModCheck;

/// <summary>
/// Einträge im Spielstand, deren Besitzer nicht mehr existiert. Unterschieden wird zwischen
/// Resten, die sich gefahrlos entfernen lassen, und solchen, die nur angezeigt werden.
/// </summary>
sealed class Orphans
{
    /// <summary>
    /// Items, deren Besitzer weder Spielfigur, Clan noch gespeichertes Objekt ist. Typisch sind
    /// Beutesäcke und Leichen: Das Objekt verschwindet beim Neustart, der Inhalt bleibt liegen.
    /// Das Spiel kann diese Items nie wieder anzeigen.
    /// </summary>
    public List<InventoryItem> Items { get; } = [];

    /// <summary>Einträge in mod_controllers ohne zugehöriges Objekt.</summary>
    public List<long> ModControllerIds { get; } = [];

    /// <summary>
    /// Figuren, von denen nur noch Werte gespeichert sind (ID → Anzahl). Das können getötete
    /// Thralls sein, aber auch Handwerker, die in eine Werkbank eingesetzt wurden – deren Werte
    /// leben unter der alten ID weiter. Deshalb nur zur Information, nie zum Entfernen.
    /// </summary>
    public Dictionary<long, int> FiguresWithStatsOnly { get; } = [];

    public bool HasRemovable => Items.Count > 0 || ModControllerIds.Count > 0;

    public static Orphans Find(SaveGame save)
    {
        var o = new Orphans();
        bool Exists(long id) => save.Characters.ContainsKey(id) || save.Objects.ContainsKey(id) || save.Guilds.ContainsKey(id);

        o.Items.AddRange(save.Items.Where(i => !Exists(i.OwnerId)));
        o.ModControllerIds.AddRange(save.ModControllerIds.Where(id => !save.Objects.ContainsKey(id)).Distinct());
        foreach (var (id, count) in save.StatsByFigure)
            if (!Exists(id)) o.FiguresWithStatsOnly[id] = count;
        return o;
    }

    public void Write(TextWriter w, Func<long, string>? itemName = null)
    {
        itemName ??= id => id.ToString();
        if (Items.Count == 0 && ModControllerIds.Count == 0 && FiguresWithStatsOnly.Count == 0)
        {
            w.WriteLine("Keine Einträge ohne Besitzer gefunden.");
            w.WriteLine();
            return;
        }

        if (Items.Count > 0)
        {
            w.WriteLine($"{Items.Count} Items liegen in {Items.Select(i => i.OwnerId).Distinct().Count()} Behältern, die es nicht mehr gibt.");
            w.WriteLine("Hinweis: Meist sind das Beutesäcke oder Leichen, die beim Neustart verschwunden sind. Das Spiel");
            w.WriteLine("kann diese Items nicht mehr anzeigen, sie lassen sich gefahrlos entfernen.");
            foreach (var g in Items.GroupBy(i => i.OwnerId).OrderBy(g => g.Key))
                w.WriteLine($"  Behälter {g.Key}: {string.Join(", ", g.OrderBy(i => i.ItemId).Select(i => itemName(i.TemplateId)))}");
            w.WriteLine();
        }
        if (ModControllerIds.Count > 0)
        {
            w.WriteLine($"{ModControllerIds.Count} Einträge der Mod-Steuerung ohne zugehöriges Objekt (harmlos, lassen sich entfernen).");
            w.WriteLine();
        }
        if (FiguresWithStatsOnly.Count > 0)
        {
            w.WriteLine($"Von {FiguresWithStatsOnly.Count} Figuren sind nur noch Werte gespeichert ({FiguresWithStatsOnly.Values.Sum()} Einträge).");
            w.WriteLine("Hinweis: Das sind meist getötete Thralls oder Tiere, aber auch Handwerker, die in eine Werkbank");
            w.WriteLine("eingesetzt wurden. Diese Einträge werden deshalb nur angezeigt und nie entfernt.");
            w.WriteLine();
        }
    }
}
