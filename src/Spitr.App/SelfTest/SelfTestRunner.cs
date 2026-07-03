using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Spitr.Core.Audio;
using Spitr.Core.Settings;
using Spitr.Core.Text;
using Spitr.Core.Recording;
using Spitr.Core.Transcription;

namespace Spitr.App.SelfTest;

/// <summary>
/// End-to-End-Selbsttest für die Windows-CI (Aufruf: `Spitr.exe --selftest
/// pfad/zu.wav`): baut die ECHTE Pipeline (RecordingController, WhisperEngine,
/// TextReplacementService, TextInsertionService inkl. Clipboard + SendInput
/// Strg+V), ersetzt nur Hotkey und Mikrofon durch programmatische Seams und
/// pastet in ein eigenes fokussiertes WPF-TextBox-Fenster. Damit ist alles
/// außer dem physischen Tastendruck verifiziert — ohne Notepad-Flakiness.
/// Exit-Code 0 = Transkript enthält die erwarteten Wörter.
/// </summary>
internal static class SelfTestRunner
{
    public static async Task<int> RunAsync(string wavPath)
    {
        Console.WriteLine($"[selftest] fixture: {wavPath}");
        if (!File.Exists(wavPath))
        {
            Console.WriteLine("[selftest] FAIL: WAV nicht gefunden");
            return 2;
        }

        var buffer = WavFile.ReadMono16(wavPath);
        Console.WriteLine($"[selftest] {buffer.Samples.Count} Samples, peak {buffer.PeakDbfs:F1} dBFS");

        // Isolierter Settings-Storage — der Selftest darf nie echte Nutzerdaten anfassen.
        var tempDir = Path.Combine(Path.GetTempPath(), "spitr-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        // Diag-Log mitschreiben und am Ende auf die Konsole kippen — sonst sind
        // Clipboard-/SendInput-Warnungen der Adapter in der CI unsichtbar.
        var logDir = Path.Combine(tempDir, "logs");
        var logStore = new Spitr.Core.Diagnostics.LogStore(logDir);
        logStore.Start(verbose: false);
        Spitr.Core.Diagnostics.DiagLog.Target = logStore;

        var modelsDir = Environment.GetEnvironmentVariable("SPITR_TEST_MODEL_DIR")
                        ?? Path.Combine(App.LocalDataDirectory, "models");
        var model = WhisperModelCatalog.SelectableModels.Single(m => m.Id == "base");
        using (var downloader = new ModelDownloader(modelsDir))
        {
            if (!downloader.IsDownloaded(model))
            {
                Console.WriteLine("[selftest] Modell fehlt, lade ggml-base…");
                await downloader.DownloadAsync(model);
            }
        }

        var settings = new SettingsStore(tempDir)
        {
            WhisperModel = "base",
            LocaleIdentifier = "de-DE",
            PlayReadyChime = false, // kein Chime-Trim — das Fixture soll unangetastet bleiben
            PlayDoneChime = false,
        };
        var history = new HistoryStore(tempDir);
        var dictionary = new DictionaryStore(tempDir);
        // Eine Wörterbuch-Regel läuft mit, damit auch der Replacement-Pfad end-to-end geprüft ist.
        dictionary.Add("spitr", "Spitr");
        dictionary.Enabled = true;

        var hotkey = new SelfTestHotkey();
        var audio = new SelfTestAudioCapture(buffer);
        var insertion = new Text.TextInsertionService();

        using var controller = new RecordingController(
            settings, history, dictionary,
            hotkey, audio, insertion, new SelfTestFeedback(),
            new TextReplacementService(),
            (_, m) => new WhisperEngine(modelsDir, m));

        // Ziel-Fenster: eigene TextBox, fokussiert — das SendInput-Strg+V landet hier.
        var box = new TextBox
        {
            AcceptsReturn = true,
            FontSize = 14,
            Margin = new Thickness(8),
        };
        AutomationProperties.SetAutomationId(box, "SelftestTextBox");
        // Jeden ankommenden Tastendruck protokollieren: unterscheidet „SendInput
        // kommt gar nicht an" (kein Event) von „kommt an, aber Paste greift nicht".
        box.PreviewKeyDown += (_, keyArgs) =>
            Console.WriteLine($"[selftest] key received: {keyArgs.Key} (mods={System.Windows.Input.Keyboard.Modifiers})");
        var window = new Window
        {
            Title = "Spitr Selftest",
            Width = 480,
            Height = 200,
            Content = box,
            Topmost = true,
        };
        window.Show();
        window.Activate();

        // CI-Desktops geben Foreground nicht immer freiwillig her — explizit
        // nach vorn zwingen und den Tastaturfokus auf die Box setzen.
        var hwnd = (global::Windows.Win32.Foundation.HWND)
            new System.Windows.Interop.WindowInteropHelper(window).Handle;
        global::Windows.Win32.PInvoke.SetForegroundWindow(hwnd);
        box.Focus();
        System.Windows.Input.Keyboard.Focus(box);
        await Task.Delay(500);
        var isForeground = global::Windows.Win32.PInvoke.GetForegroundWindow() == hwnd;
        Console.WriteLine($"[selftest] foreground={isForeground} keyboardFocus={box.IsKeyboardFocused}");

        // Zustandsübergänge sichtbar machen — bei einem Fehlschlag ist im
        // CI-Log sonst nicht unterscheidbar, ob Transkription oder Paste hakt.
        controller.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName is nameof(RecordingController.State) or nameof(RecordingController.LastInsertedText))
            {
                Console.WriteLine($"[selftest] {ev.PropertyName}: state={controller.State} lastInserted={controller.LastInsertedText?.Length.ToString() ?? "null"}");
            }
        };

        controller.Activate();
        Console.WriteLine("[selftest] Engine wird vorbereitet, Diktat startet…");

        hotkey.RaisePressed(false);
        await Task.Delay(100);
        hotkey.RaiseReleased();

        // Modell-Load + Transkription auf CI-CPUs kann dauern — großzügig warten.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        while (box.Text.Length == 0 && DateTime.UtcNow < deadline)
        {
            if (controller.State == RecordingState.Error)
            {
                Console.WriteLine($"[selftest] FAIL: Controller-Fehler: {controller.ErrorMessage}");
                return 1;
            }
            await Task.Delay(250);
        }

        var pasted = box.Text;
        var stillForeground = global::Windows.Win32.PInvoke.GetForegroundWindow() == hwnd;
        Console.WriteLine($"[selftest] TextBox-Inhalt ({pasted.Length} Zeichen): {pasted}");
        Console.WriteLine($"[selftest] LastInsertedText: {controller.LastInsertedText ?? "null"}");
        Console.WriteLine($"[selftest] foreground(after)={stillForeground} keyboardFocus(after)={box.IsKeyboardFocused}");

        // ── Zweites Diktat in DIESELBE TextBox: prüft das Smart Spacing über den
        // echten UIA-Caret-Read (WPF-TextBox bietet TextPattern — derselbe
        // Codepfad wie bei fremden Apps). Der Caret steht nach dem ersten Paste
        // direkt hinter dem Satzende; die zweite Einfügung muss mit GENAU einem
        // Leerzeichen anschließen. Die Fake-Audio-Capture liefert bei jedem
        // Stop() denselben eingecheckten Buffer, also einfach nochmal „drücken".
        var spacingOk = false;
        if (pasted.Length > 0)
        {
            // Fokus/Foreground defensiv erneut erzwingen (CI-Desktops nehmen ihn
            // gern zwischendurch weg) und den Caret ans Textende stellen.
            global::Windows.Win32.PInvoke.SetForegroundWindow(hwnd);
            box.Focus();
            System.Windows.Input.Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
            await Task.Delay(250);

            Console.WriteLine("[selftest] zweites Diktat (Smart-Spacing-Check)…");
            hotkey.RaisePressed(false);
            await Task.Delay(100);
            hotkey.RaiseReleased();

            var spacingDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
            while (box.Text.Length <= pasted.Length && DateTime.UtcNow < spacingDeadline)
            {
                if (controller.State == RecordingState.Error)
                {
                    Console.WriteLine($"[selftest] FAIL: Controller-Fehler beim zweiten Diktat: {controller.ErrorMessage}");
                    return 1;
                }
                await Task.Delay(250);
            }

            var combined = box.Text;
            Console.WriteLine($"[selftest] TextBox-Inhalt nach 2. Diktat ({combined.Length} Zeichen): {combined}");

            // Naht prüfen: der erste Text endet getrimmt (ohne Whitespace), der
            // zweite Einfüge-Text beginnt getrimmt — zwischen beiden muss also
            // exakt ein Leerzeichen stehen: weder „…gabe.Dies"-Aneinanderkleben
            // (Caret-Read lieferte null) noch ein Doppel-Leerzeichen.
            var tail = combined.Length > pasted.Length ? combined[pasted.Length..] : "";
            spacingOk = tail.Length > 1 && tail[0] == ' ' && !char.IsWhiteSpace(tail[1]);
            Console.WriteLine(spacingOk
                ? "[selftest] smart spacing: ok"
                : $"[selftest] smart spacing: Naht falsch (tail beginnt mit: \"{(tail.Length == 0 ? "<leer>" : tail[..Math.Min(tail.Length, 6)])}\")");
        }

        // Diag-Log auf die Konsole spülen (Adapter-Warnungen sichtbar machen).
        Spitr.Core.Diagnostics.DiagLog.Target = null;
        logStore.Dispose();
        foreach (var logFile in Directory.GetFiles(logDir))
        {
            Console.WriteLine($"[selftest] --- {Path.GetFileName(logFile)} ---");
            foreach (var line in File.ReadAllLines(logFile)) Console.WriteLine($"[selftest] {line}");
        }

        var letters = new string(pasted.ToLowerInvariant().Where(char.IsLetter).ToArray());
        var ok = letters.Contains("test") && letters.Contains("spracheingabe");
        // Wörterbuch-Regel „spitr → Spitr": nur prüfen, wenn Whisper das Wort
        // überhaupt erkannt hat (Eigennamen sind modellabhängig).
        if (pasted.Contains("spitr", StringComparison.OrdinalIgnoreCase) &&
            !pasted.Contains("Spitr"))
        {
            Console.WriteLine("[selftest] FAIL: Wörterbuch-Ersetzung nicht angewendet");
            ok = false;
        }
        if (!spacingOk)
        {
            Console.WriteLine("[selftest] FAIL: Smart Spacing — genau ein Leerzeichen an der Naht erwartet");
            ok = false;
        }

        Console.WriteLine(ok ? "[selftest] OK" : "[selftest] FAIL: erwartete Wörter fehlen");
        try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        return ok ? 0 : 1;
    }
}
