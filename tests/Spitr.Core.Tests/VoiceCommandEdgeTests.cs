using Spitr.Core.Commands;
using Spitr.Core.Settings;

namespace Spitr.Core.Tests;

/// <summary>
/// Randfälle und die dokumentiert-unperfekten Verhaltensweisen des
/// Befehls-Matchings, damit jede künftige Änderung der Matching-Strategie eine
/// bewusste ist.
/// </summary>
public sealed class VoiceCommandEdgeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly VoiceCommandInterpreter _interpreter = new();
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly DictionaryStore _dictionary;

    public VoiceCommandEdgeTests()
    {
        _settings = new SettingsStore(_dir.Path);
        _history = new HistoryStore(_dir.Path);
        _dictionary = new DictionaryStore(_dir.Path);
    }

    public void Dispose() => _dir.Dispose();

    private VoiceCommand? Match(string transcript) =>
        _interpreter.Match(transcript, _settings, _history, _dictionary);

    [Fact]
    public void EmptyTranscriptMatchesNothing()
    {
        Assert.Null(Match(""));
    }

    [Fact]
    public void WhitespaceTranscriptMatchesNothing()
    {
        Assert.Null(Match("   \n "));
    }

    [Fact]
    public void PerformingDoesNotTouchUnrelatedState()
    {
        _history.Enabled = true;
        _dictionary.Enabled = true;
        _settings.LocaleIdentifier = "de-DE";
        Match("pause")?.Perform();
        // Nur IsPaused darf sich ändern.
        Assert.True(_settings.IsPaused);
        Assert.True(_history.Enabled);
        Assert.True(_dictionary.Enabled);
        Assert.Equal("de-DE", _settings.LocaleIdentifier);
    }

    // MARK: - Dokumentierte Grenzen (Substring + längster gewinnt)

    [Fact]
    public void MatchingIsSubstringBasedNotWordBased()
    {
        // BEKANNT: Phrasen werden als Substrings gematcht — ein längeres Wort,
        // das eine Phrase enthält, löst sie trotzdem aus. Dokumentiert, damit
        // ein künftiger Wechsel auf Wortgrenzen eine bewusste Änderung ist.
        Assert.Equal("pause", Match("verschnaufpause")?.Id);
    }

    [Fact]
    public void WithMultipleMatchesTheLongestPhraseWins()
    {
        // BEKANNT: Nennt ein Transkript zwei Befehle, gewinnt der mit der
        // längsten gematchten Phrase — hier „deutsch" (7) vor „weiter" (6).
        Assert.Equal("langDE", Match("weiter auf deutsch")?.Id);
    }

    [Fact]
    public void CommandIdsAreUnique()
    {
        var ids = _interpreter.Commands(_settings, _history, _dictionary)
            .Select(c => c.Id)
            .ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void EveryPhraseResolvesToItsOwnCommand()
    {
        // Jede deklarierte Phrase muss, allein gesprochen, den Befehl auslösen,
        // zu dem sie gehört — schützt davor, dass die Phrase eines Befehls von
        // der eines anderen überschattet wird.
        foreach (var command in _interpreter.Commands(_settings, _history, _dictionary))
        {
            foreach (var phrase in command.Phrases)
            {
                var hit = Match(phrase);
                Assert.True(hit?.Id == command.Id,
                    $"Phrase \"{phrase}\" löste {hit?.Id ?? "nichts"} aus, erwartet {command.Id}");
            }
        }
    }
}
