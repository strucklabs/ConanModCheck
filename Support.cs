namespace ConanModCheck;

/// <summary>Grobe Bauteil-Arten, wie sie für Grundriss, Höhenschnitt und Stabilität gebraucht werden.</summary>
enum PieceKind { Foundation, FenceFoundation, Ceiling, Wall, Fence, Pillar, Roof, Stair, Bridge, Other }

/// <summary>
/// Näherung dafür, welche Bauteile auf anderen stehen. Die Höhen stammen aus dem Spielstand:
/// Fundamente und Decken liegen mit ihrer Oberkante auf der Ebene, Wände sitzen mit ihrer
/// Mitte 128 darüber, Pfeiler 96, Dächer 128, Zäune stehen mit dem Fuß auf ihrem Fundament.
/// Die echte Stabilitätsberechnung des Spiels ist nicht bekannt.
/// </summary>
static class Support
{
    public const double Grid = 256;

    const double Touch = 40;                 // Höhenabstand, bis zu dem zwei Teile als aufeinanderliegend gelten
    const double Reach = Grid * 0.62;        // waagerechter Abstand für „steht darauf“ (Wände sitzen 128 neben der Fundamentmitte)
    const int MaxOverhang = 3;               // so viele Decken dürfen frei vom letzten tragenden Teil wegragen

    public static PieceKind KindOf(string cls)
    {
        var c = SaveGame.ShortClass(cls).ToLowerInvariant();
        if (c.Contains("bridge")) return PieceKind.Bridge;
        if (c.Contains("fencefoundation")) return PieceKind.FenceFoundation;
        if (c.Contains("pillar")) return PieceKind.Pillar;
        if (c.Contains("fence") || c.Contains("palisade")) return PieceKind.Fence;
        if (c.Contains("ceiling")) return PieceKind.Ceiling;
        if (c.Contains("foundation") || c.Contains("fundation")) return PieceKind.Foundation;
        if (c.Contains("wall") || c.Contains("door") || c.Contains("gate") || c.Contains("window")
            || c.Contains("railing") || c.Contains("crenel")) return PieceKind.Wall;
        if (c.Contains("roof") || c.Contains("wedge")) return PieceKind.Roof;
        if (c.Contains("stair") || c.Contains("ramp")) return PieceKind.Stair;
        // Dreiecke ohne nähere Angabe sind meist Fundamente
        if (c.Contains("triangle")) return PieceKind.Foundation;
        return PieceKind.Other;
    }

    /// <summary>Unter- und Oberkante eines Bauteils (Z).</summary>
    public static (double Bottom, double Top) Extent(BuildingPiece p) => KindOf(p.Class) switch
    {
        PieceKind.Foundation or PieceKind.FenceFoundation => (p.Z - 64, p.Z),
        PieceKind.Ceiling or PieceKind.Bridge => (p.Z - 24, p.Z),
        PieceKind.Wall or PieceKind.Roof or PieceKind.Stair => (p.Z - 128, p.Z + 128),
        PieceKind.Pillar => (p.Z - 96, p.Z + 160),
        PieceKind.Fence => (p.Z, p.Z + 192),
        _ => (p.Z - 64, p.Z + 64),
    };

    /// <summary>Höhe der Ebene, auf der ein Teil steht (für die Einfärbung nach Stockwerk).</summary>
    public static double FloorZ(BuildingPiece p) => KindOf(p.Class) switch
    {
        PieceKind.Wall or PieceKind.Roof => p.Z - 128,
        PieceKind.Pillar => p.Z - 96,
        PieceKind.Stair => p.Z + 128,
        _ => p.Z,
    };

    /// <summary>
    /// Länge eines Brückenteils ab seinem Drehpunkt, der an einem Ende liegt. Gemessen an der
    /// langen Hängebrücke (zwei Teile treffen sich nach gut 640 Einheiten an den Pfeilern);
    /// für andere Brücken ist das eine Schätzung.
    /// </summary>
    public static double BridgeLength(string cls) => cls.Contains("long", StringComparison.OrdinalIgnoreCase) ? 640 : 384;

    /// <summary>Platzierte Objekte, die wie eine Wand quer stehen: Zäune, Palisaden, Tore und Türen.</summary>
    public static bool IsFenceObject(string cls)
    {
        var c = SaveGame.ShortClass(cls).ToLowerInvariant();
        return c.Contains("fence") || c.Contains("palisade") || c.Contains("gate") || c.Contains("door");
    }

    /// <summary>Stockwerk je Teil, ab 0 für das unterste.</summary>
    public static Dictionary<BuildingPiece, int> Levels(IReadOnlyList<BuildingPiece> pieces)
    {
        var levels = new Dictionary<BuildingPiece, int>();
        if (pieces.Count == 0) return levels;
        var foundations = pieces.Where(p => KindOf(p.Class) is PieceKind.Foundation or PieceKind.FenceFoundation).ToList();
        double min = (foundations.Count > 0 ? foundations : pieces).Min(FloorZ);
        foreach (var p in pieces) levels[p] = (int)Math.Clamp(Math.Round((FloorZ(p) - min) / Grid), 0, 20);
        return levels;
    }

    /// <summary>
    /// Teile, die nach dem Entfernen der <paramref name="removed"/> keinen Halt mehr hätten:
    /// Sie stehen (auch über andere Teile hinweg) nur auf entfernten Teilen. Teile, die schon
    /// vorher nach dieser Näherung keinen Halt haben, werden nicht gemeldet.
    /// </summary>
    public static HashSet<BuildingPiece> AtRisk(IReadOnlyList<BuildingPiece> pieces, Func<BuildingPiece, bool> removed)
    {
        var result = new HashSet<BuildingPiece>();
        int n = pieces.Count;
        var gone = new bool[n];
        bool any = false;
        for (int i = 0; i < n; i++) any |= gone[i] = removed(pieces[i]);
        if (!any) return result;

        var kind = new PieceKind[n];
        var ext = new (double Bottom, double Top)[n];
        var cells = new Dictionary<(int, int), List<int>>();
        for (int i = 0; i < n; i++)
        {
            kind[i] = KindOf(pieces[i].Class);
            ext[i] = Extent(pieces[i]);
            var key = ((int)Math.Floor(pieces[i].X / Grid), (int)Math.Floor(pieces[i].Y / Grid));
            if (!cells.TryGetValue(key, out var list)) cells[key] = list = [];
            list.Add(i);
        }
        IEnumerable<int> Near(int i)
        {
            int cx = (int)Math.Floor(pieces[i].X / Grid), cy = (int)Math.Floor(pieces[i].Y / Grid);
            for (int dx = -2; dx <= 2; dx++)
                for (int dy = -2; dy <= 2; dy++)
                    if (cells.TryGetValue((cx + dx, cy + dy), out var list))
                        foreach (var j in list) if (j != i) yield return j;
        }
        double Dist(int a, int b) => Math.Sqrt(Math.Pow(pieces[a].X - pieces[b].X, 2) + Math.Pow(pieces[a].Y - pieces[b].Y, 2));
        static bool Flat(PieceKind k) => k is PieceKind.Ceiling or PieceKind.Roof or PieceKind.Foundation or PieceKind.Bridge;

        // Wer trägt wen (unten → oben), wer hängt seitlich an wem (Decken an Decken)
        var carries = new List<int>[n];
        var beside = new List<int>[n];
        var hasSupport = new bool[n];
        for (int i = 0; i < n; i++) { carries[i] = []; beside[i] = []; }
        for (int i = 0; i < n; i++)
            foreach (var j in Near(i))
            {
                double d = Dist(i, j);
                if (d <= Reach && Math.Abs(ext[i].Top - ext[j].Bottom) <= Touch && ext[j].Bottom > ext[i].Bottom + 8)
                {
                    carries[i].Add(j);
                    hasSupport[j] = true;
                }
                if (Flat(kind[i]) && Flat(kind[j]) && d <= Grid * 1.1 && Math.Abs(pieces[i].Z - pieces[j].Z) <= Touch)
                    beside[i].Add(j);
            }

        // Am Boden: Fundamente, außerdem alles, was weder getragen wird noch seitlich anhängt
        bool Grounded(int i) => kind[i] is PieceKind.Foundation or PieceKind.FenceFoundation
            || (!hasSupport[i] && (beside[i].Count == 0 || kind[i] is PieceKind.Stair or PieceKind.Pillar or PieceKind.Other));

        bool[] Reached(bool withRemoved)
        {
            var steps = Enumerable.Repeat(int.MaxValue, n).ToArray();
            var queue = new LinkedList<int>();
            for (int i = 0; i < n; i++)
                if ((withRemoved || !gone[i]) && Grounded(i)) { steps[i] = 0; queue.AddLast(i); }
            while (queue.Count > 0)
            {
                int i = queue.First!.Value;
                queue.RemoveFirst();
                foreach (var j in carries[i])
                    if ((withRemoved || !gone[j]) && steps[j] > 0) { steps[j] = 0; queue.AddFirst(j); }
                if (steps[i] < MaxOverhang)
                    foreach (var j in beside[i])
                        if ((withRemoved || !gone[j]) && steps[j] > steps[i] + 1) { steps[j] = steps[i] + 1; queue.AddLast(j); }
            }
            return steps.Select(s => s != int.MaxValue).ToArray();
        }

        var before = Reached(true);
        var after = Reached(false);
        for (int i = 0; i < n; i++)
            if (!gone[i] && before[i] && !after[i]) result.Add(pieces[i]);
        return result;
    }
}
