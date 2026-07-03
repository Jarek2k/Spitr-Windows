namespace Spitr.Core.Settings;

/// <summary>
/// Die anbietbaren Hold-to-Talk-Tasten. Bewusst NICHT dabei: rechts-Alt — auf
/// deutschen Layouts ist das AltGr (Strg+Alt) und wird zum Tippen von @ € [ ] { }
/// gebraucht. Alle Kandidaten sind Tasten, die selbst nichts tippen; die gewählte
/// Taste wird vom Hook geschluckt (CapsLock togglet also nicht).
/// </summary>
public enum HoldKey
{
    /// <summary>Strg rechts (Default) — auf jeder QWERTZ-Tastatur vorhanden.</summary>
    RightCtrl,

    /// <summary>Feststelltaste.</summary>
    CapsLock,

    /// <summary>Umschalt rechts.</summary>
    RightShift,

    /// <summary>Pause/Untbr.</summary>
    Pause,
}

public static class HoldKeyExtensions
{
    /// <summary>Windows-Virtual-Key-Code der Taste (reine Konstante, kein Win32-Bezug).</summary>
    public static ushort VirtualKey(this HoldKey key) => key switch
    {
        HoldKey.RightCtrl => 0xA3,  // VK_RCONTROL
        HoldKey.CapsLock => 0x14,   // VK_CAPITAL
        HoldKey.RightShift => 0xA1, // VK_RSHIFT
        HoldKey.Pause => 0x13,      // VK_PAUSE
        _ => 0xA3,
    };

    public static string DisplayName(this HoldKey key) => key switch
    {
        HoldKey.RightCtrl => "Strg rechts",
        HoldKey.CapsLock => "Feststelltaste",
        HoldKey.RightShift => "Umschalt rechts",
        HoldKey.Pause => "Pause",
        _ => "Strg rechts",
    };
}
