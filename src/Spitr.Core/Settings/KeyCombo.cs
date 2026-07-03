namespace Spitr.Core.Settings;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// Ein konfigurierbarer globaler Shortcut: Taste + Modifier. <see cref="Label"/>
/// ist das beim Aufnehmen erfasste Basiszeichen, damit die Anzeige nie
/// Virtual-Key-Codes zurückübersetzen muss.
/// </summary>
public sealed record KeyCombo(ushort VirtualKey, KeyModifiers Modifiers, string Label)
{
    /// <summary>
    /// Mindestens eine „starke" Modifier-Taste (Strg/Alt/Win) — nackte oder reine
    /// Umschalt-Chords lösen viel zu leicht aus.
    /// </summary>
    public bool IsValid => (Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Win)) != 0;

    /// <summary>True, wenn Taste und (relevante) Modifier exakt diesem Chord entsprechen.</summary>
    public bool Matches(ushort virtualKey, KeyModifiers modifiers) =>
        virtualKey == VirtualKey && modifiers == Modifiers;

    /// <summary>z. B. "Strg+Alt+Umschalt+V".</summary>
    public string DisplayString
    {
        get
        {
            var parts = new List<string>(4);
            if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Strg");
            if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Umschalt");
            if (Modifiers.HasFlag(KeyModifiers.Win)) parts.Add("Win");
            parts.Add(Label.ToUpperInvariant());
            return string.Join("+", parts);
        }
    }

    /// <summary>Default für „Letzte Spracheingabe erneut einfügen": Strg+Alt+Umschalt+V.</summary>
    public static KeyCombo ReinsertDefault { get; } =
        new(0x56 /* 'V' */, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, "v");
}
