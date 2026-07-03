using Spitr.Core.Settings;

namespace Spitr.Core.Commands;

/// <summary>
/// Baut die Sprachbefehle live gegen die Stores und matcht Transkripte dagegen.
///
/// Eine Quelle der Wahrheit: Der Interpreter erzeugt die Befehlsliste aus den
/// Stores, und sowohl der Matcher als auch die Einstellungs-Liste lesen sie —
/// die dem Nutzer gezeigte Liste kann also nie von dem abweichen, was
/// tatsächlich läuft. Ein neuer Befehl ist ein Eintrag in <see cref="Commands"/>.
///
/// Windows-Anpassung: v1 ist Whisper-only, daher entfallen die
/// Engine-Umschalt-Befehle des Originals (offline / engineApple / engineWhisper).
/// </summary>
public sealed class VoiceCommandInterpreter
{
    /// <summary>Alle Befehle, gebaut gegen die übergebenen Live-Stores.</summary>
    public IReadOnlyList<VoiceCommand> Commands(SettingsStore settings,
                                                HistoryStore history,
                                                DictionaryStore dictionary) =>
    [
        new VoiceCommand
        {
            Id = "pause", Title = "Pausieren",
            Phrases = ["pause", "pausier", "anhalten", "stopp"],
            Perform = () => settings.IsPaused = true,
        },
        new VoiceCommand
        {
            Id = "resume", Title = "Fortsetzen",
            Phrases = ["weiter", "fortsetzen", "aktivier", "los gehts", "los geht's"],
            Perform = () => settings.IsPaused = false,
        },
        new VoiceCommand
        {
            Id = "langDE", Title = "Sprache: Deutsch",
            Phrases = ["deutsch", "german"],
            Perform = () => settings.LocaleIdentifier = "de-DE",
        },
        new VoiceCommand
        {
            Id = "langEN", Title = "Sprache: Englisch",
            Phrases = ["englisch", "english"],
            Perform = () => settings.LocaleIdentifier = "en-US",
        },
        new VoiceCommand
        {
            Id = "dictOn", Title = "Wörterbuch an",
            Phrases = ["wörterbuch an", "wörterbuch ein", "dictionary on"],
            Perform = () => dictionary.Enabled = true,
        },
        new VoiceCommand
        {
            Id = "dictOff", Title = "Wörterbuch aus",
            Phrases = ["wörterbuch aus", "dictionary off"],
            Perform = () => dictionary.Enabled = false,
        },
        new VoiceCommand
        {
            Id = "histOn", Title = "Verlauf an",
            Phrases = ["verlauf an", "verlauf ein", "history on"],
            Perform = () => history.Enabled = true,
        },
        new VoiceCommand
        {
            Id = "histOff", Title = "Verlauf aus",
            Phrases = ["verlauf aus", "history off"],
            Perform = () => history.Enabled = false,
        },
    ];

    /// <summary>
    /// Liefert den passenden Befehl für ein gesprochenes Transkript, sonst null.
    /// Bei mehreren Treffern gewinnt der Befehl mit der längsten gematchten
    /// Phrase — „wörterbuch aus" schlägt also einen kürzeren Zufalls-Treffer.
    /// Bei Längen-Gleichstand gewinnt der zuerst deklarierte Befehl (wie
    /// Swifts max(by:), das bei Gleichheit das erste Element behält).
    /// </summary>
    public VoiceCommand? Match(string transcript,
                               SettingsStore settings,
                               HistoryStore history,
                               DictionaryStore dictionary)
    {
        var needle = transcript.ToLowerInvariant();
        VoiceCommand? best = null;
        var bestLength = 0;
        foreach (var command in Commands(settings, history, dictionary))
        {
            var length = LongestMatch(command, needle);
            if (length > bestLength)
            {
                best = command;
                bestLength = length;
            }
        }
        return best;
    }

    /// <summary>Länge der längsten Phrase dieses Befehls, die in needle vorkommt (0 = kein Treffer).</summary>
    private static int LongestMatch(VoiceCommand command, string needle) =>
        command.Phrases
            .Where(p => p.Length > 0 && needle.Contains(p, StringComparison.Ordinal))
            .Select(p => p.Length)
            .DefaultIfEmpty(0)
            .Max();
}
