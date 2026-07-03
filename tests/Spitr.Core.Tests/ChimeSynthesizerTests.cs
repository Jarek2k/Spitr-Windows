using Spitr.Core.Feedback;

namespace Spitr.Core.Tests;

// Prüft die portierte Ton-Synthese aus FeedbackSoundService.swift: die
// annoncierte Cue-Dauer (Trim-Fenster des RecordingControllers!) muss zur
// tatsächlichen Sample-Länge passen, die Samples müssen gültiges, hörbares
// Float-PCM sein, und die Stile müssen unterscheidbar bleiben.
public class ChimeSynthesizerTests
{
    private static readonly ReadyChimeStyle[] AllStyles = Enum.GetValues<ReadyChimeStyle>();

    [Theory]
    [InlineData(ReadyChimeStyle.Single, 0.15)]                    // 1 × 0,15 s, keine Lücke
    [InlineData(ReadyChimeStyle.Double, 0.085 + 0.085 + 0.03)]    // 2 Noten + 1 Lücke
    [InlineData(ReadyChimeStyle.Rising, 0.085 + 0.10 + 0.03)]     // 660 Hz + 988 Hz + Lücke
    public void ReadyChimeDurationMatchesTheSwiftNoteTiming(ReadyChimeStyle style, double expected)
    {
        Assert.Equal(expected, ChimeSynthesizer.ReadyChimeDuration(style), precision: 12);
    }

    [Theory]
    [InlineData(ReadyChimeStyle.Single)]
    [InlineData(ReadyChimeStyle.Double)]
    [InlineData(ReadyChimeStyle.Rising)]
    public void ReadySampleCountMatchesTheAdvertisedDuration(ReadyChimeStyle style)
    {
        // ReadyChimeDuration ist die Single Source of Truth für das Trim-Fenster —
        // die tatsächliche Sample-Länge darf höchstens um einen Audio-Puffer
        // (1024 Frames ≈ 23 ms) abweichen (Int-Truncation pro Note/Lücke).
        var samples = ChimeSynthesizer.ReadySamples(style);
        var actual = samples.Length / (double)ChimeSynthesizer.SampleRate;
        var advertised = ChimeSynthesizer.ReadyChimeDuration(style);
        Assert.True(Math.Abs(actual - advertised) <= 1024.0 / ChimeSynthesizer.SampleRate,
            $"{style}: Samples ergeben {actual:F4}s, annonciert {advertised:F4}s");
    }

    [Theory]
    [InlineData(ReadyChimeStyle.Single)]
    [InlineData(ReadyChimeStyle.Double)]
    [InlineData(ReadyChimeStyle.Rising)]
    public void ReadyCuesAreValidAudiblePcm(ReadyChimeStyle style)
    {
        var samples = ChimeSynthesizer.ReadySamples(style);
        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.InRange(s, -1f, 1f));
        // Nicht still: die Sinus-Amplitude von 0,6 muss deutlich durchkommen.
        Assert.True(samples.Max(s => MathF.Abs(s)) > 0.3f);
    }

    [Fact]
    public void DoneChimeIsValidAudiblePcm()
    {
        var samples = ChimeSynthesizer.DoneSamples();
        Assert.NotEmpty(samples);
        Assert.All(samples, s => Assert.InRange(s, -1f, 1f));
        // Amplitude 0,55 — hörbar, aber unter Full Scale.
        Assert.True(samples.Max(s => MathF.Abs(s)) > 0.3f);
    }

    [Fact]
    public void StylesProduceDistinctCues()
    {
        // Start- und Ende-Cues dürfen nie verwechselbar sein — jeder Stil (und
        // der Done-Chime) hat eine eigene Noten-Sequenz.
        var single = ChimeSynthesizer.ReadySamples(ReadyChimeStyle.Single);
        var dbl = ChimeSynthesizer.ReadySamples(ReadyChimeStyle.Double);
        var rising = ChimeSynthesizer.ReadySamples(ReadyChimeStyle.Rising);
        var done = ChimeSynthesizer.DoneSamples();

        Assert.False(single.SequenceEqual(dbl));
        Assert.False(single.SequenceEqual(rising));
        Assert.False(dbl.SequenceEqual(rising));
        foreach (var ready in new[] { single, dbl, rising })
            Assert.False(done.SequenceEqual(ready));
    }

    [Fact]
    public void EveryStyleHasSamplesAndADuration()
    {
        // Guard gegen ein neues Enum-Mitglied ohne Noten-Sequenz: Synthese und
        // Dauer müssen für jeden Stil definiert sein.
        foreach (var style in AllStyles)
        {
            Assert.NotEmpty(ChimeSynthesizer.ReadySamples(style));
            Assert.True(ChimeSynthesizer.ReadyChimeDuration(style) > 0);
        }
    }
}
