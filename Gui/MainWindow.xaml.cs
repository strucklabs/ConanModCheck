using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using ConanModCheck.IoStore;
using Microsoft.Win32;

namespace ConanModCheck.Gui;

public partial class MainWindow : Window
{
    readonly GuiSettings _settings = GuiSettings.Load();
    readonly ObservableCollection<string> _modFiles = [];
    readonly ObservableCollection<FolderRow> _rows = [];

    // Ergebnis der letzten Prüfung
    SaveGame? _save;
    GameData? _game;
    string? _gameLoadedFor;
    List<ModPackage> _mods = [];
    string? _tempDir;
    string? _analyzedDb;
    Cleanup? _plan;
    string _reportText = "";
    bool _busy;

    /// <summary>Nach dem Sprachwechsel die vorherige Prüfung im neuen Fenster wiederholen.</summary>
    bool _analyzeOnLoad;

    public MainWindow()
    {
        InitializeComponent();
        UiTranslator.Apply(this);
        Loaded += async (_, _) =>
        {
            if (_analyzeOnLoad && _settings.DbPath is { } db && File.Exists(db)) await Analyze(db, clearLog: true);
        };
        LanguageBox.SelectedIndex = L.English ? 0 : 1;
        ModList.ItemsSource = _modFiles;
        FolderGrid.ItemsSource = _rows;
        Map.SelectionChanged += Map_SelectionChanged;
        Map.ViewChanged += Map_ViewChanged;
        Map.SectionChanged += RefreshSection;
        Section.ItemClicked += item => Map.Select(item.Building, item.Piece, item.Object);

        if (_settings.DbPath != null && File.Exists(_settings.DbPath)) SetDb(_settings.DbPath);
        foreach (var f in _settings.ModFiles.Where(f => File.Exists(f) || Directory.Exists(f))) _modFiles.Add(f);
        GameDirBox.Text = _settings.GameDir ?? "";
        OodleBox.Text = _settings.OodlePath ?? "";
    }

    // ---------- Auswahl ----------

    void SetDb(string path)
    {
        _settings.DbPath = path;
        DbPathText.Text = path;
    }

    void AddModFiles(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if (!_modFiles.Contains(p, StringComparer.OrdinalIgnoreCase)) _modFiles.Add(p);
    }

    void ChooseDb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = L.T("Spielstand auswählen"),
            Filter = L.T("Conan-Spielstand (*.db)|*.db|Alle Dateien (*.*)|*.*"),
        };
        if (_settings.DbPath != null) dlg.InitialDirectory = Path.GetDirectoryName(_settings.DbPath);
        if (dlg.ShowDialog(this) == true) SetDb(dlg.FileName);
    }

    void AddMods_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = L.T("Mod-Dateien hinzufügen"),
            Filter = L.T("Mod-Dateien (*.zip;*.pak;*.utoc)|*.zip;*.pak;*.utoc|Alle Dateien (*.*)|*.*"),
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) AddModFiles(dlg.FileNames);
    }

    void AddModFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L.T("Ordner mit Mod-Dateien auswählen"), Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddModFiles(dlg.FolderNames);
    }

    void RemoveMod_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in ModList.SelectedItems.Cast<string>().ToList()) _modFiles.Remove(item);
    }

    void ChooseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = L.T("Installationsordner von Conan Exiles auswählen") };
        if (dlg.ShowDialog(this) == true) GameDirBox.Text = dlg.FolderName;
    }

    void ChooseOodle_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = L.T("Oodle-Bibliothek auswählen"), Filter = "Oodle (oo2core_*.dll)|oo2core_*.dll|DLL (*.dll)|*.dll" };
        if (dlg.ShowDialog(this) == true) OodleBox.Text = dlg.FileName;
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files)
        {
            if (f.EndsWith(".db", StringComparison.OrdinalIgnoreCase)) SetDb(f);
            else AddModFiles([f]);
        }
    }

    // ---------- Prüfen ----------

    async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var db = _settings.DbPath;
        if (db == null || !File.Exists(db))
        {
            MessageBox.Show(this, L.T("Bitte zuerst den Spielstand (game.db) auswählen."), Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await Analyze(db, clearLog: true);
    }

    async Task Analyze(string db, bool clearLog)
    {
        var modFiles = _modFiles.ToList();
        string? gameDir = Blank(GameDirBox.Text), oodle = Blank(OodleBox.Text);
        if (oodle != null && !File.Exists(oodle))
        {
            // Die gemerkte DLL ist weg (Spiel deinstalliert o. ä.): neu suchen und später neu fragen
            Log(L.F("Die gemerkte Oodle-Bibliothek fehlt ({0}), es wird neu gesucht.", oodle));
            oodle = null;
            OodleBox.Text = "";
            _settings.OodleChoice = null;
        }
        var previous = _rows.Where(r => r.Remove).Select(r => r.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool sameDb = string.Equals(_analyzedDb, db, StringComparison.OrdinalIgnoreCase);

        SetBusy(true, L.T("Wird geprüft …"));
        if (clearLog) LogBox.Clear();
        else Log("");
        try
        {
            DisposeMods();
            var tempDir = Loader.NewTempDir();
            var (game, mods, save) = await Task.Run(() =>
            {
                var libraries = GameData.SteamLibraries();
                Loader.LoadOodle(oodle, libraries, Log);
                // Spieldaten nur neu laden, wenn sich der Ordner geändert hat
                if (_game == null || _gameLoadedFor != (gameDir ?? ""))
                {
                    _game?.Dispose();
                    _game = Loader.LoadGame(gameDir, libraries, Log);
                    _gameLoadedFor = gameDir ?? "";
                }
                else Log(L.F("Spiel: {0}", _game.GameDir));
                var mods = Loader.LoadMods(modFiles, [], _game, tempDir, Log);
                Log(L.F("Spielstand: {0}", db));
                return (_game, mods, SaveGame.Load(db));
            });
            _tempDir = tempDir;
            _mods = mods;
            _save = save;
            _analyzedDb = db;

            BuildRows(sameDb ? previous : null);
            // Karte und Verzeichnis zuerst: Bericht und Vorschau setzen nur Links auf das, was dort steht
            RefreshMap(keepView: sameDb);
            RefreshDirectory();
            RefreshResults();
            RefreshOwnership();
            AskAboutOodle();
            UpdateOodleInfo();
            SaveSettings();
            SetBusy(false, L.F("Geprüft: {0} Mod-Ordner im Spielstand, {1} Mod-Datei(en) geladen.", _rows.Count, _mods.Count));
            if (game == null && Oodle.IsLoaded)
                Log(L.T("Tipp: Unter „Einstellungen“ lässt sich der Conan Exiles-Ordner angeben."));
        }
        catch (Exception ex)
        {
            Log(L.F("Fehler: {0}", ex.Message));
            SetBusy(false, L.T("Prüfung fehlgeschlagen, Einzelheiten im Verlauf."));
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Füllt die Übersicht. Nichts ist vorab zum Entfernen angehakt: Mod-Dateien dienen zum Erkennen,
    /// angehakt wird von Hand. Bei erneuter Prüfung desselben Spielstands bleibt die bisherige Auswahl.
    /// </summary>
    void BuildRows(HashSet<string>? keepSelection)
    {
        _rows.Clear();
        ShowModDetails(null);
        var save = _save!;
        var stats = Report.FolderStats(save).ToDictionary(s => s.Folder, StringComparer.OrdinalIgnoreCase);
        // Ein Bericht ohne Ausgabe liefert je Mod den Abschnitt für die Detailansicht samt Warnungen
        var describer = new Report(save, [], _game, TextWriter.Null, _mods);
        var described = new Dictionary<ModPackage, (string Text, List<string> Warnings, List<string> Findings)>();
        (string Text, List<string> Warnings, List<string> Findings) Describe(ModPackage m) =>
            described.TryGetValue(m, out var d) ? d : described[m] = describer.DescribeMod(m);
        // Items und Erlerntes lassen sich nur mit Mod-Datei zuordnen
        (string Items, string Learned) Counts(ModPackage? m)
        {
            if (m is not { HasFile: true }) return ("–", "–");
            var (physical, learned) = Report.ItemCounts(save, m);
            return (physical.ToString(L.Numbers), learned.ToString(L.Numbers));
        }
        var folders = new SortedSet<string>(stats.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var m in _mods) folders.UnionWith(m.ModFolders);

        foreach (var f in folders)
        {
            var mod = _mods.FirstOrDefault(m => m.ModFolders.Contains(f));
            var s = stats.GetValueOrDefault(f);
            var d = Describe(mod ?? ModPackage.FromFolder(f));
            var (items, learned) = Counts(mod);
            var row = new FolderRow(f, mod)
            {
                Objects = s?.Objects ?? 0,
                Controllers = s?.Controllers ?? 0,
                Pieces = s?.Pieces ?? 0,
                Props = s?.Props ?? 0,
                Buffs = s?.Buffs ?? 0,
                Items = items,
                Learned = learned,
                Warnings = d.Warnings,
                Details = d.Text,
            };
            row.SetQuietly(keepSelection?.Contains(f) == true);
            row.Changed = OnRowChanged;
            _rows.Add(row);
        }

        // Mods ganz ohne eigenen Ordner (ersetzen nur Spieldateien) trotzdem zeigen
        foreach (var m in _mods.Where(m => m.ModFolders.Count == 0))
        {
            var d = Describe(m);
            var (items, learned) = Counts(m);
            var row = new FolderRow($"({m.Name})", m) { Items = items, Learned = learned, Warnings = d.Warnings, Details = d.Text };
            row.SetQuietly(keepSelection?.Contains(row.Folder) == true);
            row.Changed = OnRowChanged;
            _rows.Add(row);
        }
    }

    /// <summary>Steht das Gebäude oder Objekt mit dieser ID auf der Karte?</summary>
    bool CanShowOnMap(long id) => _mapBuildings.Any(b => b.Id == id) || _mapObjects.Any(o => o.Object.Id == id);

    /// <summary>Sprung aus Bericht oder Details: zur Karte wechseln und die Stelle auswählen.</summary>
    void ShowOnMap(long id)
    {
        if (Tabs.SelectedItem != MapTabItem) Remember();
        Tabs.SelectedItem = MapTabItem;
        // Erst nach dem Umschalten hat die Karte ihre Größe, sonst passt das Einzoomen nicht
        Dispatcher.BeginInvoke(() => Map.SelectAndZoom(id), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>Sprung zu Weltkoordinaten (für Fundorte ohne eigenes Objekt auf der Karte).</summary>
    void ShowOnMapAt(double x, double y)
    {
        if (Tabs.SelectedItem != MapTabItem) Remember();
        Tabs.SelectedItem = MapTabItem;
        Dispatcher.BeginInvoke(() => Map.ShowAt(x, y), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    MapLinks Links => new(_dirObjectIds.Contains, ShowInDirectory, ShowOnMapAt,
        name => _save?.Characters.FirstOrDefault(c => c.Value == name) is { Value: not null } c ? c.Key : null,
        _dirItemIds.Contains, ShowItemInDirectory);

    void FolderGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        ShowModDetails(FolderGrid.SelectedItem as FolderRow);

    void ShowModDetails(FolderRow? row)
    {
        ModDetailView.Document = row == null ? null : ReportDocument.Build(row.Details, Links);
        ModDetailHint.Visibility = row == null ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnRowChanged(FolderRow row)
    {
        // Eine Mod mit mehreren Ordnern wird immer als Ganzes an- oder abgewählt
        if (row.Mod != null)
            foreach (var other in _rows.Where(r => r != row && r.Mod == row.Mod)) other.SetQuietly(row.Remove);
        RefreshResults();
        Map.RefreshColors();
        RefreshFindings();
        RefreshSection();
        UpdateMapInfo();
        RefreshDirectory();
        if (CurrentItemFilter() == ItemFilter.SelectedMods) RefreshOwnership();
        else RecomputeOwnershipMarks();
    }

    void IncludeContents_Changed(object sender, RoutedEventArgs e) => RefreshResults();

    /// <summary>Die ausgewählten Mods: geladene Mod-Dateien und Ordner ohne Datei.</summary>
    List<ModPackage> SelectedMods()
    {
        var result = new List<ModPackage>();
        foreach (var r in _rows.Where(r => r.Remove))
        {
            if (r.Mod != null) { if (!result.Contains(r.Mod)) result.Add(r.Mod); }
            else result.Add(ModPackage.FromFolder(r.Folder));
        }
        return result;
    }

    void RefreshResults()
    {
        if (_save == null) return;
        var selected = SelectedMods();

        var report = new StringWriter();
        new Report(_save, selected, _game, report, _mods).Write();
        _reportText = report.ToString();
        ReportView.Document = ReportDocument.Build(_reportText, Links);

        var preview = new StringWriter();
        bool orphans = IncludeOrphansBox.IsChecked == true;
        if (selected.Count == 0 && !orphans)
        {
            preview.WriteLine(L.T("In der Übersicht ist noch nichts zum Entfernen ausgewählt."));
            _plan = null;
        }
        else
        {
            if (selected.Count > 0) preview.WriteLine(L.F("Ausgewählt: {0}", string.Join(", ", selected.Select(m => m.Name))));
            if (orphans) preview.WriteLine(L.T("Dazu: Reste ohne Besitzer"));
            preview.WriteLine();
            _plan = Cleanup.Plan(_save, selected, _game, IncludeContentsBox.IsChecked == true, orphans, _mods);
            _plan.Preview(preview, ItemNames.Build(_game, _mods));
            if (!_plan.IsEmpty)
                preview.WriteLine(L.T("Hinweis: Das ist nur eine Vorschau. Erst „Jetzt bereinigen …“ ändert den Spielstand."));
        }
        PreviewView.Document = ReportDocument.Build(preview.ToString(), Links);
        ApplyButton.IsEnabled = _plan is { IsEmpty: false } && !_busy;
    }

    // ---------- Bereinigen ----------

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _plan.IsEmpty || _analyzedDb == null || _busy) return;
        var db = _analyzedDb;

        var summary = new StringBuilder();
        summary.AppendLine(L.F("Aus dem Spielstand\n{0}\nwird Folgendes entfernt:", db));
        summary.AppendLine();
        // Welche Mods betroffen sind, mit Namen, damit kein versehentlich angehakter Eintrag durchrutscht
        var chosen = SelectedMods().Select(m => m.HasFile ? m.Name : m.ModFolders.First()).Order(StringComparer.CurrentCultureIgnoreCase).ToList();
        summary.AppendLine(L.F("Zum Entfernen angehakt ({0}):", chosen.Count));
        foreach (var name in chosen.Take(15)) summary.AppendLine("   " + name);
        if (chosen.Count > 15) summary.AppendLine("   " + L.F("… und {0} weitere", chosen.Count - 15));
        summary.AppendLine();
        if (_plan.Objects.Count > 0) summary.AppendLine("• " + L.F("{0} Objekte samt Inhalt (Mod-Steuerung, Gebäude, Platziertes)", _plan.Objects.Count));
        if (_plan.Items.Count > 0) summary.AppendLine("• " + L.F("{0} Items, Rezepte oder Talente", _plan.Items.Count));
        if (_plan.Buffs.Count > 0) summary.AppendLine("• " + L.F("{0} aktive Effekte", _plan.Buffs.Count));
        if (_plan.Orphans is { } o) summary.AppendLine("• " + L.F("Reste ohne Besitzer: {0} Items, {1} Steuerungs-Einträge", o.Items.Count, o.ModControllerIds.Count));
        if (_plan.Skipped.Count > 0) { summary.AppendLine(); summary.AppendLine(L.F("{0} Dinge bleiben stehen (siehe Vorschau).", _plan.Skipped.Count)); }
        summary.AppendLine();
        summary.AppendLine(L.T("Vorher wird eine Sicherungskopie angelegt. Der Server muss gestoppt sein."));
        summary.AppendLine(L.T("Danach die Mods auch aus der modlist.txt nehmen, sonst legt der Server ihre Steuerung neu an."));
        summary.AppendLine();
        summary.Append(L.T("Jetzt bereinigen?"));

        if (MessageBox.Show(this, summary.ToString(), L.T("Bereinigen"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        SetBusy(true, L.T("Wird bereinigt …"));
        var plan = _plan;
        var output = new StringWriter();
        try
        {
            var backup = await Task.Run(() => plan.Apply(db, output));
            Log(output.ToString().TrimEnd());
            SetBusy(false, L.T("Bereinigt."));
            // Erste Zeile der Ausgabe ist der Pfad der Sicherung, der kommt unten gesondert
            var details = string.Join("\n", output.ToString().Trim().Split('\n').Skip(1).Select(l => l.TrimEnd('\r'))).Trim();
            MessageBox.Show(this, details + "\n\n" + L.F("Die Sicherungskopie liegt hier:\n{0}", backup),
                L.T("Bereinigung abgeschlossen"), MessageBoxButton.OK, MessageBoxImage.Information);
            // Neu prüfen, damit Übersicht und Bericht den neuen Stand zeigen
            await Analyze(db, clearLog: false);
            Tabs.SelectedItem = CleanupTab;
        }
        catch (Exception ex)
        {
            Log(L.F("Fehler: {0}", ex.Message));
            SetBusy(false, L.T("Bereinigung fehlgeschlagen, am Spielstand wurde nichts geändert."));
            MessageBox.Show(this, ex.Message, L.T("Bereinigung fehlgeschlagen"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Karte ----------

    /// <summary>Eintrag der Fundstellen-Liste.</summary>
    sealed record Finding(long Id, string Text, System.Windows.Media.Brush Brush, int Status);

    void RefreshMap(bool keepView)
    {
        if (_save == null) return;
        var save = _save;
        var buildings = save.Pieces
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => new MapBuilding
            {
                Id = kv.Key,
                Owner = save.BuildingOwner.GetValueOrDefault(kv.Key),
                Pieces = kv.Value,
                Bounds = new Rect(new Point(kv.Value.Min(p => p.X), kv.Value.Min(p => p.Y)), new Point(kv.Value.Max(p => p.X), kv.Value.Max(p => p.Y))),
                ModPieces = kv.Value.Count(p => ModPaths.FolderOf(p.Class) != null),
                Origin = save.Objects.TryGetValue(kv.Key, out var origin) && !(origin.X == 0 && origin.Y == 0) ? new Point(origin.X, origin.Y) : null,
            })
            .ToList();

        var objects = new List<MapObject>();
        foreach (var o in save.Objects.Values)
        {
            if (save.Pieces.ContainsKey(o.Id) || (o.X == 0 && o.Y == 0)) continue;
            // Mod-Controller liegen unsichtbar am Weltursprung und gehören nicht auf die Karte
            var mod = ModPaths.FolderOf(o.Class) is { } f && !ModPaths.IsController(o.Class) ? f : null;
            if (save.Characters.ContainsKey(o.Id)) objects.Add(new MapObject(o, MapObjectKind.Player, save.CharacterGuild.GetValueOrDefault(o.Id)));
            else if (save.FollowerOwner.TryGetValue(o.Id, out var fo)) objects.Add(new MapObject(o, MapObjectKind.Figure, fo, mod));
            else if (save.FigureOwner.TryGetValue(o.Id, out var g)) objects.Add(new MapObject(o, MapObjectKind.Figure, g, mod));
            else if (save.BuildingOwner.TryGetValue(o.Id, out var b)) objects.Add(new MapObject(o, MapObjectKind.Placeable, b, mod));
            else if (mod != null) objects.Add(new MapObject(o, MapObjectKind.Placeable, 0, mod));
            // Ohne erkennbaren Besitzer (z. B. eingesetzte Thralls, Haustiere): trotzdem zeigen, damit jeder Fundort ein Ziel hat
            else objects.Add(new MapObject(o, IsFigureClass(o.Class) ? MapObjectKind.Figure : MapObjectKind.Placeable, 0));
        }

        _mapBuildings = buildings;
        _mapObjects = objects;
        Map.SetData(buildings, objects, IsFolderSelected, keepView);
        BuildLegend(buildings);
        RefreshFindings();
        UpdateMapInfo();
        RefreshSection();
    }

    void RefreshSection()
    {
        if (Map.Section == null) { SectionPanel.Visibility = Visibility.Collapsed; return; }
        SectionPanel.Visibility = Visibility.Visible;
        SectionTitle.Text = L.F("Höhenschnitt von A nach B, {0:N0} lang", Map.SectionLength);
        Section.SetItems(Map.SectionItems(), Map.SectionLength);
        Section.SetSelection(Map.SelectedBuilding, Map.SelectedPiece, Map.SelectedObject);
    }

    void CloseSection_Click(object sender, RoutedEventArgs e) => Map.Section = null;

    void SectionDepth_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Feuert schon beim Aufbau der Oberfläche, bevor alle Regler existieren
        if (!IsLoaded) return;
        SectionFrontText.Text = SectionFrontSlider.Value.ToString("N0", L.Numbers);
        SectionBackText.Text = SectionBackSlider.Value.ToString("N0", L.Numbers);
        Map.SectionFront = SectionFrontSlider.Value;
        Map.SectionBack = SectionBackSlider.Value;
    }

    static bool IsFigureClass(string cls)
    {
        var c = SaveGame.ShortClass(cls);
        return c.Contains("NPC", StringComparison.OrdinalIgnoreCase) || c.Contains("Thrall", StringComparison.OrdinalIgnoreCase)
            || c.Contains("_pet", StringComparison.OrdinalIgnoreCase) || c.Contains("Mount", StringComparison.OrdinalIgnoreCase);
    }

    List<MapBuilding> _mapBuildings = [];
    List<MapObject> _mapObjects = [];

    bool IsFolderSelected(string folder) => _rows.Any(r => r.Remove && r.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>Alle Stellen mit Mod-Inhalten: angehakte Mods (rot) zuerst, dann übrige Mods (gelb).</summary>
    void RefreshFindings()
    {
        if (_save == null) return;
        var list = new List<Finding>();
        foreach (var b in _mapBuildings.Where(b => b.ModPieces > 0))
        {
            var folders = b.Pieces.Select(p => ModPaths.FolderOf(p.Class)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
            int status = folders.Any(IsFolderSelected) ? 2 : 1;
            int risk = status == 2 ? Map.RiskCount(b) : 0;
            list.Add(new Finding(b.Id, L.P(b.ModPieces, "{0} – Gebäude, 1 Mod-Teil · {2}", "{0} – Gebäude, {1:N0} Mod-Teile · {2}", string.Join(", ", folders), b.ModPieces, MapImages.IslandOf(b.Bounds.X).Name)
                + (risk > 0 ? " · " + L.P(risk, "1 Teil gefährdet", "{0:N0} Teile gefährdet", risk) : ""),
                status == 2 ? MapView.SelectedModBrush : MapView.ModBrush, status));
        }
        foreach (var o in _mapObjects.Where(o => o.ModFolder != null))
        {
            int status = IsFolderSelected(o.ModFolder!) ? 2 : 1;
            var name = o.Kind == MapObjectKind.Figure && _save.FigureNames.TryGetValue(o.Object.Id, out var n) ? n : Ownership.PrettyClass(o.Object.Class);
            list.Add(new Finding(o.Object.Id, $"{o.ModFolder} – {name} · {MapImages.IslandOf(o.Object.X).Name}",
                status == 2 ? MapView.SelectedModBrush : MapView.ModBrush, status));
        }
        FindingsList.ItemsSource = list.OrderByDescending(f => f.Status).ThenBy(f => f.Text, StringComparer.CurrentCultureIgnoreCase).ToList();

        // Was die Liste umfasst und was nicht: Mod-Items in Behältern und Inventaren des Spiels stehen nicht darin
        var hint = new StringBuilder(list.Count == 0 ? L.T("Keine Gebäude oder Objekte aus Mods auf der Karte.")
            : L.F("{0} Fundstellen: Gebäude mit Mod-Teilen und Objekte aus Mods. Klick springt hin.", list.Count));
        var modIds = new HashSet<long>(_mods.SelectMany(m => m.NewIds));
        var modObjects = new HashSet<long>(_mapObjects.Where(o => o.ModFolder != null).Select(o => o.Object.Id));
        var elsewhere = _save.Items.Where(i => modIds.Contains(i.TemplateId) && !modObjects.Contains(i.OwnerId)).ToList();
        if (elsewhere.Count > 0)
            hint.Append(' ').Append(L.F("Nicht hier: {0:N0} Mod-Items, -Rezepte u. Ä. bei Spielern, Figuren und Behältern des Spiels (siehe „Besitz“, „Verzeichnis“).",
                elsewhere.Count));
        FindingsHint.Text = hint.ToString();
    }

    void Findings_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FindingsList.SelectedItem is Finding f) Map.SelectAndZoom(f.Id);
    }

    void BuildLegend(List<MapBuilding> buildings)
    {
        LegendPanel.Children.Clear();
        void Entry(System.Windows.Media.Brush brush, string text, bool round = false)
        {
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(round
                ? new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = brush, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }
                : new System.Windows.Shapes.Rectangle { Width = 12, Height = 12, Fill = brush, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 210 });
            LegendPanel.Children.Add(row);
        }
        foreach (var owner in buildings.GroupBy(b => b.Owner).OrderByDescending(g => g.Count()))
            Entry(Map.OwnerBrush(owner.Key), L.F("{0} ({1} Gebäude)", _save!.OwnerName(owner.Key), owner.Count()));
        Entry(MapView.ModBrush, L.T("Mod-Inhalte"));
        Entry(MapView.SelectedModBrush, L.T("Mod-Inhalte, die in der Übersicht zum Entfernen angehakt sind"));
        Entry(MapView.RiskBrush, L.T("Bauteile, die nur auf angehakten Mod-Teilen stehen und einstürzen könnten (Näherung)"));
        var hint = new System.Windows.Controls.TextBlock { Text = L.T("Höhere Stockwerke sind heller gezeichnet, Wände und Zäune als dunkle Linien, Pfeiler als dunkle Quadrate. Helle Lücken in Wänden sind Türen, Tore und Bögen; ein dünner Strich darin ist ein Türflügel."), TextWrapping = TextWrapping.Wrap, MaxWidth = 228, Margin = new Thickness(0, 2, 0, 2) };
        LegendPanel.Children.Add(hint);
        Entry(MapView.GroupBrush, L.T("Gruppe mit Anzahl (herausgezoomt); Farbe nach dem wichtigsten Inhalt"), round: true);
        Entry(MapView.PlaceableBrush, L.T("Platzierte Objekte (Truhen, Werkbänke …)"), round: true);
        Entry(MapView.FigureBrush, L.T("Thralls und Tiere"), round: true);
        Entry(MapView.PlayerBrush, L.T("Spieler"), round: true);
    }

    void MapFit_Click(object sender, RoutedEventArgs e) => Map.FitAll();

    /// <summary>Springt auf eine Insel: auf alles, was dort steht, sonst auf ihre ungefähre Fläche.</summary>
    void MapIsland_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        var rects = _mapBuildings.Where(b => MapImages.IslandOf(b.Bounds.X).Key == key).Select(b => b.Bounds)
            .Concat(_mapObjects.Where(o => MapImages.IslandOf(o.Object.X).Key == key).Select(o => new Rect(o.Object.X, o.Object.Y, 1, 1)))
            .ToList();
        if (rects.Count > 0) Map.FitRect(rects.Aggregate(Rect.Union));
        else
        {
            var info = MapImages.All.First(m => m.Key == key);
            Map.FitRect(new Rect(info.CenterX - info.Size / 2, info.CenterY - info.Size / 2, info.Size, info.Size));
        }
    }

    void ResetRotation_Click(object sender, RoutedEventArgs e) => Map.ResetRotation();

    void Map_ViewChanged() =>
        ResetRotationButton.Visibility = Math.Abs(Map.Angle) > 0.01 ? Visibility.Visible : Visibility.Collapsed;

    void MapFilter_Changed(object sender, RoutedEventArgs e)
    {
        // Feuert schon beim Laden der Oberfläche, solange noch nicht alle Haken existieren
        if (!IsLoaded) return;
        Map.ShowPlaceables = ShowPlaceablesBox.IsChecked == true;
        Map.ShowFigures = ShowFiguresBox.IsChecked == true;
        Map.ShowPlayers = ShowPlayersBox.IsChecked == true;
        Map.OnlyMods = OnlyModsBox.IsChecked == true;
        Map.ResolveProbe();
        Map.InvalidateVisual();
        RefreshSection();
    }

    void Map_SelectionChanged()
    {
        UpdateMapInfo();
        Section.SetSelection(Map.SelectedBuilding, Map.SelectedPiece, Map.SelectedObject);
    }

    bool _updatingLevels;

    void UpdateMapInfo()
    {
        if (_save == null) return;
        var save = _save;
        LevelPanel.Visibility = Visibility.Collapsed;
        MapTeleportButton.Visibility = Visibility.Collapsed;
        MapDirectoryButton.Visibility = Map.SelectedBuilding != null || Map.SelectedObject != null ? Visibility.Visible : Visibility.Collapsed;

        if (Map.SelectedBuilding is { } b)
        {
            var text = new StringBuilder();
            text.AppendLine(L.F("Gebäude {0}", b.Id));
            text.AppendLine(L.F("Insel: {0}", MapImages.IslandOf(b.Bounds.X).Name));
            text.AppendLine(L.F("Besitzer: {0}", save.OwnerName(b.Owner)));
            text.AppendLine(b.ModPieces > 0
                ? L.P(b.Pieces.Count, "1 Bauteil, aus einer Mod", "{0:N0} Bauteile, davon {1:N0} aus Mods", b.Pieces.Count, b.ModPieces)
                : L.P(b.Pieces.Count, "1 Bauteil", "{0:N0} Bauteile", b.Pieces.Count));
            foreach (var g in b.Pieces.Select(p => ModPaths.FolderOf(p.Class)).Where(f => f != null).GroupBy(f => f).OrderByDescending(g => g.Count()))
                text.AppendLine($"   {g.Key}: {g.Count():N0}");
            if (Map.RiskCount(b) is > 0 and var risk)
                text.AppendLine(L.P(risk, "1 Teil steht nur auf Teilen der angehakten Mods und könnte einstürzen (Näherung).", "{0:N0} Teile stehen nur auf Teilen der angehakten Mods und könnten einstürzen (Näherung).", risk));
            if (save.Objects.TryGetValue(b.Id, out var o)) text.AppendLine(SaveGame.Coords(o));
            // Manche Gebäude haben ihren gespeicherten Bezugspunkt weit neben den Bauteilen
            if (MapView.OriginOffset(b) is > FarOrigin and var off)
                text.AppendLine(L.F("Diese Koordinaten sind der gespeicherte Bezugspunkt des Gebäudes; er liegt {0:N0} Einheiten neben den Bauteilen.", off));
            if (Map.SelectedPiece is { } piece)
            {
                // Das angeklickte Bauteil
                var (level, atRisk) = Map.PieceInfo(b, piece);
                text.AppendLine();
                text.AppendLine(L.F("Bauteil: {0}", Ownership.PrettyClass(piece.Class)));
                text.AppendLine($"   {SaveGame.ShortClass(piece.Class)}");
                text.AppendLine(L.F("Ebene {0}, Höhe Z {1:N0}", level + 1, piece.Z));
                if (ModPaths.FolderOf(piece.Class) is { } pieceMod) text.AppendLine(L.F("Aus der Mod: {0}", pieceMod));
                if (atRisk) text.AppendLine(L.T("Steht nur auf Teilen angehakter Mods und könnte einstürzen (Näherung)."));
            }
            MapInfoText.Text = text.ToString().TrimEnd();

            // Ebenen: dieselbe Stockwerkszählung wie Einfärbung und Bauteil-Info. Zu einer Ebene gehören
            // der Boden und alles, was darauf steht; angezeigt wird die Bodenhöhe (Mittel der Teile).
            var pieceLevels = Map.PieceLevels(b);
            var levels = b.Pieces.GroupBy(p => pieceLevels.GetValueOrDefault(p))
                .OrderBy(g => g.Key)
                .Select(g => (Level: g.Key, FloorZ: g.Average(Support.FloorZ), Count: g.Count()))
                .ToList();
            _updatingLevels = true;
            LevelBox.Items.Clear();
            LevelBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = L.T("Alle Ebenen"), Tag = null });
            foreach (var l in levels)
                LevelBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = L.P(l.Count, "Ebene {0} (Z {1:N0}) – 1 Teil", "Ebene {0} (Z {1:N0}) – {2} Teile", l.Level + 1, l.FloorZ, l.Count), Tag = l.Level });
            // Die gewählte Ebene bleibt erhalten, wenn die Auswahl nur neu beschrieben wird
            LevelBox.SelectedIndex = Math.Max(0, levels.FindIndex(l => l.Level == Map.Level) + 1);
            _updatingLevels = false;
            LevelPanel.Visibility = levels.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            MapTeleportButton.Visibility = o != null ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (Map.SelectedObject is { } m)
        {
            var o = m.Object;
            string name = m.Kind switch
            {
                MapObjectKind.Player => L.F("Spieler „{0}“", save.Characters.GetValueOrDefault(o.Id)),
                MapObjectKind.Figure => save.FigureNames.TryGetValue(o.Id, out var n) ? n : Ownership.PrettyClass(o.Class),
                _ => Ownership.PrettyClass(o.Class),
            };
            var text = new StringBuilder();
            text.AppendLine($"{name} (ID {o.Id})");
            text.AppendLine(L.F("Insel: {0}", MapImages.IslandOf(o.X).Name));
            if (m.Kind != MapObjectKind.Player) text.AppendLine(L.F("Besitzer: {0}", save.OwnerName(m.Owner)));
            else if (save.CharacterGuild.TryGetValue(o.Id, out var g) && save.Guilds.TryGetValue(g, out var gn)) text.AppendLine(L.F("Clan: {0}", gn));
            if (save.ItemsByOwner.TryGetValue(o.Id, out var items))
            {
                var physical = items.Where(i => m.Kind == MapObjectKind.Placeable || i.InvType is 0 or 1 or 2).ToList();
                if (physical.Count > 0) text.AppendLine(L.F("{0:N0} Items (Einzelheiten im Reiter „Besitz“)", physical.Sum(i => (long)i.Quantity)));
            }
            if (ModPaths.FolderOf(o.Class) is { } folder) text.AppendLine(L.F("Aus der Mod: {0}", folder));
            text.Append(SaveGame.Coords(o));
            MapInfoText.Text = text.ToString();
            MapTeleportButton.Visibility = Visibility.Visible;
        }
        else if (Map.ProbeSelected && Map.Probe is { } probe) MapInfoText.Text = ProbeText(save, probe);
        else MapInfoText.Text = L.T("Nichts ausgewählt. Auf ein Gebäude oder Objekt klicken.");
    }

    /// <summary>Erklärung zum „?“: was an der Stelle steht und warum die Karte dort nichts zeichnet.</summary>
    string ProbeText(SaveGame save, MapProbe probe)
    {
        var text = new StringBuilder();
        text.AppendLine(L.T("Warum ist hier nichts zu sehen?"));
        text.AppendLine();
        if (probe.Id is { } id)
        {
            var what = _mapBuildings.Any(b => b.Id == id) ? L.F("Gebäude {0}", id)
                : save.Objects.TryGetValue(id, out var o) ? $"{ObjectName(save, o)} (ID {id})" : $"ID {id}";
            text.AppendLine(L.F("Hier steht: {0}", what));
            text.AppendLine(L.T("Es ist unter „Anzeigen“ ausgeblendet:"));
            foreach (var r in Map.HiddenBecause(id)) text.AppendLine("• " + r);
        }
        else
        {
            // Was laut Spielstand an dieser Stelle liegt, die Karte aber nicht zeichnet
            var there = save.Objects.Values.Where(o => Math.Abs(o.X - probe.At.X) < 1 && Math.Abs(o.Y - probe.At.Y) < 1).ToList();
            if (there.Count == 0) text.AppendLine(L.T("Laut Spielstand steht an dieser Stelle nichts, was die Karte zeichnen kann."));
            else
            {
                text.AppendLine(L.T("Laut Spielstand liegt hier, wird auf der Karte aber nicht gezeichnet:"));
                foreach (var o in there.Take(5)) text.AppendLine($"• {ObjectName(save, o)} (ID {o.Id})");
            }
        }
        text.AppendLine();
        text.Append($"X {probe.At.X:0} / Y {probe.At.Y:0}");
        return text.ToString();
    }

    static string ObjectName(SaveGame save, WorldObject o) =>
        save.Characters.TryGetValue(o.Id, out var player) ? L.F("Spieler „{0}“", player)
        : save.FigureNames.TryGetValue(o.Id, out var n) ? n : Ownership.PrettyClass(o.Class);

    void LevelBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingLevels) return;
        Map.Level = (LevelBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as int?;
    }

    /// <summary>Ab diesem Abstand gilt der Bezugspunkt eines Gebäudes als „neben den Bauteilen“.</summary>
    const double FarOrigin = 300;

    void MapTeleport_Click(object sender, RoutedEventArgs e)
    {
        WorldObject? o = Map.SelectedObject?.Object ?? (Map.SelectedBuilding is { } b ? _save?.Objects.GetValueOrDefault(b.Id) : null);
        if (o != null) CopyTeleport(o);
    }

    /// <summary>Mausrad über Bericht und Vorschau scrollt, auch ohne vorher hineinzuklicken.</summary>
    void DocumentView_MouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is not DependencyObject root) return;
        var viewer = FindChild<System.Windows.Controls.ScrollViewer>(root);
        if (viewer == null) return;
        viewer.ScrollToVerticalOffset(viewer.VerticalOffset - e.Delta / 3.0);
        e.Handled = true;
    }

    static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }


    // ---------- Oodle ----------

    /// <summary>Fragt einmal, wie eine in einem anderen Spiel gefundene Oodle-DLL verwendet werden soll.</summary>
    void AskAboutOodle()
    {
        var loaded = Oodle.LoadedFrom;
        if (loaded == null || OodleLocation.IsNextToProgram(loaded)) return;
        if (_settings.OodleChoice != null || Blank(OodleBox.Text) != null) return; // schon entschieden oder selbst eingetragen

        var dialog = new OodleDialog(loaded, OodleLocation.ProgramDir) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.CopyChosen == null) return; // beim nächsten Mal erneut fragen

        if (dialog.CopyChosen == true) CopyOodle(loaded);
        else
        {
            OodleBox.Text = loaded;
            _settings.OodleChoice = "original";
            Log(L.F("Oodle wird weiter von dort geladen: {0}", loaded));
        }
    }

    void CopyOodle(string source)
    {
        try
        {
            var target = OodleLocation.CopyNextToProgram(source);
            OodleBox.Text = "";
            _settings.OodleChoice = "copy";
            Log(L.F("Oodle neben das Programm kopiert: {0}", target));
            StatusText.Text = L.T("Oodle-Bibliothek kopiert. Ab dem nächsten Start wird sie von dort geladen.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, L.F("Die Datei konnte nicht kopiert werden:\n{0}\n\nSie wird weiter vom ursprünglichen Ort geladen.", ex.Message),
                L.T("Oodle kopieren"), MessageBoxButton.OK, MessageBoxImage.Warning);
            OodleBox.Text = source;
            _settings.OodleChoice = "original";
        }
        UpdateOodleInfo();
        SaveSettings();
    }

    void CopyOodle_Click(object sender, RoutedEventArgs e)
    {
        var source = Blank(OodleBox.Text) ?? Oodle.LoadedFrom;
        if (source != null && File.Exists(source)) CopyOodle(source);
    }

    /// <summary>Zeigt unter „Einstellungen“, woher Oodle geladen wird.</summary>
    void UpdateOodleInfo()
    {
        var loaded = Oodle.LoadedFrom;
        var copied = Path.Combine(OodleLocation.ProgramDir, loaded != null ? Path.GetFileName(loaded) : "");
        if (loaded == null)
        {
            OodleInfoText.Text = L.T("Keine Oodle-Bibliothek gefunden.");
            CopyOodleButton.Visibility = Visibility.Collapsed;
        }
        else if (OodleLocation.IsNextToProgram(loaded) || _settings.OodleChoice == "copy" && File.Exists(copied))
        {
            OodleInfoText.Text = L.F("Geladen aus dem Programmordner:\n{0}", copied);
            CopyOodleButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            var game = OodleLocation.GameName(loaded);
            OodleInfoText.Text = game != null ? L.F("Geladen aus „{0}“:\n{1}", game, loaded) : L.F("Geladen aus einem anderen Ordner:\n{0}", loaded);
            CopyOodleButton.Visibility = Visibility.Visible;
        }
    }

    // ---------- Besitz ----------

    void RefreshOwnership()
    {
        if (_save == null) return;
        var filter = OwnershipSearchBox.Text;
        var mode = CurrentItemFilter();
        var nodes = Ownership.Build(_save, ItemNames.Build(_game, _mods), filter, ItemOrigin.Build(_game, _mods), mode, SelectedModNames());
        bool searching = !string.IsNullOrWhiteSpace(filter) || mode != ItemFilter.All;
        var items = nodes.Select(n => new OwnTreeItem(n, searching)).ToList();
        OwnershipTree.ItemsSource = items;
        RecomputeOwnershipMarks();
        if (searching && nodes.Count == 0)
            StatusText.Text = L.T("Keine passenden Items gefunden.");
    }

    void SearchOwnership_Click(object sender, RoutedEventArgs e) => RefreshOwnership();

    /// <summary>Farbmarkierungen im Besitz-Baum an die aktuelle Auswahl in der Übersicht anpassen; der Baum bleibt aufgeklappt, wie er ist.</summary>
    void RecomputeOwnershipMarks()
    {
        if (OwnershipTree.ItemsSource is not List<OwnTreeItem> items) return;
        var selectedMods = SelectedModNames();
        foreach (var i in items) i.Recompute(selectedMods.Contains, IsFolderSelected);
    }

    void ExpandMarked_Click(object sender, RoutedEventArgs e)
    {
        if (OwnershipTree.ItemsSource is not List<OwnTreeItem> items) return;
        foreach (var i in items) i.ExpandMarked();
    }

    void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        if (OwnershipTree.ItemsSource is not List<OwnTreeItem> items) return;
        foreach (var i in items) i.CollapseAll();
    }

    void ItemFilter_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded) RefreshOwnership();
    }

    ItemFilter CurrentItemFilter() => (ItemFilter)Math.Max(0, ItemFilterBox.SelectedIndex);

    /// <summary>Namen der Mods mit Datei, die in der Übersicht zum Entfernen angehakt sind.</summary>
    HashSet<string> SelectedModNames() =>
        _rows.Where(r => r.Remove && r.Mod is { HasFile: true }).Select(r => r.Mod!.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    void OwnershipSearch_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) { RefreshOwnership(); e.Handled = true; }
    }

    /// <summary>Rechtsklick wählt in beiden Bäumen den Eintrag unter der Maus aus, damit das Menü dazu passt.</summary>
    void Tree_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not System.Windows.Controls.TreeViewItem)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        if (element is System.Windows.Controls.TreeViewItem item) { item.IsSelected = true; item.Focus(); }
    }

    /// <summary>Beim Öffnen (Maus oder Tastatur) nur freischalten, was zur Zeile passt.</summary>
    void OwnershipTree_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        var item = OwnershipTree.SelectedItem as OwnTreeItem;
        bool place = item?.Location is { } o && !(o.X == 0 && o.Y == 0);
        ShowOnMapItem.IsEnabled = CopyTeleportItem.IsEnabled = place;
        ShowInDirectoryItem.IsEnabled = item?.Location is { } d && _dirObjectIds.Contains(d.Id);
        CopyTreeLineItem.IsEnabled = item != null;
    }

    /// <summary>Strg+C kopiert die Zeile, Enter springt wie ein Doppelklick ins Verzeichnis.</summary>
    void OwnershipTree_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (OwnershipTree.SelectedItem is not OwnTreeItem item) return;
        if (e.Key == System.Windows.Input.Key.C && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            CopyLine(item.Text, item.Coords);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter && item.Location is { } o && _dirObjectIds.Contains(o.Id))
        {
            ShowInDirectory(o.Id);
            e.Handled = true;
        }
    }

    void TreeShowOnMap_Click(object sender, RoutedEventArgs e) => ShowTreeItemOnMap();

    void OwnershipTree_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Doppelklick auf ein Objekt springt zu seinem Eintrag im Verzeichnis; sonst klappt der Baum wie gewohnt auf und zu
        if ((OwnershipTree.SelectedItem as OwnTreeItem)?.Location is { } o && _dirObjectIds.Contains(o.Id)) { ShowInDirectory(o.Id); e.Handled = true; }
    }

    void ShowTreeItemOnMap()
    {
        if ((OwnershipTree.SelectedItem as OwnTreeItem)?.Location is not { } o) return;
        if (CanShowOnMap(o.Id)) ShowOnMap(o.Id);
        else ShowOnMapAt(o.X, o.Y);
    }

    void CopyTeleport_Click(object sender, RoutedEventArgs e)
    {
        if ((OwnershipTree.SelectedItem as OwnTreeItem)?.Location is { } o) CopyTeleport(o);
    }

    void CopyTreeLine_Click(object sender, RoutedEventArgs e)
    {
        if (OwnershipTree.SelectedItem is OwnTreeItem item) CopyLine(item.Text, item.Coords);
    }

    /// <summary>Zeile samt Koordinaten in die Zwischenablage.</summary>
    void CopyLine(string text, string coords)
    {
        var line = text.Trim() + (string.IsNullOrEmpty(coords) ? "" : "   " + coords);
        Clipboard.SetText(line);
        StatusText.Text = L.F("Kopiert: {0}", line);
    }

    /// <summary>
    /// Teleport-Befehl zu einem Objekt in die Zwischenablage. Bei Gebäuden, deren gespeicherter Bezugspunkt
    /// neben den Bauteilen liegt, geht es zum Bauteil nahe der Mitte (etwas darüber), wie auf der Karte.
    /// </summary>
    void CopyTeleport(WorldObject o)
    {
        var (x, y, z) = (o.X, o.Y, o.Z);
        if (_save != null && _save.Pieces.TryGetValue(o.Id, out var pieces) && pieces.Count > 0
            && pieces.Min(p => Math.Sqrt((p.X - o.X) * (p.X - o.X) + (p.Y - o.Y) * (p.Y - o.Y))) > FarOrigin)
        {
            double cx = (pieces.Min(p => p.X) + pieces.Max(p => p.X)) / 2, cy = (pieces.Min(p => p.Y) + pieces.Max(p => p.Y)) / 2;
            var p = pieces.MinBy(p => (p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy))!;
            (x, y, z) = (p.X, p.Y, p.Z + 100);
        }
        var cmd = $"TeleportPlayer {x:0} {y:0} {z:0}";
        Clipboard.SetText(cmd);
        StatusText.Text = L.F("Kopiert: {0}", cmd);
    }

    void SaveOwnership_Click(object sender, RoutedEventArgs e)
    {
        if (_save == null) return;
        var dlg = new SaveFileDialog { Title = L.T("Besitz als Text speichern"), Filter = L.T("Textdatei (*.txt)|*.txt"), FileName = L.T("ConanModCheck-Besitz.txt") };
        if (dlg.ShowDialog(this) != true) return;
        using var w = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true));
        Ownership.WriteText(w, Ownership.Build(_save, ItemNames.Build(_game, _mods), OwnershipSearchBox.Text, ItemOrigin.Build(_game, _mods), CurrentItemFilter(), SelectedModNames()));
    }

    // ---------- Bericht ----------

    void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_reportText)) return;
        var dlg = new SaveFileDialog { Title = L.T("Bericht speichern"), Filter = L.T("Textdatei (*.txt)|*.txt"), FileName = L.T("ConanModCheck-Bericht.txt") };
        if (dlg.ShowDialog(this) == true)
            File.WriteAllText(dlg.FileName, _reportText, new UTF8Encoding(true));
    }

    void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_reportText)) Clipboard.SetText(_reportText);
    }

    // ---------- Hilfen ----------

    void Log(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(text)); return; }
        LogBox.AppendText(text + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    void SetBusy(bool busy, string status)
    {
        _busy = busy;
        AnalyzeButton.IsEnabled = !busy;
        ApplyButton.IsEnabled = !busy && _plan is { IsEmpty: false };
        StatusText.Text = status;
        Cursor = busy ? System.Windows.Input.Cursors.AppStarting : null;
    }

    /// <summary>Sprache umstellen: speichern und das Fenster in der neuen Sprache neu aufbauen.</summary>
    void Language_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var code = LanguageBox.SelectedIndex == 1 ? "de" : "en";
        if (code == L.Code) return;
        _settings.Language = code;
        SaveSettings();
        App.ApplyLanguage(code);
        var next = new MainWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left, Top = Top, Width = Width, Height = Height, WindowState = WindowState,
            _analyzeOnLoad = _save != null,
        };
        Application.Current.MainWindow = next;
        next.Show();
        Close();
    }

    void SaveSettings()
    {
        _settings.ModFiles = _modFiles.ToList();
        _settings.GameDir = Blank(GameDirBox.Text);
        _settings.OodlePath = Blank(OodleBox.Text);
        _settings.Save();
    }

    void DisposeMods()
    {
        foreach (var m in _mods) m.Dispose();
        _mods = [];
        if (_tempDir != null) Loader.DeleteTempDir(_tempDir);
        _tempDir = null;
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveSettings();
        DisposeMods();
        _game?.Dispose();
    }

    static string? Blank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
