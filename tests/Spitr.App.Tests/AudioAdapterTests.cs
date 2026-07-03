using Spitr.App.Audio;
using Spitr.Core.Feedback;

namespace Spitr.App.Tests;

// Runtime-Tests für die WASAPI-Adapter — laufen ausschließlich in der
// Windows-CI. Bewusst tolerant gegenüber Runnern OHNE Audiogeräte: jeder Test
// hat einen definierten Erfolgspfad sowohl mit als auch ohne Hardware, damit
// nichts flaked.
public sealed class AudioAdapterTests
{
    [Fact]
    public void InputDevices_wirft_nie_und_liefert_konsistente_eintraege()
    {
        var devices = new AudioDeviceService().InputDevices();

        // Ohne Geräte: leere Liste. Mit Geräten: ID und Name stets gefüllt.
        Assert.NotNull(devices);
        Assert.All(devices, device =>
        {
            Assert.False(string.IsNullOrEmpty(device.Id));
            Assert.False(string.IsNullOrEmpty(device.Name));
        });
    }

    [Fact]
    public void Start_wirft_deutsche_meldung_ohne_geraet_oder_nimmt_auf()
    {
        var service = new WasapiAudioCaptureService();
        try
        {
            service.Start();
            // Gerät vorhanden: sofort wieder stoppen → gültiger 16-kHz-Puffer.
            var buffer = service.Stop();
            Assert.Equal(16_000, buffer.SampleRate);
        }
        catch (InvalidOperationException ex)
        {
            // Devicelose Runner: die deutsche Meldung landet im
            // Fehlerzustand des Controllers.
            Assert.Contains("Mikrofon", ex.Message);
        }
    }

    [Fact]
    public void Stop_ohne_start_liefert_leeren_puffer()
    {
        var buffer = new WasapiAudioCaptureService().Stop();

        Assert.Empty(buffer.Samples);
        Assert.Equal(16_000, buffer.SampleRate);
    }

    [Fact]
    public void Chimes_werfen_ohne_ausgabegeraet_nicht()
    {
        var player = new ChimePlayer();

        // Fire-and-forget: auch ohne Ausgabegerät dürfen die Cues ein Diktat
        // niemals abbrechen.
        player.PlayReady(ReadyChimeStyle.Single);
        player.PlayReady(ReadyChimeStyle.Double);
        player.PlayReady(ReadyChimeStyle.Rising);
        player.PlayDone();
    }
}
