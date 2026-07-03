using Spitr.Core.Settings;

namespace Spitr.Core.Recording;

/// <summary>
/// Globale Hold-to-Talk-Taste + Re-Insert-Chord. Die Windows-Umsetzung
/// (WH_KEYBOARD_LL) liegt in Spitr.App; Tests injizieren einen Fake.
/// </summary>
public interface IHotkeyService
{
    /// <summary>Taste gedrückt. Argument: Command-Modus (Umschalt gehalten).</summary>
    event Action<bool>? Pressed;

    /// <summary>Taste losgelassen.</summary>
    event Action? Released;

    /// <summary>Esc während der Aufnahme — abbrechen.</summary>
    event Action? Cancelled;

    /// <summary>Re-Insert-Chord erkannt.</summary>
    event Action? ReinsertRequested;

    void Start();

    void UpdateHoldKey(HoldKey key);

    void UpdateReinsert(KeyCombo combo);

    /// <summary>Ab jetzt auf Esc lauschen (nur während einer Aufnahme aktiv).</summary>
    void BeginCancelWatch();

    void EndCancelWatch();
}
