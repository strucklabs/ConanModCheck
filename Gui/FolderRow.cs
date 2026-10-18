using System.ComponentModel;

namespace ConanModCheck.Gui;

/// <summary>Eine Zeile der Übersicht: ein Mod-Ordner im Spielstand, ggf. mit zugehöriger Mod-Datei.</summary>
sealed class FolderRow(string folder, ModPackage? mod) : INotifyPropertyChanged
{
    bool _remove;

    public string Folder { get; } = folder;
    public ModPackage? Mod { get; } = mod;
    public string ModName => Mod?.Name ?? "";
    public int Objects { get; init; }
    public int Pieces { get; init; }
    public int Props { get; init; }
    public int Buffs { get; init; }

    /// <summary>Wird aufgerufen, wenn der Haken umgesetzt wurde.</summary>
    public Action<FolderRow>? Changed { get; set; }

    public bool Remove
    {
        get => _remove;
        set
        {
            if (_remove == value) return;
            _remove = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Remove)));
            Changed?.Invoke(this);
        }
    }

    /// <summary>Setzt den Haken, ohne <see cref="Changed"/> auszulösen.</summary>
    public void SetQuietly(bool value)
    {
        if (_remove == value) return;
        _remove = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Remove)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
