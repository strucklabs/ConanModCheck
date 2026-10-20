namespace ConanModCheck;

/// <summary>
/// Inventar-Typen (item_inventory.inv_type) und was über sie bekannt ist. Bei Behältern, Thralls und
/// Tieren ist alles Inhalt. Bei Spielerfiguren unterscheiden sich die Typen; bekannt sind die, deren
/// Einträge im Spielstand eindeutig zu einer Tabelle des Spiels passen (0–2 Gegenstände, 6 Talente und
/// Rezepte, 7 Lore-Objekte aus LoreObjectsTable, 15 Zauber aus SpellTable). Alle anderen, auch Typ 3,
/// werden angezeigt und Mods zugeordnet, von der Bereinigung aber nie angefasst.
/// </summary>
static class InventoryKinds
{
    /// <summary>Gegenstände einer Spielerfigur: Inventar, Ausrüstung, Schnellleiste.</summary>
    public static bool IsItems(long invType) => invType is 0 or 1 or 2;

    /// <summary>Ist die Bedeutung dieses Inventars einer Spielerfigur bekannt? Nur dann entfernt die Bereinigung Mod-Einträge daraus.</summary>
    public static bool IsKnown(long invType) => invType is 0 or 1 or 2 or 6 or 7 or 15;

    /// <summary>Erlerntes oder Gefundenes einer Spielerfigur (keine Gegenstände, aber bekannte Bedeutung).</summary>
    public static bool IsLearned(long invType) => IsKnown(invType) && !IsItems(invType);

    public static string Name(bool character, long invType) => !character
        ? (invType is 0 or 4 ? L.T("Inhalt") : L.F("Inventar-Typ {0}", invType))
        : invType switch
        {
            0 => L.T("Inventar"),
            1 => L.T("Ausrüstung"),
            2 => L.T("Schnellleiste"),
            6 => L.T("Erlernt"),
            7 => L.T("Gefundene Lore-Objekte"),
            15 => L.T("Zauber"),
            _ => L.F("Inventar-Typ {0} (Bedeutung unbekannt)", invType),
        };
}
