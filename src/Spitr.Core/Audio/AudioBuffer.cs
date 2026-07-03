namespace Spitr.Core.Audio;

/// <summary>
/// Mono-PCM-Puffer als universelles Eingabeformat aller Engines.
/// Float-Samples in [-1, 1], typisch 16 kHz.
/// </summary>
public sealed class AudioBuffer(float[] samples, double sampleRate)
{
    public IReadOnlyList<float> Samples => samples;
    public double SampleRate => sampleRate;

    internal float[] RawSamples => samples;

    public TimeSpan Duration => TimeSpan.FromSeconds(samples.Length / sampleRate);

    /// <summary>
    /// Lautestes absolutes Sample in dBFS (0 dB = Full Scale, −∞ bei reiner Stille).
    /// Peak statt RMS, damit ein einzelnes gesprochenes Wort in einem sonst leisen
    /// Clip zählt — nur wirklich stille Aufnahmen bleiben am Noise-Floor.
    /// </summary>
    public double PeakDbfs
    {
        get
        {
            float peak = 0;
            foreach (var s in samples) peak = MathF.Max(peak, MathF.Abs(s));
            return peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity;
        }
    }

    /// <summary>
    /// Ob die Aufnahme praktisch Stille ist — niemand hat gesprochen. Whisper
    /// halluziniert aus Fast-Stille ganze Sätze, deshalb wird die Transkription
    /// dann komplett übersprungen. Echte Sprache peakt deutlich über −40 dBFS;
    /// Mikrofon-Eigenrauschen und ein ruhiger Raum bleiben darunter.
    /// </summary>
    public bool IsLikelySilent => PeakDbfs < -40;

    /// <summary>
    /// Kopie ohne die ersten <paramref name="seconds"/> Sekunden. Schneidet den
    /// Bereitschaftston weg, der über die Lautsprecher in den Aufnahmestart
    /// einblutet. Geklemmt: ein zu kurzer Puffer wird leer statt zu crashen.
    /// </summary>
    public AudioBuffer TrimLeading(double seconds)
    {
        var drop = Math.Min(samples.Length, Math.Max(0, (int)(seconds * sampleRate)));
        return new AudioBuffer(samples[drop..], sampleRate);
    }
}
