using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using Spitr.Core.Diagnostics;

namespace Spitr.App.Win32;

/// <summary>
/// Liest best-effort das Zeichen unmittelbar vor dem Caret im fokussierten
/// Element der Ziel-App per UI Automation — das Pendant zum AX-Read
/// precedingCharacter() des macOS-Originals (Selected-Text-Range → Zeichen
/// davor). Liefert null, wenn nichts fokussiert ist, das Ziel kein
/// Text-Pattern anbietet (viele Electron-/Chromium-Apps, Terminals), der Read
/// wirft oder in den Watchdog läuft — SmartSpacing fasst dann nur
/// Leerzeichen-Läufe zusammen und stellt nie ein Leerzeichen voran,
/// exakt wie das Original bei einem fehlgeschlagenen AX-Read.
/// </summary>
public static class CaretContextReader
{
    private static readonly DiagLog Log = new("insertion");

    /// <summary>
    /// Watchdog für den UIA-Read: die Aufrufe sind synchrone COM-Calls in den
    /// Ziel-Prozess und können auf einer nicht reagierenden App unbegrenzt
    /// hängen — länger als das warten wir vor dem Paste nicht.
    /// </summary>
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Eine prozessweite UIA-Client-Instanz, beim ersten Read erzeugt und nie
    /// disposed (lebt bis Prozessende). Das zugrunde liegende
    /// CUIAutomation8-COM-Objekt ist free-threaded, Aufrufe von wechselnden
    /// Threadpool-Threads sind damit unkritisch; das Lazy
    /// (ExecutionAndPublication) garantiert nur, dass die Erzeugung selbst
    /// exakt einmal passiert.
    /// </summary>
    private static readonly Lazy<UIA3Automation> Automation =
        new(() => new UIA3Automation(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Zeichen vor dem Caret des fokussierten Elements, oder null wenn
    /// unbekannt (Feldanfang zählt als unbekannt — dort ist ohnehin kein
    /// führendes Leerzeichen nötig). Läuft auf dem Transkriptions-Worker
    /// unmittelbar vor dem Paste; der Pfad ist bewusst kurz gehalten.
    /// </summary>
    public static char? PrecedingCharacter()
    {
        try
        {
            // Der eigentliche Read läuft hinter einem Watchdog auf dem
            // Threadpool: läuft er ab, arbeitet der Task im Hintergrund zu
            // Ende — er teilt außer der Automation-Instanz keinen
            // veränderlichen Zustand, das ist folgenlos.
            var read = Task.Run(ReadPrecedingCharacter);
            if (!read.Wait(ReadTimeout))
            {
                Log.Debug("caret context unavailable (timeout)");
                return null;
            }

            if (read.Result is { } preceding)
            {
                // Log-Regel: nur das Ereignis, NIE das Zeichen selbst.
                Log.Debug("caret char read");
                return preceding;
            }
        }
        catch
        {
            // AggregateException aus Wait/Result und alles andere — bewusst
            // breit: der Caret-Kontext ist rein optional (Original: nil).
        }

        Log.Debug("caret context unavailable");
        return null;
    }

    /// <summary>Der eigentliche UIA-Read; jede Abweichung vom Happy Path liefert null.</summary>
    private static char? ReadPrecedingCharacter()
    {
        try
        {
            var focused = Automation.Value.FocusedElement();
            if (focused is null) return null;

            var range = CaretRange(focused);
            if (range is null) return null;

            // Startpunkt des Caret-Ranges um ein Zeichen nach hinten ziehen;
            // 0 bewegte Einheiten = Feldanfang → nichts davor.
            if (range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1) == 0)
            {
                return null;
            }

            // Bei bestehender Selektion umfasst der Range mehr als ein Zeichen —
            // GetText(1) liefert trotzdem genau das Zeichen vor dem Range-Start.
            var text = range.GetText(1);
            return string.IsNullOrEmpty(text) ? null : text[0];
        }
        catch
        {
            // Kein Fokus-Element, Pattern nicht unterstützt, Ziel-App weg,
            // COM-Fehler — alles gleichwertig „Kontext unbekannt".
            return null;
        }
    }

    /// <summary>
    /// Text-Range an der Caret-Position: bevorzugt TextPattern2.GetCaretRange,
    /// sonst die aktuelle Selektion aus TextPattern — deren Start ist bei
    /// leerer Selektion genau die Caret-Position (so liest auch das
    /// macOS-Original über die Selected-Text-Range). Der Fallback zählt:
    /// WPF-TextBoxen etwa bieten nur TextPattern, kein TextPattern2.
    /// </summary>
    private static ITextRange? CaretRange(AutomationElement focused)
    {
        if (focused.Patterns.Text2.TryGetPattern(out var text2))
        {
            var caret = text2.GetCaretRange(out var isActive);
            if (isActive && caret is not null)
            {
                return caret.Clone();
            }
        }

        if (focused.Patterns.Text.TryGetPattern(out var text))
        {
            var selection = text.GetSelection();
            if (selection is { Length: > 0 })
            {
                return selection[0].Clone();
            }
        }

        return null;
    }
}
