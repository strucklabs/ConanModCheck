# ConanModCheck

Prüft, welche Inhalte einer Mod in einem Spielstand von **Conan Exiles Enhanced** stecken und was beim Entfernen der Mod verloren ginge. Auf Wunsch entfernt es diese Reste, mit Vorschau und Sicherungskopie.

Es gibt zwei Programme:
- `ConanModCheck.exe`: die Windows-Oberfläche, zum Bedienen mit der Maus.
- `ConanModCheck-cli.exe`: dieselben Funktionen für die Eingabeaufforderung und für Skripte.

## Oberfläche

1. **Spielstand auswählen:** die `game.db` des Servers (`ConanSandbox/Saved`). Den Server vorher stoppen, am besten mit einer Kopie arbeiten.
2. **Mod-Dateien hinzufügen** (optional): Zip, `.pak` oder `.utoc` der Mods, die entfernt werden sollen. Das geht auch per Drag & Drop. Nur mit der Mod-Datei lassen sich auch die Items einer Mod finden.
3. **Prüfen** klicken.
4. Im Reiter **Übersicht** einen Haken bei allem setzen, was entfernt werden soll. Mods aus Schritt 2 sind schon angehakt. Ordner ohne Mod-Datei lassen sich ebenfalls anhaken, z. B. Reste schon entfernter Mods.
5. Der Reiter **Bericht** zeigt die Einzelheiten, der Reiter **Bereinigung** die Vorschau. „Jetzt bereinigen …“ fragt noch einmal nach, legt eine Sicherungskopie an und entfernt dann alles.

Der Reiter **Besitz** zeigt als aufklappbaren Baum, wer was hat: Clan → Spieler (mit Stufe und letztem Login) → Inventar, Ausrüstung, Thralls und Tiere mit Namen und Ausrüstung, Behälter und Stationen mit Inhalt und Gebäude. Über das Suchfeld lässt sich ein Item finden („Wer hat Sternenmetall?“). Ein Rechtsklick auf ein Objekt kopiert den Teleport-Befehl für die Admin-Konsole.

Items aus Mods tragen im Baum den Namen ihrer Mod in eckigen Klammern. Die Auswahl neben dem Suchfeld schränkt den Baum ein: **alle Items**, **Items aus allen Mods**, **aus geladenen Mods** (deren Datei angegeben ist) oder **aus angehakten Mods** (in der Übersicht zum Entfernen markiert). So sieht man vor dem Entfernen einer Mod, wer noch Sachen daraus besitzt. Die Suche findet auch Mod-Namen.

Der Reiter **Karte** zeigt alle Gebäude, platzierten Objekte, Thralls, Tiere und Spieler von oben. Bauteile des Spiels sind grau (je Besitzer ein anderer Grauton), Mod-Inhalte gelb, Inhalte von Mods, die in der Übersicht zum Entfernen angehakt sind, rot. Siptah liegt in Conan Exiles Enhanced in derselben Welt weit östlich der Verbannten Lande; die Knöpfe „Ganze Karte“, „Verbannte Lande“ und „Siptah“ springen direkt dorthin.

Bedienung: **Mausrad** zoomt in großen Stufen, **Ziehen** verschiebt, **rechte Maustaste ziehen** vergrößert den aufgezogenen Ausschnitt, **Strg + Ziehen** dreht die Karte („Norden oben“ stellt sie zurück). Herausgezoomt werden nahe beieinanderliegende Elemente zu **Gruppen mit Anzahl** zusammengefasst, eingefärbt nach dem wichtigsten Inhalt (rot vor gelb vor grau); ein Klick auf eine Gruppe zoomt hinein, bis sie zerfällt. **Randpfeile** zeigen auf Mod-Inhalte außerhalb des Ausschnitts, ein Klick springt hin. Die **Fundstellen-Liste** führt alle Stellen mit Mod-Inhalten auf, der Haken **„Nur Mod-Inhalte“** blendet alles andere aus. Ein Klick auf ein Gebäude zeigt Besitzer, Bauteile und Position; eine einzelne Ebene lässt sich als Grundriss hervorheben. Die Formen sind vereinfacht, da sie nur aus Bauteiltyp, Lage und Drehung abgeleitet werden.

Items erscheinen überall mit Namen, auf Deutsch, wenn das Spiel eine Übersetzung hat, sonst auf Englisch. Items aus Mods, deren Datei nicht angegeben ist, bleiben Nummern.

Die Oberfläche gibt es auf **Englisch** (Standard) und **Deutsch**, umschaltbar unter „Einstellungen“; die Item-Namen kommen dann aus der passenden Sprachdatei des Spiels. Die Pfade merkt sich das Programm für den nächsten Start. Spielordner und Oodle-DLL werden automatisch gesucht und lassen sich unter „Einstellungen“ auch selbst angeben.

## Eingabeaufforderung

```
ConanModCheck-cli --db game.db --mod <Mod> [--mod <Mod> ...] [--out bericht.txt]
```

- `--db`: Spielstand des Servers (`ConanSandbox/Saved/game.db`). Den Server vor dem Kopieren stoppen.
- `--mod`: Mod-Datei als `.pak`/`.utoc`, ein Zip davon oder ein ganzer Ordner, z. B. `ConanSandbox/Saved/ExtractedMods` vom Server. Lässt sich mehrfach angeben.
- `--game`: Installationsordner von Conan Exiles. Ohne diese Angabe wird er über Steam gesucht. Ein Dedicated Server geht auch.
- `--oodle`: Pfad zu einer `oo2core_*_win64.dll`. Ohne diese Angabe wird neben der EXE und in allen Steam-Bibliotheken gesucht.
- `--out`: Bericht in eine Datei statt in die Konsole schreiben.

Ohne `--mod` zeigt der Bericht nur die Übersicht aller Mod-Ordner im Spielstand.

- `--lang en|de`: Sprache aller Ausgaben, Standard Englisch.
- `--besitz` (auch `--owners`): Statt des Berichts den Baum „Wer hat was“ ausgeben.
- `--suche <Text>` (auch `--search`): Wie `--besitz`, aber nur Items, deren Name den Text enthält.
- `--nur-mods` (auch `--mods-only`): Wie `--besitz`, aber nur Items aus Mods.

## Reste ohne Besitzer

In der Übersicht zeigt die Spalte **Mod-Controller**, ob die Mod ihre Steuerung im Spielstand hat (siehe unten). Diese Controller zählen nicht zu den „Objekten“. Im Bericht und in der Vorschau sind Warnungen rot und Hinweise gelb markiert.

Der Bericht listet außerdem Einträge auf, deren Besitzer nicht mehr existiert:

- **Items in Behältern, die es nicht mehr gibt**, meist Beutesäcke oder Leichen, die beim Neustart verschwunden sind. Das Spiel kann sie nicht mehr anzeigen. In der Oberfläche lassen sie sich über einen Haken im Reiter „Bereinigung“ mit entfernen, in der Eingabeaufforderung mit `--clean --orphans` bzw. `--apply --orphans`, auch ohne Mod.
- **Einträge der Mod-Steuerung ohne Objekt.** Sie sind harmlos und werden mit entfernt.
- **Werte von Figuren, die nicht mehr in der Welt stehen.** Diese werden nur angezeigt, nie entfernt: Darunter sind auch Handwerker-Thralls, die in eine Werkbank eingesetzt wurden und deren Werte unter der alten ID weiterleben.

## Was geprüft wird

| Im Spielstand | Woran die Mod erkannt wird |
|---|---|
| Platzierte Objekte, Mod-Steuerung, NPCs (`actor_position`) | Klassenpfad `/Game/Mods/<Ordner>/…` |
| Bauteile (`building_instances`), auch gemischte Gebäude | Klassenpfad |
| Items, erlernte Rezepte und Talente (`item_inventory`) | ID aus den Datentabellen der Mod, die es im Originalspiel nicht gibt |
| Inhalt von Mod-Behältern | Items, die mit dem Behälter verloren gingen |
| Aktive Effekte (`character_buffs`) | Pfad des Effekts |
| Mod-Daten an Objekten (`properties`) | Mod-Pfad in den Binärdaten |

Außerdem vergleicht das Werkzeug Spieltabellen, die eine Mod ersetzt, mit dem aktuellen Spiel. Fehlen in der Kopie der Mod Einträge, ist die Mod veraltet, und diese Dinge fehlen im Spiel, solange sie aktiv ist.

## Bereinigen (Eingabeaufforderung)

```
ConanModCheck-cli --db game.db --mod <Mod> --clean            (Vorschau, ändert nichts)
ConanModCheck-cli --db game.db --mod <Mod> --apply            (entfernt nach Rückfrage)
```

Ist die Mod-Datei nicht mehr da, z. B. bei Resten einer schon entfernten Mod, geht auch `--folder <Ordnername>`. Den Namen zeigt die Übersicht im Bericht. Ohne Mod-Datei lassen sich die Items dieser Mod allerdings nicht erkennen.

Was entfernt wird:
- Objekte der Mod samt allem, was an ihnen hängt: Position, Besitz, Inventar, Eigenschaften, Zustand. Dazu gehören auch die Mod-Steuerung und Gebäude, die nur aus Mod-Bauteilen bestehen.
- Items, erlernte Rezepte und Talente mit IDs der Mod, auch in Post an Spieler.
- Aktive Effekte der Mod.

Was absichtlich stehen bleibt:
- **Gemischte Gebäude** aus Mod- und Spiel-Bauteilen. Diese bitte im Spiel umbauen, sonst droht ein Einsturz.
- **Mod-Behälter, in denen noch andere Items liegen.** Diese bitte erst ausräumen. Mit `--include-contents` werden sie samt Inhalt entfernt.
- Daten an Objekten des Spiels, die nur auf die Mod verweisen.

Vor jeder Änderung wird eine Sicherungskopie `game.backup-<Datum>-<Uhrzeit>.db` neben der Datenbank angelegt. Alles läuft in einer einzigen Transaktion, danach wird die Datenbank geprüft. Wenn der Server die Datenbank gerade benutzt, bricht das Werkzeug ab. Zum Schluss sind alle Änderungen in der `game.db` selbst gespeichert, ohne `-wal`-Datei daneben. Sie kann also direkt wieder hochgeladen werden.

## Voraussetzungen

- **Oodle:** Die Dateien von Mods und Spiel sind mit Oodle komprimiert. Conan Exiles hat die Bibliothek fest eingebaut. Viele andere Spiele liefern sie als `oo2core_9_win64.dll` mit, zum Beispiel Destiny 2 oder Warhammer 40,000: Darktide. Diese DLL wird hier nicht mitgeliefert. Die Oberfläche sucht sie in allen Steam-Bibliotheken. Wird sie in einem anderen Spiel gefunden, fragt sie einmal, ob die DLL neben das Programm kopiert oder weiter vom Fundort geladen werden soll. Unter „Einstellungen“ steht, woher sie gerade geladen wird. Eine kopierte DLL bitte nicht zusammen mit dem Programm weitergeben.
- **Spieldateien:** Ohne Spieldateien lassen sich neue Items nicht von veränderten Spiel-Items trennen.

## Mod-Controller

Eine Mod bringt meist einen Mod-Controller mit. Das ist ein unsichtbares Objekt, das der Server beim Start für jede aktive Mod am Weltursprung (0/0/0) anlegt. Darüber hängt sich die Mod ins Spiel ein, zum Beispiel mit eigenen Datentabellen oder Startcode. Fehlt die Mod, lädt der Server den Controller nicht mehr, die Einträge bleiben aber im Spielstand. Die Bereinigung entfernt sie mit.

## Bauen

```
dotnet publish ConanModCheck.csproj -c Release -o dist
dotnet publish Gui/ConanModCheck.Gui.csproj -c Release -o dist
```

Das ergibt `dist/ConanModCheck-cli.exe` und `dist/ConanModCheck.exe`. Das Programm-Icon entsteht aus `tools/MakeIcon.cs` (`dotnet run --file tools/MakeIcon.cs`). Beide sind einzelne Dateien, die ohne installiertes .NET laufen. Die Oberfläche (`Gui/`) nutzt die Quelltexte aus dem Hauptordner mit. Alle Texte laufen über `L.T(…)`/`L.F(…)`; die englischen Fassungen stehen in `LangEn.cs` (Schlüssel ist der deutsche Text).
