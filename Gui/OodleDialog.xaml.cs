using System.Windows;

namespace ConanModCheck.Gui;

/// <summary>Fragt einmal, ob die gefundene Oodle-DLL neben das Programm kopiert oder weiter vom Fundort geladen werden soll.</summary>
public partial class OodleDialog : Window
{
    /// <summary>true = kopieren, false = vom Fundort laden, null = Fenster ohne Wahl geschlossen.</summary>
    public bool? CopyChosen { get; private set; }

    public OodleDialog(string dllPath, string? targetDir)
    {
        InitializeComponent();
        UiTranslator.Apply(this);
        GameText.Text = OodleLocation.GameName(dllPath) ?? L.T("Unbekannter Ordner");
        PathText.Text = dllPath;
        if (targetDir != null)
            CopyHint.Text += "\n" + L.F("Ziel: {0}", targetDir);
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        CopyChosen = true;
        DialogResult = true;
    }

    void KeepOriginal_Click(object sender, RoutedEventArgs e)
    {
        CopyChosen = false;
        DialogResult = true;
    }
}

/// <summary>Hilfen rund um den Speicherort der Oodle-DLL.</summary>
static class OodleLocation
{
    /// <summary>Ordner, in dem die EXE liegt; eine DLL dort wird beim Start bevorzugt.</summary>
    public static string ProgramDir => AppContext.BaseDirectory;

    public static bool IsNextToProgram(string? dllPath) =>
        dllPath != null && string.Equals(Path.GetDirectoryName(Path.GetFullPath(dllPath))?.TrimEnd('\\'),
            ProgramDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>"…\steamapps\common\Warhammer 40,000 DARKTIDE\binaries\…" → "Warhammer 40,000 DARKTIDE"</summary>
    public static string? GameName(string dllPath)
    {
        var parts = Path.GetFullPath(dllPath).Split(Path.DirectorySeparatorChar);
        for (int i = 0; i + 2 < parts.Length; i++)
            if (parts[i].Equals("steamapps", StringComparison.OrdinalIgnoreCase) && parts[i + 1].Equals("common", StringComparison.OrdinalIgnoreCase))
                return parts[i + 2];
        return null;
    }

    /// <summary>Kopiert die DLL neben die EXE und gibt den neuen Pfad zurück.</summary>
    public static string CopyNextToProgram(string dllPath)
    {
        var target = Path.Combine(ProgramDir, Path.GetFileName(dllPath));
        File.Copy(dllPath, target, overwrite: true);
        return target;
    }
}
