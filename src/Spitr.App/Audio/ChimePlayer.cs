using NAudio.Wave;
using Spitr.Core.Diagnostics;
using Spitr.Core.Feedback;
using Spitr.Core.Recording;
using System.Runtime.InteropServices;

namespace Spitr.App.Audio;

/// <summary>
/// Spielt die in <see cref="ChimeSynthesizer"/> synthetisierten Audio-Cues
/// (Float-PCM, 44,1 kHz mono) fire-and-forget über WaveOutEvent ab. Pro
/// Wiedergabe entsteht ein frisches Ausgabegerät, das sich nach dem Ton selbst
/// entsorgt. Sämtliche Fehler werden verschluckt und nur geloggt — ein
/// fehlendes Ausgabegerät (CI-Runner!) darf niemals ein Diktat abbrechen.
/// </summary>
public sealed class ChimePlayer : IFeedbackSoundService
{
    private static readonly DiagLog Log = new("feedback");

    /// <inheritdoc/>
    public void PlayReady(ReadyChimeStyle style) => Play(ChimeSynthesizer.ReadySamples(style));

    /// <inheritdoc/>
    public void PlayDone() => Play(ChimeSynthesizer.DoneSamples());

    private static void Play(float[] samples)
    {
        if (samples.Length == 0) return;

        WaveOutEvent? output = null;
        try
        {
            var bytes = MemoryMarshal.AsBytes<float>(samples).ToArray();
            var provider = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(ChimeSynthesizer.SampleRate, 1))
            {
                BufferLength = bytes.Length,
                // Wichtig: mit dem Default ReadFully=true würde der Provider
                // nach dem Cue endlos Stille liefern, PlaybackStopped käme nie
                // und jedes WaveOutEvent bliebe für immer offen.
                ReadFully = false,
            };
            provider.AddSamples(bytes, 0, bytes.Length);

            var device = new WaveOutEvent();
            output = device;
            // Selbst-Entsorgung nach dem Ausklingen (feuert auf dem
            // Playback-Thread, der zu diesem Zeitpunkt fertig ist).
            device.PlaybackStopped += (_, _) => device.Dispose();
            device.Init(provider);
            device.Play();
        }
        catch (Exception ex)
        {
            Log.Warning($"chime playback unavailable: {ex.GetType().Name}");
            try
            {
                output?.Dispose();
            }
            catch (Exception disposeEx)
            {
                Log.Debug($"chime dispose failed: {disposeEx.GetType().Name}");
            }
        }
    }
}
