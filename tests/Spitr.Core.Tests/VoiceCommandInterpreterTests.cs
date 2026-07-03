using Spitr.Core.Commands;
using Spitr.Core.Settings;

namespace Spitr.Core.Tests;

/// <summary>
/// Integrations-Ebene: Der Interpreter läuft gegen echte SettingsStore-,
/// HistoryStore- und DictionaryStore-Instanzen, und wir prüfen, dass das
/// Matchen einer gesprochenen Phrase den zugehörigen Zustand tatsächlich kippt.
/// </summary>
public sealed class VoiceCommandInterpreterTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly VoiceCommandInterpreter _interpreter = new();
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly DictionaryStore _dictionary;

    public VoiceCommandInterpreterTests()
    {
        _settings = new SettingsStore(_dir.Path);
        _history = new HistoryStore(_dir.Path);
        _dictionary = new DictionaryStore(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    /// <summary>Matcht und führt den Treffer direkt aus — wie der Befehlsmodus der App.</summary>
    private VoiceCommand? Run(string transcript)
    {
        var cmd = _interpreter.Match(transcript, _settings, _history, _dictionary);
        cmd?.Perform();
        return cmd;
    }

    [Fact]
    public void PauseAndResume()
    {
        Assert.Equal("pause", Run("pause bitte")?.Id);
        Assert.True(_settings.IsPaused);
        Assert.Equal("resume", Run("mach mal weiter")?.Id);
        Assert.False(_settings.IsPaused);
    }

    [Fact]
    public void EnginePhrasesMatchNoCommand()
    {
        // Windows v1 ist Whisper-only: Die Engine-Befehle des macOS-Originals
        // (offline / engineApple / engineWhisper) sind bewusst entfallen —
        // ihre Phrasen dürfen keinen Befehl mehr treffen.
        Assert.Null(_interpreter.Match("whisper", _settings, _history, _dictionary));
        Assert.Null(_interpreter.Match("apple", _settings, _history, _dictionary));
        Assert.Null(_interpreter.Match("offline", _settings, _history, _dictionary));
        Assert.Null(_interpreter.Match("wechsel zu whisperkit", _settings, _history, _dictionary));
        Assert.Null(_interpreter.Match("nimm apple speech", _settings, _history, _dictionary));
        Assert.Null(_interpreter.Match("offline modus", _settings, _history, _dictionary));
    }

    [Fact]
    public void SwitchLanguage()
    {
        Assert.Equal("langEN", Run("sprache englisch")?.Id);
        Assert.Equal("en-US", _settings.LocaleIdentifier);
        Assert.Equal("langDE", Run("auf deutsch")?.Id);
        Assert.Equal("de-DE", _settings.LocaleIdentifier);
    }

    [Fact]
    public void ToggleDictionary()
    {
        _dictionary.Enabled = true;
        Assert.Equal("dictOff", Run("wörterbuch aus")?.Id);
        Assert.False(_dictionary.Enabled);
        Assert.Equal("dictOn", Run("wörterbuch an")?.Id);
        Assert.True(_dictionary.Enabled);
    }

    [Fact]
    public void ToggleHistory()
    {
        Assert.Equal("histOff", Run("verlauf aus")?.Id);
        Assert.False(_history.Enabled);
        Assert.Equal("histOn", Run("verlauf an")?.Id);
        Assert.True(_history.Enabled);
    }

    [Fact]
    public void MatchingIsCaseInsensitive()
    {
        Assert.Equal("pause", _interpreter.Match("PAUSE", _settings, _history, _dictionary)?.Id);
    }

    [Fact]
    public void LongerPhraseWinsOverShorter()
    {
        // „wörterbuch aus" muss zu dictOff auflösen, nie zu einem kürzeren Zufalls-Treffer.
        Assert.Equal("dictOff",
            _interpreter.Match("bitte wörterbuch aus machen", _settings, _history, _dictionary)?.Id);
    }

    [Fact]
    public void UnknownTranscriptReturnsNull()
    {
        Assert.Null(_interpreter.Match("erzähl mir einen witz", _settings, _history, _dictionary));
    }

    [Fact]
    public void EveryCommandHasTitleAndExample()
    {
        foreach (var c in _interpreter.Commands(_settings, _history, _dictionary))
        {
            Assert.NotEmpty(c.Title);
            Assert.NotEmpty(c.Example);
        }
    }
}
