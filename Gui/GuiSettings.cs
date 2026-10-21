using System.IO;
using System.Text.Json;

namespace ConanModCheck.Gui;

/// <summary>Merkt sich die zuletzt gewählten Pfade, damit sie beim nächsten Start schon eingetragen sind.</summary>
sealed class GuiSettings
{
    public string? DbPath { get; set; }
    public List<string> ModFiles { get; set; } = [];
    public string? GameDir { get; set; }
    public string? OodlePath { get; set; }

    /// <summary>Wie die Oodle-DLL verwendet werden soll: "copy", "original" oder null (noch nicht gefragt).</summary>
    public string? OodleChoice { get; set; }

    /// <summary>"en" oder "de"; ohne Angabe Englisch.</summary>
    public string? Language { get; set; }

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConanModCheck", "settings.json");

    /// <summary>Felder, die beim Laden nicht gelesen werden konnten (leer = alles in Ordnung).</summary>
    public List<string> LoadProblems { get; } = [];

    /// <summary>Kopie einer teilweise unlesbaren Datei, bevor sie beim Schließen überschrieben wird.</summary>
    public static string BackupPath => FilePath + ".bak";

    /// <summary>
    /// Liest jedes Feld einzeln: Ein unlesbares Feld kostet nur dieses Feld, nicht alle Einstellungen.
    /// Eine einzelne Mod-Datei als Text statt als Liste wird übernommen. Gab es Probleme, bleibt die
    /// alte Datei als Kopie erhalten, denn beim Schließen wird sie überschrieben.
    /// </summary>
    public static GuiSettings Load()
    {
        var settings = new GuiSettings();
        if (!File.Exists(FilePath)) return settings;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var root = doc.RootElement;
            string? Text(string name)
            {
                if (!root.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                settings.LoadProblems.Add(name);
                return null;
            }
            settings.DbPath = Text(nameof(DbPath));
            settings.GameDir = Text(nameof(GameDir));
            settings.OodlePath = Text(nameof(OodlePath));
            settings.OodleChoice = Text(nameof(OodleChoice));
            settings.Language = Text(nameof(Language));
            if (root.TryGetProperty(nameof(ModFiles), out var mods))
            {
                if (mods.ValueKind == JsonValueKind.String)
                {
                    if (mods.GetString() is { Length: > 0 } single) settings.ModFiles.Add(single);
                }
                else if (mods.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in mods.EnumerateArray())
                    {
                        if (m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } f) settings.ModFiles.Add(f);
                        else if (!settings.LoadProblems.Contains(nameof(ModFiles))) settings.LoadProblems.Add(nameof(ModFiles));
                    }
                }
                else if (mods.ValueKind != JsonValueKind.Null) settings.LoadProblems.Add(nameof(ModFiles));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            settings = new GuiSettings();
            settings.LoadProblems.Add(L.T("ganze Datei"));
        }
        if (settings.LoadProblems.Count > 0)
        {
            try { File.Copy(FilePath, BackupPath, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* ohne Kopie geht es auch */ }
        }
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* nicht schlimm, dann eben beim nächsten Mal neu wählen */ }
    }
}
