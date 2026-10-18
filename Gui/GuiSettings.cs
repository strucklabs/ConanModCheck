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

    /// <summary>Nachjustierte Lage der Kartenbilder: Insel → [X, Y, Breite, Höhe] in Weltkoordinaten.</summary>
    public Dictionary<string, double[]> MapAlign { get; set; } = [];

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ConanModCheck", "settings.json");

    public static GuiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* kaputte Einstellungen einfach verwerfen */ }
        return new();
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
