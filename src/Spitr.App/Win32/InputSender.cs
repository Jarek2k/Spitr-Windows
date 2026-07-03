using Spitr.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Spitr.App.Win32;

/// <summary>
/// Synthetisiert den Einfüge-Tastendruck (Strg+V) per SendInput — das Pendant
/// zum CGEvent-Paste des macOS-Originals. Injizierte Events tragen
/// LLKHF_INJECTED, unser eigener Low-Level-Hook ignoriert sie deshalb und
/// löst keine Endlosschleife aus.
/// </summary>
public static class InputSender
{
    private static readonly DiagLog Log = new("input");

    /// <summary>
    /// Modifier, die vor dem Paste neutralisiert werden: Der Nutzer hält beim
    /// Diktat-Ende oft noch die Hotkey-Taste — ohne KEYUP-Injektion sähe die
    /// Ziel-App z.B. Strg+Alt+Shift+V statt unserem sauberen Strg+V.
    /// </summary>
    private static readonly VIRTUAL_KEY[] ModifiersToNeutralize =
    [
        VIRTUAL_KEY.VK_LSHIFT, VIRTUAL_KEY.VK_RSHIFT,
        VIRTUAL_KEY.VK_LMENU, VIRTUAL_KEY.VK_RMENU,
        VIRTUAL_KEY.VK_LWIN, VIRTUAL_KEY.VK_RWIN,
        VIRTUAL_KEY.VK_LCONTROL, VIRTUAL_KEY.VK_RCONTROL,
    ];

    /// <summary>
    /// Injiziert Strg+V: erst KEYUP für alle physisch gehaltenen Modifier,
    /// dann LCONTROL↓ V↓ V↑ LCONTROL↑ — alles in EINEM SendInput-Aufruf,
    /// damit sich keine echten Tastatur-Events dazwischenschieben (Atomizität).
    /// </summary>
    public static void SendCtrlV()
    {
        var inputs = new List<INPUT>(ModifiersToNeutralize.Length + 4);

        foreach (var key in ModifiersToNeutralize)
        {
            // Höchstwertiges Bit = Taste ist gerade unten.
            if ((PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0)
            {
                inputs.Add(KeyEvent(key, keyUp: true));
            }
        }

        inputs.Add(KeyEvent(VIRTUAL_KEY.VK_LCONTROL, keyUp: false));
        inputs.Add(KeyEvent(VIRTUAL_KEY.VK_V, keyUp: false));
        inputs.Add(KeyEvent(VIRTUAL_KEY.VK_V, keyUp: true));
        inputs.Add(KeyEvent(VIRTUAL_KEY.VK_LCONTROL, keyUp: true));

        int cbSize;
        unsafe
        {
            cbSize = sizeof(INPUT);
        }
        var sent = PInvoke.SendInput(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(inputs), cbSize);
        if (sent != inputs.Count)
        {
            // Passiert z.B. wenn UIPI die Injektion blockt (erhöhtes Zielfenster) —
            // der Aufrufer prüft das vorher, hier bleibt nur das Protokoll.
            Log.Warning($"SendInput injected {sent}/{inputs.Count} events");
        }
    }

    private static INPUT KeyEvent(VIRTUAL_KEY key, bool keyUp)
    {
        var input = new INPUT { type = INPUT_TYPE.INPUT_KEYBOARD };
        input.Anonymous.ki = new KEYBDINPUT
        {
            wVk = key,
            dwFlags = keyUp ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0,
        };
        return input;
    }
}
