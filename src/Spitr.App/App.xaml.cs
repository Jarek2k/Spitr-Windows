using System.IO;
using System.Windows;
using Spitr.App.Audio;
using Spitr.App.Overlay;
using Spitr.App.SelfTest;
using Spitr.App.Win32;
using Spitr.Core.Diagnostics;
using Spitr.Core.Recording;
using Spitr.Core.Settings;
using Spitr.Core.Text;
using Spitr.Core.Transcription;

namespace Spitr.App;

/// <summary>
/// Tray-only-App ohne Hauptfenster (Pendant zur macOS-MenuBarExtra). Baut die
/// Abhängigkeiten zusammen (Pendant zu SpitrApp/AppDelegate) und hält die
/// Single-Instance-Mutex. Mit `--selftest pfad.wav` läuft stattdessen der
/// CI-End-to-End-Test durch die echte Pipeline.
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private LogStore? _logStore;
    private SettingsStore? _settings;
    private KeyboardHookService? _hotkey;
    private Text.TextInsertionService? _insertion;
    private RecordingController? _controller;
    private TrayIconController? _tray;
    private Overlay.OverlayController? _overlay;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length >= 1 && e.Args[0] == "--selftest")
        {
            // Stdout der aufrufenden Konsole anhängen, damit die CI die
            // [selftest]-Zeilen sieht (WinExe hat standardmäßig keine Konsole).
            // global:: nötig — innerhalb von Application schattet die
            // Windows-Property (WindowCollection) den Windows.Win32-Namespace.
            global::Windows.Win32.PInvoke.AttachConsole(unchecked((uint)-1));
            var wav = e.Args.Length > 1
                ? e.Args[1]
                : Path.Combine(AppContext.BaseDirectory, "german_test.wav");
            _ = RunSelfTestAsync(wav);
            return;
        }

        if (e.Args.Length >= 1 && e.Args[0] == "--screenshot-overlays")
        {
            // Wie --selftest: Konsole anhängen, damit die CI die
            // [screenshot]-Zeilen sieht.
            global::Windows.Win32.PInvoke.AttachConsole(unchecked((uint)-1));
            var outDir = e.Args.Length > 1
                ? e.Args[1]
                : Path.Combine(AppContext.BaseDirectory, "overlay-screenshots");
            _ = RunOverlayScreenshotsAsync(outDir);
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, "Spitr-SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        Directory.CreateDirectory(SettingsDirectory);
        Directory.CreateDirectory(LocalDataDirectory);

        _logStore = new LogStore(Path.Combine(LocalDataDirectory, "logs"));
        _settings = new SettingsStore(SettingsDirectory);
        _logStore.Start(_settings.VerboseLogging);
        DiagLog.Target = _logStore;
        DiagLog.Verbose = _settings.VerboseLogging;
        _settings.PropertyChanged += (_, ev) =>
        {
            if (ev.PropertyName == nameof(SettingsStore.VerboseLogging))
            {
                DiagLog.Verbose = _settings.VerboseLogging;
            }
        };

        var history = new HistoryStore(SettingsDirectory);
        var dictionary = new DictionaryStore(SettingsDirectory);

        var modelsDirectory = Path.Combine(LocalDataDirectory, "models");
        _hotkey = new KeyboardHookService();
        _insertion = new Text.TextInsertionService();
        _controller = new RecordingController(
            _settings, history, dictionary,
            _hotkey,
            new WasapiAudioCaptureService(),
            _insertion,
            new ChimePlayer(),
            new TextReplacementService(),
            (_, model) => new WhisperEngine(modelsDirectory, model));

        _tray = new TrayIconController(_controller);
        _tray.QuitRequested += () => Shutdown();
        _overlay = new Overlay.OverlayController(_controller, _settings);
        _insertion.InsertBlockedByElevation += message =>
            Current.Dispatcher.BeginInvoke(() => _tray?.ShowNotification("Spitr", message));

        _controller.Activate();
        new DiagLog("app").Info("app started");
    }

    private async Task RunOverlayScreenshotsAsync(string outputDirectory)
    {
        int exitCode;
        try
        {
            exitCode = await OverlayScreenshotter.RunAsync(outputDirectory);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[screenshot] FAIL: {ex}");
            exitCode = 1;
        }
        Shutdown(exitCode);
    }

    private async Task RunSelfTestAsync(string wavPath)
    {
        int exitCode;
        try
        {
            exitCode = await SelfTestRunner.RunAsync(wavPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[selftest] FAIL: {ex}");
            exitCode = 3;
        }
        Shutdown(exitCode);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Letzte Chance: eine noch ausstehende Clipboard-Wiederherstellung
        // synchron ausführen (Pendant zum willTerminate-Restore am Mac).
        _insertion?.FlushPendingRestore();
        _overlay?.Dispose();
        _tray?.Dispose();
        _controller?.Dispose();
        _hotkey?.Dispose();
        DiagLog.Target = null;
        _logStore?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>%APPDATA%\Spitr — Settings/Verlauf/Wörterbuch.</summary>
    internal static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Spitr");

    /// <summary>%LOCALAPPDATA%\Spitr — Modelle + Logs (groß bzw. rotierend, kein Roaming).</summary>
    internal static string LocalDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spitr");
}
