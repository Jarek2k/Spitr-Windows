namespace Spitr.Core.Audio;

/// <summary>
/// Selbstkalibrierendes Level-Meter für die Wellenform im Overlay — Portierung
/// der Meter-Mathematik aus AudioCaptureService.swift (handleTap), ohne den
/// AVAudioEngine-Teil. Pro Audio-Block: RMS → dBFS, adaptiver Pegelbereich
/// (schnell fallender Noise-Floor, schnell springender Peak, beide mit
/// langsamer Gegendrift), Einordnung in den Bereich, dann ein Envelope-Follower
/// mit schnellem Attack und langsamem Release → normierter Pegel 0…1.
///
/// Mapping zum Swift-Original: alle Koeffizienten sind pro Block definiert,
/// nicht pro Zeiteinheit — in Swift liefert der Tap Blöcke von ~1024 Frames bei
/// Hardware-Rate (≈ 21–64 ms). Aufrufer sollten Blöcke ähnlicher Größenordnung
/// liefern, damit das Zeitverhalten (Attack/Release/Drift) dem Original
/// entspricht; eine Sample-Rate braucht die Mathematik selbst nicht, deshalb
/// nimmt <see cref="Process"/> nur den Block.
///
/// Nicht threadsicher — der Aufrufer serialisiert Process/Reset (das
/// Swift-Original erledigt das mit einem NSLock um denselben Zustand).
/// </summary>
public sealed class AdaptiveLevelMeter
{
    // Startschätzungen für den adaptiven Pegelbereich (dBFS). Kalibrieren sich
    // zur Laufzeit auf das jeweilige Mikrofon, kein Per-Mic-Tuning nötig.
    private const double InitialFloorDb = -50;
    private const double InitialPeakDb = -20;

    // Nie über weniger als diese Spanne normalisieren, damit Umgebungsrauschen
    // allein das Meter nicht auf Vollausschlag treibt, wenn nichts Lautes
    // passiert ist.
    private const double MinRangeDb = 15;

    /// <summary>
    /// Envelope-Follower für das Level-Meter: schneller Attack, langsamer
    /// Release, damit die Lücken zwischen Silben die Wellenform nicht zu einem
    /// Punkt kollabieren lassen.
    /// </summary>
    private float _envelope;

    /// <summary>
    /// Adaptiver Lautstärkebereich (dBFS), über Aufnahmen hinweg getrackt, damit
    /// das Meter zu jedem Mikrofon passt: ein langsamer Noise-Floor und eine
    /// Recent-Peak-Decke.
    /// </summary>
    private double _noiseFloorDb = InitialFloorDb;
    private double _peakDb = InitialPeakDb;

    /// <summary>
    /// Verarbeitet einen Audio-Block (Float-PCM in [-1, 1]) und liefert den
    /// normierten Pegel 0…1 für die Wellenform. Ein leerer Block lässt den
    /// Zustand unverändert (Swift: guard frameLength &gt; 0).
    /// </summary>
    public float Process(ReadOnlySpan<float> block)
    {
        if (block.IsEmpty) return _envelope;

        // RMS → dBFS; Untergrenze 1e-7, damit log10 bei Stille nicht -∞ wird.
        float sumSquares = 0;
        foreach (var s in block) sumSquares += s * s;
        var rms = MathF.Sqrt(sumSquares / block.Length);
        var db = 20 * Math.Log10(Math.Max(rms, 1e-7));

        // Adaptiver Gain: der Noise-Floor fällt schnell auf neues Leise und
        // kriecht langsam wieder hoch; der Peak springt auf neues Laut und
        // klingt langsam ab. Der aktuelle Pegel wird dann in diesen selbst-
        // kalibrierenden Bereich eingeordnet — laut → nahe 1, leise → nahe 0,
        // auf jedem Mikrofon.
        _noiseFloorDb += (db - _noiseFloorDb) * (db < _noiseFloorDb ? 0.3 : 0.0008);
        _peakDb += (db - _peakDb) * (db > _peakDb ? 0.5 : 0.004);
        var span = Math.Max(_peakDb - _noiseFloorDb, MinRangeDb);
        var level = (float)Math.Max(0, Math.Min(1, (db - _noiseFloorDb) / span));

        // Envelope-Follower: sofort auf lauteren Input springen, schnell genug
        // zurückfallen, dass Wort-/Silbenstruktur sichtbar bleibt, aber ein
        // einzelner blockgroßer Einbruch das Meter nicht kollabieren lässt.
        var coeff = level > _envelope ? 0.92f : 0.85f;
        _envelope += (level - _envelope) * coeff;
        return _envelope;
    }

    /// <summary>
    /// Setzt das Meter komplett auf den Neuzustand zurück (Envelope und
    /// Kalibrierung — wie eine frische Instanz). Hinweis zur Fidelity: Swift
    /// setzt bei jedem Aufnahmestart nur den Envelope zurück und behält
    /// Floor/Peak über Aufnahmen hinweg — dafür gibt es
    /// <see cref="ResetEnvelope"/>.
    /// </summary>
    public void Reset()
    {
        _envelope = 0;
        _noiseFloorDb = InitialFloorDb;
        _peakDb = InitialPeakDb;
    }

    /// <summary>
    /// Der Per-Aufnahme-Reset aus Swift startEngine(): nur der Envelope fällt
    /// auf 0, die selbstkalibrierte Pegelspanne bleibt erhalten.
    /// </summary>
    public void ResetEnvelope() => _envelope = 0;
}
