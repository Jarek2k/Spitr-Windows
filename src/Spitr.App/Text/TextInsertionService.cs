using Spitr.App.Win32;
using Spitr.Core.Diagnostics;

namespace Spitr.App.Text;

/// <summary>
/// Fügt transkribierten Text ins fokussierte Fenster ein — der Orchestrator
/// des Paste-Zyklus, portiert vom macOS-Original (insert()-Flow): kompletten
/// Clipboard-Inhalt sichern → eigenen Text (concealed) setzen → Strg+V
/// synthetisieren → nach kurzer Frist den alten Inhalt zurückspielen.
///
/// Schlägt der Snapshot fehl, wird bewusst NIE wiederhergestellt: das
/// User-Clipboard zu verlieren wiegt schwerer, als unseren Text liegen zu lassen.
/// </summary>
public sealed class TextInsertionService : Spitr.Core.Recording.ITextInsertionService
{
    private static readonly DiagLog Log = new("insertion");

    /// <summary>Frist zwischen SetText und Strg+V, damit das Clipboard sicher steht.</summary>
    private static readonly TimeSpan PasteDelay = TimeSpan.FromMilliseconds(50);

    /// <summary>Frist, in der die Ziel-App den Paste lesen darf, bevor wir zurückspielen.</summary>
    private static readonly TimeSpan RestoreDelay = TimeSpan.FromMilliseconds(300);

    private readonly ClipboardService _clipboard = new();
    private readonly Lock _gate = new();

    /// <summary>
    /// Der Snapshot, dem wir noch eine Wiederherstellung schulden. Als Feld
    /// gehalten, damit ein Beenden der App innerhalb des Restore-Fensters den
    /// Diktattext nicht dauerhaft auf der Zwischenablage zurücklässt
    /// (Pendant zum willTerminate-Restore des macOS-Originals).
    /// </summary>
    private ClipboardService.Snapshot? _pendingRestore;

    /// <inheritdoc/>
    public bool SmartSpacing { get; set; } = true;

    /// <summary>
    /// Meldung fürs Tray-Balloon, wenn das Zielfenster erhöht läuft und der
    /// Text deshalb nur in der Zwischenablage liegt (manuell einfügen).
    /// </summary>
    public event Action<string>? InsertBlockedByElevation;

    public TextInsertionService()
    {
        // Letzte Chance beim Prozessende — zusätzlich ruft die App in OnExit
        // explizit FlushPendingRestore() (ProcessExit feuert nicht in jedem
        // Beendigungspfad zuverlässig vor dem Ende der 2-Sekunden-Frist).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushPendingRestore();
    }

    /// <inheritdoc/>
    public void Insert(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Caret-Kontext (Zeichen vor dem Cursor) kommt erst mit UIA in Phase 6;
        // bis dahin precedingCharacter=null → SmartSpacing fasst nur
        // Leerzeichen-Läufe zusammen und stellt NIE ein führendes Leerzeichen voran.
        var prepared = Spitr.Core.Text.SmartSpacing.Prepare(text, precedingCharacter: null, SmartSpacing);
        if (prepared.Length == 0) return;

        if (ForegroundInfo.IsForegroundElevated())
        {
            // UIPI verwirft SendInput an erhöhte Fenster lautlos — gar nicht
            // erst versuchen. Text ohne Restore aufs Clipboard legen (der
            // Nutzer fügt manuell ein); concealed, damit das Diktat trotzdem
            // nicht im Win+V-Verlauf oder der Cloud landet.
            var copied = _clipboard.TrySetText(prepared, concealFromHistory: true);
            Log.Notice($"foreground elevated, paste blocked (text: {prepared.Length} chars, clipboard={copied})");
            InsertBlockedByElevation?.Invoke(copied
                ? "Das aktive Fenster läuft als Administrator — Text liegt in der Zwischenablage, bitte mit Strg+V einfügen."
                : "Das aktive Fenster läuft als Administrator — Einfügen nicht möglich.");
            return;
        }

        // Steht vom letzten Insert noch ein Restore aus (zwei Diktate < 300 ms
        // hintereinander), erst abschließen — sonst würde der neue Snapshot
        // unseren eigenen Diktattext einfangen.
        FlushPendingRestore();

        var snapshot = _clipboard.TrySnapshot();
        lock (_gate)
        {
            _pendingRestore = snapshot;
        }
        if (snapshot is null)
        {
            Log.Warning("clipboard snapshot failed, will not restore after paste");
        }

        if (!_clipboard.TrySetText(prepared, concealFromHistory: true))
        {
            // Clipboard könnte jetzt leer sein — sofort zurückspielen statt Strg+V,
            // sonst würde der alte (oder gar kein) Inhalt eingefügt.
            Log.Error($"clipboard set failed, insert aborted (text: {prepared.Length} chars)");
            FlushPendingRestore();
            return;
        }

        // Kurz warten, bis das Clipboard systemweit steht, dann einfügen.
        Thread.Sleep(PasteDelay);
        InputSender.SendCtrlV();
        Log.Info($"inserted {prepared.Length} chars via paste");

        // Restore verzögert und fire-and-forget: die Ziel-App braucht einen
        // Moment, um den Paste zu lesen. Ohne Snapshot (null) passiert nichts.
        _ = Task.Run(async () =>
        {
            await Task.Delay(RestoreDelay).ConfigureAwait(false);
            FlushPendingRestore();
        });
    }

    /// <summary>
    /// Spielt einen noch ausstehenden Clipboard-Snapshot sofort zurück.
    /// Von jedem Thread aufrufbar und idempotent — die App ruft das beim
    /// Beenden (OnExit), damit kein Diktattext auf der Zwischenablage bleibt.
    /// </summary>
    public void FlushPendingRestore()
    {
        ClipboardService.Snapshot? snapshot;
        lock (_gate)
        {
            snapshot = _pendingRestore;
            _pendingRestore = null;
        }
        if (snapshot is null) return;

        if (!_clipboard.TryRestore(snapshot))
        {
            Log.Warning($"clipboard restore failed ({snapshot.Items.Count} formats)");
        }
    }
}
