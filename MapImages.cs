namespace ConanModCheck;

/// <summary>Ein Kartenbild aus dem Spiel und die Weltfläche, die es abdeckt (quadratisch).</summary>
sealed record MapImageInfo(string Key, string Title, string TexturePath, double CenterX, double CenterY, double Size);

static class MapImages
{
    /// <summary>
    /// Bekannte Kartenbilder. Die Weltflächen sind aus Positionen im Spielstand abgeleitet
    /// (Altäre, Schmieden, Basen) und nicht exakt; sie lassen sich in der Oberfläche nachjustieren.
    /// </summary>
    public static readonly MapImageInfo[] All =
    [
        new("exiled", "Verbannte Lande", "/Game/UI/Textures/GUIs/Map/T_FullscreenMap.uasset", 0, 0, 840_000),
        new("siptah", "Siptah", "/Game/DLC/DLC_Siptah/UI/Map/T_Map_Siptah.uasset", 1_590_000, 65_000, 990_000),
    ];

    /// <summary>In Enhanced liegt Siptah in derselben Welt, weit östlich der Verbannten Lande.</summary>
    public static MapImageInfo IslandOf(double x) => x > 1_000_000 ? All[1] : All[0];
}
