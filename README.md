# Spitr für Windows — *Spit it Out*

**Windows-Port von [Spitr](https://github.com/Jarek2k/Spitr): Taste halten, sprechen,
loslassen — der Text landet im fokussierten Fenster.** Komplett on-device, kostenlos,
privat. Keine Cloud, kein Abo, keine Telemetrie.

> **Alpha.** Der Port entsteht vollständig blind (Entwicklung auf macOS, Verifikation
> über Windows-CI); auf echter Windows-Hardware ist er noch ungetestet. Der komplette
> Kern — Whisper-Transkription, Clipboard-Paste mit Restore, Hold-to-Talk-Statemachine —
> läuft in der CI end-to-end grün.

## Wie es funktioniert

**Strg rechts** halten → schwebendes Overlay mit Live-Waveform → sprechen → loslassen →
die Aufnahme wird lokal mit [Whisper](https://github.com/ggerganov/whisper.cpp)
(via Whisper.net) transkribiert → der Text wird per Zwischenablage + Strg+V eingefügt.
Deine Zwischenablage wird vorher komplett gesichert und danach wiederhergestellt;
Diktate sind für Win+V-Verlauf und Cloud-Clipboard unsichtbar markiert.

- **Esc** während der Aufnahme bricht ab.
- **Taste + Umschalt** spricht einen Befehl statt zu diktieren (»pause«, »weiter«, …).
- **Strg+Alt+Umschalt+V** fügt die letzte Spracheingabe erneut ein (Rettung bei falschem Fokus).
- Rechts-Alt wird bewusst nicht als Aufnahme-Taste angeboten — auf QWERTZ ist das
  **AltGr** und wird zum Tippen von `@ € [ ] { }` gebraucht.

## Features

- Hold-to-Talk-Diktat in jede App, audio-reaktives Overlay (5 Visualisierungs-Stile,
  inkl. KITT).
- On-device-Whisper (GGML `base`/`small`/`large-v3`, einmaliger Download mit
  Fortschrittsanzeige — die einzige Netzwerkverbindung der App, von der CI erzwungen).
- Custom Vocabulary (Bias-Hinweis), persönliches Wörterbuch (Ersetzungsregeln),
  Verlauf mit »Falsch erkanntes Wort → dauerhafte Regel«-Korrektur.
- Sprachbefehle, Pausieren/Fortsetzen, Bereitschafts-/Fertig-Töne (3 Stile),
  intelligente Leerzeichen (UIA-Caret-Kontext, degradiert sauber in Electron-Apps),
  Autostart, Tray-App mit Zustands-Icon, Diagnose-Log (enthält nie diktierten Text).
- Erkennung, dass das Zielfenster als Administrator läuft (UIPI würde den Paste
  schlucken) → Hinweis statt stillem Fehlschlag.

## Installation

Es gibt noch kein Release. Bis dahin: Zip-Artifact `spitr-win-x64` aus dem letzten
[CI-Lauf](https://github.com/Jarek2k/Spitr-Windows/actions) herunterladen, entpacken,
`Spitr.exe` starten (SmartScreen-Hinweis „Unbekannter Herausgeber" mit „Trotzdem
ausführen" bestätigen — die App ist nicht signiert). Windows 10 21H2+ / Windows 11, x64.

## Privacy

- **Keine Netzwerk-Calls** außer dem einmaligen Whisper-Modell-Download
  (Hugging Face) — ein CI-Job greppt den Quellcode dagegen.
- **Mikro nur bei gehaltener Taste.** Kein Dauer-Listening, keine VAD.
- **Keine Telemetrie.** Logs bleiben lokal (`%LOCALAPPDATA%\Spitr\logs`) und
  enthalten nie diktierten Text.

## Entwicklung

Vollständig auf macOS entwickelt: `Spitr.Core` (Logik, 166+ Tests) läuft samt echter
Whisper-Inferenz lokal auf dem Mac; `Spitr.App` (WPF/Win32) kompiliert dort via
`EnableWindowsTargeting` und wird ausschließlich über GitHub Actions verifiziert —
Adapter-Tests, ein `--selftest`-E2E (Fixture-WAV → echte Pipeline → Paste in ein
Testfenster) und gerenderte UI-Screenshots als Artifacts. Details in
[ARCHITECTURE.md](ARCHITECTURE.md), Feature-Stand in [FEATURES.md](FEATURES.md).

```
dotnet test tests/Spitr.Core.Tests          # Mac + Windows: gesamte Logik + Whisper
dotnet build src/Spitr.App -c Release       # kompiliert auch auf macOS
Spitr.exe --selftest fixture.wav            # Windows: E2E durch die echte Pipeline
Spitr.exe --screenshot-overlays out/        # Windows: Overlay-Renderings
Spitr.exe --screenshot-ui out/              # Windows: Settings/Onboarding-Renderings
```

## Bekannte Lücken (Stand Juli 2026)

- Ungetestet auf echter Hardware (Mikrofon + physischer Tastendruck brauchen einen Menschen).
- UI nur Deutsch (en-Lokalisierung vorbereitet über die Generator-Pipeline des Originals, noch nicht umgesetzt).
- Kein Shortcut-Recorder für den Re-Insert-Chord (nur Anzeige; Default Strg+Alt+Umschalt+V).
- Kein Single-File-Exe (Whisper.net-Natives vertragen sich nicht mit Self-Extract), kein Code-Signing, kein Auto-Update.
- Keine Rauschunterdrückung (Apples Voice-Isolation hat kein Windows-Pendant in v1).

## Lizenz

Wie das Original: [LICENSE](https://github.com/Jarek2k/Spitr/blob/main/LICENSE).
