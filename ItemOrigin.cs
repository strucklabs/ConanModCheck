namespace ConanModCheck;

/// <summary>Woher ein Item stammt: aus dem Spiel (inkl. DLCs) oder aus einer Mod.</summary>
sealed class ItemOrigin
{
    readonly Dictionary<long, string> _modOf = [];
    readonly HashSet<long>? _gameIds;

    public static ItemOrigin Build(GameData? game, IEnumerable<ModPackage> mods)
    {
        var o = new ItemOrigin(game == null ? null : new HashSet<long>(game.ItemIds.Concat(game.FeatIds)));
        foreach (var m in mods.Where(m => m.HasFile))
            foreach (var id in m.NewIds) o._modOf.TryAdd(id, m.Name);
        return o;
    }

    ItemOrigin(HashSet<long>? gameIds) => _gameIds = gameIds;

    /// <summary>Name der Mod, "Mod (Datei nicht geladen)" oder null für Spiel-Items bzw. wenn es sich nicht sagen lässt.</summary>
    public string? ModTag(long id)
    {
        if (_modOf.TryGetValue(id, out var mod)) return mod;
        if (_gameIds != null && !_gameIds.Contains(id)) return L.T("Mod, Datei nicht geladen");
        return null;
    }

    /// <summary>Stammt das Item aus einer Mod, deren Datei geladen ist?</summary>
    public bool IsFromLoadedMod(long id) => _modOf.ContainsKey(id);

    /// <summary>Ohne Spieldateien lässt sich nicht entscheiden, was zum Spiel gehört.</summary>
    public bool CanTellGameItems => _gameIds != null;
}
