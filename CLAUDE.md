# CLAUDE.md — Spitr für Windows

Fixierte Entscheidungen für dieses Projekt. Nicht erneut in Frage stellen, ohne dass
Jarek es explizit anstößt. Architektur-Überblick: siehe [ARCHITECTURE.md](ARCHITECTURE.md).
Das macOS-Original liegt in `/Users/jarek/dev/claude/projects/Spitr` — bei Portierungsfragen
dort nachsehen, Verhalten des Originals ist die Referenz.

## Was Spitr für Windows ist

Windows-Port der macOS Voice-to-Text App Spitr: Taste halten → sprechen → loslassen →
Text wird ins fokussierte Fenster eingefügt. On-device, kostenlos, privat, ohne Cloud,
ohne Abo, ohne Telemetrie.

## Arbeitsumgebung (wichtig!)

- Entwickelt wird **ausschließlich auf macOS** — es gibt **keine Windows-Maschine** zum
  interaktiven Testen. Nie davon ausgehen, etwas „mal eben auf Windows ausprobieren" zu können.
- .NET SDK liegt in `~/.dotnet` (nicht im PATH):
  `export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"`
- **Mac-lokal baubar/testbar:** nur `Spitr.Core` + `Spitr.Core.Tests`
  (`dotnet test tests/Spitr.Core.Tests`). Der schnelle Innenloop — hier landet ALLE Logik.
- **Windows-Verifikation:** ausschließlich über GitHub Actions (`gh run watch` / Artifacts).
  `Spitr.App` (WPF) baut auf macOS NICHT — nie versuchen, es lokal zu bauen.
- Whisper läuft dank `Whisper.net.Runtime` (osx-arm64) auch lokal auf dem Mac —
  Engine-Änderungen immer lokal integrationstesten, bevor sie in CI gehen.

## Architektur — Parnas-Module (wie das Original)

- Jedes Modul kapselt **eine austauschbare Entscheidung** hinter einem Interface.
- Speech-Engine hinter `ITranscriptionEngine`; v1 einzige Implementierung `WhisperEngine`
  (Whisper.net/whisper.cpp). **Nie** direkt gegen eine konkrete Engine programmieren.
- **`Spitr.Core` hat NULL Windows-Abhängigkeiten** (kein WPF, kein Win32, kein
  `net10.0-windows`) — alles dort muss auf macOS kompilieren und testen.
  `Spitr.App` ist die dünne Windows-Adapter-Schale (Hook, Clipboard, SendInput, WASAPI,
  Tray, Overlay, UIA) hinter den Core-Interfaces.
- Jedes UI-Control bekommt eine `AutomationId` — die FlaUI-CI-Tests und Screenshots sind
  das einzige Review-Medium für Windows-UI.

## Technische Leitplanken

- **.NET 10 (LTS), C#, WPF** mit Fluent-Theme (`ThemeMode=System`). Windows 10 21H2+, x64.
- **Engine:** Whisper.net + GGML-Modelle `base`/`small`/`large-v3` unter
  `%LOCALAPPDATA%\Spitr\models`. CPU-Runtime; CUDA/Vulkan bewusst nicht in v1.
- **Hold-Taste:** Default **Strg rechts** (`VK_RCONTROL`). **Rechts-Alt nie anbieten** —
  auf QWERTZ ist das AltGr (tippt @ € [ ]). Auswahlset: Strg rechts / CapsLock /
  Umschalt rechts / Pause. Der Hook schluckt die gewählte Taste.
- **Hook:** `WH_KEYBOARD_LL` auf dediziertem Thread (µs-Callback → Channel), Watchdog via
  `GetAsyncKeyState`, Re-Install bei Resume/Unlock, 5-min-Cap. `RegisterHotKey` nur für
  den Re-Insert-Chord (Default Strg+Umschalt+Alt+V).
- **Texteinfügung:** Clipboard-Vollsnapshot → Text setzen inkl.
  `ExcludeClipboardContentFromMonitorProcessing` + `CanIncludeInClipboardHistory=false`
  → SendInput Strg+V → Restore (~300 ms). Retry-Loop gegen Clipboard-Contention.
  Elevated-Ziel → kein Paste, Tray-Hinweis.
- **Audio:** WASAPI (NAudio), Resampling auf 16 kHz mono Float32 in Core (WdlResampler).
- **P/Invoke:** über CsWin32-Source-Generator, kein handgeschriebenes Marshalling.

## Harte Regeln (nicht verhandelbar)

- **Mikro nur während die Taste gehalten wird.** Kein Dauer-Listening, keine
  Auto-Aufnahme, keine VAD. Capture startet am Key-Down, stoppt am Key-Up.
- **Keine Netzwerk-Calls** außer dem einmaligen Modell-Download in `ModelDownloader`
  (einziger erlaubter Ort für `HttpClient` — der CI-Job `no-network-audit` erzwingt das).
  Keine Telemetrie, kein Analytics, kein Update-Check.
- **Diag-Log enthält nie diktierten Text.**

## Tests & CI

- Alle Logik-Tests in `Spitr.Core.Tests` (xUnit) — müssen auf macOS UND Windows grün sein.
- Whisper-Integrationstest: Trait `Category=Whisper`, transkribiert
  `Fixtures/german_test.wav`, Assertions per Keyword-Containment (nie Exact-Match).
- CI: `core-macos` (Tests inkl. Whisper), `windows` (Build + Tests + später
  Publish/Selftest/Screenshots), `no-network-audit` (grep-Wächter).

## Git-Commits

- Conventional Commits, **Subject-only**: `<type>: <description>`.
- Types: `feat`, `fix`, `refactor`, `test`, `docs`, `ci`, `build`, `infra`, `chore`.
- Imperativ, erste Zeile ≤ 72 Zeichen, kein Punkt am Ende, **kein Body**.
- **Kein `Co-Authored-By`-Trailer.**
- Commit nach jedem abgerundeten Stück aktiv anbieten.

## Kommunikation

- Antworten auf **Deutsch** (Code-Identifier englisch). UI-Quellsprache Deutsch. Knapp,
  keine Floskeln.
