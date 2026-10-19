namespace ConanModCheck;

/// <summary>Eine Insel der Spielwelt und ihre ungefähre Fläche (quadratisch, Weltkoordinaten).</summary>
sealed record MapImageInfo(string Key, string Title, double CenterX, double CenterY, double Size)
{
    /// <summary>Name in der gewählten Sprache.</summary>
    public string Name => L.T(Title);
}

static class MapImages
{
    /// <summary>
    /// Die Inseln in Conan Exiles Enhanced. Die Flächen sind aus Positionen im Spielstand abgeleitet
    /// und dienen nur als Rückfall, wenn auf einer Insel nichts steht.
    /// </summary>
    public static readonly MapImageInfo[] All =
    [
        new("exiled", "Verbannte Lande", 0, 0, 840_000),
        new("siptah", "Siptah", 1_590_000, 65_000, 990_000),
    ];

    /// <summary>In Enhanced liegt Siptah in derselben Welt, weit östlich der Verbannten Lande.</summary>
    public static MapImageInfo IslandOf(double x) => x > 1_000_000 ? All[1] : All[0];
}
