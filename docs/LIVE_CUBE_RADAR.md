# Live Cube Radar

Soulcrest zeigt Hidden Cubes jetzt nicht nur als mögliche Spawnpunkte (Karte, Cube Groups), sondern
auch **live**: Cubes, die der Server gerade in deiner Nähe meldet, erscheinen auf demselben
Minimap-/Weltkarten-Overlay wie die Cube-Spots, mit Entfernung und Höhe, und ein Alarmton meldet
jeden neuen Cube. Erkennung und Alarm stammen aus [Cube Radar](https://github.com/dr89mh/aion2-cube-radar)
(GPL-3.0), siehe `THIRD_PARTY_NOTICES.md`.

## Wie es funktioniert

- Soulcrest liest den Server→Client-Verkehr bereits passiv mit Npcap (Soul-Tracker, Loot-Tracker).
  Das Radar hängt sich als reiner Beobachter an dieselben, schon entschlüsselten Nachrichten
  (`NetworkLootService.HiddenCubes.cs`). Es gibt **keine zweite Aufzeichnung**, keine Speicherzugriffe,
  keine Injektion, nichts wird gesendet. Die bestehende Soul-/Loot-Auswertung bleibt unverändert.
- Ausgewertet werden nur sechs Record-Typen: Objekt erscheint (`34 36`), verschwindet (`35 36`),
  wird geöffnet (`38 36`, `3a 36`), Kartenwechsel (`21 36`) und Teleport (`23 36`).
- Cube-Kennungen: 1200039 und 1200041 (Verteron, bestätigt), 1301199 (Altgard, Hypothese). Weitere
  lernt das Radar selbst: wird ein Objekt mit dem Hidden-Cube-Schlüssel geöffnet, wird seine Kennung
  dauerhaft gespeichert (`%LOCALAPPDATA%\Soulcrest\learned\hidden-cube-templates.json`); erscheint ein
  Objekt genau auf einem bekannten Cube-Spot der Karte, gilt seine Kennung für die laufende Sitzung.
- Spielkoordinaten (Zentimeter) werden exakt in Kartenkoordinaten umgerechnet: Die Umrechnung wird
  aus den Ressourcenpunkten des Kartenpakets (`resource-nodes.json`, die native und Kartenkoordinaten
  enthalten) bestimmt. Karten ohne passende Daten zeigen keine Live-Cubes (Hinweis in den Einstellungen).
- Die eigene Position kommt aus dem Minimap-Tracking von Soulcrest und aus Kartenwechsel-/Teleport-Records.

## Anzeige

| Farbe | Bedeutung |
|---|---|
| Rot, leuchtend | Cube sichtbar (in Senderreichweite) |
| Orange, gestrichelt | außer Sicht, steht vermutlich noch da |
| Gelb | wird gerade geöffnet |
| Grau mit Kreuz | geöffnet oder weg (verschwindet nach einer Minute) |

Liegt ein Cube außerhalb des sichtbaren Kartenausschnitts, zeigt ein Pfeil am Rand die Richtung.
Unter dem Symbol stehen Entfernung und Höhenunterschied (▲ über dir, ▼ unter dir).

## Einstellungen

Overlay → Karten-Overlay → **Live Cube Radar**: Radar an/aus, Alarmton an/aus, Lautstärke, Ton testen.

## Bauen

- Lokal (Windows, .NET 10 SDK, Inno Setup 7): wie in der README beschrieben, `dotnet build`,
  `dotnet test`, `scripts/Build-Installer.ps1`.
- Über GitHub: Repository forken, diesen Stand hochladen, unter *Actions* den Workflow
  **Build (Live Cube Radar)** starten. Der Installer liegt danach als Artefakt am Lauf.

## Hinweise

- Nutzung auf eigenes Risiko: Laut Cube-Radar-README stuft der NCSOFT-Support Software, die
  Spieldaten mitliest, als Verstoß gegen die Nutzungsbedingungen ein. Das gilt für Soulcrests
  bestehenden Soul-Tracker genauso.
- Die automatische Update-Funktion von Soulcrest kann eine neuere offizielle Version ohne das Radar
  installieren. Wer das Radar behalten will, nimmt Updates nur aus dem eigenen Build.
