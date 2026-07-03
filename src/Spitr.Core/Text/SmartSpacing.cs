using System.Text.RegularExpressions;

namespace Spitr.Core.Text;

/// <summary>
/// Reine Text-Logik der intelligenten Leerzeichen beim Einfügen: fasst Läufe
/// von Leerzeichen/Tabs im Text zusammen und entscheidet anhand des Zeichens
/// vor dem Caret, ob ein führendes Leerzeichen gehört. Das Auslesen des
/// Caret-Kontexts (UIA) bleibt plattformspezifisch in Spitr.App.
/// </summary>
public static partial class SmartSpacing
{
    /// <summary>Satzzeichen, die sich ans vorige Wort anschließen — nie ein Leerzeichen davor.</summary>
    private const string TrailingPunctuation = ".,;:!?)]}";

    /// <summary>Öffnende Klammern und Anführungszeichen („ “ ‘ «) — davon nicht durch ein Leerzeichen trennen.</summary>
    private const string OpeningBracketsAndQuotes = "([{“„‘«";

    /// <summary>Läufe von 2+ Leerzeichen/Tabs — werden zu einem einzelnen Leerzeichen (einzelne Tabs bleiben).</summary>
    [GeneratedRegex("[ \\t]{2,}")]
    private static partial Regex SpaceRuns();

    /// <summary>
    /// Bereitet den Einfüge-Text vor: <paramref name="precedingCharacter"/> ist
    /// das Zeichen unmittelbar vor dem Caret (null = unbekannt oder Feldanfang —
    /// dann wird nicht geraten und kein Leerzeichen vorangestellt). Bei
    /// <paramref name="enabled"/> = false geht der Text unverändert durch.
    /// </summary>
    public static string Prepare(string text, char? precedingCharacter, bool enabled)
    {
        if (!enabled) return text;
        var collapsed = SpaceRuns().Replace(text, " ");
        return LeadingSpaceNeeded(collapsed, precedingCharacter) ? " " + collapsed : collapsed;
    }

    private static bool LeadingSpaceNeeded(string text, char? precedingCharacter)
    {
        if (text.Length == 0 || char.IsWhiteSpace(text[0])) return false;
        // Satzzeichen, die ans vorige Wort anschließen, bekommen nie ein Leerzeichen.
        if (TrailingPunctuation.Contains(text[0])) return false;
        if (precedingCharacter is not { } prev) return false; // unbekannt → nicht raten
        if (char.IsWhiteSpace(prev)) return false;
        // Auch nicht von einer öffnenden Klammer / einem öffnenden Anführungszeichen trennen.
        if (OpeningBracketsAndQuotes.Contains(prev)) return false;
        return true;
    }
}
