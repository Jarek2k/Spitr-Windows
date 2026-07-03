# Architektur

Windows-Port von [Spitr](https://github.com/Jarek2k/Spitr) (macOS). Die eine Idee bleibt:
**Parnas-Module** — jeder Service in `Spitr.Core` kapselt genau eine austauschbare
Entscheidung hinter einem Interface; der Rest der App hängt nie an der konkreten Umsetzung.

Die zweite Idee ist Windows-spezifisch: **`Spitr.Core` ist plattformneutral** (net10.0,
null Windows-Referenzen) und läuft samt Tests auf dem Entwicklungs-Mac. Alles, was Win32
berührt, lebt als dünner Adapter in `Spitr.App` (net10.0-windows, WPF) hinter einem
Core-Interface. So ist fast die gesamte App ohne Windows-Maschine testbar; die Adapter
verifiziert die Windows-CI.

## Layout

```
src/Spitr.Core/               plattformneutral, unit-testbar auf macOS
├─ Audio/          AudioBuffer (16 kHz mono f32, Silence-Gate, Trim), Resampler,
│                  IAudioCaptureService, Pegel-Mathe (adaptiver Meter)
├─ Transcription/  ITranscriptionEngine + WhisperEngine (Whisper.net),
│                  EngineSelector, WhisperModelCatalog, ModelDownloader
├─ Text/           TextReplacementService (Wörterbuch), SmartSpacing (pure Funktion)
├─ Recording/      RecordingController — die Statemachine (Idle→Recording→
│                  Transcribing→Inserting), Job-Queue; IHotkeyService,
│                  ITextInsertionService, IFeedbackSoundService (Seams zur App)
├─ Commands/       VoiceCommand + VoiceCommandInterpreter (Befehlsmodus)
├─ Settings/       SettingsStore (JSON, %APPDATA%\Spitr), DictionaryStore,
│                  HistoryStore, KeyCombo, JsonFileStorage (atomar, corrupt-safe)
├─ Feedback/       ChimeSynthesizer (In-Memory-PCM, 3 Stile)
├─ Diagnostics/    LogStore (rotierend, nie diktierter Text)
└─ Localization/   Loc + generierte strings.de/en.json

src/Spitr.App/                Windows-only, dünne Adapter-Schale (WPF)
├─ Win32/          KeyboardHookService (WH_KEYBOARD_LL), ClipboardService,
│                  InputSender (SendInput), ForegroundInfo, CaretContextReader (UIA),
│                  StartupService (HKCU-Run)
├─ Audio/          WasapiAudioCaptureService, AudioDeviceService, ChimePlayer
├─ Text/           TextInsertionService (Clipboard+Strg-V+SmartSpacing)
├─ Overlay/        OverlayWindow (topmost, click-through, non-activating) + Waveforms
├─ SettingsUi/     SettingsWindow (6 Tabs), Onboarding/, TrayIconController
└─ SelfTest/       --selftest-Modus: Fixture-WAV → echte Pipeline → Paste (für CI)

tests/Spitr.Core.Tests/       xUnit, läuft auf macOS UND Windows (+ Whisper-Integration)
tests/Spitr.App.Tests/        Windows-only Adapter-Tests (Clipboard, Hook, Styles)
```

## Datenfluss einer Spracheingabe

```
Key-Down (Strg rechts)  → KeyboardHookService → RecordingController.StartRecording
                        → WasapiAudioCaptureService (→ Resampler → 16 kHz mono)
                        → Overlay + Pegel-Stream, Ready-Chime beim ersten Buffer
Key-Up                  → 180 ms Tail → Stop → Chime-Trim → Silence-Gate
                        → serielle Transcription-Queue → WhisperEngine.Transcribe
                        → TextReplacementService (Wörterbuch)
                        → TextInsertionService (Clipboard-Snapshot → Strg+V → Restore)
                        → HistoryStore + Done-Chime
```

## Verifikation ohne Windows-Maschine

1. **Mac-lokal:** `dotnet test tests/Spitr.Core.Tests` — gesamte Logik + echte
   Whisper-Transkription (osx-arm64-Runtime) gegen `Fixtures/german_test.wav`.
2. **Windows-CI:** Build, Adapter-Tests, `--selftest` (Fixture durch die echte Pipeline
   bis zum Paste in ein FlaUI-Notepad), FlaUI-Tab-Walk mit Screenshots als Artifacts.
3. **CI-Wächter:** `no-network-audit` grept gegen Netzwerk-Code außerhalb `ModelDownloader`.

## Referenz

Verhaltensfragen („wie macht es das Original?") beantwortet der macOS-Quellcode unter
`/Users/jarek/dev/claude/projects/Spitr` — insbesondere `Features/Recording/
RecordingController.swift` (Statemachine), `Core/Text/TextInsertionService.swift`
(Clipboard-Semantik) und `Core/Transcription/` (Engine-Seam).
