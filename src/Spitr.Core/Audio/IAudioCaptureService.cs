namespace Spitr.Core.Audio;

/// <summary>
/// Mikrofon-Capture — live ausschließlich zwischen <see cref="Start"/> (Key-Down)
/// und <see cref="Stop"/> (Key-Up). Die Windows-Umsetzung (WASAPI) liegt in
/// Spitr.App; Tests injizieren einen Fake.
/// </summary>
public interface IAudioCaptureService
{
    /// <summary>
    /// Feuert auf dem ersten wirklich gelieferten Buffer — das Mikro nimmt jetzt
    /// tatsächlich auf (nach Hardware-Warm-up). Trigger für den Bereitschaftston.
    /// </summary>
    event Action? CaptureStarted;

    /// <summary>Normalisierter Eingangspegel 0…1 für die Overlay-Waveform.</summary>
    event Action<float>? LevelChanged;

    /// <summary>Geräte-ID des gewünschten Mikrofons; null/leer = Systemstandard.</summary>
    string? PreferredDeviceId { get; set; }

    /// <summary>Startet die Aufnahme. Wirft bei nicht verfügbarem Eingabegerät.</summary>
    void Start();

    /// <summary>Stoppt die Aufnahme und liefert den akkumulierten 16-kHz-Mono-Puffer.</summary>
    AudioBuffer Stop();
}
