using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace ConanModCheck.Gui;

/// <summary>
/// Anzeigeform eines Knotens aus dem Baum „Wer hat was“ für die TreeView. Mod-Inhalte werden
/// wie auf der Karte markiert (gelb: Mod, rot: zum Entfernen angehakte Mod); übergeordnete
/// Zeilen übernehmen die wichtigste Markierung ihrer Inhalte.
/// </summary>
public sealed class OwnTreeItem : INotifyPropertyChanged
{
    static readonly Brush ModBack = Frozen(Color.FromRgb(0xFF, 0xF4, 0xCC));
    static readonly Brush SelectedBack = Frozen(Color.FromRgb(0xFD, 0xEC, 0xEC));

    readonly string? _modName;   // Items: Mod, aus der sie stammen
    readonly string? _modFolder; // Behälter, Thralls, Tiere: Mod-Ordner, wenn sie selbst aus einer Mod sind
    readonly bool _isItem;
    bool _isExpanded;
    int _status;

    public string Text { get; }
    public string? Teleport { get; }
    public FontWeight Weight { get; }
    public List<OwnTreeItem> Children { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded == value) return; _isExpanded = value; Changed(nameof(IsExpanded)); }
    }

    /// <summary>0 = nichts aus Mods, 1 = Mod-Inhalt, 2 = Inhalt einer zum Entfernen angehakten Mod.</summary>
    public int Status => _status;

    /// <summary>Farbiges Kästchen vor der Zeile, in den Farben der Karte.</summary>
    public Brush Marker => _status switch { 2 => MapView.SelectedModBrush, 1 => MapView.ModBrush, _ => Brushes.Transparent };

    /// <summary>Items selbst (und Objekte aus Mods) zusätzlich farbig hinterlegt.</summary>
    public Brush RowBackground => (_isItem || _modFolder != null) && _status > 0 ? (_status == 2 ? SelectedBack : ModBack) : Brushes.Transparent;

    internal OwnTreeItem(OwnNode node, bool expand)
    {
        Text = node.Text + (node.Location != null && node.Kind != OwnKind.Figure ? $"   ({SaveGame.Coords(node.Location)})" : "");
        Teleport = node.TeleportCommand;
        Weight = node.Kind is OwnKind.Clan or OwnKind.Player ? FontWeights.SemiBold : FontWeights.Normal;
        _modName = node.ModName;
        _modFolder = node.ModFolder;
        _isItem = node.Kind == OwnKind.Item;
        // Ohne Suche nur die oberen Ebenen aufklappen, bei einer Suche alles
        _isExpanded = expand || node.Kind is OwnKind.Clan or OwnKind.Group && node.Children.Count < 8;
        Children = node.Children.Select(c => new OwnTreeItem(c, expand)).ToList();
    }

    /// <summary>Markierungen neu berechnen, z. B. nachdem in der Übersicht Mods angehakt wurden.</summary>
    /// <param name="modSelected">Ist die Mod (Name der Mod-Datei) zum Entfernen angehakt?</param>
    /// <param name="folderSelected">Ist der Mod-Ordner zum Entfernen angehakt?</param>
    public int Recompute(Func<string, bool> modSelected, Func<string, bool> folderSelected)
    {
        int own = 0;
        if (_modName != null) own = modSelected(_modName) ? 2 : 1;
        if (_modFolder != null) own = Math.Max(own, folderSelected(_modFolder) ? 2 : 1);
        int status = own;
        foreach (var c in Children) status = Math.Max(status, c.Recompute(modSelected, folderSelected));
        if (status != _status)
        {
            _status = status;
            Changed(nameof(Status));
            Changed(nameof(Marker));
            Changed(nameof(RowBackground));
        }
        return status;
    }

    /// <summary>Alle Zweige mit Mod-Inhalten aufklappen, alle anderen zuklappen.</summary>
    public void ExpandMarked()
    {
        IsExpanded = _status > 0 && Children.Count > 0;
        foreach (var c in Children) c.ExpandMarked();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
