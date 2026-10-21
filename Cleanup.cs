using Microsoft.Data.Sqlite;

namespace ConanModCheck;

/// <summary>
/// Plant und entfernt die Inhalte von Mods aus dem Spielstand. Ohne <c>Apply</c> wird nur
/// gezeigt, was passieren würde. Vor dem Ändern wird immer eine Sicherungskopie angelegt.
/// </summary>
sealed class Cleanup
{
    /// <summary>Objekte, die samt allem, was an ihnen hängt, entfernt werden.</summary>
    public List<(WorldObject Object, string Reason)> Objects { get; } = [];

    /// <summary>Einzelne Items mit Mod-IDs in Inventaren, die selbst bleiben.</summary>
    public List<InventoryItem> Items { get; } = [];

    public List<Buff> Buffs { get; } = [];

    /// <summary>Post an Spieler mit Mod-Items (character_item_mail), nach ID.</summary>
    public HashSet<long> MailTemplateIds { get; } = [];

    /// <summary>Reste ohne Besitzer, die mit entfernt werden (oder null).</summary>
    public Orphans? Orphans { get; private set; }

    /// <summary>Was absichtlich stehen bleibt, mit Begründung.</summary>
    public List<string> Skipped { get; } = [];

    readonly SaveGame _save;

    Cleanup(SaveGame save) => _save = save;

    /// <param name="loaded">Alle geladenen Mods. Eine ID, die auch zu einer nicht ausgewählten Mod gehört, bleibt.</param>
    public static Cleanup Plan(SaveGame save, List<ModPackage> mods, GameData? game, bool includeContents, bool includeOrphans = false,
        IEnumerable<ModPackage>? loaded = null)
    {
        var plan = new Cleanup(save);
        var newIds = new HashSet<long>(mods.SelectMany(m => m.NewIds));
        // Mehrdeutige IDs: Gehört eine Nummer auch zu einer Mod, die bleibt, ist nicht klar, wessen Eintrag es ist
        var others = (loaded ?? []).Where(m => !mods.Contains(m)).ToList();
        var shared = newIds.Where(id => others.Any(m => m.NewIds.Contains(id))).ToHashSet();
        newIds.ExceptWith(shared);
        var removed = new HashSet<long>();

        foreach (var mod in mods)
        {
            // Gebäude: nur ganz aus Mod-Teilen bestehende werden entfernt
            foreach (var (id, pieces) in save.Pieces)
            {
                int modPieces = pieces.Count(p => ModPaths.BelongsTo(mod, p.Class));
                if (modPieces == 0) continue;
                if (modPieces < pieces.Count)
                {
                    plan.Skipped.Add(L.F("Gebäude {0} ({1}{2}): {3} von {4} Bauteilen aus {5}. Gemischte Gebäude bitte im Spiel abreißen oder umbauen.",
                        id, save.OwnerName(save.BuildingOwner.GetValueOrDefault(id)), Where(save, id), modPieces, pieces.Count, mod.Name));
                    continue;
                }
                if (save.Objects.TryGetValue(id, out var o) && removed.Add(id))
                    plan.Objects.Add((o, L.P(pieces.Count, "Gebäude aus {0}, 1 Bauteil", "Gebäude aus {0}, {1} Bauteile", mod.Name, pieces.Count)));
            }

            // Platzierte Objekte, NPCs und Mod-Steuerung
            foreach (var o in save.Objects.Values.Where(o => ModPaths.BelongsTo(mod, o.Class) && !save.Pieces.ContainsKey(o.Id)))
            {
                if (removed.Contains(o.Id)) continue;
                var inv = save.ItemsByOwner.GetValueOrDefault(o.Id) ?? [];
                int keep = game == null ? inv.Count : inv.Count(i => !newIds.Contains(i.TemplateId));
                if (keep > 0 && !includeContents)
                {
                    plan.Skipped.Add(L.F("{0} (ID {1}{2}) enthält {3} Items, die nicht aus den entfernten Mods stammen. Bitte erst ausräumen oder Mod-Behälter samt Inhalt entfernen lassen (Konsole: --include-contents).",
                        SaveGame.ShortClass(o.Class), o.Id, Where(save, o.Id), keep));
                    continue;
                }
                removed.Add(o.Id);
                plan.Objects.Add((o, ModPaths.IsController(o.Class) ? L.F("Mod-Steuerung von {0}", mod.Name) : L.F("Objekt aus {0}", mod.Name)));
            }

            plan.Buffs.AddRange(save.Buffs.Where(b => ModPaths.BelongsTo(mod, b.Path)));
        }

        // Mod-Items in Inventaren, die stehen bleiben. Bei Spielerfiguren nur aus Inventaren, deren Bedeutung
        // bekannt ist; Einträge in unbekannten Sonderinventaren werden nur genannt
        var remaining = save.Items.Where(i => newIds.Contains(i.TemplateId) && !removed.Contains(i.OwnerId)).ToList();
        plan.Items.AddRange(remaining.Where(i => !save.Characters.ContainsKey(i.OwnerId) || InventoryKinds.IsKnown(i.InvType)));
        foreach (var type in remaining.Where(i => save.Characters.ContainsKey(i.OwnerId) && !InventoryKinds.IsKnown(i.InvType)).GroupBy(i => i.InvType).OrderBy(g => g.Key))
            plan.Skipped.Add(L.F("{0} Einträge in Inventar-Typ {1} bei {2} Spielerfiguren passen zu den entfernten Mods. Die Bedeutung dieses Inventars ist nicht bekannt, deshalb bleiben sie unangetastet.",
                type.Count(), type.Key, type.Select(i => i.OwnerId).Distinct().Count()));
        if (shared.Count > 0)
        {
            int entries = save.Items.Count(i => shared.Contains(i.TemplateId) && !removed.Contains(i.OwnerId));
            if (entries > 0)
                plan.Skipped.Add(L.F("{0} Einträge mit IDs, die zugleich zu nicht ausgewählten Mods gehören ({1}). Wem sie gehören, ist nicht eindeutig; sie bleiben.",
                    entries, string.Join(", ", others.Where(m => m.NewIds.Overlaps(shared)).Select(m => m.Name))));
        }
        plan.MailTemplateIds.UnionWith(newIds);

        if (includeOrphans)
        {
            var orphans = ConanModCheck.Orphans.Find(save);
            if (orphans.HasRemovable) plan.Orphans = orphans;
            // Nicht doppelt aufführen: Mod-Items in verwaisten Behältern zählen als Reste
            var orphanKeys = orphans.Items.ToHashSet();
            plan.Items.RemoveAll(orphanKeys.Contains);
        }
        return plan;
    }

    static string Where(SaveGame save, long id) => save.Objects.TryGetValue(id, out var o) ? $", {SaveGame.Coords(o)}" : "";

    public bool IsEmpty => Objects.Count == 0 && Items.Count == 0 && Buffs.Count == 0 && Orphans == null;

    public void Preview(TextWriter w, ItemNames? names = null)
    {
        names ??= ItemNames.Empty;
        Line(w, L.T("Vorschau der Bereinigung"));
        if (IsEmpty) w.WriteLine(L.T("Es gibt nichts zu entfernen."));

        if (Objects.Count > 0)
        {
            w.WriteLine(L.F("Objekte, die samt Inhalt entfernt werden ({0}):", Objects.Count));
            foreach (var (o, reason) in Objects.OrderBy(x => x.Reason).ThenBy(x => x.Object.Id))
            {
                int inv = _save.ItemsByOwner.GetValueOrDefault(o.Id)?.Count ?? 0;
                w.WriteLine($"  {SaveGame.ShortClass(o.Class)} (ID {o.Id}) – {reason}" + (inv > 0 ? L.F(", {0} Items darin", inv) : ""));
            }
            w.WriteLine();
        }
        if (Items.Count > 0)
        {
            w.WriteLine(L.F("Einzelne Mod-Items und erlernte Mod-Rezepte/-Talente ({0}):", Items.Count));
            foreach (var g in Items.GroupBy(i => i.OwnerId))
                w.WriteLine($"  {_save.DescribeInventoryOwner(g.Key)}: {string.Join(", ", g.Select(i => i.TemplateId).Order().Select(names.Label))}");
            w.WriteLine();
        }
        if (Buffs.Count > 0)
        {
            w.WriteLine(L.F("Aktive Effekte ({0}):", Buffs.Count));
            foreach (var b in Buffs) w.WriteLine($"  {_save.OwnerName(b.CharId)}: {SaveGame.ShortClass(b.Path)}");
            w.WriteLine();
        }
        if (Orphans != null)
        {
            if (Orphans.Items.Count > 0)
                w.WriteLine(L.F("Reste ohne Besitzer: {0} Items in {1} Behältern, die es nicht mehr gibt.", Orphans.Items.Count, Orphans.Items.Select(i => i.OwnerId).Distinct().Count()));
            if (Orphans.ModControllerIds.Count > 0)
                w.WriteLine(L.F("Reste ohne Besitzer: {0} Einträge der Mod-Steuerung ohne Objekt.", Orphans.ModControllerIds.Count));
            w.WriteLine();
        }
        if (Skipped.Count > 0)
        {
            w.WriteLine(L.T("Bleibt stehen:"));
            foreach (var s in Skipped) w.WriteLine($"  » {s}");
            w.WriteLine();
        }
        if (Objects.Count > 0 || Items.Count > 0 || Buffs.Count > 0)
        {
            w.WriteLine(L.T("Hinweis: Daten anderer Objekte, die nur auf die Mod verweisen (z. B. Einstellungen in Eigenschaften), werden nicht angefasst."));
            w.WriteLine();
        }
    }

    /// <summary>Legt eine Sicherungskopie an, entfernt alles in einer Transaktion und prüft danach die Datenbank.</summary>
    public string Apply(string dbPath, TextWriter w)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        using var db = new SqliteConnection(cs);
        db.Open();

        // Schlägt fehl, wenn ein anderer Prozess (z. B. der laufende Server) die Datenbank benutzt
        try { Exec(db, "BEGIN EXCLUSIVE"); Exec(db, "ROLLBACK"); }
        catch (SqliteException)
        {
            throw new InvalidOperationException(L.T("Die Datenbank ist gerade in Benutzung. Bitte den Server stoppen und es erneut versuchen."));
        }

        var backup = BackupPath(dbPath);
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Pooling = false }.ToString()))
        {
            target.Open();
            db.BackupDatabase(target);
            // Die Sicherung soll eine einzelne, eigenständige Datei sein
            Exec(target, "PRAGMA journal_mode = DELETE");
        }
        w.WriteLine(L.F("Sicherungskopie: {0}", backup));

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) tables.Add(r.GetString(0));
        }

        var counts = new SortedDictionary<string, int>();
        using (var tx = db.BeginTransaction())
        {
            void Delete(string table, string where, params (string Name, object Value)[] p)
            {
                if (!tables.Contains(table)) return;
                using var cmd = db.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"DELETE FROM \"{table}\" WHERE {where}";
                foreach (var (name, value) in p) cmd.Parameters.AddWithValue(name, value);
                int n = cmd.ExecuteNonQuery();
                if (n > 0) counts[table] = counts.GetValueOrDefault(table) + n;
            }

            foreach (var (o, _) in Objects)
            {
                var id = ("$id", (object)o.Id);
                Delete("item_properties", "owner_id = $id", id);
                Delete("item_inventory", "owner_id = $id", id);
                Delete("properties", "object_id = $id", id);
                Delete("buildable_health", "object_id = $id", id);
                Delete("building_instances", "object_id = $id", id);
                Delete("buildings", "object_id = $id", id);
                Delete("follower_markers", "follower_id = $id", id);
                Delete("used_smart_objects", "smart_object_actor_id = $id OR interacting_actor_id = $id", id);
                Delete("actor_bounding_box", "id = $id", id);
                Delete("mod_controllers", "id = $id", id);
                Delete("actor_position", "id = $id", id);
            }

            foreach (var i in Items)
            {
                (string, object)[] p = [("$item", i.ItemId), ("$owner", i.OwnerId), ("$inv", i.InvType), ("$tpl", i.TemplateId)];
                Delete("item_properties", "item_id = $item AND owner_id = $owner AND inv_type = $inv", p[..3]);
                Delete("item_inventory", "item_id = $item AND owner_id = $owner AND inv_type = $inv AND template_id = $tpl", p);
            }

            foreach (var b in Buffs)
                Delete("character_buffs", "char_id = $c AND buff_path = $p", ("$c", b.CharId), ("$p", b.Path));

            foreach (var t in MailTemplateIds)
                Delete("character_item_mail", "template_id = $t", ("$t", t));

            if (Orphans != null)
            {
                foreach (var i in Orphans.Items)
                {
                    (string, object)[] p = [("$item", i.ItemId), ("$owner", i.OwnerId), ("$inv", i.InvType), ("$tpl", i.TemplateId)];
                    Delete("item_properties", "item_id = $item AND owner_id = $owner AND inv_type = $inv", p[..3]);
                    Delete("item_inventory", "item_id = $item AND owner_id = $owner AND inv_type = $inv AND template_id = $tpl", p);
                }
                foreach (var id in Orphans.ModControllerIds)
                    Delete("mod_controllers", "id = $id", ("$id", id));
            }

            tx.Commit();
        }

        w.WriteLine();
        w.WriteLine(L.T("Entfernte Zeilen je Tabelle:"));
        if (counts.Count == 0) w.WriteLine("  " + L.T("keine"));
        foreach (var (t, n) in counts) w.WriteLine($"  {t,-20} {n,6}");

        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA integrity_check";
            var result = cmd.ExecuteScalar() as string;
            w.WriteLine();
            w.WriteLine(result == "ok"
                ? L.T("Prüfung der Datenbank: in Ordnung.")
                : L.F("Prüfung der Datenbank meldet Probleme: {0}. Im Zweifel die Sicherungskopie zurückspielen.", result));
        }

        // Der Spielstand läuft im WAL-Modus: Änderungen landen erst in game.db-wal. Damit die
        // game.db allein vollständig ist (z. B. zum Hochladen), wird alles zurückgeschrieben.
        Exec(db, "PRAGMA wal_checkpoint(TRUNCATE)");
        db.Close();
        SqliteConnection.ClearAllPools();
        return backup;
    }

    static string BackupPath(string dbPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;
        var name = Path.GetFileNameWithoutExtension(dbPath);
        return Path.Combine(dir, $"{name}.backup-{DateTime.Now:yyyyMMdd-HHmmss}.db");
    }

    static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static void Line(TextWriter w, string text)
    {
        w.WriteLine(new string('=', 40));
        w.WriteLine(text);
        w.WriteLine(new string('=', 40));
        w.WriteLine();
    }
}
