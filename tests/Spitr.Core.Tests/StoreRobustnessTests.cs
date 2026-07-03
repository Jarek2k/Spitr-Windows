using Spitr.Core.Feedback;
using Spitr.Core.Overlay;
using Spitr.Core.Settings;
using Spitr.Core.Transcription;

namespace Spitr.Core.Tests;

// Was passiert mit korrupten oder fremden persistierten Daten, unbekannten Ids
// und unbekannten Enum-Strings — die Stores müssen sauber degradieren, nie crashen.
public class StoreRobustnessTests
{
    /// <summary>Legt kaputte Bytes dorthin, wo der Store seine Datei erwartet.</summary>
    private static void WriteGarbage(string directory, string fileName) =>
        File.WriteAllBytes(Path.Combine(directory, fileName), [0xDE, 0xAD, 0xBE, 0xEF]);

    [Fact]
    public void HistoryToleratesCorruptedData()
    {
        using var dir = new TempDir();
        WriteGarbage(dir.Path, "history.json");
        var h = new HistoryStore(dir.Path);
        Assert.Empty(h.Entries);
        Assert.True(h.Enabled);
        // Danach weiter benutzbar.
        h.Record("ok");
        Assert.Single(h.Entries);
        // Die defekte Datei liegt in Quarantäne, statt jeden Start erneut zu stören.
        Assert.True(File.Exists(Path.Combine(dir.Path, "history.json.corrupt")));
    }

    [Fact]
    public void DictionaryToleratesCorruptedData()
    {
        using var dir = new TempDir();
        WriteGarbage(dir.Path, "dictionary.json");
        var d = new DictionaryStore(dir.Path);
        Assert.Empty(d.Rules);
        d.Add();
        Assert.Single(d.Rules);
        Assert.True(File.Exists(Path.Combine(dir.Path, "dictionary.json.corrupt")));
    }

    [Fact]
    public void SettingsTolerateCorruptedData()
    {
        using var dir = new TempDir();
        WriteGarbage(dir.Path, "settings.json");
        var s = new SettingsStore(dir.Path);
        Assert.Equal("de-DE", s.LocaleIdentifier);
        Assert.Equal(HoldKey.RightCtrl, s.HoldKey);
        Assert.True(File.Exists(Path.Combine(dir.Path, "settings.json.corrupt")));
    }

    [Fact]
    public void DictionaryUpdateWithUnknownIdIsNoop()
    {
        using var dir = new TempDir();
        var d = new DictionaryStore(dir.Path);
        d.Add();
        var before = d.Rules.ToList();
        d.Update(new ReplacementRule(Guid.NewGuid(), "x", "y"));   // fremde Id
        Assert.Equal(before, d.Rules);
    }

    [Fact]
    public void DictionaryDeleteWithUnknownIdIsNoop()
    {
        using var dir = new TempDir();
        var d = new DictionaryStore(dir.Path);
        d.Add();
        d.Delete(Guid.NewGuid());                                  // fremde Id
        Assert.Single(d.Rules);
    }

    [Fact]
    public void SettingsFallBackOnUnknownHoldKeyRawValue()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            """{ "HoldKey": "definitely-not-a-key" }""");
        Assert.Equal(HoldKey.RightCtrl, new SettingsStore(dir.Path).HoldKey);
    }

    [Fact]
    public void SettingsFallBackOnUnknownWaveformRawValue()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            """{ "WaveformStyle": "squiggles" }""");
        Assert.Equal(WaveformStyle.SignalReactive, new SettingsStore(dir.Path).WaveformStyle);
    }

    [Fact]
    public void SettingsSanitizeUnknownWhisperModel()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            """{ "WhisperModel": "ancient-model" }""");
        Assert.Equal(WhisperModelCatalog.DefaultModel, new SettingsStore(dir.Path).WhisperModel);
    }

    [Fact]
    public void SettingsFallBackPerFieldKeepingIntactValues()
    {
        using var dir = new TempDir();
        // Gültiges JSON, aber lauter unbekannte Enum-/Modell-Strings: jedes Feld
        // fällt einzeln auf seinen Default zurück, intakte Felder überleben.
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            """
            {
              "LocaleIdentifier": "en-US",
              "HoldKey": "banana",
              "WaveformStyle": "squiggles",
              "ReadyChimeStyle": "gong",
              "WhisperModel": "ancient-model"
            }
            """);
        var s = new SettingsStore(dir.Path);
        Assert.Equal(HoldKey.RightCtrl, s.HoldKey);
        Assert.Equal(WaveformStyle.SignalReactive, s.WaveformStyle);
        Assert.Equal(ReadyChimeStyle.Double, s.ReadyChimeStyle);
        Assert.Equal(WhisperModelCatalog.DefaultModel, s.WhisperModel);
        Assert.Equal("en-US", s.LocaleIdentifier);   // intaktes Feld bleibt
        // Die Datei war gültiges JSON — keine Quarantäne.
        Assert.False(File.Exists(Path.Combine(dir.Path, "settings.json.corrupt")));
    }
}
