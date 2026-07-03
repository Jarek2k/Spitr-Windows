using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Spitr.App.Audio;
using Spitr.App.Win32;
using Spitr.Core.Feedback;
using Spitr.Core.Overlay;
using Spitr.Core.Recording;
using Spitr.Core.Settings;
using Spitr.Core.Transcription;

namespace Spitr.App.SettingsUi;

/// <summary>Ein Picker-Eintrag: Wert + deutsches Label (für SelectedValuePath/DisplayMemberPath).</summary>
public sealed record PickerChoice(object Value, string Label);

/// <summary>
/// Der Tab „Allgemein" — Port von GeneralSettingsView.swift. Die Picker binden
/// per SelectedValue direkt an den SettingsStore (INotifyPropertyChanged);
/// Modell-Download und Autostart laufen über Code-Behind, weil sie
/// Seiteneffekte haben (Download anstoßen, Registry-Zustand zurücklesen).
/// </summary>
public partial class GeneralSettingsTab : UserControl
{
    /// <summary>Kuratiertes, bewusst kurzes Sprachset — identisch zum macOS-Original.</summary>
    private static readonly (string Id, string Name)[] Languages =
    [
        ("de-DE", "Deutsch"),
        ("en-US", "English (US)"),
        ("en-GB", "English (UK)"),
        ("fr-FR", "Français"),
        ("es-ES", "Español"),
        ("it-IT", "Italiano"),
        ("nl-NL", "Nederlands"),
    ];

    private readonly ModelDownloadViewModel _downloads;
    private readonly StartupService _startup = new();

    /// <summary>Eigener kleiner Player für die Hörprobe, unabhängig vom RecordingController.</summary>
    private readonly ChimePlayer _chimePreview = new();

    public GeneralSettingsTab(
        SettingsStore settings,
        RecordingController controller,
        AudioDeviceService audioDevices,
        ModelDownloadViewModel downloads)
    {
        _downloads = downloads;
        InitializeComponent();

        // ItemsSources VOR dem DataContext setzen, damit die SelectedValue-
        // Bindings beim Aktivieren sofort einen Treffer finden (sonst schreibt
        // das Two-Way-Binding kurz null in den Store zurück).
        ModelPicker.ItemsSource = WhisperModelCatalog.SelectableModels
            .Select(m => new PickerChoice(m.Id, $"{m.Id} – {m.Hint} (~{FormatSize(m.ApproxBytes)})"))
            .ToList();
        LanguagePicker.ItemsSource = Languages
            .Select(l => new PickerChoice(l.Id, l.Name))
            .ToList();
        HoldKeyPicker.ItemsSource = Enum.GetValues<HoldKey>()
            .Select(k => new PickerChoice(k, k.DisplayName()))
            .ToList();
        MicrophonePicker.ItemsSource = MicrophoneChoices(audioDevices, settings.InputDeviceId);
        WaveformPicker.ItemsSource = new List<PickerChoice>
        {
            new(WaveformStyle.SignalReactive, "Signal (reaktiv)"),
            new(WaveformStyle.SignalBare, "Signal (randlos)"),
            new(WaveformStyle.Signal, "Signal (Kapsel)"),
            new(WaveformStyle.Bars, "Balken"),
            new(WaveformStyle.Kitt, "KITT (rot)"),
        };
        ReadyChimeStylePicker.ItemsSource = new List<PickerChoice>
        {
            new(ReadyChimeStyle.Single, "Einzelton"),
            new(ReadyChimeStyle.Double, "Doppelton"),
            new(ReadyChimeStyle.Rising, "Aufsteigend (Funk)"),
        };

        // "Whisper · base" — aktualisiert sich beim Modellwechsel über den Controller.
        EngineLabel.SetBinding(TextBlock.TextProperty, new Binding(nameof(RecordingController.ActiveEngineLabel))
        {
            Source = controller,
            Mode = BindingMode.OneWay,
        });

        DataContext = settings;

        // Registry ist die Quelle der Wahrheit, kein Binding — Zustand direkt lesen.
        StartupToggle.IsChecked = _startup.IsEnabled;

        _downloads.PropertyChanged += OnDownloadsChanged;
        UpdateDownloadUi();
    }

    /// <summary>Verbundene Mikrofone + „Systemstandard"; ein verschwundenes, aber noch gewähltes Gerät bleibt sichtbar.</summary>
    private static List<PickerChoice> MicrophoneChoices(AudioDeviceService audioDevices, string selectedId)
    {
        var devices = audioDevices.InputDevices();
        var choices = new List<PickerChoice> { new("", AudioDeviceService.DefaultLabel) };
        choices.AddRange(devices.Select(d => new PickerChoice(d.Id, d.Name)));
        if (selectedId.Length > 0 && devices.All(d => d.Id != selectedId))
        {
            choices.Add(new PickerChoice(selectedId, "Nicht verfügbar"));
        }
        return choices;
    }

    private static string FormatSize(long bytes) => bytes >= 1_000_000_000
        ? string.Create(CultureInfo.CurrentCulture, $"{bytes / 1e9:0.0} GB")
        : string.Create(CultureInfo.CurrentCulture, $"{bytes / 1e6:0} MB");

    // MARK: - Modell-Download

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Während des Aufbaus (Binding setzt die Initial-Auswahl) nichts anstoßen.
        if (!IsLoaded) return;
        if (ModelPicker.SelectedValue is not string modelId) return;
        if (_downloads.IsDownloaded(modelId)) return;
        _ = _downloads.EnsureDownloadedAsync(modelId);
    }

    private void OnDownloadsChanged(object? sender, PropertyChangedEventArgs e) => UpdateDownloadUi();

    private void UpdateDownloadUi()
    {
        DownloadPanel.Visibility = _downloads.IsDownloading ? Visibility.Visible : Visibility.Collapsed;
        DownloadProgress.Value = _downloads.Progress;
        DownloadStatus.Text = string.Create(
            CultureInfo.CurrentCulture, $"Modell wird heruntergeladen… {_downloads.Progress:P0}");
        DownloadError.Text = _downloads.Error is { } error ? $"Download fehlgeschlagen: {error}" : "";
        DownloadError.Visibility = _downloads.Error is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // MARK: - Hörprobe & Autostart

    private void OnChimeStyleSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Nur echte Nutzer-Wechsel vertonen, nicht die Initial-Auswahl beim Aufbau.
        if (!IsLoaded) return;
        if (ReadyChimeStylePicker.SelectedValue is ReadyChimeStyle style)
        {
            _chimePreview.PlayReady(style);
        }
    }

    private void OnStartupToggleClick(object sender, RoutedEventArgs e)
    {
        _startup.IsEnabled = StartupToggle.IsChecked == true;
        // Zurücklesen, falls der Registry-Zugriff scheiterte — das UI lügt nie.
        StartupToggle.IsChecked = _startup.IsEnabled;
    }
}
