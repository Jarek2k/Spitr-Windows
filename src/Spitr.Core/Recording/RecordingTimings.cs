namespace Spitr.Core.Recording;

/// <summary>
/// Die Zeitkonstanten des Aufnahme-Flows (Werte aus dem macOS-Original).
/// Injizierbar, damit Tests sie auf Null setzen und deterministisch laufen.
/// </summary>
public sealed record RecordingTimings
{
    /// <summary>Nach Key-Up kurz weiter aufnehmen, damit Eingabe-Latenz das letzte Wort nicht abschneidet.</summary>
    public TimeSpan TrailingCapture { get; init; } = TimeSpan.FromMilliseconds(180);

    /// <summary>Fehlerzustand fällt danach automatisch auf Idle zurück.</summary>
    public TimeSpan ErrorIdleReset { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Anzeigedauer der Befehl-Rückmeldung im Overlay.</summary>
    public TimeSpan CommandFeedback { get; init; } = TimeSpan.FromMilliseconds(1600);

    /// <summary>Beat vor dem Re-Insert, damit die Ziel-App wieder fokussiert ist.</summary>
    public TimeSpan ReinsertFocusDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Zusätzlicher Puffer beim Wegschneiden des Bereitschaftstons.</summary>
    public TimeSpan ChimeTrimSlack { get; init; } = TimeSpan.FromMilliseconds(60);

    public static RecordingTimings Default { get; } = new();

    /// <summary>Alles auf Null — für deterministische Tests.</summary>
    public static RecordingTimings Instant { get; } = new()
    {
        TrailingCapture = TimeSpan.Zero,
        ErrorIdleReset = TimeSpan.Zero,
        CommandFeedback = TimeSpan.Zero,
        ReinsertFocusDelay = TimeSpan.Zero,
        ChimeTrimSlack = TimeSpan.Zero,
    };
}
