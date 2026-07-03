using Spitr.App.Win32;
using Spitr.Core.Settings;

namespace Spitr.App.Tests;

/// <summary>
/// Laufzeit-Tests für den echten WH_KEYBOARD_LL-Hook — nur unter Windows
/// lauffähig (CI: windows-latest, interaktiver Desktop). Ohne synthetische
/// Tastendrücke prüfbar ist der Lifecycle: Start/Dispose terminieren sauber,
/// beides ist idempotent, Update-/CancelWatch-Aufrufe werfen nie.
/// Die Timeouts fangen den Worst Case ab (hängende GetMessage-Loop beim
/// Herunterfahren), statt den Runner endlos zu blockieren.
/// </summary>
public sealed class KeyboardHookServiceTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task StartThenDispose_CompletesWithinTimeout()
    {
        await Task.Run(() =>
        {
            var service = new KeyboardHookService();
            service.Start();
            service.Dispose();
        }).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task DoubleStart_IsIdempotent()
    {
        await Task.Run(() =>
        {
            using var service = new KeyboardHookService();
            service.Start();
            service.Start(); // zweiter Aufruf darf weder werfen noch neu starten
        }).WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task DoubleDispose_IsIdempotent()
    {
        await Task.Run(() =>
        {
            var service = new KeyboardHookService();
            service.Start();
            service.Dispose();
            service.Dispose();
        }).WaitAsync(TestTimeout);
    }

    [Fact]
    public void DisposeWithoutStart_DoesNotThrow()
    {
        var service = new KeyboardHookService();
        service.Dispose();
    }

    [Fact]
    public void UpdatesBeforeStart_DoNotThrow()
    {
        using var service = new KeyboardHookService();
        service.UpdateHoldKey(HoldKey.CapsLock);
        service.UpdateHoldKey(HoldKey.Pause);
        service.UpdateReinsert(KeyCombo.ReinsertDefault);
        service.UpdateReinsert(new KeyCombo(0x42 /* 'B' */, KeyModifiers.Control | KeyModifiers.Win, "b"));
    }

    [Fact]
    public async Task UpdatesAfterStart_DoNotThrow()
    {
        await Task.Run(() =>
        {
            using var service = new KeyboardHookService();
            service.Start();
            service.UpdateHoldKey(HoldKey.RightShift);
            service.UpdateReinsert(new KeyCombo(0x56 /* 'V' */, KeyModifiers.Control | KeyModifiers.Alt, "v"));
            service.UpdateHoldKey(HoldKey.RightCtrl);
            service.UpdateReinsert(KeyCombo.ReinsertDefault);
        }).WaitAsync(TestTimeout);
    }

    [Fact]
    public void CancelWatch_TogglesWithoutStart()
    {
        using var service = new KeyboardHookService();
        service.BeginCancelWatch();
        service.EndCancelWatch();
        service.BeginCancelWatch();
        service.BeginCancelWatch(); // doppeltes Beginnen ist erlaubt
        service.EndCancelWatch();
        service.EndCancelWatch(); // doppeltes Beenden ebenso
    }
}
