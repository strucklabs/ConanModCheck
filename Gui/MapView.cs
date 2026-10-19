using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ConanModCheck.Gui;

enum PieceShape { Square, Triangle, Wall, Pillar }

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
    readonly Dictionary<long, (Geometry Normal, Geometry Mod, Geometry Selected)> _geometry = [];

    // Ansicht: Weltpunkt in der Fenstermitte, Pixel je Welteinheit, Drehung in Grad
    Point _center;
    double _scale = 0.001;
    double _angle;
    bool _hasView;

    // Maus
    enum Drag { None, Pan, Rotate, Zoom }
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

    /// <summary>Nur diese Ebene (Z) des ausgewählten Gebäudes hervorheben; null = alle.</summary>
    public double? Level { get => _level; set { _level = value; InvalidateVisual(); } }
    double? _level;

    public bool ShowPlaceables { get; set; } = true;
    public bool ShowFigures { get; set; } = true;
    public bool ShowPlayers { get; set; } = true;

    /// <summary>Nur Gebäude mit Mod-Teilen und Objekte aus Mods zeigen.</summary>
    public bool OnlyMods { get; set; }

    public double Angle => _angle;

    public event Action? SelectionChanged;
    public event Action? ViewChanged;

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
        SelectedBuilding = selectedBuilding != null ? buildings.FirstOrDefault(b => b.Id == selectedBuilding) : null;
        SelectedObject = selectedObject != null ? objects.FirstOrDefault(o => o.Object.Id == selectedObject) : null;
        if (!keepView || !_hasView) _hasView = false; // beim nächsten Zeichnen alles einpassen
        InvalidateVisual();
    }

    /// <summary>Nach Änderung der Mod-Auswahl die Farben neu berechnen. Die Ansicht bleibt, wo sie ist.</summary>
    public void RefreshColors()
    {
        _geometry.Clear();
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

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) => BeginDrag(e, Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? Drag.Rotate : Drag.Pan);

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
        else if (!_dragged && mode == Drag.Pan) Click(p);

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
    }

    void DrawDetailed(DrawingContext dc, Rect view, Transform matrix)
    {
        foreach (var b in VisibleBuildings())
        {
            if (!view.IntersectsWith(Inflated(b.Bounds, Grid))) continue;
            var ownerBrush = OwnerBrush(b.Owner);
            var g = GeometryFor(b);
            dc.PushTransform(matrix);
            bool faded = SelectedBuilding == b && _level != null;
            if (faded) dc.PushOpacity(0.18);
            DrawPieces(dc, ownerBrush, g, OnlyMods);
            if (faded)
            {
                dc.Pop();
                DrawPieces(dc, ownerBrush, LevelGeometry(b), OnlyMods);
            }
            if (b == SelectedBuilding)
            {
                var sel = Inflated(b.Bounds, Grid / 2);
                dc.DrawRectangle(null, new Pen(SelectionPen.Brush, 2 / _scale), sel);
            }
            dc.Pop();
        }

        foreach (var o in VisibleObjects())
        {
            if (!view.Contains(o.Object.X, o.Object.Y)) continue;
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

    /// <summary>Zeichnet die Bauteile eines Gebäudes; Mod-Teile mit dunklem Rand (1,5 Pixel, unabhängig vom Zoom).</summary>
    void DrawPieces(DrawingContext dc, Brush ownerBrush, (Geometry Normal, Geometry Mod, Geometry Selected) g, bool fadeNormal)
    {
        if (fadeNormal) dc.PushOpacity(0.25);
        dc.DrawGeometry(ownerBrush, null, g.Normal);
        if (fadeNormal) dc.Pop();
        double edge = 1.5 / _scale;
        dc.DrawGeometry(ModBrush, new Pen(new SolidColorBrush(ModEdge), edge), g.Mod);
        dc.DrawGeometry(SelectedModBrush, new Pen(new SolidColorBrush(SelectedModEdge), edge), g.Selected);
    }

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

    (Geometry Normal, Geometry Mod, Geometry Selected) GeometryFor(MapBuilding b)
    {
        if (_geometry.TryGetValue(b.Id, out var g)) return g;
        g = Build(b.Pieces);
        _geometry[b.Id] = g;
        return g;
    }

    (Geometry Normal, Geometry Mod, Geometry Selected) LevelGeometry(MapBuilding b) =>
        Build(b.Pieces.Where(p => _level is { } l && Math.Abs(p.Z - l) < Grid / 2));

    (Geometry, Geometry, Geometry) Build(IEnumerable<BuildingPiece> pieces)
    {
        var normal = new StreamGeometry();
        var mod = new StreamGeometry();
        var selected = new StreamGeometry();
        using (var n = normal.Open())
        using (var m = mod.Open())
        using (var s = selected.Open())
        {
            // Wände und Pfeiler zuletzt, damit sie über den Böden liegen
            foreach (var p in pieces.OrderBy(p => Shape(p.Class) is PieceShape.Wall or PieceShape.Pillar ? 1 : 0).ThenBy(p => p.Z))
            {
                var ctx = IsSelectedModPiece(p) ? s : ModPaths.FolderOf(p.Class) != null ? m : n;
                AddPiece(ctx, p);
            }
        }
        // Übereinanderliegende Teile (Fundament, Decke, Dach) dürfen sich nicht gegenseitig
        // „ausstanzen“, wie es die Standard-Füllregel (gerade/ungerade) täte
        foreach (var g in new[] { normal, mod, selected }) { g.FillRule = FillRule.Nonzero; g.Freeze(); }
        return (normal, mod, selected);
    }

    bool IsSelectedModPiece(BuildingPiece p) => ModPaths.FolderOf(p.Class) is { } f && _isSelectedMod(f);

    /// <summary>Ordnet ein Bauteil anhand seines Klassennamens einer einfachen Form zu.</summary>
    public static PieceShape Shape(string cls)
    {
        var c = SaveGame.ShortClass(cls).ToLowerInvariant();
        if (c.Contains("pillar")) return PieceShape.Pillar;
        if (c.Contains("wall") || c.Contains("fence") || c.Contains("door") || c.Contains("gate") || c.Contains("window")
            || c.Contains("railing") || c.Contains("crenel") || c.Contains("palisade")) return PieceShape.Wall;
        if (c.Contains("triangle")) return PieceShape.Triangle;
        return PieceShape.Square;
    }

    static void AddPiece(StreamGeometryContext ctx, BuildingPiece p)
    {
        double a = p.Yaw * Math.PI / 180, cos = Math.Cos(a), sin = Math.Sin(a);
        // Lokale Koordinaten drehen und an die Position schieben
        Point T(double lx, double ly) => new(p.X + lx * cos - ly * sin, p.Y + lx * sin + ly * cos);

        Point[] pts = Shape(p.Class) switch
        {
            PieceShape.Wall => [T(-10, -Grid / 2), T(10, -Grid / 2), T(10, Grid / 2), T(-10, Grid / 2)],
            PieceShape.Pillar => [T(-22, -22), T(22, -22), T(22, 22), T(-22, 22)],
            // Gleichseitiges Dreieck, Seitenlänge 256, Mittelpunkt im Schwerpunkt
            PieceShape.Triangle => [T(Grid / Math.Sqrt(3), 0), T(-Grid / (2 * Math.Sqrt(3)), Grid / 2), T(-Grid / (2 * Math.Sqrt(3)), -Grid / 2)],
            _ => [T(-Grid / 2 + 4, -Grid / 2 + 4), T(Grid / 2 - 4, -Grid / 2 + 4), T(Grid / 2 - 4, Grid / 2 - 4), T(-Grid / 2 + 4, Grid / 2 - 4)],
        };
        ctx.BeginFigure(pts[0], isFilled: true, isClosed: true);
        ctx.PolyLineTo(pts[1..], isStroked: false, isSmoothJoin: false);
    }

    static double Normalize(double a) => ((a % 360) + 540) % 360 - 180;

    static Rect Inflated(Rect r, double d) { r.Inflate(d, d); return r; }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }
}
