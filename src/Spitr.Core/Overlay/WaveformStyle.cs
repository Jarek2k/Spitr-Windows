namespace Spitr.Core.Overlay;

/// <summary>Visualisierungsstil des Aufnahme-Overlays.</summary>
public enum WaveformStyle
{
    /// <summary>Audio-reaktive Signalform, randlos (Default).</summary>
    SignalReactive,

    /// <summary>Signalform pur, ohne Kapsel/Chrome.</summary>
    SignalBare,

    /// <summary>Signalform in Kapsel mit Mikro-Glyphe.</summary>
    Signal,

    /// <summary>Pegel-Balken.</summary>
    Bars,

    /// <summary>Rote LED-Voice-Box („KITT").</summary>
    Kitt,
}
