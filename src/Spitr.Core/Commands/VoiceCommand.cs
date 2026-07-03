namespace Spitr.Core.Commands;

/// <summary>
/// Ein Sprachbefehl: Wird die Aufnahme im Befehlsmodus ausgelöst, werden die
/// gesprochenen Worte nicht als Text eingefügt, sondern gegen diese Befehle
/// gematcht, um Spitrs eigene Einstellungen freihändig umzuschalten.
/// </summary>
public sealed class VoiceCommand
{
    public required string Id { get; init; }

    /// <summary>Anzeigename für die Befehlsliste.</summary>
    public required string Title { get; init; }

    /// <summary>Gesprochene Auslöse-Phrasen (kleingeschrieben, als Substrings gematcht).</summary>
    public required IReadOnlyList<string> Phrases { get; init; }

    /// <summary>Führt den Befehl aus.</summary>
    public required Action Perform { get; init; }

    /// <summary>Die Phrase, die im UI als Beispiel gezeigt wird.</summary>
    public string Example => Phrases.FirstOrDefault() ?? "";
}
