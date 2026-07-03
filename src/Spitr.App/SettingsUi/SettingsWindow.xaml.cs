using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Spitr.App.Audio;
using Spitr.Core.Recording;
using Spitr.Core.Settings;

namespace Spitr.App.SettingsUi;

/// <summary>
/// Das Einstellungsfenster — Port von SettingsView.swift. Die Tab-Auswahl ist
/// bidirektional an <see cref="SettingsStore.RequestedTab"/> gekoppelt (wie das
/// TabView-Binding am Mac), und ein gesetztes
/// <see cref="SettingsStore.PendingCorrectionId"/> öffnet den Korrektur-Dialog
/// im Verlauf-Tab und wird dabei konsumiert. Esc schließt das Fenster.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly HistorySettingsTab _historyTab;

    public SettingsWindow(
        SettingsStore settings,
        HistoryStore history,
        DictionaryStore dictionary,
        RecordingController controller,
        AudioDeviceService audioDevices,
        string modelsDirectory)
    {
        _settings = settings;
        InitializeComponent();

        var downloads = new ModelDownloadViewModel(modelsDirectory);
        GeneralTab.Content = new GeneralSettingsTab(settings, controller, audioDevices, downloads);
        VocabularyTab.Content = new VocabularySettingsTab(settings);
        DictionaryTab.Content = new DictionarySettingsTab(dictionary);
        CommandsTab.Content = new CommandsSettingsTab(settings, controller);
        _historyTab = new HistorySettingsTab(history, dictionary);
        HistoryTab.Content = _historyTab;
        DiagnosticsTab.Content = new DiagnosticsSettingsTab(settings);

        // Angeforderten Tab respektieren (Tray-Menü/Sprachbefehl setzt ihn vor dem Öffnen).
        Tabs.SelectedIndex = (int)settings.RequestedTab;

        _settings.PropertyChanged += OnSettingsChanged;
        Closed += (_, _) => _settings.PropertyChanged -= OnSettingsChanged;

        // Eine schon beim Öffnen anstehende Korrektur (Menü → „korrigieren")
        // erst starten, wenn das Fenster steht — der Dialog ist modal.
        Loaded += (_, _) => ConsumePendingCorrection();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Settings-Events können von jedem Thread kommen (Sprachbefehle laufen
        // auf dem Transkriptions-Worker) → immer über den Dispatcher.
        switch (e.PropertyName)
        {
            case nameof(SettingsStore.RequestedTab):
                Dispatcher.BeginInvoke(() => Tabs.SelectedIndex = (int)_settings.RequestedTab);
                break;
            case nameof(SettingsStore.PendingCorrectionId):
                Dispatcher.BeginInvoke(ConsumePendingCorrection);
                break;
        }
    }

    /// <summary>
    /// Öffnet die von außen angeforderte Korrektur im Verlauf-Tab und löscht
    /// die Anforderung, damit sie genau einmal feuert (Port von consumePending).
    /// </summary>
    private void ConsumePendingCorrection()
    {
        if (_settings.PendingCorrectionId is not { } entryId) return;
        Tabs.SelectedIndex = (int)SettingsTab.History;
        _settings.PendingCorrectionId = null;
        _historyTab.OpenCorrection(entryId, this);
    }

    private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged bubbelt auch von ComboBoxen/Listen in den Tab-Inhalten
        // hoch — nur auf die echte Tab-Auswahl reagieren.
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;
        if (Tabs.SelectedIndex >= 0)
        {
            _settings.RequestedTab = (SettingsTab)Tabs.SelectedIndex;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
