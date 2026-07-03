using Spitr.Core.Audio;

namespace Spitr.Core.Tests;

// Sichert das Stille-Gate ab, das Whisper davon abhält, aus einer fast stillen
// Aufnahme einen Satz zu halluzinieren (dessen eigene No-Speech-Erkennung ist
// nicht implementiert). Die Schwelle muss echte Stille/Noise-Floor-Clips
// verwerfen, aber selbst ein leise gesprochenes Wort behalten.
// Portiert aus SpitrTests/AudioBufferTests.swift.
public class AudioBufferTests
{
    /// <summary>
    /// Konstanter Ton mit gegebenem Peak — steht für „das lauteste Sample
    /// erreicht diesen Pegel", mehr schaut das Gate nicht an.
    /// </summary>
    private static AudioBuffer Tone(float peak, int count = 16_000) =>
        new(Enumerable.Repeat(peak, count).ToArray(), 16_000);

    [Fact]
    public void PureSilenceIsSilent()
    {
        var buffer = new AudioBuffer(new float[16_000], 16_000);
        Assert.Equal(double.NegativeInfinity, buffer.PeakDbfs);
        Assert.True(buffer.IsLikelySilent);
    }

    [Fact]
    public void EmptyBufferIsSilent()
    {
        var buffer = new AudioBuffer([], 16_000);
        Assert.True(buffer.IsLikelySilent);
    }

    [Fact]
    public void NoiseFloorIsSilent()
    {
        // ~ -54 dBFS: Mikrofon-Eigenrauschen / ruhiger Raum — unter dem -40-dB-Gate.
        Assert.True(Tone(0.002f).IsLikelySilent);
    }

    [Fact]
    public void QuietSpokenWordIsNotSilent()
    {
        // ~ -34 dBFS: ein leise gesprochenes Wort peakt noch über dem Gate.
        Assert.False(Tone(0.02f).IsLikelySilent);
    }

    [Fact]
    public void NormalSpeechIsNotSilent()
    {
        // ~ -6 dBFS: gewöhnlicher Diktierpegel.
        Assert.False(Tone(0.5f).IsLikelySilent);
    }

    [Fact]
    public void PeakIgnoresSurroundingSilence()
    {
        // Ein lautes Sample inmitten von Stille: die Peak-basierte Erkennung
        // sieht das Wort trotzdem, wo ein RMS-Mittel den Clip fälschlich als
        // still lesen würde.
        var samples = new float[16_000];
        samples[8_000] = 0.3f;
        var buffer = new AudioBuffer(samples, 16_000);
        Assert.False(buffer.IsLikelySilent);
    }

    [Fact]
    public void TrimmingLeadingDropsTheChimeWindow()
    {
        // 1-s-Puffer bei 16 kHz; die ersten 0,2 s weg → 0,8 s / 12 800 Samples übrig.
        var buffer = Tone(0.5f);
        var trimmed = buffer.TrimLeading(0.2);
        Assert.Equal(12_800, trimmed.Samples.Count);
        Assert.Equal(16_000.0, trimmed.SampleRate);
        Assert.Equal(TimeSpan.FromSeconds(0.8), trimmed.Duration);
    }

    [Fact]
    public void TrimmingLeadingClampsAnOverShortBuffer()
    {
        // Mehr wegschneiden, als der Puffer hält, ergibt leer — keinen Crash.
        var buffer = Tone(0.5f, count: 1_000);
        var trimmed = buffer.TrimLeading(1.0);
        Assert.Empty(trimmed.Samples);
        Assert.True(trimmed.IsLikelySilent);
    }

    [Fact]
    public void ChimeBleedIsGatedAfterTrimming()
    {
        // Eine Aufnahme, die nur aus Chime-Bleed (lauter Anfang) plus Stille
        // besteht, liest sich auf dem vollen Puffer als Sprache, nach dem
        // Wegschneiden des Chime-Fensters aber als Stille — genau die
        // Reihenfolge, die finishRecording verwendet.
        var samples = new float[16_000];
        for (var i = 0; i < 2_400; i++) samples[i] = 0.3f;   // ~0,15 s Chime am Anfang
        var buffer = new AudioBuffer(samples, 16_000);
        Assert.False(buffer.IsLikelySilent);                     // Chime lässt es laut aussehen
        Assert.True(buffer.TrimLeading(0.21).IsLikelySilent);    // nach dem Trim weg
    }
}
