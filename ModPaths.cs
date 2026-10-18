namespace ConanModCheck;

/// <summary>Zuordnung von Klassen- und Asset-Pfaden zu Mod-Ordnern.</summary>
static class ModPaths
{
    public const string Root = "/Game/Mods/";

    /// <summary>"/Game/Mods/X/BP_Foo.BP_Foo_C" → "X", sonst null.</summary>
    public static string? FolderOf(string path)
    {
        if (!path.StartsWith(Root, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = path[Root.Length..];
        int slash = rest.IndexOf('/');
        return slash > 0 ? rest[..slash] : null;
    }

    public static bool BelongsTo(ModPackage mod, string path) => FolderOf(path) is { } f && mod.ModFolders.Contains(f);

    /// <summary>Die Steuerung einer Mod, die der Server beim Start für jede aktive Mod anlegt.</summary>
    public static bool IsController(string cls)
    {
        var c = SaveGame.ShortClass(cls);
        return c.Contains("Controller", StringComparison.OrdinalIgnoreCase)
            || c.Contains("Contoller", StringComparison.OrdinalIgnoreCase)  // so geschrieben in manchen Mods
            || c.Contains("ModCon", StringComparison.OrdinalIgnoreCase)
            || c.StartsWith("MC_", StringComparison.OrdinalIgnoreCase)
            || c.EndsWith("MC", StringComparison.OrdinalIgnoreCase);
    }
}
