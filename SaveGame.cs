using Microsoft.Data.Sqlite;

namespace ConanModCheck;

/// <param name="Yaw">Drehung um die Hochachse in Grad (für Zäune und Türen auf der Karte).</param>
sealed record WorldObject(long Id, string Class, double X, double Y, double Z, double Yaw = 0);
/// <param name="Yaw">Drehung um die Hochachse in Grad.</param>
sealed record BuildingPiece(long ObjectId, long InstanceId, string Class, double X = 0, double Y = 0, double Z = 0, double Yaw = 0);
/// <param name="Quantity">Stückzahl des Stapels (1, wenn nicht gespeichert).</param>
sealed record InventoryItem(long ItemId, long OwnerId, long InvType, long TemplateId, int Quantity = 1);
sealed record CharacterInfo(int Level, DateTime? LastOnline);
sealed record Buff(long CharId, string Path);

/// <summary>
/// Der Spielstand (game.db), einmal komplett eingelesen. Die Datenbank wird nur lesend geöffnet.
/// </summary>
sealed class SaveGame
{
    public Dictionary<long, WorldObject> Objects { get; } = [];
    public Dictionary<long, long> BuildingOwner { get; } = [];
    public Dictionary<long, List<BuildingPiece>> Pieces { get; } = [];
    public List<InventoryItem> Items { get; } = [];
    public Dictionary<long, List<InventoryItem>> ItemsByOwner { get; } = [];
    public Dictionary<long, string> Characters { get; } = [];
    public Dictionary<long, long> CharacterGuild { get; } = [];
    public Dictionary<long, string> Guilds { get; } = [];
    public Dictionary<long, long> FollowerOwner { get; } = [];
    public List<Buff> Buffs { get; } = [];
    public Dictionary<long, CharacterInfo> CharacterInfos { get; } = [];

    /// <summary>Namen von Thralls und Tieren (ThrallName, PetName).</summary>
    public Dictionary<long, string> FigureNames { get; } = [];

    /// <summary>Besitzer von Thralls und Tieren laut OwnerUniqueID (Spieler- oder Clan-ID).</summary>
    public Dictionary<long, long> FigureOwner { get; } = [];

    /// <summary>Figur-ID → Anzahl gespeicherter Werte (character_stats).</summary>
    public Dictionary<long, int> StatsByFigure { get; } = [];

    /// <summary>IDs in mod_controllers, eine Zeile je Eintrag (IDs können mehrfach vorkommen).</summary>
    public List<long> ModControllerIds { get; } = [];

    /// <summary>Objekt-ID → Namen der Eigenschaften, deren Binärdaten einen Mod-Pfad enthalten (je Mod-Ordner).</summary>
    public Dictionary<string, Dictionary<long, HashSet<string>>> PropertyRefs { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Attributpunkte je Spielerfigur (AttributePointsTotal, …Distributed, …Undistributed).</summary>
    public Dictionary<long, Dictionary<string, int>> AttributePoints { get; } = [];

    /// <summary>Komponenten mit gespeicherten Daten je Spielerfigur (Name vor dem Punkt, z. B. BP_ProgressionSystem_C).</summary>
    public Dictionary<long, HashSet<string>> CharacterComponents { get; } = [];

    /// <summary>
    /// Liest den Spielstand nur lesend. Danach liegen neben der game.db keine Dateien, die vorher nicht da waren.
    /// </summary>
    public static SaveGame Load(string dbPath)
    {
        // Lesend geöffnet legt SQLite bei einer WAL-Datenbank -wal und -shm an, kann sie beim Schließen aber
        // nicht selbst wegräumen (dafür bräuchte es Schreibzugriff). Was es vorher nicht gab, räumen wir danach weg.
        bool hadWal = File.Exists(dbPath + "-wal"), hadShm = File.Exists(dbPath + "-shm");
        try { return Read(dbPath); }
        finally { RemoveLeftovers(dbPath, hadWal, hadShm); }
    }

    /// <summary>
    /// Entfernt -wal/-shm, die erst durch das Lesen entstanden sind. Ein -wal mit Inhalt bleibt immer liegen,
    /// ebenso alles, was ein anderes Programm (etwa der laufende Server) gerade offen hat.
    /// </summary>
    static void RemoveLeftovers(string dbPath, bool hadWal, bool hadShm)
    {
        string wal = dbPath + "-wal", shm = dbPath + "-shm";
        if (!hadWal && File.Exists(wal) && new FileInfo(wal).Length == 0) DeleteIfUnused(wal);
        if (!hadShm && File.Exists(shm)) DeleteIfUnused(shm);
    }

    /// <summary>Löscht die Datei nur, wenn sie sich exklusiv öffnen lässt, also niemand sonst sie benutzt.</summary>
    static void DeleteIfUnused(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static SaveGame Read(string dbPath)
    {
        // Ohne Pooling ist die Datei nach dem Schließen wirklich frei, auch wenn unterwegs ein Fehler auftritt
        var cs = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        using var db = new SqliteConnection(cs);
        db.Open();
        var s = new SaveGame();

        foreach (var r in Query(db, "SELECT id, class, x, y, z, rx, ry, rz, rw FROM actor_position"))
        {
            double qx = Num(r, 5), qy = Num(r, 6), qz = Num(r, 7), qw = Num(r, 8);
            double yaw = Math.Atan2(2 * (qw * qz + qx * qy), 1 - 2 * (qy * qy + qz * qz)) * 180 / Math.PI;
            s.Objects[r.GetInt64(0)] = new WorldObject(r.GetInt64(0), r.IsDBNull(1) ? "" : r.GetString(1),
                Num(r, 2), Num(r, 3), Num(r, 4), yaw);
        }

        foreach (var r in Query(db, "SELECT object_id, owner_id FROM buildings"))
            s.BuildingOwner[r.GetInt64(0)] = r.GetInt64(1);

        foreach (var r in Query(db, "SELECT object_id, instance_id, class, transform1 FROM building_instances"))
        {
            var (x, y, z, yaw) = r.IsDBNull(3) ? (0d, 0d, 0d, 0d) : ReadTransform((byte[])r.GetValue(3));
            var p = new BuildingPiece(r.GetInt64(0), r.GetInt64(1), r.GetString(2), x, y, z, yaw);
            if (!s.Pieces.TryGetValue(p.ObjectId, out var list)) s.Pieces[p.ObjectId] = list = [];
            list.Add(p);
        }

        foreach (var r in Query(db, "SELECT item_id, owner_id, inv_type, template_id, data FROM item_inventory"))
        {
            long template = r.GetInt64(3);
            int quantity = r.IsDBNull(4) ? 1 : ReadQuantity((byte[])r.GetValue(4), template);
            var i = new InventoryItem(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), template, quantity);
            s.Items.Add(i);
            if (!s.ItemsByOwner.TryGetValue(i.OwnerId, out var list)) s.ItemsByOwner[i.OwnerId] = list = [];
            list.Add(i);
        }

        foreach (var r in Query(db, "SELECT id, char_name, guild, level, lastTimeOnline FROM characters"))
        {
            s.Characters[r.GetInt64(0)] = r.GetString(1);
            if (!r.IsDBNull(2)) s.CharacterGuild[r.GetInt64(0)] = r.GetInt64(2);
            DateTime? last = r.IsDBNull(4) || r.GetInt64(4) <= 0 ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)).LocalDateTime;
            s.CharacterInfos[r.GetInt64(0)] = new CharacterInfo(r.IsDBNull(3) ? 0 : r.GetInt32(3), last);
        }

        foreach (var r in Query(db, "SELECT guildId, name FROM guilds"))
            s.Guilds[r.GetInt64(0)] = r.IsDBNull(1) ? "" : r.GetString(1);

        foreach (var r in Query(db, "SELECT owner_id, follower_id FROM follower_markers"))
            s.FollowerOwner[r.GetInt64(1)] = r.GetInt64(0);

        foreach (var r in Query(db, "SELECT char_id, buff_path FROM character_buffs"))
            if (!r.IsDBNull(1)) s.Buffs.Add(new Buff(r.GetInt64(0), r.GetString(1)));

        foreach (var r in Query(db, "SELECT char_id, COUNT(*) FROM character_stats GROUP BY char_id"))
            if (!r.IsDBNull(0)) s.StatsByFigure[r.GetInt64(0)] = r.GetInt32(1);

        if (TableExists(db, "mod_controllers"))
            foreach (var r in Query(db, "SELECT id FROM mod_controllers"))
                s.ModControllerIds.Add(r.GetInt64(0));

        foreach (var r in Query(db, "SELECT object_id, name, value FROM properties"))
            if (!r.IsDBNull(2)) s.ScanProperty(r.GetInt64(0), r.GetString(1), (byte[])r.GetValue(2));

        db.Close();
        return s;
    }

    static readonly byte[] ModPathAscii = "/Game/Mods/"u8.ToArray();
    static readonly byte[] ModPathUtf16 = System.Text.Encoding.Unicode.GetBytes("/Game/Mods/");

    /// <summary>Lage eines Bauteils: Drehung (Quaternion x, y, z, w), Weltposition, Skalierung – je float.</summary>
    static (double X, double Y, double Z, double Yaw) ReadTransform(byte[] t)
    {
        if (t.Length < 28) return (0, 0, 0, 0);
        float F(int i) => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(t.AsSpan(i * 4));
        double qx = F(0), qy = F(1), qz = F(2), qw = F(3);
        double yaw = Math.Atan2(2 * (qw * qz + qx * qy), 1 - 2 * (qy * qy + qz * qz)) * 180 / Math.PI;
        return (F(4), F(5), F(6), yaw);
    }

    /// <summary>
    /// Stückzahl aus den Item-Daten: Hinter der Item-ID folgt eine Liste von Ganzzahl-Werten
    /// (Schlüssel, Wert); Schlüssel 1 ist die Anzahl.
    /// </summary>
    static int ReadQuantity(byte[] data, long templateId)
    {
        if (templateId <= 0 || templateId > int.MaxValue) return 1;
        Span<byte> id = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(id, (int)templateId);
        int marker = data.AsSpan().IndexOf("GameItem"u8);
        if (marker < 0) return 1;
        int p = data.AsSpan(marker).IndexOf(id);
        if (p < 0) return 1;
        p += marker + 4;
        if (p + 4 > data.Length) return 1;
        int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p));
        p += 4;
        if (count is < 0 or > 64) return 1;
        for (int i = 0; i < count && p + 8 <= data.Length; i++, p += 8)
        {
            int key = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p));
            int value = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(p + 4));
            if (key == 1) return value > 0 ? value : 1;
        }
        return 1;
    }

    /// <summary>Der Text am Ende einer gespeicherten Text-Eigenschaft (FString als letztes Feld).</summary>
    static string? TrailingString(byte[] v)
    {
        for (int p = 0; p + 4 < v.Length; p++)
        {
            int n = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(v.AsSpan(p));
            if (n > 1 && p + 4 + n == v.Length && v[^1] == 0)
                return System.Text.Encoding.Latin1.GetString(v, p + 4, n - 1);
            if (n < -1 && p + 4 - 2 * n == v.Length && v[^1] == 0 && v[^2] == 0)
                return System.Text.Encoding.Unicode.GetString(v, p + 4, -2 * n - 2);
        }
        return null;
    }

    /// <summary>Merkt sich, welche Mod-Ordner in den Binärdaten einer Eigenschaft vorkommen.</summary>
    void ScanProperty(long objectId, string name, byte[] value)
    {
        if (Characters.ContainsKey(objectId))
        {
            int dot = name.IndexOf('.');
            if (dot > 0)
            {
                if (!CharacterComponents.TryGetValue(objectId, out var comps)) CharacterComponents[objectId] = comps = [];
                comps.Add(name[..dot]);
            }
            // Ganzzahl ganz am Ende des Werts
            if (name.StartsWith("BP_ProgressionSystem_C.AttributePoints", StringComparison.Ordinal) && value.Length >= 4)
            {
                if (!AttributePoints.TryGetValue(objectId, out var points)) AttributePoints[objectId] = points = [];
                points[name[(dot + 1)..]] = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(value.AsSpan(value.Length - 4));
            }
        }
        if (name.EndsWith(".ThrallName", StringComparison.Ordinal) || name.EndsWith(".PetName", StringComparison.Ordinal))
        {
            if (TrailingString(value) is { Length: > 0 } n) FigureNames[objectId] = n;
        }
        else if (name.EndsWith(".OwnerUniqueID", StringComparison.Ordinal) && value.Length >= 8)
        {
            // Der Besitzer steht als 64-Bit-Zahl ganz am Ende
            long owner = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(value.AsSpan(value.Length - 8));
            if (owner > 0) FigureOwner[objectId] = owner;
        }

        foreach (var (marker, utf16) in new[] { (ModPathAscii, false), (ModPathUtf16, true) })
        {
            var span = value.AsSpan();
            int offset = 0;
            while (true)
            {
                int i = span[offset..].IndexOf(marker);
                if (i < 0) break;
                int start = offset + i + marker.Length;
                var folder = ReadFolder(value, start, utf16);
                if (folder.Length > 0)
                {
                    if (!PropertyRefs.TryGetValue(folder, out var byObject)) PropertyRefs[folder] = byObject = [];
                    if (!byObject.TryGetValue(objectId, out var props)) byObject[objectId] = props = [];
                    props.Add(name);
                }
                offset = start;
            }
        }
    }

    static string ReadFolder(byte[] b, int start, bool utf16)
    {
        var sb = new System.Text.StringBuilder();
        int step = utf16 ? 2 : 1;
        for (int p = start; p + step - 1 < b.Length; p += step)
        {
            char c = utf16 ? (char)(b[p] | b[p + 1] << 8) : (char)b[p];
            if (c == '/' || c == '.' || c == '\0' || !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or ' ')) break;
            sb.Append(c);
        }
        return sb.ToString();
    }

    public string OwnerName(long ownerId)
    {
        if (Characters.TryGetValue(ownerId, out var c))
        {
            var clan = CharacterGuild.TryGetValue(ownerId, out var g) && Guilds.TryGetValue(g, out var gn) ? ", " + L.F("Clan „{0}“", gn) : "";
            return L.F("Spieler „{0}“", c) + clan;
        }
        if (Guilds.TryGetValue(ownerId, out var guild)) return L.F("Clan „{0}“", guild);
        return ownerId switch
        {
            0 => L.T("niemand (Besitzer 0)"),
            -1 => L.T("niemand (Besitzer -1)"),
            _ => L.F("unbekannt (ID {0})", ownerId),
        };
    }

    /// <summary>Beschreibt, wem ein Inventar gehört: einer Spielfigur, einem Objekt in der Welt ...</summary>
    public string DescribeInventoryOwner(long ownerId)
    {
        if (Characters.ContainsKey(ownerId)) return OwnerName(ownerId);
        if (Objects.TryGetValue(ownerId, out var o))
        {
            var who = BuildingOwner.TryGetValue(ownerId, out var b) ? OwnerName(b)
                    : FollowerOwner.TryGetValue(ownerId, out var f) ? OwnerName(f)
                    : L.T("ohne Besitzer");
            return L.F("{0} (ID {1}, {2}) bei {3}", ShortClass(o.Class), ownerId, who, Coords(o));
        }
        return L.F("unbekanntes Objekt (ID {0})", ownerId);
    }

    public static string Coords(WorldObject o) => $"X {o.X:0} / Y {o.Y:0} / Z {o.Z:0}";

    /// <summary>"/Game/Mods/X/BP_Foo.BP_Foo_C" → "BP_Foo"</summary>
    public static string ShortClass(string cls)
    {
        var last = cls[(cls.LastIndexOf('/') + 1)..];
        int dot = last.IndexOf('.');
        return dot > 0 ? last[..dot] : last;
    }

    static bool TableExists(SqliteConnection db, string table)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $t";
        cmd.Parameters.AddWithValue("$t", table);
        return cmd.ExecuteScalar() != null;
    }

    static double Num(SqliteDataReader r, int i) => r.IsDBNull(i) ? 0 : r.GetDouble(i);

    static IEnumerable<SqliteDataReader> Query(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return r;
    }
}
