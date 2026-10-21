using System.Windows;

namespace ConanModCheck.Gui;

/// <summary>
/// Baut den Reiter „Quests“: Herkunft (Grundspiel, je Mod, unbekannt) → Quest → Figur mit Stand → Aufgaben,
/// dazu „Reise des Verbannten“ je Figur. Nur zur Anzeige. Warnungen rot, Hinweise gelb hinterlegt;
/// Mod-Quests nur mit dem farbigen Kästchen (gelb, rot bei angehakter Mod).
/// </summary>
sealed class QuestTreeBuilder(SaveGame save, QuestCatalog catalog, Func<string, bool> modSelected)
{
    readonly QuestData _data = QuestData.Read(save);

    public List<DirEntry> Build()
    {
        var tree = new List<DirEntry>();
        foreach (var byOrigin in _data.Quests.GroupBy(q => catalog.OfQuest(q.Quest))
                     .OrderBy(g => g.Key.Origin).ThenBy(g => g.Key.Mod, StringComparer.CurrentCultureIgnoreCase))
        {
            var (origin, mod) = byOrigin.Key;
            int status = origin == QuestOrigin.Mod ? (modSelected(mod!) ? 2 : 1) : 0;
            var quests = byOrigin.GroupBy(q => q.Quest, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
                .Select(g => QuestNode(g.Key, g.ToList(), status)).ToList();
            var label = origin switch
            {
                QuestOrigin.Game => L.T("Grundspiel"),
                QuestOrigin.Mod => mod!,
                QuestOrigin.Unknown => L.T("Herkunft unbekannt"),
                _ => L.T("Ohne Spieldateien nicht zuzuordnen"),
            };
            tree.Add(Group($"{label} ({quests.Count:N0})", quests, status, FontWeights.SemiBold, warning: origin == QuestOrigin.Unknown));
        }

        if (_data.Journeys.Count > 0)
        {
            var players = _data.Journeys.OrderBy(j => QuestText.Owner(save, j.OwnerId), StringComparer.CurrentCultureIgnoreCase).Select(JourneyNode).ToList();
            tree.Add(Group(L.T("Reise des Verbannten") + $" ({players.Count:N0})", players, 0, FontWeights.SemiBold));
        }
        if (_data.Unreadable.Count > 0)
        {
            var rows = _data.Unreadable.Select(u => new DirEntry($"{QuestText.Owner(save, u.OwnerId)}: {u.Property}", 0) { IsWarning = true }).ToList();
            tree.Add(Group(L.T("Nicht vollständig lesbar"), rows, 0, FontWeights.SemiBold, warning: true));
        }
        if (tree.Count == 0) tree.Add(new DirEntry(L.T("Im Spielstand sind keine Quests gespeichert."), 0));
        foreach (var top in tree) top.IsExpanded = true;
        return tree;
    }

    DirEntry QuestNode(string quest, List<QuestEntry> entries, int status)
    {
        int active = entries.Count(e => e.Active), done = entries.Count - active;
        var players = entries.OrderByDescending(e => e.Active).ThenBy(e => QuestText.Owner(save, e.OwnerId), StringComparer.CurrentCultureIgnoreCase)
            .Select(e => PlayerNode(e, status)).ToList();
        bool warning = players.Any(p => p.IsWarning), hint = players.Any(p => p.IsHint);
        return Group(L.F("{0} – {1} aktiv, {2} abgeschlossen", quest, active, done), players, status, FontWeights.Normal, warning, hint);
    }

    DirEntry PlayerNode(QuestEntry q, int status)
    {
        var owner = QuestText.Owner(save, q.OwnerId);
        var open = q.Tasks.Where(t => !t.Done).Select(t => t.Name).ToList();
        var text = q.Active
            ? L.F("{0} – aktiv seit {1}, zuletzt geändert {2}, aktuelle Aufgabe: {3}", owner, QuestText.Date(q.Started), QuestText.Date(q.Updated), open.Count == 0 ? L.T("keine") : string.Join(", ", open))
            : L.F("{0} – abgeschlossen am {1}, {2} Aufgaben", owner, QuestText.Date(q.Updated), q.Tasks.Count);
        var notes = catalog.Notes(q, _data, save.CharacterInfos.GetValueOrDefault(q.OwnerId)?.LastOnline).ToList();
        var children = new List<DirEntry>();
        foreach (var (note, warning) in notes) children.Add(new DirEntry(note, 0) { IsWarning = warning, IsHint = !warning });
        foreach (var t in q.Tasks)
        {
            bool unknown = q.Active && catalog.TaskKnown(t.Name) == false;
            children.Add(new DirEntry(t.Done
                ? L.F("✓ {0} ({1} – {2})", t.Name, QuestText.Date(t.Started), QuestText.Date(t.Finished))
                : L.F("▶ {0} (seit {1})", t.Name, QuestText.Date(t.Started)), 0) { IsWarning = unknown });
        }
        return new DirEntry(text, status, null, () => children)
        {
            IsWarning = notes.Any(n => n.Warning),
            IsHint = notes.Any(n => !n.Warning),
            SearchText = $"{text} {string.Join(" ", q.Tasks.Select(t => t.Name))}",
        };
    }

    DirEntry JourneyNode(PlayerJourney j)
    {
        int done = j.Entries.Count(e => e.Completed);
        var text = L.F("{0} – aktiv „{1}“, {2} von {3} abgeschlossen, {4} Story-Stufen", QuestText.Owner(save, j.OwnerId),
            j.Active ?? L.T("keine"), done, j.Entries.Count, j.StoryStages);
        var unknown = catalog.UnknownJourneys(j);
        var children = new List<DirEntry>();
        if (unknown.Count > 0)
            children.Add(new DirEntry(L.F("Reise-Einträge, die es weder im Spiel noch in einer geladenen Mod gibt: {0}", string.Join(", ", unknown)), 0) { IsWarning = true });
        foreach (var e in j.Entries.Where(e => e.Unlocked && !e.Completed && e.Step > 0).OrderByDescending(e => (double)e.Step / Math.Max(1, e.Steps)))
            children.Add(new DirEntry(L.F("In Arbeit: {0} – Schritt {1} von {2}", e.Name, e.Step, e.Steps), 0));
        var completed = j.Entries.Where(e => e.Completed).Select(e => e.Name).ToList();
        if (completed.Count > 0) children.Add(new DirEntry(L.F("Abgeschlossen: {0}", string.Join(", ", completed)), 0));
        return new DirEntry(text, 0, null, () => children) { IsWarning = unknown.Count > 0, SearchText = text };
    }

    static DirEntry Group(string text, List<DirEntry> members, int status, FontWeight weight, bool warning = false, bool hint = false) =>
        new(text, Math.Max(status, members.Select(m => m.Status).DefaultIfEmpty(0).Max()), null, () => members)
        { Members = members, Weight = weight, IsWarning = warning, IsHint = hint && !warning };
}
