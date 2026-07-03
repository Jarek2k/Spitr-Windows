namespace Spitr.Core.Recording;

/// <summary>
/// Fügt Text ins fokussierte Fenster ein (Clipboard-Snapshot → Strg+V → Restore).
/// Windows-Umsetzung in Spitr.App; Tests injizieren einen Fake.
/// </summary>
public interface ITextInsertionService
{
    /// <summary>Intelligente Leerzeichen (führendes Leerzeichen je nach Caret-Kontext).</summary>
    bool SmartSpacing { get; set; }

    void Insert(string text);
}
