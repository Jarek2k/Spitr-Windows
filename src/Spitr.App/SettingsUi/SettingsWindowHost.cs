using System.Windows;

namespace Spitr.App.SettingsUi;

/// <summary>
/// Verwaltet das eine Einstellungsfenster der App: erster Aufruf erzeugt es
/// über die Factory, jeder weitere holt das offene Fenster nur in den
/// Vordergrund (Pendant zum macOS-Settings-Window, das es auch nur einmal
/// gibt). Beim Schließen wird die Referenz freigegeben, der nächste Aufruf
/// baut frisch. Nur vom UI-Thread aufrufen.
/// </summary>
public static class SettingsWindowHost
{
    private static SettingsWindow? _window;

    /// <summary>Zeigt das Einstellungsfenster bzw. fokussiert das bereits offene.</summary>
    public static void ShowOrFocus(Func<SettingsWindow> factory)
    {
        if (_window is { } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
            {
                existing.WindowState = WindowState.Normal;
            }
            existing.Show();
            existing.Activate();
            return;
        }

        var window = factory();
        _window = window;
        window.Closed += (_, _) => _window = null;
        window.Show();
        window.Activate();
    }
}
