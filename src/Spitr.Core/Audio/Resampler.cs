using NAudio.Dsp;

namespace Spitr.Core.Audio;

/// <summary>
/// Rechnet Mikrofon-Audio beliebiger Rate/Kanalzahl auf das Engine-Format um:
/// 16 kHz mono Float — das Pendant zum AVAudioConverter im Swift-Original
/// (AudioCaptureService.swift, converter(for:)). Kern ist NAudios rein
/// managed <see cref="WdlResampler"/> (NAudio.Core), läuft also auch auf macOS.
///
/// Mehrkanal-Input wird VOR dem Resampling per Kanal-Mittelwert zu Mono
/// gemischt — so läuft der Filter nur einmal. Ist die Quelle bereits mono auf
/// Zielrate, wird ohne Filter durchgereicht (Fast Path).
///
/// Lebenszyklus wie im Original: pro Aufnahme eine frische Instanz, am Ende
/// einmal <see cref="Flush"/> — danach die Instanz verwerfen. Nicht
/// threadsicher; der Aufrufer serialisiert die Aufrufe (WASAPI liefert seine
/// Blöcke ohnehin sequenziell auf dem Capture-Thread).
/// </summary>
public sealed class Resampler(double sourceRate, int sourceChannels, double targetRate = 16_000)
{
    /// <summary>Kanalzahl der Quelle, validiert beim Konstruieren.</summary>
    private readonly int _channels = sourceChannels >= 1
        ? sourceChannels
        : throw new ArgumentOutOfRangeException(nameof(sourceChannels), sourceChannels, "Mindestens ein Eingangskanal erforderlich.");

    /// <summary>Ausgangs- pro Eingangs-Sample; auch Basis der Flush-Erwartung.</summary>
    private readonly double _ratio = targetRate / sourceRate;

    /// <summary>Null bei gleicher Rate — dann wird nur (falls nötig) gedownmixt.</summary>
    private readonly WdlResampler? _wdl = CreateWdl(sourceRate, targetRate);

    /// <summary>
    /// Gefütterte Mono-Frames bzw. gelieferte Ausgangs-Samples. Der Output wird
    /// pro Block auf ⌊input × ratio⌋ gedeckelt: WDLs Interpolationsschleife
    /// würde an ungeraden Blockgrenzen sonst leicht überproduzieren und dabei
    /// intern Position verlieren (Sample-Duplikate). Mit dem Deckel bleibt der
    /// Rest sauber gepuffert, und <see cref="Flush"/> kennt die exakte
    /// Differenz zur Erwartung.
    /// </summary>
    private long _inputFrames;
    private long _outputFrames;

    private static WdlResampler? CreateWdl(double sourceRate, double targetRate)
    {
        if (sourceRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceRate), sourceRate, "Quellrate muss positiv sein.");
        if (targetRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetRate), targetRate, "Zielrate muss positiv sein.");
        if (sourceRate == targetRate) return null;

        var wdl = new WdlResampler();
        // Interpolation + 2 kaskadierte Tiefpass-Stufen (kein Sinc): fürs
        // Downsampling von Sprache die übliche Qualität/CPU-Balance.
        wdl.SetMode(true, 2, false);
        wdl.SetFilterParms();
        // Input-getrieben: wir schieben Blöcke hinein, sobald das Gerät liefert,
        // und nehmen, was an Output herausfällt.
        wdl.SetFeedMode(true);
        wdl.SetRates(sourceRate, targetRate);
        return wdl;
    }

    /// <summary>
    /// Interleaved Float-Input beliebiger Rate/Kanalzahl → mono 16 kHz. Wegen
    /// der Filterlatenz kann ein Block weniger Output liefern als rechnerisch
    /// erwartet — der Rest kommt mit späteren Blöcken bzw. <see cref="Flush"/>.
    /// </summary>
    public float[] Process(ReadOnlySpan<float> interleaved)
    {
        if (interleaved.IsEmpty) return [];

        var mono = Downmix(interleaved);
        _inputFrames += mono.Length;

        if (_wdl is null)
        {
            // Fast Path: Quelle läuft schon auf Zielrate — nichts zu filtern.
            _outputFrames += mono.Length;
            return mono;
        }

        // Nie mehr ausgeben, als die bisher gefütterte Menge exakt hergibt —
        // der Bruchteil-Rest bleibt im Filter, bis weitere Blöcke (oder Flush)
        // ihn abholen.
        var allowed = (long)Math.Floor(_inputFrames * _ratio) - _outputFrames;
        var resampled = ResampleBlock(mono, (int)Math.Max(0, allowed));
        _outputFrames += resampled.Length;
        return resampled;
    }

    /// <summary>
    /// Spült die vom Filter zurückgehaltenen Restsamples am Aufnahmeende aus,
    /// indem Stille nachgeschoben wird, bis die Gesamtlänge der Erwartung
    /// (Eingangsframes × Ratenverhältnis) entspricht.
    /// </summary>
    public float[] Flush()
    {
        if (_wdl is null) return [];

        var expected = (long)Math.Round(_inputFrames * _ratio);
        var missing = (int)(expected - _outputFrames);
        if (missing <= 0) return [];

        var tail = new float[missing];
        var filled = 0;
        var zeros = new float[256];
        // Hartes Iterationslimit als Wächter — zurückgehalten sind nur wenige
        // Samples, normal reichen ein bis zwei Durchläufe.
        var guard = 64;
        while (filled < missing && guard-- > 0)
        {
            var chunk = ResampleBlock(zeros, missing - filled);
            Array.Copy(chunk, 0, tail, filled, chunk.Length);
            filled += chunk.Length;
        }

        _outputFrames += filled;
        return filled == missing ? tail : tail[..filled];
    }

    /// <summary>Kanal-Mittelwert pro Frame; Mono wird nur kopiert.</summary>
    private float[] Downmix(ReadOnlySpan<float> interleaved)
    {
        if (_channels == 1) return interleaved.ToArray();

        if (interleaved.Length % _channels != 0)
            throw new ArgumentException($"Interleaved-Länge {interleaved.Length} ist kein Vielfaches der Kanalzahl {_channels}.", nameof(interleaved));

        var frames = interleaved.Length / _channels;
        var mono = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            float sum = 0;
            var offset = frame * _channels;
            for (var channel = 0; channel < _channels; channel++)
                sum += interleaved[offset + channel];
            mono[frame] = sum / _channels;
        }
        return mono;
    }

    /// <summary>
    /// Ein Mono-Block durch den WDL-Filter, Output auf <paramref name="maxOut"/>
    /// Samples gedeckelt. Nicht konsumierter Input bleibt intern gepuffert und
    /// fließt in den nächsten Aufruf ein.
    /// </summary>
    private float[] ResampleBlock(float[] mono, int maxOut)
    {
        var wdl = _wdl!;
        // Im input-getriebenen Modus meldet ResamplePrepare, wie viele Samples
        // in den internen Puffer geschrieben werden sollen (== Blocklänge).
        var feed = wdl.ResamplePrepare(mono.Length, 1, out var inBuffer, out var inOffset);
        Array.Copy(mono, 0, inBuffer, inOffset, Math.Min(feed, mono.Length));

        var output = new float[maxOut];
        var produced = wdl.ResampleOut(output, 0, feed, maxOut, 1);
        return produced == maxOut ? output : output[..produced];
    }
}
