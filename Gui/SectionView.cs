using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ConanModCheck.Gui;

/// <summary>
/// Höhenschnitt entlang der Schnittlinie auf der Karte: waagerecht die Strecke von A nach B,
/// senkrecht die Höhe, beides im selben Maßstab. Die Maus über einem Teil zeigt, was es ist.
/// </summary>
sealed class SectionView : FrameworkElement
{
    const double Pad = 34;

    List<SectionItem> _items = [];
    double _length;
    SectionItem? _hover;

    // Beim Zeichnen gemerkt, damit die Maus das Teil darunter findet
    readonly List<(Rect Screen, SectionItem Item)> _hits = [];

    static readonly Brush Background = Frozen(Color.FromRgb(0xF7, 0xF6, 0xF2));
    static readonly Brush TextBrush = Frozen(Color.FromRgb(0x55, 0x52, 0x4B));
    static readonly Pen GridPen = FrozenPen(Color.FromRgb(0xE2, 0xDF, 0xD6), 1);
    static readonly Pen HoverPen = FrozenPen(Color.FromRgb(0x1C, 0x7E, 0xD6), 2);
    static readonly Pen MarkerPen = FrozenPen(Color.FromRgb(0x33, 0x33, 0x33), 1.2);
    static readonly Pen SelectedPen = FrozenPen(Color.FromRgb(0x1C, 0x7E, 0xD6), 3);
    static readonly Pen SelectedOuterPen = FrozenPen(Colors.White, 5);
    static readonly Pen HoverThinPen = FrozenPen(Color.FromArgb(0xB0, 0x1C, 0x7E, 0xD6), 1);

    public SectionView()
    {
        ClipToBounds = true;
    }

    public void SetItems(List<SectionItem> items, double length)
    {
        _items = items;
        _length = length;
        _hover = null;
        InvalidateVisual();
    }

    /// <summary>Klick auf ein Teil im Schnitt.</summary>
    public event Action<SectionItem>? ItemClicked;

    MapBuilding? _selBuilding;
    BuildingPiece? _selPiece;
    MapObject? _selObject;

    /// <summary>Auswahl von der Karte übernehmen: Bauteil kräftig umrandet, übrige Teile des Gebäudes dünn.</summary>
    public void SetSelection(MapBuilding? building, BuildingPiece? piece, MapObject? obj)
    {
        _selBuilding = building;
        _selPiece = piece;
        _selObject = obj;
        InvalidateVisual();
    }

    SectionItem? HitAt(Point p)
    {
        // Zuletzt gezeichnete Teile liegen oben
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            var r = _hits[i].Screen;
            r.Inflate(2, 2);
            if (r.Contains(p)) return _hits[i].Item;
        }
        return null;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (HitAt(e.GetPosition(this)) is { } item) ItemClicked?.Invoke(item);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hit = HitAt(e.GetPosition(this));
        Cursor = hit != null ? Cursors.Hand : null;
        if (hit != _hover) { _hover = hit; InvalidateVisual(); }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover != null) { _hover = null; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        _hits.Clear();
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");
        FormattedText Text(string s, Brush? brush = null) =>
            new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, brush ?? TextBrush, dpi);

        if (_items.Count == 0)
        {
            dc.DrawText(Text(L.T("Nichts im Schnitt. Eine Linie quer durch ein Gebäude ziehen.")), new Point(10, 10));
            return;
        }

        // Ausschnitt: ganze Strecke, Höhe aller Teile; gleicher Maßstab in beiden Richtungen
        double s0 = Math.Min(0, _items.Min(i => i.S0)), s1 = Math.Max(_length, _items.Max(i => i.S1));
        double z0 = _items.Min(i => i.Z0) - 64, z1 = _items.Max(i => i.Z1) + 64;
        double k = Math.Min((w - 2 * Pad) / Math.Max(s1 - s0, 1), (h - 2 * Pad) / Math.Max(z1 - z0, 1));
        if (k <= 0) return;
        double ox = (w - (s1 - s0) * k) / 2 - s0 * k;
        double zMid = (z0 + z1) / 2;
        Point P(double s, double z) => new(ox + s * k, h / 2 - (z - zMid) * k);

        // Höhenlinien im Raster der Stockwerke, bei kleinem Maßstab gröber
        double step = new[] { 256.0, 1024, 2560, 10000 }.FirstOrDefault(st => st * k >= 18, 10000);
        for (double z = Math.Ceiling(z0 / step) * step; z <= z1; z += step)
        {
            var y = P(0, z).Y;
            dc.DrawLine(GridPen, new Point(0, y), new Point(w, y));
            dc.DrawText(Text("Z " + z.ToString("N0", L.Numbers)), new Point(4, y - 14));
        }

        foreach (var item in _items)
        {
            if (item.Dot)
            {
                var c = P(item.S0, item.Z0);
                dc.DrawEllipse(item.Fill, item.Edge, c, 4, 4);
                _hits.Add((new Rect(c.X - 4, c.Y - 4, 8, 8), item));
                continue;
            }
            var a = P(item.S0, item.Z1);
            var b = P(item.S1, item.Z0);
            var r = new Rect(a, b);
            // Quer geschnittene Wände sind sehr schmal: mindestens 2 Pixel breit
            if (r.Width < 2) r = new Rect(r.X + r.Width / 2 - 1, r.Y, 2, r.Height);
            if (r.Height < 2) r = new Rect(r.X, r.Y + r.Height / 2 - 1, r.Width, 2);
            dc.DrawRectangle(item.Fill, r.Width > 4 && r.Height > 4 ? item.Edge : null, r);
            _hits.Add((r, item));
        }

        // Markierungspunkte für Mod-Inhalte obenauf, damit auch verdeckte Teile auffindbar bleiben;
        // sie zählen beim Überfahren mit der Maus als oberste Treffer
        foreach (var (r, item) in _hits.Where(h => h.Item.Marker != null).ToList())
        {
            var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
            dc.DrawEllipse(item.Marker, MarkerPen, c, 4, 4);
            _hits.Add((new Rect(c.X - 4, c.Y - 4, 8, 8), item));
        }

        // Auswahl von der Karte: das Gebäude dünn, das Bauteil bzw. Objekt kräftig umrandet
        foreach (var (r, item) in _hits)
        {
            bool strong = (_selPiece != null && item.Piece == _selPiece) || (_selObject != null && item.Object == _selObject);
            bool weak = !strong && _selPiece == null && _selBuilding != null && item.Building == _selBuilding;
            if (!strong && !weak) continue;
            var box = r;
            box.Inflate(1, 1);
            if (strong) dc.DrawRectangle(null, SelectedOuterPen, box);
            dc.DrawRectangle(null, strong ? SelectedPen : HoverThinPen, box);
        }

        // Enden der Schnittlinie
        var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        foreach (var (s, label) in new[] { (0.0, "A"), (_length, "B") })
        {
            var x = P(s, 0).X;
            dc.DrawLine(HoverPen, new Point(x, h - 14), new Point(x, h - 4));
            dc.DrawText(new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold, 11, HoverPen.Brush, dpi), new Point(x + 3, h - 17));
        }

        if (_hover != null)
        {
            var hit = _hits.First(x => x.Item == _hover);
            if (_hover.Dot) dc.DrawEllipse(null, HoverPen, new Point(hit.Screen.X + 4, hit.Screen.Y + 4), 6, 6);
            else dc.DrawRectangle(null, HoverPen, hit.Screen);
            var text = Text(_hover.Label, Brushes.Black);
            var box = new Rect(w - text.Width - 18, 6, text.Width + 12, text.Height + 6);
            if (box.X < 4) box.X = 4;
            dc.DrawRoundedRectangle(Brushes.White, GridPen, box, 3, 3);
            dc.DrawText(text, new Point(box.X + 6, box.Y + 3));
        }
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    static Pen FrozenPen(Color c, double w) { var p = new Pen(new SolidColorBrush(c), w); p.Freeze(); return p; }
}
