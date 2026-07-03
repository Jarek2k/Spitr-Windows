#!/usr/bin/env bash
# Erzeugt das deterministische Test-Fixture für den Whisper-Integrationstest.
# Läuft nur auf macOS (say + afconvert); das Ergebnis ist eingecheckt, damit
# CI nie von TTS abhängt. Nach Neu-Erzeugung lokal verifizieren:
#   dotnet test tests/Spitr.Core.Tests --filter "Category=Whisper"
set -euo pipefail
cd "$(dirname "$0")/.."

say -v Anna "Dies ist ein Test der Spracheingabe mit Spitr." -o /tmp/spitr_fixture.aiff
afconvert -f WAVE -d LEI16@16000 -c 1 /tmp/spitr_fixture.aiff \
  tests/Spitr.Core.Tests/Fixtures/german_test.wav
rm /tmp/spitr_fixture.aiff
echo "OK: tests/Spitr.Core.Tests/Fixtures/german_test.wav"
