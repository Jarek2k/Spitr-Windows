using Spitr.Core.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;

namespace Spitr.App.Win32;

/// <summary>
/// Roher Win32-Zwischenablage-Zugriff für den Paste-Zyklus: kompletten Inhalt
/// sichern, eigenen Text setzen (mit Conceal-Markern gegen Win+V-Verlauf und
/// Cloud-Sync), später wiederherstellen. Bewusst ohne die WPF-Clipboard-Klasse:
/// alle Methoden sind von jedem Thread aufrufbar (kein STA-Zwang) und geben bei
/// Fehlern kontrolliert false/null zurück statt zu werfen.
/// </summary>
public sealed class ClipboardService
{
    private static readonly DiagLog Log = new("clipboard");

    // Standard-Clipboard-Formate (winuser.h) — CsWin32 generiert die CF_*-
    // Konstanten nicht einzeln, daher hier lokal definiert.
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_BITMAP = 2;
    private const uint CF_METAFILEPICT = 3;
    private const uint CF_ENHMETAFILE = 14;
    private const uint CF_OWNERDISPLAY = 0x0080;
    private const uint CF_DSPBITMAP = 0x0082;
    private const uint CF_DSPMETAFILEPICT = 0x0083;
    private const uint CF_DSPENHMETAFILE = 0x008E;
    private const uint CF_PRIVATEFIRST = 0x0200;
    private const uint CF_PRIVATELAST = 0x02FF;
    private const uint CF_GDIOBJFIRST = 0x0300;
    private const uint CF_GDIOBJLAST = 0x03FF;

    /// <summary>Marker: Clipboard-Monitore (Verlauf, Manager) sollen den Eintrag ignorieren.</summary>
    private const string ExcludeFromMonitoringFormatName = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>Marker (DWORD 0): Eintrag darf nicht in den Win+V-Verlauf.</summary>
    private const string CanIncludeInHistoryFormatName = "CanIncludeInClipboardHistory";

    /// <summary>Marker (DWORD 0): Eintrag darf nicht in die Cloud-Zwischenablage synchronisiert werden.</summary>
    private const string CanUploadToCloudFormatName = "CanUploadToCloudClipboard";

    /// <summary>
    /// Backoff-Pausen vor den Wiederholungsversuchen von OpenClipboard — die
    /// Zwischenablage ist ein globaler Mutex, den andere Apps kurz halten können.
    /// </summary>
    private static readonly int[] OpenRetryDelaysMs = [20, 40, 60, 80, 100];

    /// <summary>
    /// Momentaufnahme aller HGLOBAL-basierten Formate der Zwischenablage.
    /// Bild-Inhalte überleben über CF_DIB/CF_DIBV5/PNG, die HGLOBAL sind.
    /// </summary>
    public sealed record Snapshot(IReadOnlyList<(uint Format, byte[] Bytes)> Items);

    /// <summary>
    /// Sichert den kompletten Zwischenablage-Inhalt (alle HGLOBAL-Formate).
    /// Gibt bei jedem Fehler unterwegs null zurück — der Aufrufer stellt dann
    /// bewusst NIE wieder her, statt einen halben Zustand zurückzuschreiben.
    /// </summary>
    public unsafe Snapshot? TrySnapshot()
    {
        if (!TryOpenClipboard()) return null;
        try
        {
            var items = new List<(uint Format, byte[] Bytes)>();
            uint format = 0;
            while ((format = PInvoke.EnumClipboardFormats(format)) != 0)
            {
                if (!IsHGlobalFormat(format)) continue;

                var handle = PInvoke.GetClipboardData(format);
                // Fehlgeschlagen (z.B. Delayed Rendering einer beendeten App) →
                // Snapshot ist unvollständig und damit wertlos.
                if (handle.IsNull) return null;

                var hGlobal = (HGLOBAL)(void*)handle;
                var size = PInvoke.GlobalSize(hGlobal);
                if (size == 0)
                {
                    // Leere Objekte sind gültig (z.B. reine Marker-Formate).
                    items.Add((format, []));
                    continue;
                }
                if (size > int.MaxValue) return null;

                var ptr = PInvoke.GlobalLock(hGlobal);
                if (ptr is null) return null;
                try
                {
                    items.Add((format, new ReadOnlySpan<byte>(ptr, (int)size).ToArray()));
                }
                finally
                {
                    PInvoke.GlobalUnlock(hGlobal);
                }
            }

            Log.Debug($"snapshot: {items.Count} formats, {items.Sum(i => i.Bytes.LongLength)} bytes");
            return new Snapshot(items);
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>
    /// Setzt <paramref name="text"/> als CF_UNICODETEXT. Mit
    /// <paramref name="concealFromHistory"/> kommen die drei Microsoft-
    /// Marker-Formate dazu, die den Eintrag aus dem Win+V-Verlauf und der
    /// Cloud-Synchronisierung heraushalten — Diktate können Geheimnisse sein
    /// und parken hier nur für die Dauer eines Pastes. Schlägt auch das Setzen
    /// eines Markers fehl, gilt der ganze Aufruf als gescheitert (false),
    /// damit nie unmarkierter Diktattext liegen bleibt.
    /// </summary>
    public bool TrySetText(string text, bool concealFromHistory)
    {
        if (!TryOpenClipboard()) return false;
        try
        {
            if (!PInvoke.EmptyClipboard())
            {
                Log.Error("EmptyClipboard failed");
                return false;
            }

            // UTF-16 inklusive Null-Terminator, wie CF_UNICODETEXT es verlangt.
            var byteCount = (text.Length + 1) * sizeof(char);
            var bytes = new byte[byteCount];
            System.Text.Encoding.Unicode.GetBytes(text, bytes);
            if (!TrySetHGlobalData(CF_UNICODETEXT, bytes)) return false;

            if (concealFromHistory && !TrySetConcealMarkers()) return false;

            Log.Debug($"set text: {text.Length} chars, conceal={concealFromHistory}");
            return true;
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>
    /// Spielt einen Snapshot zurück (EmptyClipboard, dann Format für Format).
    /// </summary>
    public bool TryRestore(Snapshot snapshot)
    {
        if (!TryOpenClipboard()) return false;
        try
        {
            if (!PInvoke.EmptyClipboard())
            {
                Log.Error("EmptyClipboard failed during restore");
                return false;
            }

            foreach (var (format, bytes) in snapshot.Items)
            {
                if (!TrySetHGlobalData(format, bytes)) return false;
            }

            Log.Debug($"restored {snapshot.Items.Count} formats");
            return true;
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>
    /// Öffnet die Zwischenablage mit Wiederholungen: ein Sofortversuch, danach
    /// bis zu fünf weitere mit wachsendem Backoff (20–100 ms) — ein anderer
    /// Prozess kann sie gerade offen halten.
    /// </summary>
    private static bool TryOpenClipboard()
    {
        // hWndNewOwner = null ist erlaubt; EmptyClipboard setzt den Besitzer dann auf null.
        if (PInvoke.OpenClipboard(default)) return true;
        foreach (var delay in OpenRetryDelaysMs)
        {
            Thread.Sleep(delay);
            if (PInvoke.OpenClipboard(default)) return true;
        }
        Log.Warning("OpenClipboard failed after retries");
        return false;
    }

    /// <summary>
    /// Nur Formate, deren Daten-Handle laut Win32-Vertrag ein HGLOBAL ist,
    /// lassen sich per GlobalLock kopieren. GDI-Handles (Bitmaps, Metafiles),
    /// Owner-Display sowie die Private-/GDI-Objekt-Bereiche werden übersprungen.
    /// </summary>
    private static bool IsHGlobalFormat(uint format) =>
        format is not (CF_BITMAP or CF_METAFILEPICT or CF_ENHMETAFILE or CF_OWNERDISPLAY
            or CF_DSPBITMAP or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE)
        && format is not (>= CF_PRIVATEFIRST and <= CF_PRIVATELAST)   // Besitzer-verwaltete Handles
        && format is not (>= CF_GDIOBJFIRST and <= CF_GDIOBJLAST);    // GDI-Objekt-Handles

    /// <summary>
    /// Setzt die drei Conceal-Marker: bei „Exclude…" zählt die bloße Präsenz
    /// des Formats, die beiden „Can…"-Formate erwarten explizit DWORD 0.
    /// </summary>
    private static bool TrySetConcealMarkers()
    {
        ReadOnlySpan<byte> dwordZero = [0, 0, 0, 0];
        foreach (var name in (ReadOnlySpan<string>)[
                     ExcludeFromMonitoringFormatName,
                     CanIncludeInHistoryFormatName,
                     CanUploadToCloudFormatName])
        {
            var format = PInvoke.RegisterClipboardFormat(name);
            if (format == 0 || !TrySetHGlobalData(format, dwordZero))
            {
                Log.Error("conceal marker could not be set");
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Kopiert <paramref name="bytes"/> in ein frisches GMEM_MOVEABLE-HGLOBAL
    /// und übergibt es der Zwischenablage. Nach erfolgreichem SetClipboardData
    /// gehört das Handle dem SYSTEM — dann darf es NICHT freigegeben werden;
    /// nur im Fehlerfall geben wir es selbst wieder frei.
    /// </summary>
    private static unsafe bool TrySetHGlobalData(uint format, ReadOnlySpan<byte> bytes)
    {
        var hGlobal = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)bytes.Length);
        if (hGlobal.IsNull)
        {
            Log.Error($"GlobalAlloc failed ({bytes.Length} bytes)");
            return false;
        }

        if (bytes.Length > 0)
        {
            // Null-große Objekte sind nicht lockbar — die dürfen direkt aufs Clipboard.
            var ptr = PInvoke.GlobalLock(hGlobal);
            if (ptr is null)
            {
                PInvoke.GlobalFree(hGlobal);
                Log.Error("GlobalLock failed");
                return false;
            }
            bytes.CopyTo(new Span<byte>(ptr, bytes.Length));
            PInvoke.GlobalUnlock(hGlobal);
        }

        if (PInvoke.SetClipboardData(format, new HANDLE(hGlobal.Value)).IsNull)
        {
            PInvoke.GlobalFree(hGlobal);
            Log.Error("SetClipboardData failed");
            return false;
        }
        return true;
    }
}
