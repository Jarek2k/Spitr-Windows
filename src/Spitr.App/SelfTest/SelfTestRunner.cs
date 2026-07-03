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
        box.Focus();

        controller.Activate();
        Console.WriteLine("[selftest] Engine wird vorbereitet, Diktat startet…");

        hotkey.RaisePressed(false);
        await Task.Delay(100);
        hotkey.RaiseReleased();

        // Modell-Load + Transkription auf CI-CPUs kann dauern — großzügig warten.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(180);
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
        Console.WriteLine($"[selftest] TextBox-Inhalt ({pasted.Length} Zeichen): {pasted}");

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

        Console.WriteLine(ok ? "[selftest] OK" : "[selftest] FAIL: erwartete Wörter fehlen");
        try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        return ok ? 0 : 1;
    }
}
