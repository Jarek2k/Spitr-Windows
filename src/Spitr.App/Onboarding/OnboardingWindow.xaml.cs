using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Spitr.App.Audio;
using Spitr.App.SettingsUi;
using Spitr.Core.Settings;
using Spitr.Core.Transcription;

namespace Spitr.App.Onboarding;

/// <summary>
/// Erststart-Assistent — Pendant zu OnboardingView.swift. Der Mac erklärt und
/// erbittet dort drei Berechtigungen; Windows braucht keine, deshalb führen die
/// drei Schritte hier durch Bedienung (Hold-to-Talk + Tastenwahl), einen
/// Mikrofon-Pegeltest und den einmaligen Modell-Download. »Fertig« setzt
/// <see cref="SettingsStore.HasCompletedOnboarding"/>; Esc schließt ohne das
/// Flag zu setzen (der Assistent erscheint dann beim nächsten Start erneut).
/// </summary>
public partial class OnboardingWindow : Window
{
    /// <summary>Schwelle wie AudioBuffer.IsLikelySilent: echte Sprache peakt deutlich darüber.</summary>
    private const double MinPeakDbfs = -40;

    private static readonly Brush SuccessBrush = MakeFrozen(Color.FromRgb(0x2E, 0xA0, 0x43));
    private static readonly Brush WarningBrush = MakeFrozen(Color.FromRgb(0xC8, 0x5A, 0x1E));
    private static readonly Brush InactiveDotBrush = MakeFrozen(Color.FromArgb(0x50, 0x80, 0x80, 0x80));

    private readonly SettingsStore _settings;
    private readonly ModelDownloadViewModel _download;
    private int _step = 1;
    private bool _micTestRunning;

    public OnboardingWindow(SettingsStore settings, string modelsDirectory)
    {
        InitializeComponent();
        _settings = settings;
        _download = new ModelDownloadViewModel(modelsDirectory);
        _download.PropertyChanged += OnDownloadChanged;

        // Schritt 1: Tastenwahl füllen, aktuelle Einstellung vorselektieren.
        foreach (var key in Enum.GetValues<HoldKey>())
        {
            KeyPicker.Items.Add(new ComboBoxItem { Content = key.DisplayName(), Tag = key });
        }
        KeyPicker.SelectedIndex = 0;
        for (var i = 0; i < KeyPicker.Items.Count; i++)
        {
            if (KeyPicker.Items[i] is ComboBoxItem { Tag: HoldKey k } && k == _settings.HoldKey)
            {
                KeyPicker.SelectedIndex = i;
                break;
            }
        }
        KeyPicker.SelectionChanged += OnKeyPicked;

        // Schritt 3: Modell-Katalog füllen, persistiertes Modell vorselektieren.
        foreach (var model in WhisperModelCatalog.SelectableModels)
        {
            ModelPicker.Items.Add(new ComboBoxItem
            {
                Content = $"{model.Id} — {model.Hint} (~{FormatSize(model.ApproxBytes)})",
                Tag = model,
            });
        }
        ModelPicker.SelectedIndex = 0;
        for (var i = 0; i < ModelPicker.Items.Count; i++)
        {
            if (ModelPicker.Items[i] is ComboBoxItem { Tag: WhisperModelCatalog.ModelInfo m }
                && m.Id == _settings.WhisperModel)
            {
                ModelPicker.SelectedIndex = i;
                break;
            }
        }
        ModelPicker.SelectionChanged += OnModelPicked;

        UpdateInstruction();
        ShowStep(1);
    }

    // MARK: - Navigation

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 1, 3);
        Step1Panel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepCounter.Text = $"Schritt {_step} von 3";
        BackButton.IsEnabled = _step > 1;
        NextButton.Visibility = _step < 3 ? Visibility.Visible : Visibility.Collapsed;
        FinishButton.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        // Enter navigiert immer über den sichtbaren Primär-Knopf.
        NextButton.IsDefault = _step < 3;
        FinishButton.IsDefault = _step == 3;

        var dots = new[] { Dot1, Dot2, Dot3 };
        for (var i = 0; i < dots.Length; i++)
        {
            dots[i].Fill = i + 1 == _step ? SystemColors.AccentColorBrush : InactiveDotBrush;
        }

        if (_step == 3) UpdateModelStatus();
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void OnNextClick(object sender, RoutedEventArgs e) => ShowStep(_step + 1);

    private void OnFinishClick(object sender, RoutedEventArgs e)
    {
        _settings.HasCompletedOnboarding = true;
        Close();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // Esc schließt den Assistenten, ohne das Onboarding als erledigt zu markieren.
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _download.PropertyChanged -= OnDownloadChanged;
        base.OnClosed(e);
    }

    // MARK: - Schritt 1: Bedienung

    private void OnKeyPicked(object sender, SelectionChangedEventArgs e)
    {
        if ((KeyPicker.SelectedItem as ComboBoxItem)?.Tag is not HoldKey key) return;
        _settings.HoldKey = key;
        UpdateInstruction();
    }

    /// <summary>Die Kurzanleitung folgt der gerade gewählten Taste.</summary>
    private void UpdateInstruction() => Step1Instruction.Text =
        $"»{_settings.HoldKey.DisplayName()}« gedrückt halten → sprechen → loslassen — " +
        "der Text landet im fokussierten Fenster. Esc bricht ab, +Umschalt spricht einen Befehl.";

    // MARK: - Schritt 2: Mikrofon-Check

    private async void OnMicTestClick(object sender, RoutedEventArgs e)
    {
        if (_micTestRunning) return;
        _micTestRunning = true;
        MicTestButton.IsEnabled = false;
        MicResultPanel.Visibility = Visibility.Collapsed;
        MicHelpPanel.Visibility = Visibility.Collapsed;
        LevelBar.Value = 0;

        // Pro Test eine FRISCHE Capture-Instanz — dieselbe Lektion wie im
        // RecordingController: wiederverwendete Captures schleppen Gerätezustand mit.
        var capture = new WasapiAudioCaptureService { PreferredDeviceId = _settings.InputDeviceId };
        void OnLevel(float level) => Dispatcher.BeginInvoke(() => LevelBar.Value = level);
        capture.LevelChanged += OnLevel;
        try
        {
            // Start/Stop auf dem Threadpool — WASAPI-Init darf das UI nicht einfrieren.
            await Task.Run(capture.Start);
            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            var buffer = await Task.Run(capture.Stop);
            ShowMicResult(buffer.PeakDbfs);
        }
        catch (Exception ex)
        {
            // Die Audio-Schicht wirft bereits deutsche Meldungen (z. B. „Kein
            // Mikrofon verfügbar …" auf Runnern ohne Audiogeräte).
            ShowMicFailure(ex.Message);
        }
        finally
        {
            capture.LevelChanged -= OnLevel;
            LevelBar.Value = 0;
            MicTestButton.IsEnabled = true;
            _micTestRunning = false;
        }
    }

    private void ShowMicResult(double peakDbfs)
    {
        if (peakDbfs > MinPeakDbfs)
        {
            MicResultIcon.Text = "\uE73E"; // MDL2 CheckMark
            MicResultIcon.Foreground = SuccessBrush;
            MicResultText.Text = "Mikrofon funktioniert";
            MicResultPanel.Visibility = Visibility.Visible;
            MicHelpPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var measured = double.IsNegativeInfinity(peakDbfs)
            ? "nur Stille aufgenommen"
            : $"Spitze {peakDbfs:F0} dBFS";
        ShowMicFailure($"Kaum Signal erkannt ({measured}). Sprich näher am Mikrofon " +
                       "oder prüfe die Mikrofon-Freigabe in den Windows-Einstellungen.");
    }

    /// <summary>Fehler/zu leise: Hinweis plus Absprung in die Windows-Datenschutzseite.</summary>
    private void ShowMicFailure(string message)
    {
        MicResultIcon.Text = "\uE7BA"; // MDL2 Warning
        MicResultIcon.Foreground = WarningBrush;
        MicResultText.Text = message;
        MicResultPanel.Visibility = Visibility.Visible;
        MicHelpPanel.Visibility = Visibility.Visible;
    }

    private void OnOpenMicSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // ms-settings: braucht ShellExecute — es gibt keine EXE dahinter.
            Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Die Settings-App ließ sich nicht öffnen — der Hinweistext daneben
            // beschreibt die nötige Einstellung auch so.
        }
    }

    // MARK: - Schritt 3: Modell

    private void OnModelPicked(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedModel() is not { } model) return;
        _settings.WhisperModel = model.Id;
        DownloadErrorText.Visibility = Visibility.Collapsed;
        UpdateModelStatus();
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        if (SelectedModel() is not { } model) return;
        DownloadErrorText.Visibility = Visibility.Collapsed;
        DownloadButton.IsEnabled = false;
        DownloadProgress.Value = 0;
        DownloadProgress.Visibility = Visibility.Visible;
        try
        {
            await _download.EnsureDownloadedAsync(model.Id);
        }
        catch (Exception ex)
        {
            // Falls das ViewModel Fehler wirft, statt sie (nur) in Error abzulegen.
            ShowDownloadError(ex.Message);
        }
        UpdateModelStatus();
    }

    private void OnDownloadLaterToggled(object sender, RoutedEventArgs e) => UpdateFinishEnabled();

    private void OnDownloadChanged(object? sender, PropertyChangedEventArgs e) =>
        // Fortschritts-Meldungen können von einem Hintergrund-Thread kommen.
        Dispatcher.BeginInvoke(() =>
        {
            DownloadProgress.Value = _download.Progress;
            if (_download.Error is { Length: > 0 } error) ShowDownloadError(error);
            UpdateModelStatus();
        });

    /// <summary>Statuszeile, Download-Knopf und »Fertig« an den Modellzustand angleichen.</summary>
    private void UpdateModelStatus()
    {
        if (SelectedModel() is not { } model) return;
        var downloaded = _download.IsDownloaded(model.Id);
        if (downloaded)
        {
            ModelStatusIcon.Visibility = Visibility.Visible;
            ModelStatusText.Text = "Bereits vorhanden — Spitr ist startklar.";
            DownloadButton.IsEnabled = false;
            DownloadProgress.Visibility = Visibility.Collapsed;
        }
        else if (_download.IsDownloading)
        {
            ModelStatusIcon.Visibility = Visibility.Collapsed;
            ModelStatusText.Text = $"Wird heruntergeladen … {_download.Progress:P0}";
            DownloadButton.IsEnabled = false;
            DownloadProgress.Visibility = Visibility.Visible;
        }
        else
        {
            ModelStatusIcon.Visibility = Visibility.Collapsed;
            ModelStatusText.Text = $"Noch nicht heruntergeladen (~{FormatSize(model.ApproxBytes)}).";
            DownloadButton.IsEnabled = true;
        }
        UpdateFinishEnabled(downloaded);
    }

    /// <summary>»Fertig« ist frei, sobald das Modell da ist ODER „später" angehakt wurde.</summary>
    private void UpdateFinishEnabled(bool? downloadedOverride = null)
    {
        var downloaded = downloadedOverride
            ?? (SelectedModel() is { } model && _download.IsDownloaded(model.Id));
        FinishButton.IsEnabled = downloaded || DownloadLaterCheck.IsChecked == true;
    }

    private void ShowDownloadError(string message)
    {
        DownloadErrorText.Text = message;
        DownloadErrorText.Visibility = Visibility.Visible;
    }

    private WhisperModelCatalog.ModelInfo? SelectedModel() =>
        (ModelPicker.SelectedItem as ComboBoxItem)?.Tag as WhisperModelCatalog.ModelInfo;

    /// <summary>~Bytes → „148 MB" / „3,1 GB" für die Picker-Beschriftung.</summary>
    private static string FormatSize(long bytes) => bytes >= 1_000_000_000
        ? $"{bytes / 1_000_000_000.0:0.#} GB"
        : $"{bytes / 1_000_000.0:0} MB";

    private static Brush MakeFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
