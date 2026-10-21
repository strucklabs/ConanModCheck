namespace ConanModCheck;

/// <summary>Eine Aufgabe einer Quest: erledigt (mit Ende) oder offen (aktuelle Aufgabe).</summary>
sealed record QuestTask(string Name, bool Done, DateTime? Started, DateTime? Finished);

/// <summary>Eine Quest einer Figur, aktiv oder abgeschlossen.</summary>
/// <param name="FromNpcState">Aus dem Quest-Zustand für NPCs (BP_AC_PersistentNPCQuestState) statt dem des Spielers.</param>
sealed record QuestEntry(long OwnerId, string Quest, bool Active, bool FromNpcState, DateTime? Started, DateTime? Updated, List<QuestTask> Tasks);

/// <summary>Ein Eintrag der Reise des Verbannten: Schritt X von Y.</summary>
sealed record JourneyEntry(string Name, int Step, int Steps, bool Unlocked)
{
    public bool Completed => Steps > 0 && Step >= Steps;
}

/// <summary>Reise des Verbannten einer Figur.</summary>
sealed record PlayerJourney(long OwnerId, string? Active, List<JourneyEntry> Entries, int StoryStages);

/// <summary>Texte für Quests, gemeinsam für Bericht und Oberfläche.</summary>
static class QuestText
{
    public static string Date(DateTime? t) => t is not { } d ? "?" : d.ToString(L.English ? "yyyy-MM-dd HH:mm" : "dd.MM.yyyy HH:mm");

    public static string Owner(SaveGame save, long id) =>
        save.Characters.TryGetValue(id, out var name) ? L.F("Spieler „{0}“", name) : save.DescribeInventoryOwner(id);

    /// <summary>Kennzeichen hinter dem Namen: [Mod], [Herkunft unbekannt]; leer beim Spiel.</summary>
    public static string Tag(QuestOrigin origin, string? mod) => origin switch
    {
        QuestOrigin.Mod => $" [{mod}]",
        QuestOrigin.Unknown => " [" + L.T("Herkunft unbekannt") + "]",
        _ => "",
    };

    /// <summary>Hinweis, warum Quests nicht bereinigt werden (Bericht und Reiter).</summary>
    public static string NotCleaned => L.T("Nur zur Anzeige: Quest-Daten liegen als Liste innerhalb eines Datenblocks an der Spielerfigur und werden deshalb von der Bereinigung nicht angefasst.");
}

/// <summary>Woher eine Quest, Aufgabe oder Reise stammt.</summary>
enum QuestOrigin { Game, Mod, Unknown, Unchecked }

/// <summary>
/// Quests und Reise des Verbannten aus dem Spielstand – nur zum Anzeigen und Zuordnen. Die Daten liegen als
/// Liste innerhalb eines Datenblocks an der Figur (properties); die Bereinigung fasst sie deshalb nie an.
/// </summary>
sealed class QuestData
{
    public List<QuestEntry> Quests { get; } = [];
    public List<PlayerJourney> Journeys { get; } = [];

    /// <summary>Eigenschaften, die sich nicht vollständig lesen ließen (Figur, Eigenschaft).</summary>
    public List<(long OwnerId, string Property)> Unreadable { get; } = [];

    const string PlayerState = "BP_AC_PlayerQuestState_C.", NpcState = "BP_AC_PersistentNPCQuestState_C.", Journey = "BP_AC_ExilesJourney_C.";

    /// <summary>Eigenschaften, die der Spielstand dafür aufbewahren muss.</summary>
    public static bool IsQuestProperty(string name) =>
        name.StartsWith(PlayerState, StringComparison.Ordinal) || name.StartsWith(NpcState, StringComparison.Ordinal) || name.StartsWith(Journey, StringComparison.Ordinal);

    public static QuestData Read(SaveGame save)
    {
        var data = new QuestData();
        foreach (var ((owner, name), value) in save.QuestBlobs.OrderBy(kv => kv.Key.Id).ThenBy(kv => kv.Key.Name, StringComparer.Ordinal))
        {
            bool npc = name.StartsWith(NpcState, StringComparison.Ordinal);
            try
            {
                bool complete;
                if (name.EndsWith(".m_ActiveQuests", StringComparison.Ordinal))
                {
                    var root = PropertyReader.ReadStruct(value, out complete);
                    foreach (var q in Structs(root, "ActiveQuests"))
                    {
                        var tasks = Structs(q, "CompletedTasks").Select(t => new QuestTask(Str(t, "TaskName"), true, Time(t, "TaskStartedTimestamp"), Time(t, "TaskCompletedTimestamp")))
                            .Concat(Structs(q, "ActiveTasks").Select(t => new QuestTask(Str(t, "TaskName"), false, Time(t, "TaskStartedTimestamp"), null)))
                            .ToList();
                        data.Quests.Add(new QuestEntry(owner, Str(q, "QuestName"), true, npc, Time(q, "QuestStartedTimestamp"), Time(q, "QuestLastUpdateTimestamp"), tasks));
                    }
                }
                else if (name.EndsWith(".m_CompletedQuests", StringComparison.Ordinal))
                {
                    var root = PropertyReader.ReadStruct(value, out complete);
                    foreach (var q in Structs(root, "CompletedQuests"))
                    {
                        var tasks = Structs(q, "LastCompletionCompletedTasks")
                            .Select(t => new QuestTask(Str(t, "TaskName"), true, Time(t, "TaskStartedTimestamp"), Time(t, "TaskCompletedTimestamp"))).ToList();
                        var start = tasks.Select(t => t.Started).Where(t => t != null).DefaultIfEmpty(null).Min();
                        data.Quests.Add(new QuestEntry(owner, Str(q, "QuestName"), false, npc, start, Time(q, "LastCompletionTime"), tasks));
                    }
                }
                else if (name == Journey + "m_JourneyData")
                {
                    var entries = PropertyReader.ReadStructArray(value, out complete)
                        .Select(e => new JourneyEntry(Str(e, "Name"), (int)Num(e, "CurrentStep"), (int)Num(e, "NumSteps"), e.GetValueOrDefault("Unlocked") is true))
                        .ToList();
                    data.JourneyOf(owner).Entries.AddRange(entries);
                }
                else if (name == Journey + "m_ActiveJourneyName")
                {
                    var active = PropertyReader.ReadString(value, out complete);
                    var j = data.JourneyOf(owner);
                    data.Journeys[data.Journeys.IndexOf(j)] = j with { Active = active is "" or "None" ? null : active };
                }
                else if (name == Journey + "CompletedStoryStages")
                {
                    var stages = PropertyReader.ReadStringArray(value, out complete);
                    var j = data.JourneyOf(owner);
                    data.Journeys[data.Journeys.IndexOf(j)] = j with { StoryStages = stages.Count };
                }
                else continue;
                if (!complete) data.Unreadable.Add((owner, name));
            }
            catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidDataException or InvalidCastException)
            {
                data.Unreadable.Add((owner, name));
            }
        }
        return data;
    }

    PlayerJourney JourneyOf(long owner)
    {
        var j = Journeys.FirstOrDefault(x => x.OwnerId == owner);
        if (j == null) Journeys.Add(j = new PlayerJourney(owner, null, [], 0));
        return j;
    }

    static IEnumerable<Dictionary<string, object?>> Structs(Dictionary<string, object?> d, string key) =>
        d.GetValueOrDefault(key) is List<object?> list ? list.OfType<Dictionary<string, object?>>() : [];

    static string Str(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key) as string ?? "";
    static long Num(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key) is long l ? l : 0;
    static DateTime? Time(Dictionary<string, object?> d, string key) => d.GetValueOrDefault(key) as DateTime?;
}

/// <summary>
/// Gleicht Quest-, Aufgaben- und Reisenamen mit den Tabellen des Spiels und der geladenen Mods ab.
/// Die Namen stehen als Zeilennamen in den Tabellen; geprüft wird, ob ein Name im Namensverzeichnis
/// dieser Tabellen vorkommt.
/// </summary>
sealed class QuestCatalog(GameData? game, IEnumerable<ModPackage> mods)
{
    readonly List<ModPackage> _mods = mods.Where(m => m.HasFile).ToList();

    public bool CanCheck => game != null;

    /// <summary>Ab so vielen Tagen ohne Änderung bei späterer Anwesenheit der Figur gibt es einen Hinweis.</summary>
    public const int StaleDays = 30;

    public (QuestOrigin Origin, string? Mod) OfQuest(string quest)
    {
        if (game != null && game.QuestNames.Contains(quest)) return (QuestOrigin.Game, null);
        if (_mods.FirstOrDefault(m => m.QuestNames.Contains(quest)) is { } mod) return (QuestOrigin.Mod, mod.Name);
        return (game == null ? QuestOrigin.Unchecked : QuestOrigin.Unknown, null);
    }

    public (QuestOrigin Origin, string? Mod) OfJourney(string journey)
    {
        if (game != null && game.JourneyNames.Contains(journey)) return (QuestOrigin.Game, null);
        if (_mods.FirstOrDefault(m => m.JourneyNames.Contains(journey)) is { } mod) return (QuestOrigin.Mod, mod.Name);
        return (game == null ? QuestOrigin.Unchecked : QuestOrigin.Unknown, null);
    }

    /// <summary>Gibt es die Aufgabe in einer Aufgabentabelle des Spiels oder einer geladenen Mod? (null = ohne Spieldateien nicht prüfbar)</summary>
    public bool? TaskKnown(string task)
    {
        if (_mods.Any(m => m.QuestNames.Contains(task))) return true;
        if (game == null) return null;
        return game.QuestTaskNames.Contains(task) || game.QuestNames.Contains(task);
    }

    /// <summary>Mods, die Quest-Tabellen des Spiels ersetzen – ohne sie können Aufgaben fehlen, die es nur in ihrer Fassung gibt.</summary>
    public List<string> ModsChangingGameQuests() => _mods.Where(m => m.OverriddenQuestTables.Count > 0).Select(m => m.Name).ToList();

    /// <summary>
    /// Auffälligkeiten einer Quest: (Text, Warnung?). Warnungen: Quest oder Aufgabe einer aktiven Quest gibt es
    /// weder im Spiel noch in einer geladenen Mod. Hinweis: gleichzeitig aktiv und abgeschlossen.
    /// </summary>
    /// <param name="lastOnline">Wann die Figur zuletzt online war (für „lange unverändert“).</param>
    public IEnumerable<(string Text, bool Warning)> Notes(QuestEntry q, QuestData data, DateTime? lastOnline = null)
    {
        if (OfQuest(q.Quest).Origin == QuestOrigin.Unknown)
            yield return (L.T("Diese Quest gibt es weder im Spiel noch in einer geladenen Mod."), true);
        if (q.Active)
        {
            foreach (var t in q.Tasks.Where(t => TaskKnown(t.Name) == false).DistinctBy(t => t.Name))
                yield return (L.F("Aufgabe „{0}“ gibt es in keiner Aufgabentabelle des Spiels oder einer geladenen Mod.", t.Name), true);
            if (data.Quests.Any(o => o != q && !o.Active && o.OwnerId == q.OwnerId && o.Quest == q.Quest))
                yield return (L.T("Gleichzeitig aktiv und abgeschlossen (bei wiederholbaren Quests üblich)."), false);
            // Lange nichts passiert, obwohl die Figur danach noch online war: vielleicht hängt die Quest
            if (q.Updated is { } updated && lastOnline is { } online && (online - updated).TotalDays >= StaleDays)
                yield return (L.F("Seit {0:N0} Tagen unverändert, obwohl die Figur danach noch online war – die Quest könnte hängen oder wurde liegen gelassen.", (online - updated).TotalDays), false);
        }
    }

    /// <summary>Reise-Einträge, die es weder im Spiel noch in einer geladenen Mod gibt.</summary>
    public List<string> UnknownJourneys(PlayerJourney j) =>
        j.Entries.Select(e => e.Name).Append(j.Active ?? "").Where(n => n.Length > 0 && OfJourney(n).Origin == QuestOrigin.Unknown).Distinct().ToList();
}
