using Spitr.Core.Audio;

namespace Spitr.Core.Tests;

// Verhaltens-Tests, abgeleitet aus der portierten Meter-Mathematik
// (AudioCaptureService.swift, handleTap): Stille liest sich als 0, lauter Input
// treibt das Meter sofort hoch (schneller Attack), der Rückfall ist graduell
// (langsamer Release), Dauerpegel kalibriert sich als neuer Noise-Floor ein,
// und Reset stellt den Frischzustand wieder her.
public class AdaptiveLevelMeterTests
{
    /// <summary>Blockgröße wie der Swift-Tap (bufferSize: 1024).</summary>
    private const int BlockSize = 1024;

    private static float[] Silence() => new float[BlockSize];

    /// <summary>
    /// Sinus statt Konstante, damit der Block wie echtes Audio ein RMS unter
    /// dem Peak hat (RMS ≈ amplitude/√2).
    /// </summary>
    private static float[] Tone(float amplitude)
    {
        var block = new float[BlockSize];
        for (var i = 0; i < BlockSize; i++)
            block[i] = amplitude * MathF.Sin(2 * MathF.PI * 440 * i / 16_000);
        return block;
    }

    [Fact]
    public void SilenceReadsAsZero()
    {
        var meter = new AdaptiveLevelMeter();
        float level = 1;
        for (var i = 0; i < 20; i++) level = meter.Process(Silence());
        Assert.True(level < 0.01f, $"Stille sollte ~0 liefern, war {level}");
    }

    [Fact]
    public void LoudBlockAfterSilenceRisesFast()
    {
        var meter = new AdaptiveLevelMeter();
        for (var i = 0; i < 20; i++) meter.Process(Silence());

        // Schneller Attack (0.92 pro Block): ein einziger lauter Block treibt
        // das Meter sofort in den oberen Bereich.
        var level = meter.Process(Tone(0.5f));
        Assert.True(level > 0.8f, $"Lauter Block sollte sofort hoch ausschlagen, war {level}");
    }

    [Fact]
    public void ReleaseAfterLoudIsGradual()
    {
        var meter = new AdaptiveLevelMeter();
        for (var i = 0; i < 20; i++) meter.Process(Silence());
        var loud = meter.Process(Tone(0.5f));

        // Langsamer Release (0.85 pro Block): ein einzelner stiller Block
        // kollabiert das Meter nicht auf 0 …
        var afterOne = meter.Process(Silence());
        Assert.True(afterOne > 0, "Ein stiller Block darf das Meter nicht sofort auf 0 ziehen");
        Assert.True(afterOne < loud, "Ohne Input muss der Pegel fallen");

        // … aber nach vielen stillen Blöcken liegt es wieder praktisch bei 0.
        float level = afterOne;
        for (var i = 0; i < 50; i++) level = meter.Process(Silence());
        Assert.True(level < 0.01f, $"Nach langer Stille sollte das Meter ~0 zeigen, war {level}");
    }

    [Fact]
    public void ConstantInputSelfCalibratesTowardTheFloor()
    {
        // Dauerhaft gleich lautes Signal (z. B. Lüfterrauschen) wird als neuer
        // Noise-Floor gelernt: der Floor kriecht langsam (0.0008/Block) an den
        // Pegel heran, der Peak klingt ab (0.004/Block), die Spanne klemmt bei
        // MinRangeDb — das Meter liest den Dauerton zunehmend als Grundrauschen
        // statt als Ausschlag. Genau das hält Umgebungsrauschen von der
        // Wellenform fern, ohne echte Sprache zu dämpfen.
        var meter = new AdaptiveLevelMeter();
        var block = Tone(0.05f);

        var early = meter.Process(block);
        float late = early;
        for (var i = 0; i < 6_000; i++) late = meter.Process(block);

        Assert.True(early > 0.5f, $"Frisches Meter sollte den Mittelpegel deutlich zeigen, war {early}");
        Assert.True(late < 0.2f, $"Nach Selbstkalibrierung sollte der Dauerton fast verschwinden, war {late}");
        Assert.True(late < early / 2, "Die Kalibrierung muss den Pegel klar absenken");
    }

    [Fact]
    public void ResetRestoresTheFreshState()
    {
        var meter = new AdaptiveLevelMeter();
        for (var i = 0; i < 50; i++) meter.Process(Tone(0.5f));

        meter.Reset();

        // Nach Reset verhält sich das Meter exakt wie eine frische Instanz —
        // Envelope UND Kalibrierung sind zurückgesetzt.
        var block = Tone(0.05f);
        var fresh = new AdaptiveLevelMeter();
        Assert.Equal(fresh.Process(block), meter.Process(block));

        // Und ein stiller Block liest sich nach Reset sofort wieder als exakt 0.
        meter.Reset();
        Assert.Equal(0f, meter.Process(Silence()));
    }

    [Fact]
    public void ResetEnvelopeKeepsCalibrationLikeSwiftStart()
    {
        // Der Per-Aufnahme-Reset (Swift startEngine) leert nur den Envelope;
        // die gelernte Pegelspanne überlebt zwischen Aufnahmen — deshalb passt
        // das Meter ab der zweiten Aufnahme sofort zum Mikrofon.
        var meter = new AdaptiveLevelMeter();
        var block = Tone(0.05f);
        for (var i = 0; i < 6_000; i++) meter.Process(block);
        var calibrated = meter.Process(block);

        meter.ResetEnvelope();

        // Kurz einschwingen lassen (der Envelope startet wieder bei 0) — dann
        // liegt der Pegel nahe dem kalibrierten Wert, nicht beim hohen
        // Frischzustand.
        float again = 0;
        for (var i = 0; i < 10; i++) again = meter.Process(block);
        Assert.True(Math.Abs(again - calibrated) < 0.05f,
            $"Kalibrierung muss ResetEnvelope überleben: vorher {calibrated}, nachher {again}");
    }
}
