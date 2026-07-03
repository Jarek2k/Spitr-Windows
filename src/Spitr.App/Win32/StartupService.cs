using Microsoft.Win32;
using Spitr.Core.Diagnostics;

namespace Spitr.App.Win32;

/// <summary>
/// „Beim Anmelden starten" über den klassischen Run-Key
/// (HKCU\Software\Microsoft\Windows\CurrentVersion\Run, Wert "Spitr").
/// Kein Task-Scheduler, kein Dienst — der Run-Key ist für eine Tray-App der
/// einfachste, für Nutzer transparenteste Mechanismus (sichtbar im Task-Manager
/// unter Autostart). Alle Registry-Fehler werden tolerant geloggt; das UI liest
/// den Zustand nach dem Setzen zurück, damit es nie lügt.
/// </summary>
public sealed class StartupService
{
    private static readonly DiagLog Log = new("startup");

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Spitr";

    /// <summary>Ob der Autostart-Eintrag existiert bzw. gesetzt/entfernt werden soll.</summary>
    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex)
            {
                Log.Warning($"startup state read failed: {ex.GetType().Name}");
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                if (value)
                {
                    var exePath = Environment.ProcessPath;
                    if (string.IsNullOrEmpty(exePath))
                    {
                        Log.Warning("startup enable skipped: process path unavailable");
                        return;
                    }
                    // In Anführungszeichen, damit Pfade mit Leerzeichen funktionieren.
                    key.SetValue(ValueName, $"\"{exePath}\"");
                    Log.Info("startup entry set");
                }
                else
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                    Log.Info("startup entry removed");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"startup state write failed: {ex.GetType().Name}");
            }
        }
    }
}
