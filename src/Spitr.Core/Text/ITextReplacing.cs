using Spitr.Core.Settings;

namespace Spitr.Core.Text;

/// <summary>
/// Nachbearbeitungs-Schritt: wendet das persönliche Wörterbuch auf ein fertiges
/// Transkript an, bevor es eingefügt wird. Rein String → String, damit es
/// testbar und frei von Seiteneffekten bleibt — die Entscheidung „wie matchen
/// wir" lebt hier hinter dem Interface, die Regeln selbst im DictionaryStore.
/// </summary>
public interface ITextReplacing
{
    /// <summary>Wendet <paramref name="rules"/> der Reihe nach auf <paramref name="text"/> an und liefert den umgeschriebenen String.</summary>
    string Apply(IReadOnlyList<ReplacementRule> rules, string text);
}
