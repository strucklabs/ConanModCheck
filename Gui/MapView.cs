using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ConanModCheck.Gui;

enum PieceShape { Square, Triangle, RoofTriangle, RoofTriangleFlipped, Wall, Pillar, FenceFoundation, Bridge }

/// <summary>Wie die Teile einer Zeichenebene gezeichnet werden.</summary>
enum PieceDraw { Area, Line, Post }

/// <summary>Zeichenebene eines Gebäudes: ein Stockwerk, eine Farbgruppe, Flächen, Linien (Wände, Zäune) oder Pfeiler.</summary>
/// <param name="Status">0 = normal, 3 = steht auf angehakten Mod-Teilen, 1 = Mod-Teil, 2 = Teil einer angehakten Mod.</param>
sealed record PieceLayer(int Level, int Status, PieceDraw Draw, Geometry Geometry);

/// <summary>Ein Bauteil oder Objekt im Höhenschnitt: Strecke entlang der Schnittlinie und Höhe.</summary>
/// <param name="Marker">Farbe des Markierungspunkts, der obenauf liegt (Mod-Inhalte), sonst null.</param>
/// <param name="Building">Gebäude des Bauteils, sonst null.</param>
/// <param name="Piece">Das Bauteil, sonst null.</param>
/// <param name="Object">Das Objekt (Truhe, Figur …), sonst null.</param>
sealed record SectionItem(double S0, double S1, double Z0, double Z1, Brush Fill, Pen? Edge, bool Dot, string Label, Brush? Marker = null,
    MapBuilding? Building = null, BuildingPiece? Piece = null, MapObject? Object = null);

/// <summary>Ein Gebäude für die Karte: Bauteile, Ausdehnung und Besitzer.</summary>
sealed class MapBuilding
{
    public long Id { get; init; }
    public long Owner { get; init; }
    public required List<BuildingPiece> Pieces { get; init; }
    public Rect Bounds { get; init; }
    public int ModPieces { get; init; }
    public Point Center => new(Bounds.X + Bounds.Width / 2, Bounds.Y + Bounds.Height / 2);
}

enum MapObjectKind { Placeable, Figure, Player }

/// <param name="ModFolder">Mod-Ordner, wenn das Objekt aus einer Mod stammt (ohne Mod-Controller).</param>
sealed record MapObject(WorldObject Object, MapObjectKind Kind, long Owner, string? ModFolder = null);

/// <summary>
/// Draufsicht auf die Welt: Gebäude, platzierte Objekte, Thralls/Tiere und Spieler.
/// Weltkoordinaten: X nach rechts, Y nach unten (wie die Karte im Spiel). Die Ansicht merkt
/// sich ihren Mittelpunkt, Maßstab und Drehung, damit sie bei Änderungen nicht springt.
/// Bedienung: Ziehen verschiebt, Rechtsklick-Ziehen zoomt auf ein Rechteck, Strg + Ziehen dreht,
/// das Mausrad zoomt in großen Stufen.
/// </summary>
sealed class MapView : FrameworkElement
{
    const double Grid = 256;                // Rastermaß der Bauteile
    const double PieceZoomThreshold = 0.02; // ab hier (Pixel je Einheit) werden einzelne Bauteile gezeichnet
    const double WheelFactor = 1.75;        // Zoomstufe je Mausrad-Raste
    const double ClusterCell = 44;          // Pixel: Elemente in einer Zelle werden zu einer Gruppe

    List<MapBuilding> _buildings = [];
    List<MapObject> _objects = [];
    Func<string, bool> _isSelectedMod = _ => false;
    readonly Dictionary<long, (List<PieceLayer> Layers, HashSet<BuildingPiece> Risk, Dictionary<BuildingPiece, int> Levels)> _geometry = [];
    readonly Dictionary<long, Geometry> _openings = [];

    // Ansicht: Weltpunkt in der Fenstermitte, Pixel je Welteinheit, Drehung in Grad
    Point _center;
    double _scale = 0.001;
    double _angle;
    bool _hasView;

    // Maus
    enum Drag { None, Pan, Rotate, Zoom, Section }
    Drag _drag;
    Point _dragStart, _dragNow;
    Point _dragCenter;
    double _dragAngle;
    bool _dragged;

    // Beim Zeichnen gemerkt, für Klicks auf Gruppen und Randpfeile
    readonly List<(Point Screen, double Radius, Rect World)> _clusterHits = [];
    readonly List<(Point Screen, Point Target)> _arrowHits = [];

    public MapBuilding? SelectedBuilding { get; private set; }
    public MapObject? SelectedObject { get; private set; }

    /// <summary>Einzelnes Bauteil des ausgewählten Gebäudes (Klick auf der Karte oder im Höhenschnitt), sonst null.</summary>
    public BuildingPiece? SelectedPiece { get; private set; }

    /// <summary>
    /// Auswahl von außen, z. B. aus dem Höhenschnitt. Die Ansicht bleibt, wo sie ist; liegt die
    /// Auswahl außerhalb, wird sie in die Mitte geholt.
    /// </summary>
    public void Select(MapBuilding? building, BuildingPiece? piece, MapObject? obj)
    {
        if (building != SelectedBuilding) _level = null;
        SelectedBuilding = building;
        SelectedPiece = building != null ? piece : null;
        SelectedObject = building == null ? obj : null;
        var target = piece != null ? new Point(piece.X, piece.Y) : obj != null ? new Point(obj.Object.X, obj.Object.Y) : building?.Center;
        if (target is { } t)
        {
            var sp = ToScreen(t);
            if (sp.X < 0 || sp.Y < 0 || sp.X > ActualWidth || sp.Y > ActualHeight) { _center = t; ViewChanged?.Invoke(); }
        }
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    /// <summary>Stockwerk (ab 0) und Einsturzgefahr eines Bauteils.</summary>
    public (int Level, bool AtRisk) PieceInfo(MapBuilding b, BuildingPiece p)
    {
        var g = GeometryFor(b);
        return (g.Levels.GetValueOrDefault(p), g.Risk.Contains(p));
    }

    /// <summary>Oberstes Bauteil eines Gebäudes an einem Weltpunkt: zuerst Teile, deren Umriss den Punkt enthält, sonst das nächste.</summary>
    BuildingPiece? PieceAt(MapBuilding b, Point w)
    {
        double lineTolerance = Math.Max(24, 5 / _scale);
        BuildingPiece? best = null;
        double bestZ = double.MinValue;
        foreach (var p in b.Pieces)
        {
            bool hit;
            if (DrawOf(Shape(p.Class)) == PieceDraw.Area) hit = Contains(Outline(p), w);
            else
            {
                // Wände, Zäune und Pfeiler sind schmal: Abstand zur Mittellinie bzw. zum Mittelpunkt
                double a = p.Yaw * Math.PI / 180;
                var along = new Vector(-Math.Sin(a), Math.Cos(a));
                var rel = w - new Point(p.X, p.Y);
                double t = Math.Clamp(rel * along, DrawOf(Shape(p.Class)) == PieceDraw.Post ? 0 : -Grid / 2, DrawOf(Shape(p.Class)) == PieceDraw.Post ? 0 : Grid / 2);
                hit = (rel - along * t).Length <= lineTolerance;
            }
            if (hit && Support.Extent(p).Top > bestZ) { best = p; bestZ = Support.Extent(p).Top; }
        }
        return best ?? b.Pieces.MinBy(p => (new Point(p.X, p.Y) - w).Length);
    }

    static bool Contains(Point[] poly, Point w)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > w.Y) != (poly[j].Y > w.Y) && w.X < (poly[j].X - poly[i].X) * (w.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    /// <summary>Nur diese Ebene (Z) des ausgewählten Gebäudes hervorheben; null = alle.</summary>
    /// <summary>Nur dieses Stockwerk (ab 0, wie <see cref="Support.Levels"/>) des ausgewählten Gebäudes hervorheben; null = alle.</summary>
    public int? Level { get => _level; set { _level = value; InvalidateVisual(); } }
    int? _level;

    /// <summary>Stockwerk je Bauteil eines Gebäudes, dieselbe Zählung wie Einfärbung und Bauteil-Info.</summary>
    public Dictionary<BuildingPiece, int> PieceLevels(MapBuilding b) => GeometryFor(b).Levels;

    public bool ShowPlaceables { get; set; } = true;
    public bool ShowFigures { get; set; } = true;
    public bool ShowPlayers { get; set; } = true;

    /// <summary>Nur Gebäude mit Mod-Teilen und Objekte aus Mods zeigen.</summary>
    public bool OnlyMods { get; set; }

    public double Angle => _angle;

    /// <summary>
    /// Tiefe des Streifens, aus dem der Höhenschnitt gezeichnet wird: vor der Schnittlinie (zum
    /// Betrachter hin) und dahinter. Der Betrachter steht so, dass A links und B rechts liegt.
    /// </summary>
    public double SectionFront { get => _sectionFront; set { _sectionFront = value; InvalidateVisual(); SectionChanged?.Invoke(); } }
    public double SectionBack { get => _sectionBack; set { _sectionBack = value; InvalidateVisual(); SectionChanged?.Invoke(); } }
    double _sectionFront = 256, _sectionBack = 256;

    /// <summary>Schnittlinie in Weltkoordinaten (Umschalt + Ziehen), null = kein Höhenschnitt.</summary>
    public (Point A, Point B)? Section
    {
        get => _section;
        set { _section = value; InvalidateVisual(); SectionChanged?.Invoke(); }
    }
    (Point A, Point B)? _section;

    public event Action? SelectionChanged;
    public event Action? ViewChanged;
    public event Action? SectionChanged;

    static readonly Brush Background = Frozen(Color.FromRgb(0xF7, 0xF6, 0xF2));
    static readonly Brush GridText = Frozen(Color.FromRgb(0x99, 0x95, 0x8B));
    static readonly Color GridColor = Color.FromRgb(0xE2, 0xDF, 0xD6);

    // Mod-Teile gelb, zum Entfernen angehakte rot – beide mit dunklem Rand, damit sie auch klein trennbar bleiben
    public static readonly Brush ModBrush = Frozen(Color.FromRgb(0xF5, 0xC4, 0x00));
    static readonly Color ModEdge = Color.FromRgb(0x8A, 0x6A, 0x00);
    public static readonly Brush SelectedModBrush = Frozen(Color.FromRgb(0xD7, 0x26, 0x3D));
    static readonly Color SelectedModEdge = Color.FromRgb(0x7A, 0x0E, 0x1C);
    public static readonly Brush PlaceableBrush = Frozen(Color.FromRgb(0x2E, 0x3A, 0x46));
    public static readonly Brush FigureBrush = Frozen(Color.FromRgb(0x1F, 0x9E, 0x89));
    public static readonly Brush PlayerBrush = Frozen(Color.FromRgb(0x7B, 0x4F, 0xD6));
    public static readonly Brush GroupBrush = Frozen(Color.FromRgb(0x8C, 0x93, 0x9B));
    static readonly Pen DotPen = FrozenPen(Colors.White, 1.5);
    static readonly Pen SelectionPen = FrozenPen(Color.FromRgb(0x1C, 0x7E, 0xD6), 2);
    static readonly Pen ZoomPen = FrozenPen(Color.FromRgb(0x1C, 0x7E, 0xD6), 1.5);
    static readonly Brush ZoomFill = Frozen(Color.FromArgb(0x30, 0x1C, 0x7E, 0xD6));
    static readonly Brush SectionFill = Frozen(Color.FromArgb(0x26, 0x1C, 0x7E, 0xD6));

    // Bauteile, die nur auf Teilen angehakter Mods stehen und nach dem Entfernen einstürzen könnten
    public static readonly Brush RiskBrush = Frozen(Color.FromRgb(0xF2, 0x8C, 0x28));
    static readonly Color RiskEdge = Color.FromRgb(0x8A, 0x43, 0x00);

    /// <summary>Besitzerfarben bewusst entsättigt, damit Mod-Teile herausstechen.</summary>
    public static readonly Brush[] OwnerBrushes = new[]
    {
        Color.FromRgb(0x8C, 0x93, 0x9B), Color.FromRgb(0x6F, 0x87, 0x96), Color.FromRgb(0x7F, 0x8F, 0x7A),
        Color.FromRgb(0x98, 0x91, 0x8A), Color.FromRgb(0x7A, 0x7F, 0x99), Color.FromRgb(0x66, 0x70, 0x7A),
        Color.FromRgb(0xA3, 0xA8, 0xAD), Color.FromRgb(0x8F, 0x84, 0x78),
    }.Select(Frozen).ToArray();

    public MapView()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Hand;
    }

    public void SetData(List<MapBuilding> buildings, List<MapObject> objects, Func<string, bool> isSelectedMod, bool keepView)
    {
        var selectedBuilding = SelectedBuilding?.Id;
        var selectedObject = SelectedObject?.Object.Id;
        _buildings = buildings;
        _objects = objects;
        _isSelectedMod = isSelectedMod;
        _geometry.Clear();
        _openings.Clear();
        SelectedBuilding = selectedBuilding != null ? buildings.FirstOrDefault(b => b.Id == selectedBuilding) : null;
        SelectedObject = selectedObject != null ? objects.FirstOrDefault(o => o.Object.Id == selectedObject) : null;
        if (!keepView || !_hasView) _hasView = false; // beim nächsten Zeichnen alles einpassen
        if (!keepView) Section = null;
        InvalidateVisual();
    }

    /// <summary>Nach Änderung der Mod-Auswahl die Farben neu berechnen. Die Ansicht bleibt, wo sie ist.</summary>
    public void RefreshColors()
    {
        _geometry.Clear();
        _openings.Clear();
        InvalidateVisual();
    }

    public Brush OwnerBrush(long owner) => OwnerBrushes[(int)((owner % OwnerBrushes.Length + OwnerBrushes.Length) % OwnerBrushes.Length)];

    // ---------- Ansicht ----------

    Matrix ViewMatrix()
    {
        var m = Matrix.Identity;
        m.Translate(-_center.X, -_center.Y);
        m.Rotate(_angle);
        m.Scale(_scale, _scale);
        m.Translate(ActualWidth / 2, ActualHeight / 2);
        return m;
    }

    Point ToScreen(Point world) => ViewMatrix().Transform(world);
    Point ToScreen(double x, double y) => ToScreen(new Point(x, y));

    Point ToWorld(Point screen)
    {
        var m = ViewMatrix();
        m.Invert();
        return m.Transform(screen);
    }

    /// <summary>Weltfläche, die gerade zu sehen ist (bei Drehung das umschließende Rechteck).</summary>
    Rect VisibleWorld()
    {
        var pts = new[] { new Point(0, 0), new Point(ActualWidth, 0), new Point(0, ActualHeight), new Point(ActualWidth, ActualHeight) }.Select(ToWorld).ToList();
        return new Rect(new Point(pts.Min(p => p.X), pts.Min(p => p.Y)), new Point(pts.Max(p => p.X), pts.Max(p => p.Y)));
    }

    public void FitAll()
    {
        var all = _buildings.Select(b => b.Bounds).Concat(_objects.Select(o => new Rect(o.Object.X, o.Object.Y, 1, 1))).ToList();
        if (all.Count > 0) FitRect(all.Aggregate(Rect.Union));
    }

    /// <summary>Zeigt eine Weltfläche vollständig an; die Drehung bleibt erhalten.</summary>
    public void FitRect(Rect r)
    {
        double w = Math.Max(ActualWidth, 200), h = Math.Max(ActualHeight, 200);
        r.Inflate(Math.Max(r.Width, 2000) * 0.05, Math.Max(r.Height, 2000) * 0.05);
        // Bei Drehung braucht das Rechteck mehr Platz
        double a = _angle * Math.PI / 180, c = Math.Abs(Math.Cos(a)), s = Math.Abs(Math.Sin(a));
        double rw = r.Width * c + r.Height * s, rh = r.Width * s + r.Height * c;
        _scale = Math.Clamp(Math.Min(w / rw, h / rh), 1e-6, 2);
        _center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        _hasView = true;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public void ResetRotation()
    {
        _angle = 0;
        InvalidateVisual();
        ViewChanged?.Invoke();
    }

    public void ZoomToSelection()
    {
        if (SelectedBuilding != null) FitRect(SelectedBuilding.Bounds);
        else if (SelectedObject != null) FitRect(new Rect(SelectedObject.Object.X - 2000, SelectedObject.Object.Y - 2000, 4000, 4000));
    }

    /// <summary>Wählt ein Gebäude oder Objekt aus (z. B. aus der Fundstellen-Liste) und zoomt darauf.</summary>
    public void SelectAndZoom(long id)
    {
        SelectedBuilding = _buildings.FirstOrDefault(b => b.Id == id);
        SelectedObject = SelectedBuilding == null ? _objects.FirstOrDefault(o => o.Object.Id == id) : null;
        SelectedPiece = null;
        _level = null;
        ZoomToSelection();
        SelectionChanged?.Invoke();
    }

    // ---------- Maus ----------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var world = ToWorld(p);
        _scale = Math.Clamp(_scale * (e.Delta > 0 ? WheelFactor : 1 / WheelFactor), 1e-6, 2);
        // Den Weltpunkt unter der Maus festhalten
        _center += world - ToWorld(p);
        InvalidateVisual();
        ViewChanged?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) => BeginDrag(e,
        Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Drag.Rotate : Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Drag.Section : Drag.Pan);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _section != null) { Section = null; e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e) => BeginDrag(e, Drag.Zoom);

    void BeginDrag(MouseButtonEventArgs e, Drag mode)
    {
        Focus();
        _drag = mode;
        _dragStart = _dragNow = e.GetPosition(this);
        _dragCenter = _center;
        _dragAngle = _angle;
        _dragged = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_drag == Drag.None) return;
        var p = e.GetPosition(this);
        if (!_dragged && (p - _dragStart).Length < 4) return;
        _dragged = true;
        _dragNow = p;
        switch (_drag)
        {
            case Drag.Pan:
                Cursor = Cursors.SizeAll;
                // Bildschirmverschiebung in Weltrichtung umrechnen (Drehung beachten)
                var m = ViewMatrix(); m.Invert();
                var d = m.Transform(_dragStart) - m.Transform(p);
                _center = _dragCenter + d;
                break;
            case Drag.Rotate:
                Cursor = Cursors.ScrollAll;
                var mid = new Point(ActualWidth / 2, ActualHeight / 2);
                double a0 = Math.Atan2(_dragStart.Y - mid.Y, _dragStart.X - mid.X);
                double a1 = Math.Atan2(p.Y - mid.Y, p.X - mid.X);
                _angle = Normalize(_dragAngle + (a1 - a0) * 180 / Math.PI);
                break;
            case Drag.Zoom:
            case Drag.Section:
                Cursor = Cursors.Cross;
                break;
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => EndDrag(e);
    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) => EndDrag(e);

    void EndDrag(MouseButtonEventArgs e)
    {
        if (_drag == Drag.None) return;
        var mode = _drag;
        _drag = Drag.None;
        ReleaseMouseCapture();
        Cursor = Cursors.Hand;
        var p = e.GetPosition(this);

        if (mode == Drag.Zoom && _dragged)
        {
            // Auf das aufgezogene Rechteck zoomen (in Bildschirmrichtung, Drehung bleibt)
            var r = new Rect(_dragStart, p);
            if (r.Width > 8 && r.Height > 8)
            {
                _center = ToWorld(new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
                _scale = Math.Clamp(_scale * Math.Min(ActualWidth / r.Width, ActualHeight / r.Height), 1e-6, 2);
            }
        }
        else if (mode == Drag.Section && _dragged)
        {
            var a = ToWorld(_dragStart);
            var b = ToWorld(p);
            if ((b - a).Length >= Grid) Section = (a, b);
        }
        else if (!_dragged && mode is Drag.Pan or Drag.Section) Click(p);

        InvalidateVisual();
        ViewChanged?.Invoke();
        e.Handled = true;
    }

    void Click(Point screen)
    {
        // Randpfeil: zum Ziel springen
        foreach (var (pos, target) in _arrowHits)
            if ((pos - screen).Length < 14) { _center = target; _scale = Math.Max(_scale, 0.004); return; }

        // Gruppe: hineinzoomen, bis sie zerfällt
        foreach (var (pos, radius, world) in _clusterHits)
            if ((pos - screen).Length <= radius + 3) { FitRect(world); return; }

        SelectedObject = null;
        SelectedBuilding = null;
        SelectedPiece = null;
        _level = null;

        double best = 8;
        foreach (var o in VisibleObjects())
        {
            double d = (ToScreen(o.Object.X, o.Object.Y) - screen).Length;
            if (d < best) { best = d; SelectedObject = o; }
        }
        if (SelectedObject == null)
        {
            var w = ToWorld(screen);
            double tolerance = Math.Max(Grid / 2, 6 / _scale);
            double bestDist = double.MaxValue;
            foreach (var b in VisibleBuildings())
            {
                var bounds = b.Bounds;
                bounds.Inflate(tolerance, tolerance);
                if (!bounds.Contains(w)) continue;
                double d = b.Pieces.Min(p => Math.Abs(p.X - w.X) + Math.Abs(p.Y - w.Y));
                if (d < bestDist) { bestDist = d; SelectedBuilding = b; }
            }
            if (bestDist > tolerance * 2) SelectedBuilding = null;
            if (SelectedBuilding is { } sb) SelectedPiece = PieceAt(sb, w);
        }
        SelectionChanged?.Invoke();
    }

    IEnumerable<MapBuilding> VisibleBuildings() => OnlyMods ? _buildings.Where(b => b.ModPieces > 0) : _buildings;

    IEnumerable<MapObject> VisibleObjects() => _objects.Where(o =>
        (!OnlyMods || o.ModFolder != null) && o.Kind switch
        {
            MapObjectKind.Placeable => ShowPlaceables,
            MapObjectKind.Figure => ShowFigures,
            _ => ShowPlayers,
        });

    /// <summary>0 = normal, 1 = Mod-Inhalt, 2 = Inhalt einer zum Entfernen angehakten Mod.</summary>
    int Status(MapBuilding b) => b.ModPieces == 0 ? 0 : b.Pieces.Any(IsSelectedModPiece) ? 2 : 1;
    int Status(MapObject o) => o.ModFolder == null ? 0 : _isSelectedMod(o.ModFolder) ? 2 : 1;

    // ---------- Zeichnen ----------

    protected override void OnRender(DrawingContext dc)
    {
        _clusterHits.Clear();
        _arrowHits.Clear();
        if (!_hasView && ActualWidth >= 50 && ActualHeight >= 50 && (_buildings.Count > 0 || _objects.Count > 0)) FitAll();

        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        var view = VisibleWorld();
        var matrix = new MatrixTransform(ViewMatrix());
        matrix.Freeze();
        DrawGrid(dc, view, matrix);

        bool detailed = _scale >= PieceZoomThreshold;
        if (detailed) DrawDetailed(dc, view, matrix);
        else DrawClusters(dc, view);

        DrawEdgeArrows(dc, view);

        if (_drag == Drag.Zoom && _dragged)
            dc.DrawRectangle(ZoomFill, ZoomPen, new Rect(_dragStart, _dragNow));
        if (_drag == Drag.Section && _dragged) DrawSectionLine(dc, ToWorld(_dragStart), ToWorld(_dragNow));
        else if (_section is { } s) DrawSectionLine(dc, s.A, s.B);
    }

    /// <summary>Schnittlinie mit dem Streifen, aus dem der Höhenschnitt gezeichnet wird; A und B an den Enden.</summary>
    void DrawSectionLine(DrawingContext dc, Point a, Point b)
    {
        var d = b - a;
        if (d.Length < 1) return;
        d.Normalize();
        // Normale zeigt zum Betrachter (nach vorne)
        var n = new Vector(-d.Y, d.X);
        var band = new StreamGeometry();
        using (var g = band.Open())
        {
            g.BeginFigure(ToScreen(a + n * _sectionFront), true, true);
            g.PolyLineTo([ToScreen(b + n * _sectionFront), ToScreen(b - n * _sectionBack), ToScreen(a - n * _sectionBack)], false, false);
        }
        band.Freeze();
        dc.DrawGeometry(SectionFill, null, band);
        dc.DrawLine(SelectionPen, ToScreen(a), ToScreen(b));

        // Blickrichtung: Pfeil von der Vorderkante auf die Linie
        var mid = ToScreen(a + (b - a) / 2);
        var toward = ToScreen(a + (b - a) / 2 + n) - mid;
        if (toward.Length > 0)
        {
            toward.Normalize();
            var tail = ToScreen(a + (b - a) / 2 + n * _sectionFront) + toward * 26;
            var tip = mid + toward * 4;
            var side = new Vector(-toward.Y, toward.X) * 6;
            dc.DrawLine(SelectionPen, tail, tip);
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(tip, true, true);
                g.PolyLineTo([tip + toward * 12 + side, tip + toward * 12 - side], false, false);
            }
            head.Freeze();
            dc.DrawGeometry(SelectionPen.Brush, null, head);
        }
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        foreach (var (p, label) in new[] { (a, "A"), (b, "B") })
        {
            var sp = ToScreen(p);
            dc.DrawEllipse(Brushes.White, SelectionPen, sp, 8, 8);
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, SelectionPen.Brush, dpi);
            dc.DrawText(text, new Point(sp.X - text.Width / 2, sp.Y - text.Height / 2));
        }
    }

    void DrawDetailed(DrawingContext dc, Rect view, Transform matrix)
    {
        foreach (var b in VisibleBuildings())
        {
            if (!view.IntersectsWith(Inflated(b.Bounds, Grid * 3))) continue;
            var owner = ((SolidColorBrush)OwnerBrush(b.Owner)).Color;
            var g = GeometryFor(b);
            dc.PushTransform(matrix);
            bool faded = SelectedBuilding == b && _level != null;
            if (faded) dc.PushOpacity(0.18);
            DrawPieces(dc, owner, g.Layers, OnlyMods);
            if (!faded) DrawOpenings(dc, OpeningsFor(b));
            if (faded)
            {
                dc.Pop();
                DrawPieces(dc, owner, LevelGeometry(b), OnlyMods);
                DrawOpenings(dc, Openings(b.Pieces.Where(p => _level is { } l && g.Levels.GetValueOrDefault(p) == l)));
            }
            if (b == SelectedBuilding)
            {
                var sel = Inflated(b.Bounds, Grid / 2);
                dc.DrawRectangle(null, new Pen(SelectionPen.Brush, 2 / _scale), sel);
                if (SelectedPiece is { } sp)
                {
                    // Umriss des ausgewählten Bauteils, kräftig und unabhängig vom Zoom gut sichtbar
                    var outline = new StreamGeometry();
                    using (var ctx = outline.Open())
                    {
                        var pts = Outline(sp);
                        ctx.BeginFigure(pts[0], isFilled: false, isClosed: true);
                        ctx.PolyLineTo(pts[1..], isStroked: true, isSmoothJoin: true);
                    }
                    outline.Freeze();
                    dc.DrawGeometry(null, new Pen(Brushes.White, 6 / _scale), outline);
                    dc.DrawGeometry(null, new Pen(SelectionPen.Brush, 3 / _scale), outline);
                }
            }
            dc.Pop();
        }

        // Zäune, Tore und Türen als Linie in ihrer Ausrichtung, wie die Wände der Gebäude
        double lineWidth = Math.Max(2.2, 20 * _scale);
        foreach (var o in VisibleObjects().Where(o => o.Kind == MapObjectKind.Placeable && Support.IsFenceObject(o.Object.Class)))
        {
            if (!view.Contains(o.Object.X, o.Object.Y)) continue;
            var (a, b) = FenceEnds(o.Object);
            // Türflügel dünn, damit der Durchlass erkennbar bleibt
            double width = IsDoorObject(o.Object) ? Math.Max(1, lineWidth * 0.35) : lineWidth;
            var color = Status(o) switch { 2 => SelectedModEdge, 1 => ModEdge, _ => Darken(((SolidColorBrush)OwnerBrush(o.Owner)).Color, 0.55) };
            if (o == SelectedObject) dc.DrawLine(LinePen(((SolidColorBrush)SelectionPen.Brush).Color, width + 4), ToScreen(a), ToScreen(b));
            dc.DrawLine(LinePen(color, width), ToScreen(a), ToScreen(b));
            if (Status(o) != 0) dc.DrawLine(LinePen(((SolidColorBrush)(Status(o) == 2 ? SelectedModBrush : ModBrush)).Color, Math.Max(1, width - 2)), ToScreen(a), ToScreen(b));
        }

        foreach (var o in VisibleObjects())
        {
            if (!view.Contains(o.Object.X, o.Object.Y)) continue;
            if (o.Kind == MapObjectKind.Placeable && Support.IsFenceObject(o.Object.Class)) continue;
            var p = ToScreen(o.Object.X, o.Object.Y);
            double r = o.Kind == MapObjectKind.Player ? 6 : 4;
            var brush = Status(o) switch { 2 => SelectedModBrush, 1 => ModBrush, _ => KindBrush(o.Kind) };
            dc.DrawEllipse(brush, o == SelectedObject ? SelectionPen : DotPen, p, r, r);
        }
    }

    /// <summary>
    /// Herausgezoomt: Gebäude und Mod-Objekte, die auf dem Bildschirm nah beieinander liegen,
    /// werden zu Gruppen mit Anzahl zusammengefasst. Die Farbe richtet sich nach dem wichtigsten
    /// Inhalt: rot (angehakte Mod) vor gelb (Mod) vor grau.
    /// </summary>
    void DrawClusters(DrawingContext dc, Rect view)
    {
        var cells = new Dictionary<(int, int), (int Count, int Status, double Sx, double Sy, Rect World)>();
        void Add(Point world, Rect worldRect, int status)
        {
            var s = ToScreen(world);
            var key = ((int)Math.Floor(s.X / ClusterCell), (int)Math.Floor(s.Y / ClusterCell));
            var c = cells.TryGetValue(key, out var e) ? e : (0, 0, 0.0, 0.0, worldRect);
            cells[key] = (c.Item1 + 1, Math.Max(c.Item2, status), c.Item3 + s.X, c.Item4 + s.Y, Rect.Union(c.Item5, worldRect));
        }
        foreach (var b in VisibleBuildings())
            if (view.IntersectsWith(b.Bounds)) Add(b.Center, b.Bounds, Status(b));
        foreach (var o in VisibleObjects())
        {
            if (!view.Contains(o.Object.X, o.Object.Y)) continue;
            // Normale Objekte gehen herausgezoomt in der Masse unter, nur Mod-Objekte und Spieler zählen
            if (o.ModFolder != null) Add(new Point(o.Object.X, o.Object.Y), new Rect(o.Object.X, o.Object.Y, 1, 1), Status(o));
        }

        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var (_, c) in cells.OrderBy(kv => kv.Value.Status))
        {
            var center = new Point(c.Sx / c.Count, c.Sy / c.Count);
            var brush = c.Status switch { 2 => SelectedModBrush, 1 => ModBrush, _ => GroupBrush };
            var edge = new Pen(new SolidColorBrush(c.Status switch { 2 => SelectedModEdge, 1 => ModEdge, _ => Colors.White }), 1.5);
            if (c.Count == 1)
            {
                dc.DrawEllipse(brush, edge, center, 5, 5);
                _clusterHits.Add((center, 5, c.World));
                continue;
            }
            double r = 9 + Math.Min(8, Math.Log2(c.Count) * 1.5);
            dc.DrawEllipse(brush, edge, center, r, r);
            var text = new FormattedText(c.Count.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, 11, c.Status == 1 ? Brushes.Black : Brushes.White, dpi);
            dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
            _clusterHits.Add((center, r, c.World));
        }

        // Spieler bleiben auch herausgezoomt einzeln sichtbar
        if (ShowPlayers && !OnlyMods)
            foreach (var o in _objects.Where(o => o.Kind == MapObjectKind.Player && view.Contains(o.Object.X, o.Object.Y)))
                dc.DrawEllipse(PlayerBrush, o == SelectedObject ? SelectionPen : DotPen, ToScreen(o.Object.X, o.Object.Y), 5, 5);

        if (SelectedBuilding is { } sb && view.IntersectsWith(sb.Bounds))
            dc.DrawEllipse(null, SelectionPen, ToScreen(sb.Center), 12, 12);
    }

    /// <summary>Pfeile am Rand zeigen auf Mod-Inhalte außerhalb des Ausschnitts (je Richtung einer, mit Anzahl).</summary>
    void DrawEdgeArrows(DrawingContext dc, Rect view)
    {
        var targets = VisibleBuildings().Where(b => b.ModPieces > 0).Select(b => (P: b.Center, S: Status(b)))
            .Concat(VisibleObjects().Where(o => o.ModFolder != null).Select(o => (P: new Point(o.Object.X, o.Object.Y), S: Status(o))));

        var mid = new Point(ActualWidth / 2, ActualHeight / 2);
        var bins = new Dictionary<int, (int Count, int Status, Point Nearest, double Dist)>();
        foreach (var (p, s) in targets)
        {
            var sp = ToScreen(p);
            if (sp.X >= 0 && sp.Y >= 0 && sp.X <= ActualWidth && sp.Y <= ActualHeight) continue;
            double ang = Math.Atan2(sp.Y - mid.Y, sp.X - mid.X);
            int bin = (int)Math.Round(ang / (Math.PI / 12)); // 24 Richtungen
            double dist = (sp - mid).Length;
            var e = bins.TryGetValue(bin, out var x) ? x : (0, 0, p, double.MaxValue);
            bins[bin] = (e.Item1 + 1, Math.Max(e.Item2, s), dist < e.Item4 ? p : e.Item3, Math.Min(dist, e.Item4));
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");
        foreach (var (bin, b) in bins)
        {
            double ang = bin * Math.PI / 12;
            double dx = Math.Cos(ang), dy = Math.Sin(ang);
            // Schnittpunkt der Richtung mit dem (eingerückten) Rand
            double hw = ActualWidth / 2 - 16, hh = ActualHeight / 2 - 16;
            double t = Math.Min(Math.Abs(dx) > 1e-6 ? hw / Math.Abs(dx) : double.MaxValue, Math.Abs(dy) > 1e-6 ? hh / Math.Abs(dy) : double.MaxValue);
            var tip = new Point(mid.X + dx * t, mid.Y + dy * t);

            var arrow = new StreamGeometry();
            using (var g = arrow.Open())
            {
                var back = new Point(tip.X - dx * 14, tip.Y - dy * 14);
                g.BeginFigure(tip, true, true);
                g.LineTo(new Point(back.X - dy * 7, back.Y + dx * 7), true, false);
                g.LineTo(new Point(back.X + dy * 7, back.Y - dx * 7), true, false);
            }
            arrow.Freeze();
            var brush = b.Status == 2 ? SelectedModBrush : ModBrush;
            dc.DrawGeometry(brush, new Pen(new SolidColorBrush(b.Status == 2 ? SelectedModEdge : ModEdge), 1.2), arrow);
            if (b.Count > 1)
            {
                var text = new FormattedText(b.Count.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, Brushes.Black, dpi);
                dc.DrawText(text, new Point(tip.X - dx * 26 - text.Width / 2, tip.Y - dy * 26 - text.Height / 2));
            }
            _arrowHits.Add((new Point(tip.X - dx * 7, tip.Y - dy * 7), b.Nearest));
        }
    }

    Brush KindBrush(MapObjectKind kind) => kind switch { MapObjectKind.Placeable => PlaceableBrush, MapObjectKind.Figure => FigureBrush, _ => PlayerBrush };

    /// <summary>
    /// Zeichnet die Bauteile eines Gebäudes Stockwerk für Stockwerk: höhere Stockwerke heller,
    /// Wände und Zäune als Linien mit Mindestbreite, Umrisse der Teile erst ab einer gewissen Zoomstufe.
    /// Mod-Teile haben immer einen dunklen Rand (1,5 Pixel, unabhängig vom Zoom).
    /// </summary>
    void DrawPieces(DrawingContext dc, Color owner, List<PieceLayer> layers, bool fadeNormal)
    {
        double px = 1 / _scale;                         // ein Bildschirmpixel in Welteinheiten
        double lineWidth = Math.Max(20, 2.2 * px);       // Wände und Zäune nie dünner als gut 2 Pixel
        double postWidth = Math.Max(PillarSize, 3.5 * px); // Pfeiler nie kleiner als gut 3 Pixel
        bool outlines = Grid * _scale >= 14;             // Umrisse erst, wenn ein Rasterfeld groß genug ist
        foreach (var l in layers)
        {
            bool fade = fadeNormal && l.Status == 0;
            if (fade) dc.PushOpacity(0.25);
            var fill = l.Status switch { 1 => ((SolidColorBrush)ModBrush).Color, 2 => ((SolidColorBrush)SelectedModBrush).Color, 3 => ((SolidColorBrush)RiskBrush).Color, _ => Lighten(owner, l.Level) };
            var edge = l.Status switch { 1 => ModEdge, 2 => SelectedModEdge, 3 => RiskEdge, _ => Darken(fill, 0.62) };
            if (l.Draw != PieceDraw.Area)
            {
                // Wände, Zäune und Pfeiler dunkel. Farbige bekommen einen dunklen Rand: erst breit in
                // Randfarbe, dann schmaler darüber. Pfeiler sind ein ganz kurzer Strich mit eckigen
                // Enden, also ein Quadrat in Strichbreite, gedreht wie das Teil
                double width = l.Draw == PieceDraw.Post ? postWidth : lineWidth;
                if (l.Status != 0) dc.DrawGeometry(null, LinePen(edge, width + 3 * px), l.Geometry);
                dc.DrawGeometry(null, LinePen(l.Status == 0 ? Darken(fill, 0.55) : fill, width), l.Geometry);
            }
            else
            {
                var pen = l.Status != 0 ? new Pen(new SolidColorBrush(edge), 1.5 * px) : outlines ? new Pen(new SolidColorBrush(edge), px) : null;
                dc.DrawGeometry(new SolidColorBrush(fill), pen, l.Geometry);
            }
            if (fade) dc.Pop();
        }
    }

    /// <summary>
    /// Enden eines Zauns, Tors oder einer Tür, gut 240 Einheiten lang. Zäune stehen quer zu ihrer
    /// Blickrichtung; Türen und Tore sind gegenüber ihrem Rahmen um 90° gedreht gespeichert (bei allen
    /// 25 im Testspielstand), liegen also in Blickrichtung.
    /// </summary>
    static (Point A, Point B) FenceEnds(WorldObject o)
    {
        double a = o.Yaw * Math.PI / 180;
        bool door = IsDoorObject(o);
        // Türflügel füllen nur die Lücke im Rahmen, Zäune die ganze Länge
        double half = door ? OpeningOf(o.Class) / 2 - 4 : 120;
        var d = (door ? new Vector(Math.Cos(a), Math.Sin(a)) : new Vector(-Math.Sin(a), Math.Cos(a))) * half;
        var c = new Point(o.X, o.Y);
        return (c - d, c + d);
    }

    static bool IsDoorObject(WorldObject o) => OpeningOf(o.Class) > 0;

    static Pen LinePen(Color c, double width) => new(new SolidColorBrush(c), width) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };

    /// <summary>Höhere Stockwerke heller, bis höchstens halb zu Weiß.</summary>
    static Color Lighten(Color c, int level)
    {
        double t = Math.Min(0.5, level * 0.13);
        return Color.FromRgb((byte)(c.R + (255 - c.R) * t), (byte)(c.G + (255 - c.G) * t), (byte)(c.B + (255 - c.B) * t));
    }

    static Color Darken(Color c, double f) => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    void DrawGrid(DrawingContext dc, Rect view, Transform matrix)
    {
        // Linienabstand so wählen, dass etwa alle 100–250 Pixel eine Linie kommt
        double[] steps = [256, 1024, 2560, 5000, 10000, 25000, 50000, 100000, 250000, 500000, 1000000, 2500000];
        double step = steps.FirstOrDefault(s => s * _scale >= 100, steps[^1]);
        var pen = new Pen(new SolidColorBrush(GridColor), 1 / _scale);
        dc.PushTransform(matrix);
        for (double x = Math.Floor(view.Left / step) * step; x <= view.Right; x += step)
            dc.DrawLine(pen, new Point(x, view.Top), new Point(x, view.Bottom));
        for (double y = Math.Floor(view.Top / step) * step; y <= view.Bottom; y += step)
            dc.DrawLine(pen, new Point(view.Left, y), new Point(view.Right, y));
        dc.Pop();

        // Beschriftung nur ohne Drehung, sonst stünde sie schräg im Bild
        if (Math.Abs(_angle) > 0.01) return;
        var typeface = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double x = Math.Floor(view.Left / step) * step; x <= view.Right; x += step)
            dc.DrawText(new FormattedText($"X {x.ToString("N0", Numbers)}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, GridText, dpi), new Point(ToScreen(x, 0).X + 3, 2));
        for (double y = Math.Floor(view.Top / step) * step; y <= view.Bottom; y += step)
            dc.DrawText(new FormattedText($"Y {y.ToString("N0", Numbers)}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, GridText, dpi), new Point(3, ToScreen(0, y).Y + 2));
    }

    static NumberFormatInfo Numbers => L.Numbers;

    Geometry OpeningsFor(MapBuilding b)
    {
        if (!_openings.TryGetValue(b.Id, out var g)) _openings[b.Id] = g = Openings(b.Pieces);
        return g;
    }

    /// <summary>Durchgänge (Türen, Tore, Bögen) als kurze Striche in der Lücke ihres Wandteils.</summary>
    static Geometry Openings(IEnumerable<BuildingPiece> pieces)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
            foreach (var p in pieces)
            {
                double gap = OpeningOf(p.Class) / 2;
                if (gap <= 0 || DrawOf(Shape(p.Class)) != PieceDraw.Line) continue;
                double a = p.Yaw * Math.PI / 180;
                var along = new Vector(-Math.Sin(a), Math.Cos(a));
                var c = new Point(p.X, p.Y);
                ctx.BeginFigure(c - along * gap, isFilled: false, isClosed: false);
                ctx.LineTo(c + along * gap, isStroked: true, isSmoothJoin: false);
            }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Durchgänge obenauf als heller Schlitz in der Wandlinie: Über Türen stehen oft Wände eines
    /// höheren Stockwerks, deren Linie die Lücke in der Draufsicht sonst verdecken würde.
    /// </summary>
    void DrawOpenings(DrawingContext dc, Geometry openings)
    {
        double px = 1 / _scale;
        double width = Math.Max(20, 2.2 * px) * 0.55;
        var pen = new Pen(OpeningBrush, width) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
        dc.DrawGeometry(null, pen, openings);
    }

    static readonly Brush OpeningBrush = Frozen(Color.FromArgb(0xE6, 0xF7, 0xF6, 0xF2));

    (List<PieceLayer> Layers, HashSet<BuildingPiece> Risk, Dictionary<BuildingPiece, int> Levels) GeometryFor(MapBuilding b)
    {
        if (_geometry.TryGetValue(b.Id, out var g)) return g;
        var risk = Support.AtRisk(b.Pieces, IsSelectedModPiece);
        var levels = Support.Levels(b.Pieces);
        g = (Build(b.Pieces, risk, levels), risk, levels);
        _geometry[b.Id] = g;
        return g;
    }

    /// <summary>Anzahl der Bauteile, die nur auf Teilen angehakter Mods stehen (Näherung).</summary>
    public int RiskCount(MapBuilding b) => GeometryFor(b).Risk.Count;

    List<PieceLayer> LevelGeometry(MapBuilding b)
    {
        var g = GeometryFor(b);
        return Build(b.Pieces.Where(p => _level is { } l && g.Levels.GetValueOrDefault(p) == l), g.Risk, g.Levels);
    }

    int PieceStatus(BuildingPiece p, HashSet<BuildingPiece> risk) =>
        IsSelectedModPiece(p) ? 2 : ModPaths.FolderOf(p.Class) != null ? 1 : risk.Contains(p) ? 3 : 0;

    static readonly int[] StatusOrder = [0, 2, 3, 1]; // Reihenfolge innerhalb eines Stockwerks: normal, gefährdet, Mod, angehakte Mod

    List<PieceLayer> Build(IEnumerable<BuildingPiece> pieces, HashSet<BuildingPiece> risk, Dictionary<BuildingPiece, int> levels)
    {
        var layers = new List<PieceLayer>();
        foreach (var group in pieces
            .GroupBy(p => (Level: levels.GetValueOrDefault(p), Status: PieceStatus(p, risk), Draw: DrawOf(Shape(p.Class))))
            .OrderBy(g => g.Key.Level).ThenBy(g => g.Key.Draw).ThenBy(g => StatusOrder[g.Key.Status]))
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
                foreach (var p in group.OrderBy(p => p.Z)) AddPiece(ctx, p);
            // Übereinanderliegende Teile (Fundament, Decke, Dach) dürfen sich nicht gegenseitig
            // „ausstanzen“, wie es die Standard-Füllregel (gerade/ungerade) täte
            geometry.FillRule = FillRule.Nonzero;
            geometry.Freeze();
            layers.Add(new PieceLayer(group.Key.Level, group.Key.Status, group.Key.Draw, geometry));
        }
        return layers;
    }

    bool IsSelectedModPiece(BuildingPiece p) => ModPaths.FolderOf(p.Class) is { } f && _isSelectedMod(f);

    static PieceDraw DrawOf(PieceShape s) => s switch { PieceShape.Wall => PieceDraw.Line, PieceShape.Pillar => PieceDraw.Post, _ => PieceDraw.Area };

    const double PillarSize = 44; // Kantenlänge eines Pfeilers, geschätzt

    static bool ShortHas(string cls, string part) => SaveGame.ShortClass(cls).Contains(part, StringComparison.OrdinalIgnoreCase);

    /// <summary>Ordnet ein Bauteil anhand seines Klassennamens einer einfachen Form zu.</summary>
    public static PieceShape Shape(string cls) => Support.KindOf(cls) switch
    {
        PieceKind.Pillar => PieceShape.Pillar,
        PieceKind.Wall or PieceKind.Fence => PieceShape.Wall,
        PieceKind.FenceFoundation => PieceShape.FenceFoundation,
        PieceKind.Bridge => PieceShape.Bridge,
        // Dach-Keile füllen die dreieckige Lücke zwischen zwei Dachreihen, die um 60° gegeneinander
        // gedreht sind: Basis durch den Drehpunkt, Spitze in Blickrichtung, bei „Inverted“ entgegen.
        // Errechnet aus den Ecken der Nachbardächer an der Argos-Sternwarte.
        _ when ShortHas(cls, "wedge") && ShortHas(cls, "roof") => ShortHas(cls, "inverted") ? PieceShape.RoofTriangleFlipped : PieceShape.RoofTriangle,
        // Andere Keile (Aesir-Langhaus) sind flache Dreiecke mit dem Drehpunkt in der Mitte:
        // Ihre Nachbarn liegen wie bei Dreiecksdecken bei 90°, 210° und 330°
        _ when ShortHas(cls, "wedge") => PieceShape.Triangle,
        _ => ShortHas(cls, "triangle")
            ? ShortHas(cls, "roof") ? ShortHas(cls, "flipped") ? PieceShape.RoofTriangleFlipped : PieceShape.RoofTriangle : PieceShape.Triangle
            : PieceShape.Square,
    };

    const double TriangleRadius = Grid / 1.7320508075688772 - 6; // Umkreis des Dreiecks mit Seite 256, minus Fuge
    const double TriangleEdge = Grid / 2 - 6;                     // halbe Seite, minus Fuge
    const double TriangleHeight = Grid * 0.8660254037844386 - 9;  // Höhe des Dreiecks, minus Fuge

    /// <summary>Umriss eines Bauteils in der Draufsicht (Weltkoordinaten); Wände und Zäune als schmales Rechteck.</summary>
    static Point[] Outline(BuildingPiece p)
    {
        double a = p.Yaw * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        // Lokale Koordinaten drehen und an die Position schieben
        Point T(double lx, double ly) => new(p.X + lx * cos - ly * sin, p.Y + lx * sin + ly * cos);
        Point Corner(double deg) => T(TriangleRadius * Math.Cos(deg * Math.PI / 180), TriangleRadius * Math.Sin(deg * Math.PI / 180));
        return Shape(p.Class) switch
        {
            PieceShape.Wall => [T(-10, -Grid / 2), T(10, -Grid / 2), T(10, Grid / 2), T(-10, Grid / 2)],
            PieceShape.Pillar => [T(-PillarSize / 2, -PillarSize / 2), T(PillarSize / 2, -PillarSize / 2), T(PillarSize / 2, PillarSize / 2), T(-PillarSize / 2, PillarSize / 2)],
            // Zaunfundament: schmaler Balken unter dem Zaun
            // Brücke: 256 breit, vom Drehpunkt 128 nach hinten und bis zu 640 in Blickrichtung
            PieceShape.Bridge => BridgeOutline(p.Class, T),
            PieceShape.FenceFoundation => [T(-40, -Grid / 2 + 2), T(40, -Grid / 2 + 2), T(40, Grid / 2 - 2), T(-40, Grid / 2 - 2)],
            // Gleichseitiges Dreieck, Seitenlänge 256, Drehpunkt im Schwerpunkt. Laut Spielstand liegen die
            // Nachbarn (Kanten) in lokaler Richtung 90°, 210° und 330°, die Spitzen also bei 30°, 150° und 270°;
            // leicht verkleinert wie die Quadrate, damit die Fugen sichtbar bleiben
            PieceShape.Triangle => [Corner(30), Corner(150), Corner(270)],
            // Schräges Dreiecksdach: Drehpunkt mitten auf einer Kante, die Spitze 222 Einheiten in
            // Blickrichtung, bei „Flipped“ entgegen. Gemessen an Dächern über Dreiecksdecken und an einem
            // runden Bau, bei dem Flipped-Dächer von der Wandkrone nach außen auf Bodendreiecke abfallen
            PieceShape.RoofTriangle => [T(3, -TriangleEdge), T(3, TriangleEdge), T(TriangleHeight, 0)],
            PieceShape.RoofTriangleFlipped => [T(-3, -TriangleEdge), T(-3, TriangleEdge), T(-TriangleHeight, 0)],
            _ => [T(-Grid / 2 + 4, -Grid / 2 + 4), T(Grid / 2 - 4, -Grid / 2 + 4), T(Grid / 2 - 4, Grid / 2 - 4), T(-Grid / 2 + 4, Grid / 2 - 4)],
        };
    }

    /// <summary>
    /// Breite des Durchgangs in einem Wand- oder Zaunteil, für die Lücke in der Linie: Türen 128,
    /// Tore 192, Bögen 160; 0 = geschlossen. Die Maße sind geschätzt. Fenster bleiben bewusst
    /// geschlossen: Ganze Glashäuser bestehen aus „Fensterwänden“ und sähen sonst wie Zäune aus.
    /// </summary>
    static double OpeningOf(string cls)
    {
        var c = SaveGame.ShortClass(cls).ToLowerInvariant();
        if (c.Contains("gate")) return 192;
        if (c.Contains("door")) return 128;
        if (c.Contains("arch") && !c.Contains("archer")) return 160;
        return 0;
    }

    static Point[] BridgeOutline(string cls, Func<double, double, Point> T)
    {
        var (from, to) = Support.BridgeSpan(cls);
        double h = Grid / 2 - 4;
        return [T(from + 4, -h), T(to - 4, -h), T(to - 4, h), T(from + 4, h)];
    }

    static void AddPiece(StreamGeometryContext ctx, BuildingPiece p)
    {
        var draw = DrawOf(Shape(p.Class));
        if (draw == PieceDraw.Post)
        {
            // Pfeiler: winziger Strich in Blickrichtung, die Stiftbreite macht daraus ein Quadrat
            double a = p.Yaw * Math.PI / 180;
            var d = new Vector(Math.Cos(a), Math.Sin(a)) * 0.5;
            var c = new Point(p.X, p.Y);
            ctx.BeginFigure(c - d, isFilled: false, isClosed: false);
            ctx.LineTo(c + d, isStroked: true, isSmoothJoin: false);
            return;
        }
        if (draw == PieceDraw.Line)
        {
            // Wand oder Zaun als Mittellinie; die Breite kommt beim Zeichnen vom Stift.
            // Türen, Tore und Bögen bekommen in der Mitte eine Lücke.
            double a = p.Yaw * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
            var along = new Vector(-sin, cos);
            var c = new Point(p.X, p.Y);
            double gap = OpeningOf(p.Class) / 2;
            if (gap <= 0)
            {
                ctx.BeginFigure(c - along * (Grid / 2), isFilled: false, isClosed: false);
                ctx.LineTo(c + along * (Grid / 2), isStroked: true, isSmoothJoin: false);
                return;
            }
            // Die eckigen Linienenden ragen noch etwas in die Lücke; deshalb sind die Maße großzügig
            ctx.BeginFigure(c - along * (Grid / 2), isFilled: false, isClosed: false);
            ctx.LineTo(c - along * gap, isStroked: true, isSmoothJoin: false);
            ctx.BeginFigure(c + along * gap, isFilled: false, isClosed: false);
            ctx.LineTo(c + along * (Grid / 2), isStroked: true, isSmoothJoin: false);
            return;
        }
        var pts = Outline(p);
        ctx.BeginFigure(pts[0], isFilled: true, isClosed: true);
        ctx.PolyLineTo(pts[1..], isStroked: true, isSmoothJoin: false);
    }

    // ---------- Höhenschnitt ----------

    /// <summary>
    /// Alles im Streifen entlang der Schnittlinie, umgerechnet in Strecke ab A und Höhe.
    /// Reihenfolge streng von hinten nach vorne, damit Näheres Entfernteres verdeckt. Mod-Inhalte
    /// bekommen einen Markierungspunkt, der obenauf gezeichnet wird.
    /// </summary>
    public List<SectionItem> SectionItems()
    {
        var items = new List<(double Depth, SectionItem Item)>();
        Brush? MarkerFor(int status) => status switch { 1 => ModBrush, 2 => SelectedModBrush, 3 => RiskBrush, _ => null };
        if (_section is not { } sec) return [];
        var dir = sec.B - sec.A;
        double length = dir.Length;
        dir.Normalize();
        double Along(Point p) => (p - sec.A) * dir;
        // Abstand zur Linie, positiv nach vorne (zum Betrachter)
        double Across(Point p) => (p - sec.A).X * -dir.Y + (p - sec.A).Y * dir.X;
        bool Inside(Point p) => Across(p) <= _sectionFront && Across(p) >= -_sectionBack && Along(p) >= -Grid && Along(p) <= length + Grid;

        var area = new Rect(sec.A, sec.B);
        double reach = Math.Max(_sectionFront, _sectionBack) + Grid;
        area.Inflate(reach, reach);
        foreach (var b in VisibleBuildings().Where(b => area.IntersectsWith(Inflated(b.Bounds, Grid))))
        {
            var g = GeometryFor(b);
            var owner = ((SolidColorBrush)OwnerBrush(b.Owner)).Color;
            foreach (var p in b.Pieces)
            {
                // Mitte des Umrisses statt Drehpunkt: Brücken haben ihren Drehpunkt am Ende
                var outline = Outline(p);
                var center = new Point(outline.Average(q => q.X), outline.Average(q => q.Y));
                if (!Inside(center)) continue;
                int status = PieceStatus(p, g.Risk);
                if (OnlyMods && status == 0) continue;
                var s = outline.Select(Along).ToList();
                var (z0, z1) = Support.Extent(p);
                var fill = status switch { 1 => ModBrush, 2 => SelectedModBrush, 3 => RiskBrush, _ => Frozen(Lighten(owner, g.Levels.GetValueOrDefault(p))) };
                Pen? edge = status switch
                {
                    1 => FrozenPen(ModEdge, 1),
                    2 => FrozenPen(SelectedModEdge, 1),
                    3 => FrozenPen(RiskEdge, 1),
                    _ => FrozenPen(Darken(((SolidColorBrush)fill).Color, 0.62), 1),
                };
                string label = Ownership.PrettyClass(p.Class)
                    + (ModPaths.FolderOf(p.Class) is { } f ? $" [{f}]" : "")
                    + (status == 3 ? " – " + L.T("steht nur auf Teilen angehakter Mods") : "")
                    + " · Z " + p.Z.ToString("N0", Numbers);
                items.Add((Across(center), new SectionItem(s.Min(), s.Max(), z0, z1, fill, edge, false, label, MarkerFor(status), b, p)));
            }
        }
        foreach (var o in VisibleObjects())
        {
            var pt = new Point(o.Object.X, o.Object.Y);
            if (!Inside(pt)) continue;
            int status = Status(o);
            var fill = status switch { 2 => SelectedModBrush, 1 => ModBrush, _ => KindBrush(o.Kind) };
            string label = Ownership.PrettyClass(o.Object.Class) + (o.ModFolder is { } f ? $" [{f}]" : "") + " · Z " + o.Object.Z.ToString("N0", Numbers);
            double s = Along(pt);
            if (o.Kind == MapObjectKind.Placeable && Support.IsFenceObject(o.Object.Class))
            {
                var (a, b) = FenceEnds(o.Object);
                double sa = Along(a), sb = Along(b);
                var color = status == 0 ? Frozen(Darken(((SolidColorBrush)OwnerBrush(o.Owner)).Color, 0.55)) : fill;
                // Zäune stehen mit dem Fuß am Bezugspunkt. Türen und Tore haben ihn etwa auf halber Höhe
                // (Türen 20 unter der Rahmenmitte, Tore in der Rahmenmitte), sie reichen also bis zum Boden.
                double bottom = !IsDoorObject(o.Object) ? o.Object.Z
                    : o.Object.Z - (SaveGame.ShortClass(o.Object.Class).Contains("gate", StringComparison.OrdinalIgnoreCase) ? 128 : 108);
                double top = IsDoorObject(o.Object) ? bottom + 220 : bottom + 192;
                items.Add((Across(pt), new SectionItem(Math.Min(sa, sb), Math.Max(sa, sb), bottom, top, color, null, false, label, MarkerFor(status), Object: o)));
                continue;
            }
            items.Add((Across(pt), new SectionItem(s, s, o.Object.Z, o.Object.Z, fill, DotPen, true, label, MarkerFor(status), Object: o)));
        }
        return items.OrderBy(i => i.Depth).Select(i => i.Item).ToList();
    }

    public double SectionLength => _section is { } s ? (s.B - s.A).Length : 0;

    static double Normalize(double a) => ((a % 360) + 540) % 360 - 180;

    static Rect Inflated(Rect r, double d) { r.Inflate(d, d); return r; }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }
}
