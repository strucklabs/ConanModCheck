using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ConanModCheck.Gui;

/// <summary>
/// Grid als Inhalt eines ScrollViewers. Passt alles hinein, füllt es die sichtbare Höhe aus und die
/// Sternzeilen teilen sich den Rest (lange Listen rollen dann in sich). Wird es zu eng, bekommt jede
/// Auto-Zeile ihre volle Höhe und jede Sternzeile ihre MinHeight, und der ScrollViewer rollt das Ganze.
/// So bleibt kein Bedienelement unerreichbar, egal wie lang Listen werden oder wie klein das Fenster ist.
/// </summary>
sealed class FitGrid : Grid
{
    ScrollViewer? _viewer;

    protected override Size MeasureOverride(Size constraint)
    {
        if (_viewer == null && FindViewer() is { } viewer)
        {
            _viewer = viewer;
            // Ändert sich die sichtbare Höhe, muss die Aufteilung neu berechnet werden
            _viewer.SizeChanged += (_, _) => InvalidateMeasure();
        }
        if (!double.IsInfinity(constraint.Height) || _viewer == null) return base.MeasureOverride(constraint);

        // Mindesthöhe: Auto-Zeilen so hoch wie ihr Inhalt, Sternzeilen ihre MinHeight, feste Zeilen ihre Höhe
        var need = new double[Math.Max(1, RowDefinitions.Count)];
        for (int i = 0; i < RowDefinitions.Count; i++)
        {
            var h = RowDefinitions[i].Height;
            need[i] = h.IsAbsolute ? h.Value : RowDefinitions[i].MinHeight;
        }
        foreach (UIElement child in InternalChildren)
        {
            int row = Math.Min(GetRow(child), need.Length - 1);
            if (GetRowSpan(child) != 1 || (RowDefinitions.Count > 0 && !RowDefinitions[row].Height.IsAuto)) continue;
            child.Measure(new Size(constraint.Width, double.PositiveInfinity));
            need[row] = Math.Max(need[row], child.DesiredSize.Height);
        }

        double height = Math.Max(_viewer.ViewportHeight > 0 ? _viewer.ViewportHeight : _viewer.ActualHeight, need.Sum());
        var desired = base.MeasureOverride(new Size(constraint.Width, height));
        return new Size(desired.Width, height);
    }

    ScrollViewer? FindViewer()
    {
        for (DependencyObject? o = VisualTreeHelper.GetParent(this); o != null; o = VisualTreeHelper.GetParent(o))
            if (o is ScrollViewer sv) return sv;
        return null;
    }
}
