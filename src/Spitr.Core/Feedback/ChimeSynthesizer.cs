namespace Spitr.Core.Feedback;

/// <summary>
/// Synthetisiert die kurzen Audio-Cues in-memory als Float-PCM (mono,
/// 44,1 kHz): den Bereitschaftston in drei Stilen und den Abschluss-Chime.
/// Portierung der Ton-Synthese aus FeedbackSoundService.swift — dort wurden
/// 16-Bit-WAV-Daten für AVAudioPlayer gebaut; hier bleiben es Float-Samples in
/// [-1, 1], das Abspielen (WASAPI) übernimmt Spitr.App. Selbstgenügsam: kein
/// gebundeltes Asset, alle Töne entstehen aus Frequenz + Hüllkurve.
/// </summary>
public static class ChimeSynthesizer
{
    /// <summary>
    /// Ein Ton eines Cues: eine Frequenz, gehalten für eine Dauer (Sekunden).
    /// Zwischen aufeinanderfolgenden Noten fügt die Synthese eine kurze stille
    /// Lücke ein.
    /// </summary>
    private readonly record struct Note(double Frequency, double Duration);

    /// <summary>
    /// Stille Lücke zwischen den Noten eines mehrtönigen Cues. Kurz, damit die
    /// zwei Pieptöne als ein knackiges „beep-beep" lesen, nicht als zwei
    /// getrennte Töne.
    /// </summary>
    private const double InterNoteGap = 0.03;

    /// <summary>Abtastrate der synthetisierten Cues (wie in Swift: 44,1 kHz).</summary>
    public static int SampleRate => 44_100;

    /// <summary>Die Noten des jeweiligen Stils, in Reihenfolge (exakt wie in Swift).</summary>
    private static Note[] Notes(ReadyChimeStyle style) => style switch
    {
        // Ein einzelner warmer Blip (880 Hz). Minimal.
        ReadyChimeStyle.Single => [new Note(880, 0.15)],
        // Zwei gleiche Blips — das vertraute „beep-beep" eines Sprachmemo-/
        // Recorder-Start-Cues. Der Default.
        ReadyChimeStyle.Double => [new Note(880, 0.085), new Note(880, 0.085)],
        // Zwei aufsteigende Noten — der Push-to-talk-„Talk-Permit"-Cue, der als
        // „los, sprich" gelesen wird.
        ReadyChimeStyle.Rising => [new Note(660, 0.085), new Note(988, 0.10)],
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, "Unbekannter ReadyChimeStyle."),
    };

    /// <summary>
    /// Gesamtlänge eines Bereitschafts-Cues in Sekunden (alle Noten + die Lücken
    /// dazwischen). Der RecordingController schneidet genau dieses Fenster (plus
    /// Puffer) an Lautsprecher-Bleed vom Aufnahmestart weg — Single Source of
    /// Truth mit der Synthese unten.
    /// </summary>
    public static double ReadyChimeDuration(ReadyChimeStyle style)
    {
        var notes = Notes(style);
        var tones = notes.Sum(n => n.Duration);
        var gaps = Math.Max(0, notes.Length - 1) * InterNoteGap;
        return tones + gaps;
    }

    /// <summary>
    /// Rendert einen Bereitschafts-Cue: eine oder mehrere Sinus-Noten mit
    /// Raised-Cosine-Hüllkurven (Ein-/Ausblenden ohne Klicks), getrennt durch
    /// kurze stille Lücken.
    /// </summary>
    public static float[] ReadySamples(ReadyChimeStyle style)
    {
        const double fade = 0.012;   // 12 ms ein/aus
        const double amplitude = 0.6;
        var gapFrames = (int)(SampleRate * InterNoteGap);

        var pcm = new List<float>();
        var notes = Notes(style);
        for (var index = 0; index < notes.Length; index++)
        {
            if (index > 0) pcm.AddRange(Enumerable.Repeat(0f, gapFrames));
            AppendNote(pcm, notes[index].Frequency, notes[index].Duration, fade, amplitude);
        }
        return [.. pcm];
    }

    /// <summary>
    /// Der Abschluss-Chime: zwei kurze absteigende Noten (G#5 → C#5) — liest
    /// sich als sanfte „fertig"-Bestätigung, klar unterscheidbar von jedem
    /// Bereitschafts-Cue.
    /// </summary>
    public static float[] DoneSamples()
    {
        const double noteDuration = 0.085;
        const double fade = 0.01;
        const double amplitude = 0.55;
        double[] frequencies = [830.0, 554.0];   // ~G#5, ~C#5

        var pcm = new List<float>();
        foreach (var frequency in frequencies)
            AppendNote(pcm, frequency, noteDuration, fade, amplitude);
        return [.. pcm];
    }

    /// <summary>
    /// Hängt eine Sinus-Note mit Raised-Cosine-Fade-In/Out an. Identische
    /// Sample-Mathematik wie in Swift; der Clamp auf [-1, 1] entspricht dem
    /// Clamp vor der Int16-Quantisierung dort.
    /// </summary>
    private static void AppendNote(List<float> pcm, double frequency, double duration, double fade, double amplitude)
    {
        var frameCount = (int)(SampleRate * duration);
        for (var i = 0; i < frameCount; i++)
        {
            var t = i / (double)SampleRate;
            var env = 1.0;
            if (t < fade) env = 0.5 * (1 - Math.Cos(Math.PI * t / fade));
            var tail = duration - t;
            if (tail < fade) env = Math.Min(env, 0.5 * (1 - Math.Cos(Math.PI * tail / fade)));
            var value = Math.Sin(2 * Math.PI * frequency * t) * amplitude * env;
            pcm.Add((float)Math.Clamp(value, -1, 1));
        }
    }
}
