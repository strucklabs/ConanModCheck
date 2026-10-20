using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace ConanModCheck.Gui;

/// <summary>Reiter „Verzeichnis“ und die Sprünge zwischen Bericht, Verzeichnis, Besitz-Baum und Karte.</summary>
public partial class MainWindow
{
    // Einträge der untersten Ebene; der Baum darüber entsteht beim Filtern
    List<DirEntry> _dirObjects = [];
    List<DirEntry> _dirItems = [];
    bool _dirCanTellGameItems;
    readonly HashSet<long> _dirObjectIds = [];
    readonly HashSet<long> _dirItemIds = [];

    /// <summary>Woher gesprungen wurde, für „Zurück“.</summary>
    readonly Stack<TabItem> _history = new();

    /// <summary>Verzeichnis neu aufbauen (nach der Prüfung und wenn sich die Auswahl der Mods ändert).</summary>
    void RefreshDirectory()
    {
        if (_save == null) return;
        var origin = ItemOrigin.Build(_game, _mods);
        var builder = new DirectoryBuilder(_save, ItemNames.Build(_game, _mods), origin, IsFolderSelected, SelectedModNames().Contains);
        _dirObjects = builder.Objects();
        _dirItems = builder.Items();
        _dirCanTellGameItems = origin.CanTellGameItems;
        _dirObjectIds.Clear();
        foreach (var e in _dirObjects) if (e.ObjectId is { } id) _dirObjectIds.Add(id);
        _dirItemIds.Clear();
        foreach (var e in _dirItems) if (e.ItemId is { } id) _dirItemIds.Add(id);
        ApplyDirFilter();
    }

    bool DirShowsItems => DirModeBox.SelectedIndex == 1;

    void ApplyDirFilter()
    {
        if (!IsLoaded) return;
        var source = DirShowsItems ? _dirItems : _dirObjects;
        var term = DirSearchBox.Text.Trim();
        int minStatus = DirFilterBox.SelectedIndex switch { 1 => 1, 2 => 2, _ => 0 };
        var shown = source.Where(e => e.Status >= minStatus
            && (term.Length == 0 || e.SearchText.Contains(term, StringComparison.CurrentCultureIgnoreCase))).ToList();
        var tree = DirectoryBuilder.Tree(shown, DirShowsItems, _dirCanTellGameItems);
        // Bei einer Suche die Gruppen gleich aufklappen, solange es überschaubar bleibt
        if (term.Length > 0 && shown.Count <= 300)
            foreach (var top in tree)
            {
                top.IsExpanded = true;
                foreach (var g in top.Members ?? []) if (g.Members != null) g.IsExpanded = true;
            }
        DirTree.ItemsSource = tree;
    }

    /// <summary>Weg von der obersten Ebene bis zum gesuchten Eintrag (über die Gruppen), sonst null.</summary>
    static List<DirEntry>? FindPath(IEnumerable<DirEntry> level, Func<DirEntry, bool> match)
    {
        foreach (var e in level)
        {
            if (match(e)) return [e];
            if (e.Members != null && FindPath(e.Members, match) is { } rest) { rest.Insert(0, e); return rest; }
        }
        return null;
    }

    void DirFilter_Changed(object sender, SelectionChangedEventArgs e) => ApplyDirFilter();
    void DirSearch_Click(object sender, RoutedEventArgs e) => ApplyDirFilter();
    void DirSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) ApplyDirFilter();
    }

    // ---------- Springen ----------

    /// <summary>Den aktuellen Reiter merken, damit „Zurück“ dorthin führt.</summary>
    void Remember()
    {
        if (Tabs.SelectedItem is TabItem current) _history.Push(current);
        DirBackButton.IsEnabled = MapBackButton.IsEnabled = true;
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count > 0) Tabs.SelectedItem = _history.Pop();
        DirBackButton.IsEnabled = MapBackButton.IsEnabled = _history.Count > 0;
    }

    /// <summary>Zum Eintrag eines Objekts (Gebäude, Behälter, Figur, Spieler) im Verzeichnis.</summary>
    void ShowInDirectory(long objectId) => Open(false, objectId);

    /// <summary>Zum Eintrag einer Item-Art im Verzeichnis.</summary>
    void ShowItemInDirectory(long itemId) => Open(true, itemId);

    void Open(bool items, long id)
    {
        if (Tabs.SelectedItem != DirectoryTabItem) Remember();
        Tabs.SelectedItem = DirectoryTabItem;
        DirModeBox.SelectedIndex = items ? 1 : 0;
        ApplyDirFilter();
        bool Match(DirEntry e) => e.IsLeaf && e.Primary && (items ? e.ItemId : e.ObjectId) == id;
        var path = FindPath((List<DirEntry>)DirTree.ItemsSource, Match);
        if (path == null)
        {
            // Durch Suche oder Filter ausgeblendet: beides zurücksetzen
            DirSearchBox.Text = "";
            DirFilterBox.SelectedIndex = 0;
            ApplyDirFilter();
            path = FindPath((List<DirEntry>)DirTree.ItemsSource, Match);
        }
        if (path == null) return;
        foreach (var e in path) e.IsExpanded = true;
        path[^1].IsSelected = true;
        // Nach dem Umschalten des Reiters die Zeile in den sichtbaren Bereich holen
        Dispatcher.BeginInvoke(() => BringPathIntoView(DirTree, path), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Holt eine Zeile in einer virtualisierten Baumansicht ins Bild: Ebene für Ebene den Container
    /// erzeugen lassen (sonst gibt es ihn noch nicht), dann die Zeile oben ins Bild scrollen.
    /// </summary>
    static void BringPathIntoView(TreeView tree, List<DirEntry> path)
    {
        tree.UpdateLayout();
        ItemsControl parent = tree;
        TreeViewItem? target = null;
        foreach (var entry in path)
        {
            int index = parent.Items.IndexOf(entry);
            if (index < 0) return;
            var item = parent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
            if (item == null)
            {
                if (FindChild<VirtualizingStackPanel>(parent) is { } panel) panel.BringIndexIntoViewPublic(index);
                parent.UpdateLayout();
                item = parent.ItemContainerGenerator.ContainerFromIndex(index) as TreeViewItem;
            }
            if (item == null) return;
            item.UpdateLayout();
            parent = target = item;
        }
        // Die Zeile oben ins Bild holen, damit die aufgeklappten Unterpunkte darunter sichtbar sind.
        // Erst wenn die Auswahl die Zeile ins Bild geholt hat (sonst schiebt sie sie wieder an den unteren Rand)
        tree.Dispatcher.BeginInvoke(() =>
        {
            if (FindChild<ScrollViewer>(tree) is not { } scroll) return;
            if (target is not { } item) return;
            // Pixelweise: Abstand der Zeile zum oberen Rand ausrechnen und so weit scrollen (mit etwas Luft darüber)
            var top = item.TranslatePoint(new Point(0, 0), scroll).Y;
            scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + top - 24));
            item.Focus();
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    /// <summary>Zur Karte: ausgewähltes Objekt, sonst die Koordinaten.</summary>
    void ShowObjectOnMap(WorldObject o)
    {
        if (CanShowOnMap(o.Id)) ShowOnMap(o.Id);
        else ShowOnMapAt(o.X, o.Y);
    }

    // ---------- Verzeichnis: Klicks ----------

    void DirCoords_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Hyperlink)?.DataContext is DirEntry { Location: { } o }) ShowObjectOnMap(o);
    }

    void DirShowOnMap_Click(object sender, RoutedEventArgs e)
    {
        if (DirTree.SelectedItem is DirEntry { Location: { } o }) ShowObjectOnMap(o);
    }

    void DirOpen_Click(object sender, RoutedEventArgs e) => OpenSelectedDirEntry();

    void DirTree_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (OpenSelectedDirEntry()) e.Handled = true;
    }

    /// <summary>Unterpunkt mit Verweis: zum Eintrag des Objekts bzw. der Item-Art springen.</summary>
    bool OpenSelectedDirEntry()
    {
        if (DirTree.SelectedItem is not DirEntry entry) return false;
        // Gruppen und die Einträge selbst sind kein Verweis; nur ihre Unterpunkte springen
        if (entry.IsLeaf || entry.Members != null) return false;
        if (entry.ItemId is { } item && _dirItemIds.Contains(item)) { ShowItemInDirectory(item); return true; }
        if (entry.ObjectId is { } obj && _dirObjectIds.Contains(obj)) { ShowInDirectory(obj); return true; }
        return false;
    }

    // ---------- Besitz-Baum ----------

    void TreeCoords_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Hyperlink)?.DataContext is OwnTreeItem { Location: { } o }) ShowObjectOnMap(o);
    }

    void TreeShowInDirectory_Click(object sender, RoutedEventArgs e)
    {
        if ((OwnershipTree.SelectedItem as OwnTreeItem)?.Location is { } o) ShowInDirectory(o.Id);
    }

    // ---------- Karte ----------

    void MapDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (Map.SelectedBuilding is { } b) ShowInDirectory(b.Id);
        else if (Map.SelectedObject is { } o) ShowInDirectory(o.Object.Id);
    }
}
