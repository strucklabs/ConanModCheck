using System.Text;
using ConanModCheck;

Console.OutputEncoding = Encoding.UTF8;

string? dbPath = null, gameDir = null, oodlePath = null, outPath = null;
var modInputs = new List<string>();
var folderInputs = new List<string>();
bool noGame = false, clean = false, apply = false, includeContents = false, yes = false, orphans = false, owners = false;
string? search = null;
bool onlyMods = false;

for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Nach {args[i]} fehlt ein Wert.");
    switch (args[i].ToLowerInvariant())
    {
        case "--db": dbPath = Next(); break;
        case "--mod": case "--mods": modInputs.Add(Next()); break;
        case "--game": gameDir = Next(); break;
        case "--no-game": noGame = true; break;
        case "--oodle": oodlePath = Next(); break;
        case "--out": outPath = Next(); break;
        case "--folder": folderInputs.Add(Next()); break;
        case "--clean": clean = true; break;
        case "--apply": clean = apply = true; break;
        case "--include-contents": includeContents = true; break;
        case "--orphans": orphans = true; break;
        case "--besitz": owners = true; break;
        case "--nur-mods": owners = onlyMods = true; break;
        case "--suche": owners = true; search = Next(); break;
        case "--yes": yes = true; break;
        case "-h": case "--help": case "/?": PrintHelp(); return 0;
        default:
            // Ohne Schalter: .db ist der Spielstand, alles andere eine Mod
            if (args[i].EndsWith(".db", StringComparison.OrdinalIgnoreCase)) dbPath = args[i];
            else modInputs.Add(args[i]);
            break;
    }
}

if (dbPath == null) { PrintHelp(); return 1; }
if (!File.Exists(dbPath)) { Console.Error.WriteLine($"Spielstand nicht gefunden: {dbPath}"); return 1; }
if (clean && modInputs.Count == 0 && folderInputs.Count == 0 && !orphans)
{
    Console.Error.WriteLine("Für die Bereinigung bitte mit --mod, --folder oder --orphans angeben, was entfernt werden soll.");
    return 1;
}

var tempDir = Loader.NewTempDir();
var mods = new List<ModPackage>();
GameData? game = null;
try
{
    var libraries = GameData.SteamLibraries();
    if (modInputs.Count > 0 || !noGame) Loader.LoadOodle(oodlePath, libraries, Log);
    if (!noGame) game = Loader.LoadGame(gameDir, libraries, Log);
    mods = Loader.LoadMods(modInputs, folderInputs, game, tempDir, Log);

    Log($"Spielstand: {dbPath}");
    var save = SaveGame.Load(dbPath);
    Log("");

    if (clean)
    {
        if (mods.Any(m => m.HasFile) && game == null)
            Log("Hinweis: Ohne Spieldateien werden Items in Mod-Behältern vorsichtshalber alle als fremd gezählt.");
        var plan = Cleanup.Plan(save, mods, game, includeContents, orphans);
        plan.Preview(Console.Out, ItemNames.Build(game, mods));
        if (!apply)
        {
            Console.WriteLine("Das war nur eine Vorschau, am Spielstand wurde nichts geändert. Zum Ausführen --apply anhängen.");
            return 0;
        }
        if (plan.IsEmpty) return 0;
        if (!yes)
        {
            Console.Write("Jetzt wirklich entfernen? Vorher wird eine Sicherungskopie angelegt. [ja/nein] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "ja", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Abgebrochen, nichts geändert.");
                return 0;
            }
        }
        plan.Apply(dbPath, Console.Out);
        return 0;
    }

    if (owners)
    {
        using var ownWriter = outPath != null ? new StreamWriter(outPath, false, new UTF8Encoding(true)) : null;
        Ownership.WriteText(ownWriter ?? Console.Out, Ownership.Build(save, ItemNames.Build(game, mods), search, ItemOrigin.Build(game, mods), onlyMods ? ItemFilter.AllMods : ItemFilter.All));
        return 0;
    }

    using var writer = outPath != null ? new StreamWriter(outPath, false, new UTF8Encoding(true)) : null;
    TextWriter output = writer ?? Console.Out;
    new Report(save, mods, game, output).Write();
    if (outPath != null) Console.WriteLine($"Bericht gespeichert: {outPath}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Fehler: {ex.Message}");
    return 1;
}
finally
{
    foreach (var m in mods) m.Dispose();
    game?.Dispose();
    Loader.DeleteTempDir(tempDir);
}

static void Log(string text) => Console.Error.WriteLine(text);

static void PrintHelp() => Console.WriteLine("""
    ConanModCheck – prüft, welche Inhalte einer Mod in einem Conan Exiles-Spielstand stecken
    und was beim Entfernen der Mod verloren ginge. Der Spielstand wird nur gelesen, nie verändert.

    Aufruf:
      ConanModCheck-cli --db <game.db> [--mod <Mod>]... [Optionen]

      --db <Datei>      Spielstand des Servers (ConanSandbox/Saved/game.db)
      --mod <Pfad>      Mod-Datei: .pak, .utoc oder ein Zip davon. Auch ein Ordner geht,
                        z. B. ConanSandbox/Saved/ExtractedMods. Mehrfach angebbar.
      --game <Ordner>   Installationsordner von Conan Exiles (sonst automatisch über Steam)
      --no-game         Ohne Spieldateien arbeiten
      --oodle <Datei>   Pfad zu einer oo2core_*_win64.dll (sonst automatisch gesucht)
      --out <Datei>     Bericht in eine Datei schreiben statt in die Konsole
      --besitz          Statt des Berichts zeigen, wer was besitzt (Spieler, Clans,
                        Thralls, Behälter mit Inhalt)
      --suche <Text>    Wie --besitz, aber nur Items, deren Name den Text enthält
      --nur-mods        Wie --besitz, aber nur Items aus Mods
      --folder <Name>   Mod nur über ihren Ordner unter /Game/Mods/ angeben, wenn die
                        Mod-Datei fehlt (z. B. Reste einer schon entfernten Mod)

    Bereinigung:
      --clean           Zeigt, was entfernt würde (Vorschau, ändert nichts)
      --apply           Entfernt es wirklich, nach Rückfrage und mit Sicherungskopie
      --include-contents  Auch Mod-Behälter entfernen, in denen noch andere Items liegen
      --orphans         Auch Reste ohne Besitzer entfernen (Items in Behältern, die es
                        nicht mehr gibt); geht auch ohne --mod
      --yes             Ohne Rückfrage (für Skripte)

      Gebäude aus Mod- und Spiel-Bauteilen bleiben immer stehen, sie müssen im Spiel
      umgebaut werden. Vorher den Server stoppen.

    Ohne --mod zeigt der Bericht eine Übersicht aller Mod-Ordner im Spielstand.
    Tipp: den Server vor dem Kopieren der game.db stoppen.
    """);
