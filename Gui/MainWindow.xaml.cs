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

    public MainWindow()
    {
        InitializeComponent();
        ModList.ItemsSource = _modFiles;
        FolderGrid.ItemsSource = _rows;
        Map.SelectionChanged += Map_SelectionChanged;
        Map.BackgroundMoved += bg => { StoreAlignment(bg); SelectAlignIsland(bg.Key); ShowAlignValues(); };
        foreach (var info in MapImages.All)
            AlignIslandBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = info.Title, Tag = info.Key });
        AlignIslandBox.SelectedIndex = 0;

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
            Title = "Spielstand auswählen",
            Filter = "Conan-Spielstand (*.db)|*.db|Alle Dateien (*.*)|*.*",
        };
        if (_settings.DbPath != null) dlg.InitialDirectory = Path.GetDirectoryName(_settings.DbPath);
        if (dlg.ShowDialog(this) == true) SetDb(dlg.FileName);
    }

    void AddMods_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Mod-Dateien hinzufügen",
            Filter = "Mod-Dateien (*.zip;*.pak;*.utoc)|*.zip;*.pak;*.utoc|Alle Dateien (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) AddModFiles(dlg.FileNames);
    }

    void AddModFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Ordner mit Mod-Dateien auswählen", Multiselect = true };
        if (dlg.ShowDialog(this) == true) AddModFiles(dlg.FolderNames);
    }

    void RemoveMod_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in ModList.SelectedItems.Cast<string>().ToList()) _modFiles.Remove(item);
    }

    void ChooseGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Installationsordner von Conan Exiles auswählen" };
        if (dlg.ShowDialog(this) == true) GameDirBox.Text = dlg.FolderName;
    }

    void ChooseOodle_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Title = "Oodle-Bibliothek auswählen", Filter = "Oodle (oo2core_*.dll)|oo2core_*.dll|DLL (*.dll)|*.dll" };
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
            MessageBox.Show(this, "Bitte zuerst den Spielstand (game.db) auswählen.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
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
            Log($"Die gemerkte Oodle-Bibliothek fehlt ({oodle}), es wird neu gesucht.");
            oodle = null;
            OodleBox.Text = "";
            _settings.OodleChoice = null;
        }
        var previous = _rows.Where(r => r.Remove).Select(r => r.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool sameDb = string.Equals(_analyzedDb, db, StringComparison.OrdinalIgnoreCase);

        SetBusy(true, "Wird geprüft …");
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
                else Log($"Spiel: {_game.GameDir}");
                var mods = Loader.LoadMods(modFiles, [], _game, tempDir, Log);
                Log($"Spielstand: {db}");
                return (_game, mods, SaveGame.Load(db));
            });
            _tempDir = tempDir;
            _mods = mods;
            _save = save;
            _analyzedDb = db;

            BuildRows(sameDb ? previous : null);
            RefreshResults();
            RefreshOwnership();
            RefreshMap(keepView: sameDb);
            await LoadMapImages();
            AskAboutOodle();
            UpdateOodleInfo();
            SaveSettings();
            SetBusy(false, $"Geprüft: {_rows.Count} Mod-Ordner im Spielstand, {_mods.Count} Mod-Datei(en) geladen.");
            if (game == null && Oodle.IsLoaded)
                Log("Tipp: Unter „Einstellungen“ lässt sich der Conan Exiles-Ordner angeben.");
        }
        catch (Exception ex)
        {
            Log($"Fehler: {ex.Message}");
            SetBusy(false, "Prüfung fehlgeschlagen, Einzelheiten im Verlauf.");
            MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Füllt die Übersicht. Ordner geladener Mod-Dateien sind vorausgewählt, sonst bleibt die bisherige Auswahl.</summary>
    void BuildRows(HashSet<string>? keepSelection)
    {
        _rows.Clear();
        var stats = Report.FolderStats(_save!).ToDictionary(s => s.Folder, StringComparer.OrdinalIgnoreCase);
        var folders = new SortedSet<string>(stats.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var m in _mods) folders.UnionWith(m.ModFolders);

        foreach (var f in folders)
        {
            var mod = _mods.FirstOrDefault(m => m.ModFolders.Contains(f));
            var s = stats.GetValueOrDefault(f);
            var row = new FolderRow(f, mod)
            {
                Objects = s?.Objects ?? 0,
                Controllers = s?.Controllers ?? 0,
                Pieces = s?.Pieces ?? 0,
                Props = s?.Props ?? 0,
                Buffs = s?.Buffs ?? 0,
            };
            row.SetQuietly(mod != null || keepSelection?.Contains(f) == true);
            row.Changed = OnRowChanged;
            _rows.Add(row);
        }

        // Mods ganz ohne eigenen Ordner (ersetzen nur Spieldateien) trotzdem zeigen
        foreach (var m in _mods.Where(m => m.ModFolders.Count == 0))
        {
            var row = new FolderRow($"({m.Name})", m);
            row.SetQuietly(true);
            row.Changed = OnRowChanged;
            _rows.Add(row);
        }
    }

    void OnRowChanged(FolderRow row)
    {
        // Eine Mod mit mehreren Ordnern wird immer als Ganzes an- oder abgewählt
        if (row.Mod != null)
            foreach (var other in _rows.Where(r => r != row && r.Mod == row.Mod)) other.SetQuietly(row.Remove);
        RefreshResults();
        Map.RefreshColors();
        if (CurrentItemFilter() == ItemFilter.SelectedMods) RefreshOwnership();
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
        ReportView.Document = ReportDocument.Build(_reportText);

        var preview = new StringWriter();
        bool orphans = IncludeOrphansBox.IsChecked == true;
        if (selected.Count == 0 && !orphans)
        {
            preview.WriteLine("In der Übersicht ist noch nichts zum Entfernen ausgewählt.");
            _plan = null;
        }
        else
        {
            if (selected.Count > 0) preview.WriteLine($"Ausgewählt: {string.Join(", ", selected.Select(m => m.Name))}");
            if (orphans) preview.WriteLine("Dazu: Reste ohne Besitzer");
            preview.WriteLine();
            _plan = Cleanup.Plan(_save, selected, _game, IncludeContentsBox.IsChecked == true, orphans);
            _plan.Preview(preview, ItemNames.Build(_game, _mods));
            if (!_plan.IsEmpty)
                preview.WriteLine("Hinweis: Das ist nur eine Vorschau. Erst „Jetzt bereinigen …“ ändert den Spielstand.");
        }
        PreviewView.Document = ReportDocument.Build(preview.ToString());
        ApplyButton.IsEnabled = _plan is { IsEmpty: false } && !_busy;
    }

    // ---------- Bereinigen ----------

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_plan == null || _plan.IsEmpty || _analyzedDb == null || _busy) return;
        var db = _analyzedDb;

        var summary = new StringBuilder();
        summary.AppendLine($"Aus dem Spielstand\n{db}\nwird Folgendes entfernt:\n");
        if (_plan.Objects.Count > 0) summary.AppendLine($"• {_plan.Objects.Count} Objekte samt Inhalt (Mod-Steuerung, Gebäude, Platziertes)");
        if (_plan.Items.Count > 0) summary.AppendLine($"• {_plan.Items.Count} Items, Rezepte oder Talente");
        if (_plan.Buffs.Count > 0) summary.AppendLine($"• {_plan.Buffs.Count} aktive Effekte");
        if (_plan.Orphans is { } o) summary.AppendLine($"• Reste ohne Besitzer: {o.Items.Count} Items, {o.ModControllerIds.Count} Steuerungs-Einträge");
        if (_plan.Skipped.Count > 0) summary.AppendLine($"\n{_plan.Skipped.Count} Dinge bleiben stehen (siehe Vorschau).");
        summary.AppendLine("\nVorher wird eine Sicherungskopie angelegt. Der Server muss gestoppt sein.");
        summary.AppendLine("Danach die Mods auch aus der modlist.txt nehmen, sonst legt der Server ihre Steuerung neu an.\n");
        summary.Append("Jetzt bereinigen?");

        if (MessageBox.Show(this, summary.ToString(), "Bereinigen", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        SetBusy(true, "Wird bereinigt …");
        var plan = _plan;
        var output = new StringWriter();
        try
        {
            var backup = await Task.Run(() => plan.Apply(db, output));
            Log(output.ToString().TrimEnd());
            SetBusy(false, "Bereinigt.");
            // Erste Zeile der Ausgabe ist der Pfad der Sicherung, der kommt unten gesondert
            var details = string.Join("\n", output.ToString().Trim().Split('\n').Skip(1).Select(l => l.TrimEnd('\r'))).Trim();
            MessageBox.Show(this, $"{details}\n\nDie Sicherungskopie liegt hier:\n{backup}",
                "Bereinigung abgeschlossen", MessageBoxButton.OK, MessageBoxImage.Information);
            // Neu prüfen, damit Übersicht und Bericht den neuen Stand zeigen
            await Analyze(db, clearLog: false);
            Tabs.SelectedItem = CleanupTab;
        }
        catch (Exception ex)
        {
            Log($"Fehler: {ex.Message}");
            SetBusy(false, "Bereinigung fehlgeschlagen, am Spielstand wurde nichts geändert.");
            MessageBox.Show(this, ex.Message, "Bereinigung fehlgeschlagen", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ---------- Karte ----------

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
            })
            .ToList();

        var objects = new List<MapObject>();
        foreach (var o in save.Objects.Values)
        {
            if (save.Pieces.ContainsKey(o.Id) || (o.X == 0 && o.Y == 0)) continue;
            if (save.Characters.ContainsKey(o.Id)) objects.Add(new MapObject(o, MapObjectKind.Player, save.CharacterGuild.GetValueOrDefault(o.Id)));
            else if (save.FollowerOwner.TryGetValue(o.Id, out var f)) objects.Add(new MapObject(o, MapObjectKind.Figure, f));
            else if (save.FigureOwner.TryGetValue(o.Id, out var g)) objects.Add(new MapObject(o, MapObjectKind.Figure, g));
            else if (save.BuildingOwner.TryGetValue(o.Id, out var b)) objects.Add(new MapObject(o, MapObjectKind.Placeable, b));
        }

        Map.SetData(buildings, objects, IsFolderSelected, keepView);
        BuildLegend(buildings);
        UpdateMapInfo();
    }

    bool IsFolderSelected(string folder) => _rows.Any(r => r.Remove && r.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase));

    void BuildLegend(List<MapBuilding> buildings)
    {
        LegendPanel.Children.Clear();
        void Entry(System.Windows.Media.Brush brush, string text, bool round = false)
        {
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(round
                ? new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = brush, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center }
                : new System.Windows.Shapes.Rectangle { Width = 12, Height = 12, Fill = brush, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 200 });
            LegendPanel.Children.Add(row);
        }
        foreach (var owner in buildings.GroupBy(b => b.Owner).OrderByDescending(g => g.Count()))
            Entry(Map.OwnerBrush(owner.Key), $"{_save!.OwnerName(owner.Key)} ({owner.Count()} Gebäude)");
        Entry(MapView.ModBrush, "Bauteile aus Mods");
        Entry(MapView.SelectedModBrush, "Bauteile aus Mods, die in der Übersicht zum Entfernen angehakt sind");
        Entry(MapView.PlaceableBrush, "Platzierte Objekte (Truhen, Werkbänke …)", round: true);
        Entry(MapView.FigureBrush, "Thralls und Tiere", round: true);
        Entry(MapView.PlayerBrush, "Spieler", round: true);
    }

    void MapFit_Click(object sender, RoutedEventArgs e) => Map.FitAll();

    GameData? _mapImagesFor;

    /// <summary>Liest die Kartenbilder einmal je Spielinstallation und legt sie schemenhaft unter die Karte.</summary>
    async Task LoadMapImages()
    {
        if (_game == null || _mapImagesFor == _game) return;
        _mapImagesFor = _game;
        var game = _game;
        var loaded = await Task.Run(() =>
        {
            var list = new List<(MapImageInfo Info, System.Windows.Media.Imaging.BitmapSource Image)>();
            foreach (var info in MapImages.All)
            {
                try
                {
                    var package = game.TryRead(info.TexturePath);
                    var img = package == null ? null : ConanModCheck.IoStore.DxtTexture.DecodeGray(package, downscale: 2);
                    if (img != null) list.Add((info, Faint(img)));
                }
                catch (Exception) { /* fehlendes oder unbekanntes Kartenbild: dann eben ohne */ }
            }
            return list;
        });
        Map.SetBackgrounds(loaded.Select(l =>
        {
            var a = Alignment(l.Info);
            return new MapBackground(l.Info.Key, l.Image, RectFor(l.Info, a), a.Angle);
        }));
        ShowAlignValues();
        MapImageStatus.Text = loaded.Count == MapImages.All.Length ? "" : "Nicht alle Kartenbilder wurden im Spiel gefunden.";
        MapImageStatus.Visibility = MapImageStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Graustufen so aufhellen, dass die Karte nur leicht dunkler als der Hintergrund erscheint.</summary>
    static System.Windows.Media.Imaging.BitmapSource Faint(ConanModCheck.IoStore.DxtTexture.GrayImage img)
    {
        var bg = MapView.BackgroundColor;
        int baseGray = (bg.R * 299 + bg.G * 587 + bg.B * 114) / 1000;
        var px = new byte[img.Pixels.Length];
        for (int i = 0; i < px.Length; i++)
            px[i] = (byte)Math.Clamp(baseGray - (255 - img.Pixels[i]) * 0.22, 0, 255);
        var bmp = System.Windows.Media.Imaging.BitmapSource.Create(img.Width, img.Height, 96, 96,
            System.Windows.Media.PixelFormats.Gray8, null, px, img.Width);
        bmp.Freeze();
        return bmp;
    }

    Rect DefaultRect(MapImageInfo info) => new(info.CenterX - info.Size / 2, info.CenterY - info.Size / 2, info.Size, info.Size);

    /// <summary>Korrektur eines Kartenbilds: Versatz der Mitte (Welteinheiten), Maßstab (1 = 100 %), Drehung (Grad).</summary>
    sealed record MapAlignment(double Dx, double Dy, double Scale, double Angle);

    MapAlignment Alignment(MapImageInfo info) =>
        _settings.MapAlign.TryGetValue(info.Key, out var a) && a.Length == 4 ? new MapAlignment(a[0], a[1], a[2], a[3]) : new MapAlignment(0, 0, 1, 0);

    static Rect RectFor(MapImageInfo info, MapAlignment a)
    {
        double size = info.Size * a.Scale;
        return new Rect(info.CenterX + a.Dx - size / 2, info.CenterY + a.Dy - size / 2, size, size);
    }

    /// <summary>Lage aus der Karte in Korrekturwerte umrechnen und speichern.</summary>
    void StoreAlignment(MapBackground bg)
    {
        var info = MapImages.All.First(m => m.Key == bg.Key);
        var w = bg.World;
        _settings.MapAlign[bg.Key] = [w.X + w.Width / 2 - info.CenterX, w.Y + w.Height / 2 - info.CenterY, w.Width / info.Size, bg.Angle];
        _settings.Save();
    }

    MapImageInfo SelectedAlignIsland() =>
        MapImages.All.First(m => m.Key == ((AlignIslandBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "exiled"));

    void ShowAlignValues()
    {
        var a = Alignment(SelectedAlignIsland());
        var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        AlignX.Text = a.Dx.ToString("0", de);
        AlignY.Text = a.Dy.ToString("0", de);
        AlignScale.Text = (a.Scale * 100).ToString("0.00", de);
        AlignAngle.Text = a.Angle.ToString("0.0", de);
    }

    void AlignIsland_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (IsLoaded) ShowAlignValues();
    }

    void SelectAlignIsland(string key)
    {
        foreach (System.Windows.Controls.ComboBoxItem item in AlignIslandBox.Items)
            if (item.Tag as string == key) { AlignIslandBox.SelectedItem = item; break; }
    }

    // Zahlenfelder beim Anklicken komplett markieren, damit man direkt den neuen Wert tippen kann
    void AlignValue_Focus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        (sender as System.Windows.Controls.TextBox)?.SelectAll();

    void AlignValue_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox { IsKeyboardFocusWithin: false } box)
        {
            box.Focus();
            e.Handled = true;
        }
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

    void AlignValue_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) AlignValue_Commit(sender, e);
    }

    /// <summary>Eingetragene Werte übernehmen; ungültige Eingaben werden auf den letzten Stand zurückgesetzt.</summary>
    void AlignValue_Commit(object sender, RoutedEventArgs e)
    {
        var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        bool Parse(System.Windows.Controls.TextBox box, out double v) =>
            double.TryParse(box.Text.Replace('.', ','), System.Globalization.NumberStyles.Float, de, out v);
        var info = SelectedAlignIsland();
        if (Parse(AlignX, out var dx) && Parse(AlignY, out var dy) && Parse(AlignScale, out var scale) && scale > 1 && Parse(AlignAngle, out var angle))
        {
            var a = new MapAlignment(dx, dy, scale / 100, angle);
            _settings.MapAlign[info.Key] = [a.Dx, a.Dy, a.Scale, a.Angle];
            _settings.Save();
            Map.UpdateBackground(info.Key, RectFor(info, a), a.Angle);
        }
        ShowAlignValues();
    }

    void MapIsland_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;
        var info = MapImages.All.First(m => m.Key == key);
        Map.FitRect(Map.GetBackground(key)?.World ?? DefaultRect(info));
        SelectAlignIsland(key);
    }

    void AlignMap_Changed(object sender, RoutedEventArgs e)
    {
        bool on = AlignMapButton.IsChecked == true;
        Map.AlignMode = on;
        AlignHint.Visibility = ResetAlignButton.Visibility = AlignPanel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) ShowAlignValues();
        if (on && ShowMapImageBox.IsChecked != true) ShowMapImageBox.IsChecked = true;
    }

    void ResetAlign_Click(object sender, RoutedEventArgs e)
    {
        _settings.MapAlign.Clear();
        _settings.Save();
        foreach (var info in MapImages.All) Map.UpdateBackground(info.Key, DefaultRect(info), 0);
        ShowAlignValues();
    }
    void MapZoom_Click(object sender, RoutedEventArgs e) => Map.ZoomToSelection();

    void MapFilter_Changed(object sender, RoutedEventArgs e)
    {
        // Feuert schon beim Laden der Oberfläche, solange noch nicht alle Haken existieren
        if (!IsLoaded) return;
        Map.ShowPlaceables = ShowPlaceablesBox.IsChecked == true;
        Map.ShowFigures = ShowFiguresBox.IsChecked == true;
        Map.ShowPlayers = ShowPlayersBox.IsChecked == true;
        Map.ShowBackground = ShowMapImageBox.IsChecked == true;
        Map.InvalidateVisual();
    }

    void Map_SelectionChanged() => UpdateMapInfo();

    bool _updatingLevels;

    void UpdateMapInfo()
    {
        if (_save == null) return;
        var save = _save;
        LevelPanel.Visibility = Visibility.Collapsed;
        MapTeleportButton.Visibility = Visibility.Collapsed;

        if (Map.SelectedBuilding is { } b)
        {
            var text = new StringBuilder();
            text.AppendLine($"Gebäude {b.Id}");
            text.AppendLine($"Insel: {MapImages.IslandOf(b.Bounds.X).Title}");
            text.AppendLine($"Besitzer: {save.OwnerName(b.Owner)}");
            text.AppendLine($"{b.Pieces.Count:N0} Bauteile" + (b.ModPieces > 0 ? $", davon {b.ModPieces:N0} aus Mods" : ""));
            foreach (var g in b.Pieces.Select(p => ModPaths.FolderOf(p.Class)).Where(f => f != null).GroupBy(f => f).OrderByDescending(g => g.Count()))
                text.AppendLine($"   {g.Key}: {g.Count():N0}");
            if (save.Objects.TryGetValue(b.Id, out var o)) text.Append(SaveGame.Coords(o));
            MapInfoText.Text = text.ToString().TrimEnd();

            // Ebenen: Höhen im Raster von 256 zusammenfassen
            double minZ = b.Pieces.Min(p => p.Z);
            var levels = b.Pieces.GroupBy(p => Math.Round((p.Z - minZ) / 256))
                .OrderBy(g => g.Key)
                .Select(g => (Index: (int)g.Key + 1, Z: minZ + g.Key * 256, Count: g.Count()))
                .ToList();
            _updatingLevels = true;
            LevelBox.Items.Clear();
            LevelBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Alle Ebenen", Tag = null });
            foreach (var l in levels)
                LevelBox.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = $"Ebene {l.Index} – {l.Count} Teile", Tag = l.Z });
            LevelBox.SelectedIndex = 0;
            _updatingLevels = false;
            LevelPanel.Visibility = levels.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            MapTeleportButton.Visibility = o != null ? Visibility.Visible : Visibility.Collapsed;
        }
        else if (Map.SelectedObject is { } m)
        {
            var o = m.Object;
            string name = m.Kind switch
            {
                MapObjectKind.Player => $"Spieler „{save.Characters.GetValueOrDefault(o.Id)}“",
                MapObjectKind.Figure => save.FigureNames.TryGetValue(o.Id, out var n) ? n : Ownership.PrettyClass(o.Class),
                _ => Ownership.PrettyClass(o.Class),
            };
            var text = new StringBuilder();
            text.AppendLine($"{name} (ID {o.Id})");
            text.AppendLine($"Insel: {MapImages.IslandOf(o.X).Title}");
            if (m.Kind != MapObjectKind.Player) text.AppendLine($"Besitzer: {save.OwnerName(m.Owner)}");
            else if (save.CharacterGuild.TryGetValue(o.Id, out var g) && save.Guilds.TryGetValue(g, out var gn)) text.AppendLine($"Clan: {gn}");
            if (save.ItemsByOwner.TryGetValue(o.Id, out var items))
            {
                var physical = items.Where(i => m.Kind == MapObjectKind.Placeable || i.InvType is 0 or 1 or 2).ToList();
                if (physical.Count > 0) text.AppendLine($"{physical.Sum(i => (long)i.Quantity):N0} Items (Einzelheiten im Reiter „Besitz“)");
            }
            if (ModPaths.FolderOf(o.Class) is { } folder) text.AppendLine($"Aus der Mod: {folder}");
            text.Append(SaveGame.Coords(o));
            MapInfoText.Text = text.ToString();
            MapTeleportButton.Visibility = Visibility.Visible;
        }
        else MapInfoText.Text = "Nichts ausgewählt. Auf ein Gebäude oder Objekt klicken.";
    }

    void LevelBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingLevels) return;
        Map.Level = (LevelBox.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as double?;
    }

    void MapTeleport_Click(object sender, RoutedEventArgs e)
    {
        WorldObject? o = Map.SelectedObject?.Object ?? (Map.SelectedBuilding is { } b ? _save?.Objects.GetValueOrDefault(b.Id) : null);
        if (o == null) return;
        var cmd = $"TeleportPlayer {o.X:0} {o.Y:0} {o.Z:0}";
        Clipboard.SetText(cmd);
        StatusText.Text = $"Kopiert: {cmd}";
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
            Log($"Oodle wird weiter von dort geladen: {loaded}");
        }
    }

    void CopyOodle(string source)
    {
        try
        {
            var target = OodleLocation.CopyNextToProgram(source);
            OodleBox.Text = "";
            _settings.OodleChoice = "copy";
            Log($"Oodle neben das Programm kopiert: {target}");
            StatusText.Text = "Oodle-Bibliothek kopiert. Ab dem nächsten Start wird sie von dort geladen.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Die Datei konnte nicht kopiert werden:\n{ex.Message}\n\nSie wird weiter vom ursprünglichen Ort geladen.",
                "Oodle kopieren", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            OodleInfoText.Text = "Keine Oodle-Bibliothek gefunden.";
            CopyOodleButton.Visibility = Visibility.Collapsed;
        }
        else if (OodleLocation.IsNextToProgram(loaded) || _settings.OodleChoice == "copy" && File.Exists(copied))
        {
            OodleInfoText.Text = $"Geladen aus dem Programmordner:\n{copied}";
            CopyOodleButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            var game = OodleLocation.GameName(loaded);
            OodleInfoText.Text = $"Geladen aus {(game != null ? $"„{game}“" : "einem anderen Ordner")}:\n{loaded}";
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
        OwnershipTree.ItemsSource = nodes.Select(n => new OwnTreeItem(n, searching)).ToList();
        if (searching && nodes.Count == 0)
            StatusText.Text = "Keine passenden Items gefunden.";
    }

    void SearchOwnership_Click(object sender, RoutedEventArgs e) => RefreshOwnership();

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

    void OwnershipTree_PreviewMouseRightButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Rechtsklick soll den Eintrag unter der Maus auswählen, damit das Kontextmenü dazu passt
        var element = e.OriginalSource as DependencyObject;
        while (element != null && element is not System.Windows.Controls.TreeViewItem)
            element = System.Windows.Media.VisualTreeHelper.GetParent(element);
        if (element is System.Windows.Controls.TreeViewItem item) { item.IsSelected = true; item.Focus(); }
        CopyTeleportItem.IsEnabled = (OwnershipTree.SelectedItem as OwnTreeItem)?.Teleport != null;
    }

    void CopyTeleport_Click(object sender, RoutedEventArgs e)
    {
        if (OwnershipTree.SelectedItem is OwnTreeItem { Teleport: { } cmd })
        {
            Clipboard.SetText(cmd);
            StatusText.Text = $"Kopiert: {cmd}";
        }
    }

    void CopyTreeLine_Click(object sender, RoutedEventArgs e)
    {
        if (OwnershipTree.SelectedItem is OwnTreeItem item) Clipboard.SetText(item.Text.Trim());
    }

    void SaveOwnership_Click(object sender, RoutedEventArgs e)
    {
        if (_save == null) return;
        var dlg = new SaveFileDialog { Title = "Besitz als Text speichern", Filter = "Textdatei (*.txt)|*.txt", FileName = "ConanModCheck-Besitz.txt" };
        if (dlg.ShowDialog(this) != true) return;
        using var w = new StreamWriter(dlg.FileName, false, new UTF8Encoding(true));
        Ownership.WriteText(w, Ownership.Build(_save, ItemNames.Build(_game, _mods), OwnershipSearchBox.Text, ItemOrigin.Build(_game, _mods), CurrentItemFilter(), SelectedModNames()));
    }

    // ---------- Bericht ----------

    void SaveReport_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_reportText)) return;
        var dlg = new SaveFileDialog { Title = "Bericht speichern", Filter = "Textdatei (*.txt)|*.txt", FileName = "ConanModCheck-Bericht.txt" };
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
