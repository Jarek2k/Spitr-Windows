using System.Buffers.Binary;
using Windows.Win32;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Spitr.App.Win32;

/// <summary>
/// Informationen über das Vordergrundfenster. Relevant vor dem Paste: In ein
/// erhöht laufendes Fenster (Admin) kann unser nicht-erhöhter Prozess wegen
/// UIPI kein SendInput liefern — der Tastendruck verpufft lautlos.
/// </summary>
public static class ForegroundInfo
{
    /// <summary>
    /// True, wenn der Prozess des Vordergrundfensters erhöht (elevated) läuft.
    /// Jeder Fehlerpfad liefert false — „nicht erhöht" ist die harmlose
    /// Annahme, der Paste-Versuch schlägt dann schlimmstenfalls leise fehl.
    /// </summary>
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
