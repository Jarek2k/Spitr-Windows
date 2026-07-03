using Spitr.Core.Audio;

namespace Spitr.Core.Tests;

// Verhaltens-Tests für den 16-kHz-Mono-Resampler: Längen- und Tonhöhen-Erhalt
// beim Downsampling, Downmix-Mathematik (Kanal-Mittelwert), der Fast Path bei
// bereits passendem Format und das Ausspülen der Filterlatenz per Flush.
public sealed class ResamplerTests
{
    /// <summary>Blockgröße wie ein typischer WASAPI-Callback (~10 ms bei 48 kHz).</summary>
    private const int BlockSize = 480;

    private static float[] Sine(double frequency, double sampleRate, double seconds, float amplitude = 0.5f)
    {
        var samples = new float[(int)(sampleRate * seconds)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = amplitude * MathF.Sin((float)(2 * Math.PI * frequency * i / sampleRate));
        return samples;
    }

    /// <summary>
    /// Vorzeichenwechsel zwischen benachbarten Samples; exakte Nullen (z. B.
    /// der Stille-Tail aus Flush) zählen nicht.
    /// </summary>
    private static int ZeroCrossings(IReadOnlyList<float> samples)
    {
        var count = 0;
        for (var i = 1; i < samples.Count; i++)
            if (samples[i - 1] * samples[i] < 0) count++;
        return count;
    }

    /// <summary>Blockweise durch den Resampler plus abschließendes Flush.</summary>
    private static List<float> ProcessAll(Resampler resampler, float[] interleaved, int blockSize)
    {
        var output = new List<float>();
        for (var offset = 0; offset < interleaved.Length; offset += blockSize)
        {
            var length = Math.Min(blockSize, interleaved.Length - offset);
            output.AddRange(resampler.Process(interleaved.AsSpan(offset, length)));
        }
        output.AddRange(resampler.Flush());
        return output;
    }

    [Fact]
    public void Downsampling_48k_erhaelt_laenge_und_tonhoehe()
    {
        // 1 s Sinus 440 Hz bei 48 kHz → ~16 000 Samples, weiterhin 440 Hz
        // (880 Nulldurchgänge pro Sekunde).
        var input = Sine(440, 48_000, 1.0);
        var output = ProcessAll(new Resampler(48_000, 1), input, BlockSize);

        Assert.InRange(output.Count, 15_840, 16_160);           // 16 000 ± 1 %
        Assert.InRange(ZeroCrossings(output), 836, 924);        // 880 ± 5 %
    }

    [Fact]
    public void Stereo_mit_identischen_kanaelen_entspricht_mono()
    {
        var mono = Sine(440, 48_000, 0.25);
        var stereo = new float[mono.Length * 2];
        for (var i = 0; i < mono.Length; i++)
        {
            stereo[2 * i] = mono[i];
            stereo[2 * i + 1] = mono[i];
        }

        // Der Mittelwert zweier identischer Kanäle ist exakt der Kanalwert —
        // beide Pfade müssen also samplegenau dasselbe liefern.
        var monoOut = ProcessAll(new Resampler(48_000, 1), mono, BlockSize);
        var stereoOut = ProcessAll(new Resampler(48_000, 2), stereo, BlockSize * 2);

        Assert.Equal(monoOut, stereoOut);
    }

    [Fact]
    public void Stereo_mit_gegenphasigen_kanaelen_mittelt_zu_stille()
    {
        // L = +0.5, R = −0.5 konstant → Kanal-Mittelwert 0 vor dem Resampling.
        var stereo = new float[9_600];
        for (var i = 0; i < stereo.Length; i += 2)
        {
            stereo[i] = 0.5f;
            stereo[i + 1] = -0.5f;
        }

        var output = ProcessAll(new Resampler(48_000, 2), stereo, BlockSize * 2);

        Assert.NotEmpty(output);
        Assert.All(output, sample => Assert.True(MathF.Abs(sample) < 1e-6f, $"Sample {sample} ist nicht ~0"));
    }

    [Fact]
    public void Passthrough_16k_mono_liefert_input_unveraendert()
    {
        var input = Sine(440, 16_000, 0.1);
        var resampler = new Resampler(16_000, 1);

        var output = resampler.Process(input);

        Assert.Equal(input.Length, output.Length);
        Assert.Equal(input, output);
        // Kein Filter im Spiel → nichts auszuspülen.
        Assert.Empty(resampler.Flush());
    }

    [Fact]
    public void Flush_liefert_den_rest_fuer_die_erwartete_gesamtlaenge()
    {
        // 1 s + 2 Samples bei 48 kHz → erwartet round(48 002 / 3) = 16 001.
        // Während des Streamings liefert der Resampler höchstens
        // ⌊input × ratio⌋ = 16 000 aus; den Bruchteil-Rest am Ende spült erst
        // Flush aus. Gefüttert wird in 1024er-Blöcken (die Swift-Tap-Größe),
        // die sich nicht glatt durch das Ratenverhältnis teilen.
        const int totalInput = 48_002;
        var input = Sine(440, 48_000, 1.0);
        Array.Resize(ref input, totalInput);
        var resampler = new Resampler(48_000, 1);

        var streamed = 0;
        for (var offset = 0; offset < input.Length; offset += 1024)
            streamed += resampler.Process(input.AsSpan(offset, Math.Min(1024, input.Length - offset))).Length;
        var tail = resampler.Flush();

        Assert.True(streamed < 16_001, $"Der Resampler sollte vor dem Flush Samples zurückhalten (streamed={streamed})");
        Assert.NotEmpty(tail);
        Assert.Equal(16_001, streamed + tail.Length);
        // Ein zweiter Flush hat nichts mehr auszuspülen.
        Assert.Empty(resampler.Flush());
    }
}
