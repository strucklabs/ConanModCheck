# ConanModCheck – notes for Claude

C# tool (.NET 10, Windows) that shows what content of mods is stored in a Conan Exiles Enhanced
save game (`game.db`, SQLite in WAL mode) and removes it safely (preview, backup). Two front ends
share one code base: a console tool (`ConanModCheck.csproj` → `ConanModCheck-cli.exe`) and a WPF
GUI (`Gui/ConanModCheck.Gui.csproj` → `ConanModCheck.exe`).

Personal paths and test setup of the maintainer are in `CLAUDE.local.md` (not in the repository).

## Build

```
dotnet build ConanModCheck.csproj -c Debug
dotnet build Gui/ConanModCheck.Gui.csproj -c Debug
dotnet publish ConanModCheck.csproj -c Release -o dist
dotnet publish Gui/ConanModCheck.Gui.csproj -c Release -o dist
```

Build the console project explicitly before testing the CLI; building only the GUI does not
refresh `bin/Debug/.../ConanModCheck-cli.exe`.

## Layout

- `SaveGame.cs` – reads the save (read-only, no pooling) and removes `-wal`/`-shm` files it created itself.
- `ModPackage.cs` – mod files (.pak/.utoc/.ucas, zip, Steam Workshop `.pak` containers); `NewIds` = row numbers of mod data tables that the game uses in none of its own tables.
- `GameData.cs` – game files: item/feat IDs, `RowIds` of all game data tables, localization.
- `Cleanup.cs` – plans and applies the removal (backup first, one transaction, integrity check, WAL checkpoint).
- `InventoryKinds.cs` – meaning of `item_inventory.inv_type` per owner; decides what the cleanup may touch.
- `Report.cs`, `Ownership.cs`, `Orphans.cs`, `Support.cs` (piece kinds, extents, collapse risk).
- `Gui/` – `MainWindow*`, `MapView` (map, pieces, section line), `SectionView`, `DirectoryBuilder`/`DirEntry` (Directory tab), `ReportDocument`, `FitGrid`.
- `IoStore/` – UE5 IoStore/Zen/pak readers; Oodle is loaded from a DLL the user provides.

## Hard rules

- Never commit or ship the Oodle DLL (`oo2core_*_win64.dll`) or any Funcom/game assets.
- The analysis must never change the save. Only `Cleanup.Apply` writes, after a backup.
- Never delete `character_stats` of missing characters (placed crafting thralls depend on them).
- A number counts as a mod ID only if no game data table uses it; without game files, IDs below 1000 never count.
- On player characters the cleanup only removes mod entries from known inventory types (0/1/2 items, 6 learned, 7 lore, 15 spells). Type 3 and every unknown type are shown, never removed.
- IDs that also belong to a loaded mod that is not selected are kept.
- Mixed buildings (game and mod pieces) are never removed.

## Conventions

- Code comments and translation keys are German. UI texts go through `L.T`/`L.F`; English texts live in `LangEn.cs` (key = German text). XAML texts are translated by `UiTranslator`. After changing texts, check that no key is missing or unused.
- UI texts: neutral and beginner-friendly, never tailored to one person.
- Colour rule: coloured row backgrounds only for notes and warnings; mod content is marked by a coloured square (yellow mod, red checked mod, orange at risk).
- Edit files with UTF-8 aware tools only (umlauts). Do not use PowerShell `Set-Content`/`Get-Content` on source files.
- Text in Windows PowerShell 5.1 scripts with umlauts needs a UTF-8 BOM; typographic quotes („ “) break PowerShell strings.

## Testing

- Test only on copies of a save game, never on the user's files.
- Before a GUI test, check for a running `ConanModCheck` instance; if the user's own instance runs, ask them to close it. Only stop processes you started yourself.
- GUI tests overwrite `%APPDATA%\ConanModCheck\settings.json`: back it up first and restore it afterwards (compare the hash).
- Cleanup tests: check backup equals the original, `PRAGMA integrity_check`, no `-wal`/`-shm` left, and that no deleted inventory row of a remaining owner carries a game table ID.
- Report what was and was not verified. A partial check is never reported as a complete one.

## Working agreements

- Commit only after the user asks for it; commit messages in German, written to a file and passed with `git commit -F`.
- When working through an approved check list, do not ask for a commit after each item; ask at the end.
