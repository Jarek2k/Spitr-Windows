# Spitr für Windows — *Spit it Out*

**Windows-Port von [Spitr](https://github.com/Jarek2k/Spitr): Taste halten, sprechen,
loslassen — der Text landet im fokussierten Fenster.** Komplett on-device, kostenlos,
privat. Keine Cloud, kein Abo, keine Telemetrie.

> **Pre-Alpha.** Der Port entsteht gerade. Noch nichts hier ist benutzbar.

## Wie es funktioniert

**Strg rechts** halten → schwebendes Overlay mit Live-Waveform → sprechen → loslassen →
die Aufnahme wird lokal mit [Whisper](https://github.com/ggerganov/whisper.cpp)
transkribiert → der Text wird per Zwischenablage + Strg+V eingefügt (deine Zwischenablage
wird vorher gesichert und danach wiederhergestellt, Diktate tauchen nicht in Win+V auf).

## Prinzipien

1. **Privat by design** — nichts verlässt den Rechner; einzige Ausnahme ist der
   einmalige Whisper-Modell-Download.
2. **Nie heimlich am Zuhören** — das Mikro ist nur live, solange die Taste physisch
   gehalten wird.
3. **Leichtgewichtig** — gute Erkennung, ohne die Maschine zu belegen.
4. **Natives Windows-Gefühl** — Tray-App, Fluent-Design, sauberes Settings-Fenster.

## Status / Entwicklung

Wird komplett auf macOS entwickelt und über GitHub Actions (Windows-Runner) verifiziert —
siehe [ARCHITECTURE.md](ARCHITECTURE.md). Feature-Stand: [FEATURES.md](FEATURES.md).

## Lizenz

Wie das Original: [LICENSE](https://github.com/Jarek2k/Spitr/blob/main/LICENSE).
