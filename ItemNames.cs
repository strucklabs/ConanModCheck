using ConanModCheck.IoStore;

namespace ConanModCheck;

/// <summary>
/// Übersetzt Item- und Talent-IDs in lesbare Namen, in der gewählten Sprache; fehlt sie, in der
/// anderen, sonst bleibt es bei der Nummer.
/// </summary>
sealed class ItemNames
{
    readonly List<Dictionary<long, TextRef>> _texts = [];
    readonly List<Dictionary<(string, string), string>> _german = [];
    readonly List<Dictionary<(string, string), string>> _english = [];

    public static ItemNames Empty { get; } = new();

    public static ItemNames Build(GameData? game, IEnumerable<ModPackage> mods)
    {
        var n = new ItemNames();
        // Mods zuerst: eine Mod, die ein Spiel-Item umbenennt, soll gewinnen
        foreach (var m in mods)
        {
            n._texts.Add(m.Texts);
            n._german.Add(m.German);
            n._english.Add(m.English);
        }
        if (game != null)
        {
            n._texts.Add(game.Texts);
            n._german.Add(game.German);
            n._english.Add(game.English);
        }
        return n;
    }

    public string? Name(long id)
    {
        foreach (var texts in _texts)
        {
            if (!texts.TryGetValue(id, out var t)) continue;
            var key = (t.Namespace, t.Key);
            var (first, second) = L.English ? (_english, _german) : (_german, _english);
            foreach (var d in first) if (d.TryGetValue(key, out var s) && s.Length > 0) return s;
            foreach (var d in second) if (d.TryGetValue(key, out var s) && s.Length > 0) return s;
            if (t.Source.Length > 0) return t.Source;
        }
        return null;
    }

    /// <summary>"Stahlfeuer (14173)" oder nur "14173", wenn kein Name bekannt ist.</summary>
    public string Label(long id) => Name(id) is { } name ? $"{name} ({id})" : id.ToString();
}
