using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32;
using Spitr.Core.Diagnostics;
using Spitr.Core.Recording;
using Spitr.Core.Settings;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Spitr.App.Win32;

/// <summary>
/// Windows-Umsetzung des globalen Hold-to-Talk-Triggers (Port des macOS
/// HotkeyService, dort NSEvent-Monitore). Ein WH_KEYBOARD_LL-Hook auf einem
/// eigenen Thread meldet Drücken/Loslassen der Hold-Taste und schluckt sie
/// dabei komplett (CapsLock togglet nicht, Apps sehen kein gehaltenes Strg).
/// Der Re-Insert-Chord läuft über RegisterHotKey auf demselben Thread.
///
/// Threading-Vertrag: <see cref="Pressed"/>/<see cref="Released"/>/
/// <see cref="Cancelled"/>/<see cref="ReinsertRequested"/> feuern auf einem
/// ThreadPool-Task (nie auf dem Hook-Thread) und können relativ zueinander
/// nur sequenziell, aber auf wechselnden Threads eintreffen — Abonnenten
/// (RecordingController serialisiert intern per Lock) müssen thread-sicher sein.
/// </summary>
public sealed class KeyboardHookService : IHotkeyService, IDisposable
{
    public event Action<bool>? Pressed;
    public event Action? Released;
    public event Action? Cancelled;
    public event Action? ReinsertRequested;

    /// <summary>Id unserer RegisterHotKey-Registrierung (WM_HOTKEY-wParam).</summary>
    private const int ReinsertHotkeyId = 0xB00F;

    // WM_APP (0x8000) ist eine reine Konstante und steht bewusst nicht in
    // NativeMethods.txt — Nachrichten in [WM_APP..0xBFFF] sind für private
    // Thread-/Fensternachrichten reserviert.
    private const uint WM_APP = 0x8000;

    /// <summary>An den Hook-Thread: Re-Insert-Chord neu registrieren (RegisterHotKey
    /// wirkt nur auf dem Thread, dem die Message-Queue gehört).</summary>
    private const uint WmApplyReinsert = WM_APP + 1;

    /// <summary>An den Hook-Thread: Hook aus- und wieder einhängen (nach Resume/Unlock).</summary>
    private const uint WmRehook = WM_APP + 2;

    /// <summary>Poll-Intervall des Stuck-Key-Watchdogs.</summary>
    private const int WatchdogIntervalMs = 250;

    /// <summary>Harte Obergrenze für einen einzelnen Halt (5 Minuten).</summary>
    private const long MaxHoldDurationMs = 5 * 60_000;

    private enum HookEventKind
    {
        Press,
        Release,
        Cancel,
        Reinsert,
    }

    /// <summary>Winziges Nachrichtenobjekt vom Hook-Callback an den Dispatcher.</summary>
    private readonly record struct HookEvent(HookEventKind Kind, bool CommandMode);

    private readonly DiagLog _log = new("hotkey");
    private readonly object _lifecycle = new();

    // GC-Rooting: Der HOOKPROC-Delegate MUSS die Lebensdauer des Hooks überleben.
    // Ein nur an SetWindowsHookEx übergebener Delegate wird sonst irgendwann
    // eingesammelt und der nächste Tastendruck ruft ins Leere — der klassische
    // LL-Hook-Absturz. Deshalb liegt er hier in einem readonly-Feld.
    private readonly HOOKPROC _hookProc;

    /// <summary>
    /// Kanal Hook-Callback → Dispatcher. Der Callback schreibt nur (TryWrite,
    /// Mikrosekunden); AllowSynchronousContinuations bleibt aus, damit niemals
    /// Abonnenten-Code synchron auf dem Hook-Thread läuft. Windows entfernt
    /// LL-Hooks stillschweigend, deren Callback zu langsam antwortet.
    /// </summary>
    private readonly Channel<HookEvent> _events = Channel.CreateUnbounded<HookEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>Stuck-Key-Watchdog; läuft nur, während die Taste als gehalten gilt.</summary>
    private readonly Timer _watchdog;

    /// <summary>Signalisiert, dass der Hook-Thread installiert hat und seine Queue steht.</summary>
    private readonly ManualResetEventSlim _threadReady = new(false);

    private Thread? _hookThread;
    private Task? _dispatchTask;
    private bool _disposed;

    /// <summary>Win32-Thread-Id des Hook-Threads; 0 solange er nicht bereit ist.
    /// Wird erst NACH dem ersten user32-Aufruf publiziert, damit PostThreadMessage
    /// garantiert eine existierende Message-Queue vorfindet.</summary>
    private volatile uint _hookThreadId;

    /// <summary>VK der aktuellen Hold-Taste — volatile reicht: der Callback liest,
    /// Settings schreiben; ein Marshalling auf den Hook-Thread ist unnötig.</summary>
    private volatile ushort _holdVk;

    /// <summary>Esc-Überwachung aktiv? Nur während einer laufenden Aufnahme true.</summary>
    private volatile bool _cancelWatch;

    /// <summary>1 = Hold-Taste gilt als gehalten. Interlocked-Übergänge, damit
    /// Hook-Callback und Watchdog ein Release exakt einmal melden.</summary>
    private int _heldState;

    /// <summary>TickCount64 beim Beginn des aktuellen Halts (für die 5-Minuten-Kappe).</summary>
    private long _heldSinceMs;

    /// <summary>Gewünschter Re-Insert-Chord; der Hook-Thread registriert ihn bei
    /// Start bzw. nach WmApplyReinsert. Volatile-Referenz statt Lock.</summary>
    private volatile KeyCombo? _pendingReinsert = KeyCombo.ReinsertDefault;

    /// <summary>Nur vom Hook-Thread berührt: aktuelles Hook-Handle + Registrierungsstatus.</summary>
    private SafeHandle? _hook;
    private bool _reinsertRegistered;

    public KeyboardHookService()
    {
        _hookProc = HookCallback;
        _holdVk = HoldKey.RightCtrl.VirtualKey();
        _watchdog = new Timer(WatchdogTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Startet Hook-Thread + Event-Dispatcher. Idempotent; blockiert kurz, bis
    /// der Hook installiert ist, damit UpdateReinsert() danach sicher posten kann.
    /// </summary>
    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_hookThread is not null) return;

            // Eigener Thread statt UI-Thread: der Callback darf nie hinter WPF-
            // Arbeit hängen (zu langsame LL-Hooks fliegen raus, s.o.). STA ist
            // für Hook + RegisterHotKey nicht nötig.
            _hookThread = new Thread(HookThreadMain)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "Spitr Keyboard Hook",
            };
            _hookThread.Start();
            _threadReady.Wait(TimeSpan.FromSeconds(5));

            _dispatchTask = Task.Run(DispatchLoopAsync);

            // Nach Standby/Sperren verliert der LL-Hook gern still seine Wirkung —
            // bei Resume/Unlock einmal neu einhängen. SystemEvents kann in
            // exotischen Sessions fehlschlagen; der Hook selbst läuft trotzdem.
            try
            {
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
                SystemEvents.SessionSwitch += OnSessionSwitch;
            }
            catch (Exception ex)
            {
                _log.Warning($"SystemEvents subscription failed: {ex.GetType().Name}");
            }
        }
    }

    public void UpdateHoldKey(HoldKey key)
    {
        _holdVk = key.VirtualKey();

        // Wechsel mitten im Halt: Das KeyUp der ALTEN Taste matcht nicht mehr —
        // die Session sofort deterministisch schließen statt auf den Watchdog
        // zu warten (Pendant zum isHeld-Reset im macOS-Original).
        if (Interlocked.CompareExchange(ref _heldState, 0, 1) == 1)
        {
            _log.Notice("hold key changed while held, synthesizing release");
            _events.Writer.TryWrite(new HookEvent(HookEventKind.Release, false));
        }
    }

    public void UpdateReinsert(KeyCombo combo)
    {
        ArgumentNullException.ThrowIfNull(combo);
        _pendingReinsert = combo;
        // RegisterHotKey muss auf dem Thread mit der Message-Loop laufen —
        // deshalb nur posten; läuft der Thread (noch) nicht, wendet er den
        // gemerkten Chord ohnehin beim Start an.
        PostToHookThread(WmApplyReinsert);
    }

    public void BeginCancelWatch() => _cancelWatch = true;

    public void EndCancelWatch() => _cancelWatch = false;

    /// <summary>
    /// Beendet Hook-Thread und Dispatcher. Idempotent; der Thread räumt Hook und
    /// Hotkey-Registrierung selbst ab (beides gehört ihm).
    /// </summary>
    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
            }
            catch (Exception ex)
            {
                _log.Warning($"SystemEvents unsubscribe failed: {ex.GetType().Name}");
            }

            // WM_QUIT beendet die GetMessage-Loop; das Aufräumen (Unhook +
            // UnregisterHotKey) passiert im Thread selbst hinter der Loop.
            var threadId = _hookThreadId;
            if (threadId != 0)
            {
                PInvoke.PostThreadMessage(threadId, PInvoke.WM_QUIT, default, default);
            }

            if (_hookThread is { IsAlive: true } thread && !thread.Join(TimeSpan.FromSeconds(3)))
            {
                _log.Warning("hook thread did not exit within timeout");
            }

            // Erst den Kanal schließen (Dispatcher läuft leer), DANN den Timer
            // entsorgen — der Dispatcher fasst den Timer noch an.
            _events.Writer.TryComplete();
            try
            {
                _dispatchTask?.Wait(TimeSpan.FromSeconds(3));
            }
            catch (AggregateException)
            {
                // Dispatcher-Fehler sind bereits geloggt; Dispose bleibt still.
            }

            _watchdog.Dispose();
            _threadReady.Dispose();
        }
    }

    // ---------------------------------------------------------------- Hook-Thread

    private void HookThreadMain()
    {
        var threadId = PInvoke.GetCurrentThreadId();
        try
        {
            InstallHook();
            ApplyReinsertOnHookThread();
        }
        finally
        {
            // Id erst publizieren, nachdem user32 angefasst wurde: ab da existiert
            // die Message-Queue und PostThreadMessage von außen kommt sicher an.
            _hookThreadId = threadId;
            _threadReady.Set();
        }

        while (true)
        {
            var result = PInvoke.GetMessage(out MSG msg, HWND.Null, 0, 0);
            if (result.Value <= 0) break; // 0 = WM_QUIT, -1 = Fehler

            if (msg.hwnd.IsNull)
            {
                switch (msg.message)
                {
                    case PInvoke.WM_HOTKEY:
                        if ((int)msg.wParam.Value == ReinsertHotkeyId)
                        {
                            _events.Writer.TryWrite(new HookEvent(HookEventKind.Reinsert, false));
                        }
                        continue;
                    case WmApplyReinsert:
                        ApplyReinsertOnHookThread();
                        continue;
                    case WmRehook:
                        Rehook();
                        continue;
                }
            }

            PInvoke.TranslateMessage(in msg);
            PInvoke.DispatchMessage(in msg);
        }

        if (_reinsertRegistered)
        {
            PInvoke.UnregisterHotKey(HWND.Null, ReinsertHotkeyId);
            _reinsertRegistered = false;
        }
        RemoveHook();
    }

    private void InstallHook()
    {
        try
        {
            _hook = PInvoke.SetWindowsHookEx(
                WINDOWS_HOOK_ID.WH_KEYBOARD_LL,
                _hookProc,
                PInvoke.GetModuleHandle((string?)null),
                0);
            if (_hook.IsInvalid)
            {
                _log.Error($"SetWindowsHookEx failed (error {Marshal.GetLastPInvokeError()})");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"SetWindowsHookEx threw {ex.GetType().Name}");
        }
    }

    private void RemoveHook()
    {
        _hook?.Dispose(); // SafeHandle-Release ruft UnhookWindowsHookEx
        _hook = null;
    }

    /// <summary>Nach Resume/Unlock: Hook aus- und wieder einhängen.</summary>
    private void Rehook()
    {
        try
        {
            RemoveHook();
            InstallHook();
            _log.Notice("keyboard hook reinstalled");
        }
        catch (Exception ex)
        {
            _log.Error($"rehook failed: {ex.GetType().Name}");
        }
    }

    /// <summary>Registriert den aktuell gewünschten Re-Insert-Chord (nur Hook-Thread).</summary>
    private void ApplyReinsertOnHookThread()
    {
        try
        {
            if (_reinsertRegistered)
            {
                PInvoke.UnregisterHotKey(HWND.Null, ReinsertHotkeyId);
                _reinsertRegistered = false;
            }

            var combo = _pendingReinsert;
            if (combo is null) return;

            var modifiers = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
            if ((combo.Modifiers & KeyModifiers.Control) != 0) modifiers |= HOT_KEY_MODIFIERS.MOD_CONTROL;
            if ((combo.Modifiers & KeyModifiers.Alt) != 0) modifiers |= HOT_KEY_MODIFIERS.MOD_ALT;
            if ((combo.Modifiers & KeyModifiers.Shift) != 0) modifiers |= HOT_KEY_MODIFIERS.MOD_SHIFT;
            if ((combo.Modifiers & KeyModifiers.Win) != 0) modifiers |= HOT_KEY_MODIFIERS.MOD_WIN;

            if (PInvoke.RegisterHotKey(HWND.Null, ReinsertHotkeyId, modifiers, combo.VirtualKey))
            {
                _reinsertRegistered = true;
            }
            else
            {
                // Typisch: Chord ist bereits systemweit vergeben. Kein harter Fehler —
                // Diktat funktioniert weiter, nur Re-Insert nicht.
                _log.Warning($"RegisterHotKey failed (error {Marshal.GetLastPInvokeError()})");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"ApplyReinsert threw {ex.GetType().Name}");
        }
    }

    // ------------------------------------------------------------- Hook-Callback

    /// <summary>
    /// Der LL-Hook-Callback. Läuft synchron im Tastatur-Eingabepfad des Systems:
    /// hier passiert NUR Zustandslogik + TryWrite in den Kanal (Mikrosekunden),
    /// niemals Abonnenten-Code, I/O oder Locks mit unbekannter Haltedauer.
    /// </summary>
    private LRESULT HookCallback(int code, WPARAM wParam, LPARAM lParam)
    {
        // Nur HC_ACTION (0) trägt gültige Tastendaten; alles andere durchreichen.
        if (code < 0)
        {
            return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
        }

        uint vkCode;
        KBDLLHOOKSTRUCT_FLAGS flags;
        unsafe
        {
            var info = (KBDLLHOOKSTRUCT*)lParam.Value;
            vkCode = info->vkCode;
            flags = info->flags;
        }

        // Injizierte Events (u. a. unser eigenes SendInput-Strg+V) nie anfassen,
        // sonst füttert sich der Hook selbst.
        //
        // AltGr-Feinheit: Deutsche Layouts synthetisieren beim AltGr-Druck ein
        // zusätzliches LCtrl-Down (vkCode 0xA2, scanCode 0x21D). Unsere Hold-
        // Tasten sind RCtrl/CapsLock/RShift/Pause — LCtrl matcht also nie und
        // das synthetische Event läuft unten unverändert durch CallNextHookEx,
        // AltGr-Zeichen (@ € [ ] { }) funktionieren weiter.
        if ((flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) != 0)
        {
            return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
        }

        var message = (uint)wParam.Value;
        var isKeyDown = message is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
        var isKeyUp = message is PInvoke.WM_KEYUP or PInvoke.WM_SYSKEYUP;

        if (vkCode == _holdVk)
        {
            if (isKeyDown && Interlocked.CompareExchange(ref _heldState, 1, 0) == 0)
            {
                Volatile.Write(ref _heldSinceMs, Environment.TickCount64);
                // Umschalt im Moment des Drucks = Command-Modus (wie am Mac).
                var commandMode = PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_SHIFT) < 0;
                _events.Writer.TryWrite(new HookEvent(HookEventKind.Press, commandMode));
            }
            else if (isKeyUp && Interlocked.CompareExchange(ref _heldState, 0, 1) == 1)
            {
                _events.Writer.TryWrite(new HookEvent(HookEventKind.Release, false));
            }

            // Die Hold-Taste wird IMMER geschluckt — auch Auto-Repeat-Downs
            // während des Haltens (kein neues Event) und verspätete Ups:
            // CapsLock darf nie togglen, Apps dürfen kein gehaltenes Strg sehen.
            return new LRESULT(1);
        }

        // Esc bricht die laufende Aufnahme ab — nur solange der CancelWatch an
        // ist, sonst bleibt Esc unangetastet.
        if (_cancelWatch && isKeyDown && vkCode == (uint)VIRTUAL_KEY.VK_ESCAPE)
        {
            _events.Writer.TryWrite(new HookEvent(HookEventKind.Cancel, false));
            return new LRESULT(1);
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    // ---------------------------------------------------------------- Dispatcher

    /// <summary>
    /// Einzelner Leser des Kanals: hebt die Hook-Events auf einen ThreadPool-
    /// Task und feuert dort die öffentlichen Events. Abonnenten-Ausnahmen werden
    /// geloggt statt den Dispatcher zu töten.
    /// </summary>
    private async Task DispatchLoopAsync()
    {
        await foreach (var evt in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                switch (evt.Kind)
                {
                    case HookEventKind.Press:
                        _watchdog.Change(WatchdogIntervalMs, WatchdogIntervalMs);
                        Pressed?.Invoke(evt.CommandMode);
                        break;
                    case HookEventKind.Release:
                        _watchdog.Change(Timeout.Infinite, Timeout.Infinite);
                        Released?.Invoke();
                        break;
                    case HookEventKind.Cancel:
                        Cancelled?.Invoke();
                        break;
                    case HookEventKind.Reinsert:
                        ReinsertRequested?.Invoke();
                        break;
                }
            }
            catch (Exception ex)
            {
                // Nur Typ loggen — DiagLog-Regel: nie Inhalte in die Log-Datei.
                _log.Error($"subscriber threw {ex.GetType().Name}");
            }
        }
    }

    // ------------------------------------------------------------------ Watchdog

    /// <summary>
    /// LL-Hooks sind best-effort: unter Last kann das KeyUp verloren gehen und
    /// die Aufnahme liefe endlos. Solange „gehalten" gilt, wird alle 250 ms der
    /// echte Tastenzustand gegengeprüft und ein verlorenes Release synthetisiert;
    /// zusätzlich gilt eine harte 5-Minuten-Kappe pro Halt.
    /// </summary>
    private void WatchdogTick(object? state)
    {
        try
        {
            if (Volatile.Read(ref _heldState) != 1) return;

            var physicallyDown = PInvoke.GetAsyncKeyState(_holdVk) < 0;
            var overCap = Environment.TickCount64 - Volatile.Read(ref _heldSinceMs) > MaxHoldDurationMs;
            if (physicallyDown && !overCap) return;

            if (Interlocked.CompareExchange(ref _heldState, 0, 1) == 1)
            {
                _log.Notice(overCap
                    ? "watchdog: hold exceeded hard cap, forcing release"
                    : "watchdog: lost release recovered, synthesizing release");
                _events.Writer.TryWrite(new HookEvent(HookEventKind.Release, false));
            }
        }
        catch (Exception ex)
        {
            _log.Error($"watchdog tick threw {ex.GetType().Name}");
        }
    }

    // -------------------------------------------------------------- SystemEvents

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        try
        {
            _log.Notice("resume detected, reinstalling hook");
            PostToHookThread(WmRehook);
        }
        catch (Exception ex)
        {
            _log.Error($"resume rehook failed: {ex.GetType().Name}");
        }
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionUnlock) return;
        try
        {
            _log.Notice("session unlock detected, reinstalling hook");
            PostToHookThread(WmRehook);
        }
        catch (Exception ex)
        {
            _log.Error($"unlock rehook failed: {ex.GetType().Name}");
        }
    }

    private void PostToHookThread(uint message)
    {
        var threadId = _hookThreadId;
        if (threadId == 0) return; // Thread läuft (noch) nicht — Zustand greift beim Start
        if (!PInvoke.PostThreadMessage(threadId, message, default, default))
        {
            _log.Warning($"PostThreadMessage 0x{message:X} failed");
        }
    }
}
