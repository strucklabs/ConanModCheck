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
}

enum MapObjectKind { Placeable, Figure, Player }

sealed record MapObject(WorldObject Object, MapObjectKind Kind, long Owner);

/// <summary>
/// Draufsicht auf die Welt: Gebäude, platzierte Objekte, Thralls/Tiere und Spieler.
/// Weltkoordinaten: X nach rechts, Y nach unten (wie die Karte im Spiel).
/// </summary>
sealed class MapView : FrameworkElement
{
    const double Grid = 256;               // Rastermaß der Bauteile
    const double PieceZoomThreshold = 0.02; // ab hier (Pixel je Einheit) werden einzelne Bauteile gezeichnet

    List<MapBuilding> _buildings = [];
    List<MapObject> _objects = [];
    Func<string, bool> _isSelectedMod = _ => false;
    readonly Dictionary<long, (Geometry Normal, Geometry Mod, Geometry Selected)> _geometry = [];

    double _scale = 0.001;  // Pixel je Welteinheit
    Point _offset;          // Bildschirmposition des Weltursprungs
    Point? _dragStart;
    Point _dragOffset;
    bool _dragged;
    bool _fitPending;       // Einpassen, sobald die Karte eine Größe hat (Reiter war beim Laden unsichtbar)

    public MapBuilding? SelectedBuilding { get; private set; }
    public MapObject? SelectedObject { get; private set; }

    /// <summary>Nur diese Ebene (Z) des ausgewählten Gebäudes hervorheben; null = alle.</summary>
    public double? Level { get => _level; set { _level = value; _geometry.Remove(SelectedBuilding?.Id ?? -1); InvalidateVisual(); } }
    double? _level;

    public bool ShowPlaceables { get; set; } = true;
    public bool ShowFigures { get; set; } = true;
    public bool ShowPlayers { get; set; } = true;

    public event Action? SelectionChanged;

    static readonly Brush Background = Frozen(new SolidColorBrush(Color.FromRgb(0xF4, 0xF1, 0xEA)));
    static readonly Pen GridPen = FrozenPen(Color.FromRgb(0xDD, 0xD8, 0xCC), 1);
    static readonly Brush GridText = Frozen(new SolidColorBrush(Color.FromRgb(0x99, 0x93, 0x86)));
    public static readonly Brush ModBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE8, 0x59, 0x0C)));
    public static readonly Brush SelectedModBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC9, 0x1A, 0x1A)));
    public static readonly Brush PlaceableBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4C, 0x5D, 0x70)));
    public static readonly Brush FigureBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2B, 0x8A, 0x3E)));
    public static readonly Brush PlayerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC2, 0x25, 0x5C)));
    static readonly Pen SelectionPen = FrozenPen(Color.FromRgb(0x1C, 0x7E, 0xD6), 2);

    /// <summary>Gut unterscheidbare Farben für Besitzer.</summary>
    public static readonly Brush[] OwnerBrushes = new[]
    {
        Color.FromRgb(0x5F, 0x7A, 0x96), Color.FromRgb(0x8C, 0x6D, 0x46), Color.FromRgb(0x6A, 0x8E, 0x5A),
        Color.FromRgb(0x84, 0x5E, 0x8E), Color.FromRgb(0x4E, 0x8C, 0x8A), Color.FromRgb(0x9A, 0x8A, 0x4A),
        Color.FromRgb(0x7A, 0x7A, 0x7A), Color.FromRgb(0xA0, 0x60, 0x60),
    }.Select(c => Frozen(new SolidColorBrush(c))).ToArray();

    public MapView()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Hand;
    }

    public void SetData(List<MapBuilding> buildings, List<MapObject> objects, Func<string, bool> isSelectedMod, bool keepView)
    {
        var selectedId = SelectedBuilding?.Id;
        _buildings = buildings;
        _objects = objects;
        _isSelectedMod = isSelectedMod;
        _geometry.Clear();
        SelectedBuilding = selectedId != null ? buildings.FirstOrDefault(b => b.Id == selectedId) : null;
        SelectedObject = null;
        if (!keepView) _fitPending = true;
        InvalidateVisual();
    }

    /// <summary>Nach Änderung der Mod-Auswahl die Farben neu berechnen.</summary>
    public void RefreshColors()
    {
        _geometry.Clear();
        InvalidateVisual();
    }

    public Brush OwnerBrush(long owner) => OwnerBrushes[(int)((owner % OwnerBrushes.Length + OwnerBrushes.Length) % OwnerBrushes.Length)];

    // ---------- Ansicht ----------

    public void FitAll()
    {
        if (ActualWidth < 50 || ActualHeight < 50) { _fitPending = true; return; }
        _fitPending = false;
        var all = _buildings.Select(b => b.Bounds).Concat(_objects.Select(o => new Rect(o.Object.X, o.Object.Y, 1, 1))).ToList();
        if (all.Count == 0) return;
        var r = all.Aggregate(Rect.Union);
        FitRect(r);
    }

    public void FitRect(Rect r)
    {
        double w = Math.Max(ActualWidth, 200), h = Math.Max(ActualHeight, 200);
        r.Inflate(Math.Max(r.Width, 2000) * 0.05, Math.Max(r.Height, 2000) * 0.05);
        _scale = Math.Min(w / r.Width, h / r.Height);
        _offset = new Point(w / 2 - (r.X + r.Width / 2) * _scale, h / 2 - (r.Y + r.Height / 2) * _scale);
        InvalidateVisual();
    }

    public void ZoomToSelection()
    {
        if (SelectedBuilding != null) FitRect(SelectedBuilding.Bounds);
        else if (SelectedObject != null)
        {
            var o = SelectedObject.Object;
            FitRect(new Rect(o.X - 2000, o.Y - 2000, 4000, 4000));
        }
    }

    Point ToScreen(double x, double y) => new(x * _scale + _offset.X, y * _scale + _offset.Y);
    Point ToWorld(Point p) => new((p.X - _offset.X) / _scale, (p.Y - _offset.Y) / _scale);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var p = e.GetPosition(this);
        var world = ToWorld(p);
        double factor = e.Delta > 0 ? 1.25 : 0.8;
        _scale = Math.Clamp(_scale * factor, 1e-5, 2);
        _offset = new Point(p.X - world.X * _scale, p.Y - world.Y * _scale);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _dragStart = e.GetPosition(this);
        _dragOffset = _offset;
        _dragged = false;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (!_dragged && (p - start).Length < 4) return;
        _dragged = true;
        Cursor = Cursors.SizeAll;
        _offset = _dragOffset + (p - start);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        Cursor = Cursors.Hand;
        if (_dragStart != null && !_dragged) Select(e.GetPosition(this));
        _dragStart = null;
    }

    void Select(Point screen)
    {
        SelectedObject = null;
        SelectedBuilding = null;
        _level = null;

        // Zuerst Objekte (kleine Punkte), dann Gebäude
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
            foreach (var b in _buildings)
            {
                var bounds = b.Bounds;
                bounds.Inflate(tolerance, tolerance);
                if (!bounds.Contains(w)) continue;
                double d = b.Pieces.Min(p => Math.Abs(p.X - w.X) + Math.Abs(p.Y - w.Y));
                if (d < bestDist) { bestDist = d; SelectedBuilding = b; }
            }
            if (bestDist > tolerance * 2) SelectedBuilding = null;
        }
        _geometry.Clear();
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    IEnumerable<MapObject> VisibleObjects() => _objects.Where(o => o.Kind switch
    {
        MapObjectKind.Placeable => ShowPlaceables,
        MapObjectKind.Figure => ShowFigures,
        _ => ShowPlayers,
    });

    // ---------- Zeichnen ----------

    protected override void OnRender(DrawingContext dc)
    {
        if (_fitPending && ActualWidth >= 50 && ActualHeight >= 50) FitAll();
        var size = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Background, null, size);
        DrawGrid(dc);

        var view = new Rect(ToWorld(new Point(0, 0)), ToWorld(new Point(ActualWidth, ActualHeight)));
        bool detailed = _scale >= PieceZoomThreshold;

        foreach (var b in _buildings)
        {
            if (!view.IntersectsWith(Inflated(b.Bounds, Grid))) continue;
            var ownerBrush = OwnerBrush(b.Owner);
            if (!detailed)
            {
                // Herausgezoomt: Gebäude als Fläche, mindestens ein paar Pixel groß
                var tl = ToScreen(b.Bounds.Left, b.Bounds.Top);
                var br = ToScreen(b.Bounds.Right, b.Bounds.Bottom);
                var r = new Rect(tl, br);
                if (r.Width < 4) r.Inflate((4 - r.Width) / 2, 0);
                if (r.Height < 4) r.Inflate(0, (4 - r.Height) / 2);
                var brush = b.ModPieces == 0 ? ownerBrush : b.Pieces.Any(p => IsSelectedModPiece(p)) ? SelectedModBrush : ModBrush;
                dc.DrawRectangle(brush, b == SelectedBuilding ? SelectionPen : null, r);
                continue;
            }

            var g = GeometryFor(b);
            dc.PushTransform(new MatrixTransform(_scale, 0, 0, _scale, _offset.X, _offset.Y));
            bool faded = SelectedBuilding == b && _level != null;
            if (faded) dc.PushOpacity(0.18);
            dc.DrawGeometry(ownerBrush, null, g.Normal);
            dc.DrawGeometry(ModBrush, null, g.Mod);
            dc.DrawGeometry(SelectedModBrush, null, g.Selected);
            if (faded)
            {
                dc.Pop();
                var level = LevelGeometry(b);
                dc.DrawGeometry(ownerBrush, null, level.Normal);
                dc.DrawGeometry(ModBrush, null, level.Mod);
                dc.DrawGeometry(SelectedModBrush, null, level.Selected);
            }
            dc.Pop();

            if (b == SelectedBuilding)
                dc.DrawRectangle(null, SelectionPen, new Rect(ToScreen(b.Bounds.Left - Grid / 2, b.Bounds.Top - Grid / 2),
                    ToScreen(b.Bounds.Right + Grid / 2, b.Bounds.Bottom + Grid / 2)));
        }

        double radius = detailed ? 4 : 2.5;
        foreach (var o in VisibleObjects())
        {
            if (!view.Contains(o.Object.X, o.Object.Y)) continue;
            var brush = o.Kind switch { MapObjectKind.Placeable => PlaceableBrush, MapObjectKind.Figure => FigureBrush, _ => PlayerBrush };
            var p = ToScreen(o.Object.X, o.Object.Y);
            dc.DrawEllipse(brush, o == SelectedObject ? SelectionPen : null, p, o.Kind == MapObjectKind.Player ? radius + 2 : radius, o.Kind == MapObjectKind.Player ? radius + 2 : radius);
        }
    }

    void DrawGrid(DrawingContext dc)
    {
        // Linienabstand so wählen, dass etwa alle 100–250 Pixel eine Linie kommt
        double[] steps = [256, 1024, 2560, 5000, 10000, 25000, 50000, 100000, 250000, 500000, 1000000, 2500000];
        double step = steps.FirstOrDefault(s => s * _scale >= 100, steps[^1]);
        var tl = ToWorld(new Point(0, 0));
        var br = ToWorld(new Point(ActualWidth, ActualHeight));
        var typeface = new Typeface("Segoe UI");
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double x = Math.Floor(tl.X / step) * step; x <= br.X; x += step)
        {
            var sx = ToScreen(x, 0).X;
            dc.DrawLine(GridPen, new Point(sx, 0), new Point(sx, ActualHeight));
            dc.DrawText(new FormattedText($"X {x.ToString("N0", Numbers)}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, GridText, dpi), new Point(sx + 3, 2));
        }
        for (double y = Math.Floor(tl.Y / step) * step; y <= br.Y; y += step)
        {
            var sy = ToScreen(0, y).Y;
            dc.DrawLine(GridPen, new Point(0, sy), new Point(ActualWidth, sy));
            dc.DrawText(new FormattedText($"Y {y.ToString("N0", Numbers)}", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 10, GridText, dpi), new Point(3, sy + 2));
        }
    }

    static readonly NumberFormatInfo Numbers = new() { NumberGroupSeparator = "." };

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
        normal.Freeze(); mod.Freeze(); selected.Freeze();
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

    static Rect Inflated(Rect r, double d) { r.Inflate(d, d); return r; }

    static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }
    static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
