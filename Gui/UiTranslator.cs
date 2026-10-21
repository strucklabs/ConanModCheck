using System.Windows;
using System.Windows.Controls;

namespace ConanModCheck.Gui;

/// <summary>
/// Übersetzt die festen Texte eines Fensters (aus dem XAML) einmal nach dem Laden. So bleiben
/// die XAML-Dateien lesbar, und alle Texte laufen trotzdem über dieselbe Übersetzungstabelle.
/// </summary>
static class UiTranslator
{
    public static void Apply(DependencyObject root)
    {
        if (!L.English) return;
        Walk(root);
    }

    static void Walk(DependencyObject o)
    {
        switch (o)
        {
            case Window w when w.Title is { Length: > 0 }:
                w.Title = L.T(w.Title);
                break;
            case TextBlock t when t.Text is { Length: > 0 } && t.Inlines.Count <= 1:
                t.Text = L.T(t.Text);
                break;
            case DataGrid g:
                foreach (var c in g.Columns)
                    if (c.Header is string h) c.Header = L.T(h);
                break;
        }
        if (o is HeaderedContentControl { Header: string header } hc) hc.Header = L.T(header);
        if (o is HeaderedItemsControl { Header: string ih } hi) hi.Header = L.T(ih);
        if (o is ContentControl { Content: string content } cc and not Window) cc.Content = L.T(content);
        if (o is FrameworkElement { ToolTip: string tip } fe) fe.ToolTip = L.T(tip);
        if (o is MenuItem { InputGestureText: { Length: > 0 } gesture } mi) mi.InputGestureText = L.T(gesture);
        // Kontextmenüs hängen nicht im logischen Baum, sondern an einer Eigenschaft
        if (o is FrameworkElement { ContextMenu: { } menu }) Walk(menu);

        foreach (var child in LogicalTreeHelper.GetChildren(o))
            if (child is DependencyObject d) Walk(d);
    }
}
