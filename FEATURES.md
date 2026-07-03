# Spitr für Windows — Feature-Katalog / Port-Status

Lebende Liste. Referenz ist der Feature-Katalog des macOS-Originals
(`/Users/jarek/dev/claude/projects/Spitr/FEATURES.md`). Bei jedem portierten Feature
**hier den Status pflegen**.

Status: ✅ portiert · 🧪 portiert, nur CI-getestet · 🔧 in Arbeit · 🔜 geplant · ➖ entfällt auf Windows

## Kern

| Feature | Status | Windows-Umsetzung |
|---|---|---|
| Hold-to-Talk | 🔜 | WH_KEYBOARD_LL, Default Strg rechts (AltGr-Problem: rechts-Alt nie anbieten) |
| On-device-Transkription | 🔜 | Whisper.net (whisper.cpp), GGML base/small/large-v3 |
| Apple-Speech-Engine | ➖ | kein Windows-Äquivalent; v1 ist Whisper-only, `ITranscriptionEngine`-Seam bleibt |
| WhisperKit-Engine | ➖→🔜 | ersetzt durch `WhisperEngine` (Whisper.net) |
| Aufnahme abbrechen (Esc) | 🔜 | Esc-Watch im Hook, nur während Aufnahme |
| Engine-Prewarm beim Start | 🔜 | `WhisperFactory.FromPath` beim App-Start |
| Text-Insertion mit Clipboard-Restore | 🔜 | Snapshot aller Formate → Strg+V (SendInput) → Restore; Win+V-Exclusion-Formate |
| Intelligente Leerzeichen | 🔜 | UIA TextPattern2.GetCaretRange; Graceful-Skip (Electron) |
| Silence-Gate (< −40 dBFS) | 🔜 | 1:1-Port in Core |
| Chime-Trim + 180-ms-Tail | 🔜 | 1:1-Port in Core |

## Bedienung & Anzeige

| Feature | Status | Windows-Umsetzung |
|---|---|---|
| Menüleisten-App | 🔜 | Tray-App (H.NotifyIcon), Icon-States idle/recording/processing |
| Aufnahme-Overlay | 🔜 | WPF-Fenster: borderless, topmost, click-through, non-activating |
| Visualisierungs-Stil wählbar | 🔜 | 5 Stile portieren (Signal reaktiv/pur, Signal, Balken, KITT) |
| Ton bei Aufnahmebereitschaft (3 Stile) | 🔜 | ChimeSynthesizer-Port, Playback via WASAPI |
| Ton bei Aufnahme-Ende | 🔜 | dito |
| Mehrsprachige Oberfläche | 🔜 | v1: de + en (Generator-Pipeline wie Original) |

## Konfiguration

| Feature | Status | Windows-Umsetzung |
|---|---|---|
| Einstellungen (6 Tabs) | 🔜 | WPF SettingsWindow: Allgemein/Vokabular/Wörterbuch/Befehle/Verlauf/Diagnose |
| Whisper-Modellwahl | 🔜 | base (Default) / small (empfohlen) / large-v3; Download mit Fortschrittsbalken |
| Sprachauswahl | 🔜 | wie Original (de Default) |
| Konfigurierbare Aufnahme-Taste | 🔜 | Strg rechts / CapsLock / Umschalt rechts / Pause |
| Mikrofon-Auswahl | 🔜 | MMDeviceEnumerator, Geräte-ID |
| Sprachisolierung | ➖ | Apple-spezifisch; evtl. später WASAPI-Communications-Kategorie |
| Beim Anmelden starten | 🔜 | HKCU-Run-Key |
| Custom Vocabulary | 🔜 | → Whisper `initial_prompt` |
| Personal Dictionary | 🔜 | 1:1-Port (Regex-Wortgrenzen) |
| Spracheingabe-Verlauf + Korrektur-Flow | 🔜 | 1:1-Port |
| Letzte Spracheingabe erneut einfügen | 🔜 | RegisterHotKey, Default Strg+Umschalt+Alt+V |
| Sprachbefehl-Modus (+ Umschalt) | 🔜 | 1:1-Port des Interpreters |
| Pausieren | 🔜 | 1:1-Port |

## Datenschutz & Onboarding

| Feature | Status | Windows-Umsetzung |
|---|---|---|
| Onboarding | 🔜 | 3 Schritte: Bedienung/Taste, Mikro-Pegeltest, Modell-Download mit Fortschritt |
| Mikro nur bei gehaltener Taste | 🔜 | harte Regel, wie Original |
| Keine Netzwerk-Calls | 🔜 | einzige Ausnahme ModelDownloader; CI-Wächter `no-network-audit` |
| Diagnose-Protokoll | 🔜 | rotierendes Log `%LOCALAPPDATA%\Spitr\logs`, nie diktierter Text |
