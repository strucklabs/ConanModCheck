using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace ConanModCheck.Gui;

/// <summary>
/// Eine Zeile im Verzeichnis: ein Objekt (Gebäude, Behälter, Figur, Spieler), eine Item-Art oder
/// eine Angabe dazu. Unterpunkte entstehen erst beim Aufklappen. Farben wie auf der Karte.
/// </summary>
public sealed class DirEntry : INotifyPropertyChanged
{
    static readonly Brush HintBack = Frozen(Color.FromRgb(0xFF, 0xF4, 0xCC));
    static readonly Brush WarnBack = Frozen(Color.FromRgb(0xFD, 0xEC, 0xEC));

    Func<IEnumerable<DirEntry>>? _load;
    bool _isExpanded, _isSelected;

    public string Text { get; }

    /// <summary>Koordinaten als anklickbarer Text hinter der Zeile, sonst leer.</summary>
    public string Coords { get; }
    public FontWeight Weight { get; init; } = FontWeights.Normal;

    /// <summary>0 = nichts aus Mods, 1 = Mod-Inhalt, 2 = Inhalt einer zum Entfernen angehakten Mod.</summary>
    public int Status { get; }
    public Brush Marker => Status switch { 2 => MapView.SelectedModBrush, 1 => MapView.ModBrush, _ => Brushes.Transparent };
    // Farbig hinterlegte Zeilen bedeuten im Bericht Hinweise und Warnungen; Mod-Inhalte kennzeichnet
    // hier nur das Kästchen vor der Zeile
    public bool HasRowBackground => IsWarning || IsHint;
    public Brush RowBackground => IsWarning ? WarnBack : IsHint ? HintBack : Brushes.Transparent;

    /// <summary>Warnung (rot hinterlegt) bzw. Hinweis (gelb hinterlegt) – z. B. im Reiter „Quests“.</summary>
    internal bool IsWarning { get; init; }
    internal bool IsHint { get; init; }

    /// <summary>Stammt die Zeile selbst aus einer Mod (dann farbig hinterlegt) oder nur ihr Inhalt (dann nur das Kästchen)?</summary>
    internal bool OwnStatus { get; init; }

    /// <summary>Lage in der Welt, für den Sprung zur Karte.</summary>
    internal WorldObject? Location { get; }

    /// <summary>Verweis auf einen Objekt-Eintrag (bei Unterpunkten: Doppelklick springt dorthin).</summary>
    internal long? ObjectId { get; init; }

    /// <summary>Verweis auf einen Item-Eintrag.</summary>
    internal long? ItemId { get; init; }

    /// <summary>Text, in dem die Suche sucht (Zeile, Mod, Besitzer, Klasse).</summary>
    internal string SearchText { get; init; } = "";

    /// <summary>Oberste Ebene, unter der der Eintrag steht: "" = Grundspiel, sonst Mod oder ein Sonderknoten (siehe DirectoryBuilder).</summary>
    internal string Origin { get; init; } = "";

    /// <summary>Name, unter dem gleichartige Einträge zusammengefasst werden; leer = keine Namensebene.</summary>
    internal string Group { get; init; } = "";

    /// <summary>Haupteintrag (Sprungziel). Zweiteinträge: gemischte Gebäude stehen zusätzlich bei jeder beteiligten Mod.</summary>
    internal bool Primary { get; init; } = true;

    /// <summary>Eintrag eines Objekts oder einer Item-Art, also unterhalb der Gruppen (kein Unterpunkt, keine Gruppe).</summary>
    internal bool IsLeaf { get; init; }

    /// <summary>Bei Gruppen: die Einträge darunter, für Sprünge ohne aufzuklappen.</summary>
    internal IReadOnlyList<DirEntry>? Members { get; init; }

    public ObservableCollection<DirEntry> Children { get; } = [];

    internal DirEntry(string text, int status, WorldObject? location = null, Func<IEnumerable<DirEntry>>? load = null)
    {
        Text = text;
        Status = status;
        Location = location;
        Coords = location is { } o && !(o.X == 0 && o.Y == 0) ? SaveGame.Coords(o) : "";
        _load = load;
        // Platzhalter, damit das Aufklapp-Dreieck erscheint
        if (load != null) Children.Add(new DirEntry("…", 0));
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            if (value && _load != null)
            {
                Children.Clear();
                foreach (var c in _load()) Children.Add(c);
                _load = null;
            }
            Changed(nameof(IsExpanded));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; Changed(nameof(IsSelected)); }
    }

    /// <summary>Name für Bildschirmleser und UI Automation (sonst der Klassenname), auch bei noch nicht erzeugten Zeilen.</summary>
    public override string ToString() => string.IsNullOrEmpty(Coords) ? Text : $"{Text}  {Coords}";

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
