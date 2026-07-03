using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Spitr.App.Win32;

/// <summary>
/// Informationen über das Vordergrundfenster. Relevant vor dem Paste: In ein
/// erhöht laufendes Fenster (Admin) kann ein NIEDRIGER laufender Prozess wegen
/// UIPI kein SendInput liefern — der Tastendruck verpufft lautlos. Läuft Spitr
/// selbst erhöht (z. B. der CI-Runner), gibt es keine UIPI-Schranke.
/// </summary>
public static class ForegroundInfo
{
    /// <summary>Elevation des eigenen Prozesses — ändert sich nie, einmal ermitteln.</summary>
    private static readonly Lazy<bool> CurrentProcessElevated = new(() =>
    {
        using var process = PInvoke.GetCurrentProcess_SafeHandle();
        return IsProcessElevated(process);
    });

    /// <summary>
    /// True, wenn UIPI den Paste ins Vordergrundfenster blocken würde: das Ziel
    /// läuft erhöht, wir selbst nicht. Jeder Fehlerpfad liefert false — „nicht
    /// blockiert" ist die harmlose Annahme, der Paste-Versuch schlägt dann
    /// schlimmstenfalls leise fehl.
    /// </summary>
    public static bool IsPasteBlockedByElevation() =>
        !CurrentProcessElevated.Value && IsForegroundElevated();

    /// <summary>True, wenn der Prozess des Vordergrundfensters erhöht läuft.</summary>
    public static bool IsForegroundElevated()
    {
        var hwnd = PInvoke.GetForegroundWindow();
        if (hwnd.IsNull) return false;

        if (PInvoke.GetWindowThreadProcessId(hwnd, out var processId) == 0 || processId == 0)
        {
            return false;
        }

        using var process = PInvoke.OpenProcess_SafeHandle(
            PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION,
            bInheritHandle: false,
            processId);
        if (process.IsInvalid) return false;

        return IsProcessElevated(process);
    }

    private static bool IsProcessElevated(SafeHandle process)
    {
        if (!PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
        {
            return false;
        }

        using (token)
        {
            // TOKEN_ELEVATION ist ein einzelnes DWORD (TokenIsElevated).
            Span<byte> elevation = stackalloc byte[sizeof(uint)];
            if (!PInvoke.GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenElevation, elevation, out var written)
                || written != elevation.Length)
            {
                return false;
            }
            return BinaryPrimitives.ReadUInt32LittleEndian(elevation) != 0;
        }
    }
}
