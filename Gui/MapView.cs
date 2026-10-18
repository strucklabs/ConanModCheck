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

/// <summary>Ein Kartenbild unter der Karte: abgedeckte Weltfläche und Drehung (Grad, um die Mitte).</summary>
sealed class MapBackground(string key, ImageSource image, Rect world, double angle)
{
    public string Key { get; } = key;
    public ImageSource Image { get; } = image;
    public Rect World { get; set; } = world;
    public double Angle { get; set; } = angle;
}

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

    /// <summary>Kartenbilder der Inseln, schemenhaft unter allem anderen.</summary>
    readonly List<MapBackground> _backgrounds = [];
    public bool ShowBackground { get; set; } = true;

    /// <summary>Ausrichtmodus: Ziehen verschiebt das Kartenbild unter der Maus, das Mausrad skaliert es.</summary>
    public bool AlignMode { get => _alignMode; set { _alignMode = value; Cursor = value ? Cursors.SizeAll : Cursors.Hand; } }
    bool _alignMode;
    int _alignIndex = -1;
    Rect _alignStartRect;

    /// <summary>Ein Kartenbild wurde im Ausrichtmodus verschoben oder skaliert.</summary>
    public event Action<MapBackground>? BackgroundMoved;

    public bool ShowPlaceables { get; set; } = true;
    public bool ShowFigures { get; set; } = true;
    public bool ShowPlayers { get; set; } = true;

    public event Action? SelectionChanged;

    public static readonly Color BackgroundColor = Color.FromRgb(0xF7, 0xF6, 0xF2);
    static readonly Brush Background = Frozen(new SolidColorBrush(BackgroundColor));
    static readonly Pen GridPen = FrozenPen(Color.FromRgb(0xE2, 0xDF, 0xD6), 1);
    static readonly Brush GridText = Frozen(new SolidColorBrush(Color.FromRgb(0x99, 0x95, 0x8B)));

    // Mod-Teile gelb, zum Entfernen angehakte rot – beide mit dunklem Rand, damit sie auch klein trennbar bleiben
    public static readonly Brush ModBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF5, 0xC4, 0x00)));
    static readonly Color ModEdge = Color.FromRgb(0x8A, 0x6A, 0x00);
    public static readonly Brush SelectedModBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xD7, 0x26, 0x3D)));
    static readonly Color SelectedModEdge = Color.FromRgb(0x7A, 0x0E, 0x1C);
    public static readonly Brush PlaceableBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0x3A, 0x46)));
    public static readonly Brush FigureBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1F, 0x9E, 0x89)));
    public static readonly Brush PlayerBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x7B, 0x4F, 0xD6)));
    static readonly Pen DotPen = FrozenPen(Colors.White, 1.5);
    static readonly Pen SelectionPen = FrozenPen(Color.FromRgb(0x1C, 0x7E, 0xD6), 2);

    /// <summary>Besitzerfarben bewusst entsättigt, damit Mod-Teile herausstechen.</summary>
    public static readonly Brush[] OwnerBrushes = new[]
    {
        Color.FromRgb(0x8C, 0x93, 0x9B), Color.FromRgb(0x6F, 0x87, 0x96), Color.FromRgb(0x7F, 0x8F, 0x7A),
        Color.FromRgb(0x98, 0x91, 0x8A), Color.FromRgb(0x7A, 0x7F, 0x99), Color.FromRgb(0x66, 0x70, 0x7A),
        Color.FromRgb(0xA3, 0xA8, 0xAD), Color.FromRgb(0x8F, 0x84, 0x78),
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

    /// <summary>Setzt die Kartenbilder (Graustufen) und die Weltflächen, die sie abdecken.</summary>
    public void SetBackgrounds(IEnumerable<MapBackground> backgrounds)
    {
        _backgrounds.Clear();
        _backgrounds.AddRange(backgrounds);
        InvalidateVisual();
    }

    public MapBackground? GetBackground(string key) => _backgrounds.FirstOrDefault(b => b.Key == key);

    public void UpdateBackground(string key, Rect world, double angle)
    {
        if (GetBackground(key) is not { } b) return;
        b.World = world;
        b.Angle = angle;
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
        if (AlignMode && BackgroundAt(world) is int bi and >= 0)
        {
            // Kartenbild fein um den Mauspunkt skalieren
            var bg = _backgrounds[bi];
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
                bg.Angle += e.Delta > 0 ? 0.1 : -0.1; // Strg + Mausrad: fein drehen
            else
            {
                var r = bg.World;
                double f = e.Delta > 0 ? 1.002 : 1 / 1.002;
                bg.World = new Rect(world.X - (world.X - r.X) * f, world.Y - (world.Y - r.Y) * f, r.Width * f, r.Height * f);
            }
            BackgroundMoved?.Invoke(bg);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
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
        _alignIndex = AlignMode ? BackgroundAt(ToWorld(_dragStart.Value)) : -1;
        if (_alignIndex >= 0) _alignStartRect = _backgrounds[_alignIndex].World;
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
        if (_alignIndex >= 0)
        {
            var d = (p - start) / _scale;
            _backgrounds[_alignIndex].World = new Rect(_alignStartRect.X + d.X, _alignStartRect.Y + d.Y, _alignStartRect.Width, _alignStartRect.Height);
        }
        else _offset = _dragOffset + (p - start);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        Cursor = AlignMode ? Cursors.SizeAll : Cursors.Hand;
        if (_alignIndex >= 0 && _dragged)
            BackgroundMoved?.Invoke(_backgrounds[_alignIndex]);
        else if (_dragStart != null && !_dragged && !AlignMode) Select(e.GetPosition(this));
        _dragStart = null;
        _alignIndex = -1;
    }

    /// <summary>Index des Kartenbilds unter einem Weltpunkt (oder des nächstgelegenen), -1 wenn keins.</summary>
    int BackgroundAt(Point world)
    {
        if (_backgrounds.Count == 0) return -1;
        int hit = _backgrounds.FindIndex(b => b.World.Contains(world));
        if (hit >= 0) return hit;
        return _backgrounds.Select((b, i) => (i, d: (new Point(b.World.X + b.World.Width / 2, b.World.Y + b.World.Height / 2) - world).Length))
            .OrderBy(t => t.d).First().i;
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
        if (ShowBackground && _backgrounds.Count > 0)
        {
            dc.PushTransform(new MatrixTransform(_scale, 0, 0, _scale, _offset.X, _offset.Y));
            foreach (var b in _backgrounds)
            {
                var w = b.World;
                dc.PushTransform(new RotateTransform(b.Angle, w.X + w.Width / 2, w.Y + w.Height / 2));
                dc.DrawImage(b.Image, w);
                dc.Pop();
            }
            dc.Pop();
        }
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
                bool selectedMod = b.ModPieces > 0 && b.Pieces.Any(p => IsSelectedModPiece(p));
                var brush = b.ModPieces == 0 ? ownerBrush : selectedMod ? SelectedModBrush : ModBrush;
                var edge = b == SelectedBuilding ? SelectionPen
                         : b.ModPieces == 0 ? null : new Pen(new SolidColorBrush(selectedMod ? SelectedModEdge : ModEdge), 1);
                dc.DrawRectangle(brush, edge, r);
                continue;
            }

            var g = GeometryFor(b);
            dc.PushTransform(new MatrixTransform(_scale, 0, 0, _scale, _offset.X, _offset.Y));
            bool faded = SelectedBuilding == b && _level != null;
            if (faded) dc.PushOpacity(0.18);
            DrawPieces(dc, ownerBrush, g);
            if (faded)
            {
                dc.Pop();
                DrawPieces(dc, ownerBrush, LevelGeometry(b));
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
            dc.DrawEllipse(brush, o == SelectedObject ? SelectionPen : DotPen, p, o.Kind == MapObjectKind.Player ? radius + 2 : radius, o.Kind == MapObjectKind.Player ? radius + 2 : radius);
        }
    }

    /// <summary>Zeichnet die Bauteile eines Gebäudes; Mod-Teile mit dunklem Rand (1,5 Pixel, unabhängig vom Zoom).</summary>
    void DrawPieces(DrawingContext dc, Brush ownerBrush, (Geometry Normal, Geometry Mod, Geometry Selected) g)
    {
        dc.DrawGeometry(ownerBrush, null, g.Normal);
        double edge = 1.5 / _scale;
        dc.DrawGeometry(ModBrush, new Pen(new SolidColorBrush(ModEdge), edge), g.Mod);
        dc.DrawGeometry(SelectedModBrush, new Pen(new SolidColorBrush(SelectedModEdge), edge), g.Selected);
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

    static Rect Inflated(Rect r, double d) { r.Inflate(d, d); return r; }

    static Brush Frozen(SolidColorBrush b) { b.Freeze(); return b; }
    static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        InvalidateVisual();
    }
}
