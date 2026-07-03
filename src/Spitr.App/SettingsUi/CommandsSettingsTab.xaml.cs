using System.ComponentModel;
using System.Windows.Controls;
using Spitr.Core.Recording;
using Spitr.Core.Settings;

namespace Spitr.App.SettingsUi;

/// <summary>Anzeige-Zeile der Befehlsliste: Titel + Beispiel-Phrase in »…«.</summary>
public sealed record CommandRow(string Title, string Example);

/// <summary>
/// Der Tab „Befehle" — Port von CommandsSettingsView.swift. Reine Referenz:
/// die Befehle kommen aus <see cref="RecordingController.AvailableCommands"/>
/// (dieselbe Quelle wie der Matcher), die Fußnote nennt die aktuell
/// konfigurierte Aufnahme-Taste.
/// </summary>
public partial class CommandsSettingsTab : UserControl
{
    private readonly SettingsStore _settings;

    public CommandsSettingsTab(SettingsStore settings, RecordingController controller)
    {
        _settings = settings;
        InitializeComponent();

        CommandsList.ItemsSource = controller.AvailableCommands
            .Select(c => new CommandRow(c.Title, $"»{c.Example}«"))
            .ToList();
        UpdateFootnote();

        // Die Fußnote folgt einem Tastenwechsel im Allgemein-Tab; Abo nur
        // solange der Tab lebt (der Store überlebt das Fenster).
        Loaded += (_, _) =>
        {
            UpdateFootnote();
            _settings.PropertyChanged += OnSettingsChanged;
        };
        Unloaded += (_, _) => _settings.PropertyChanged -= OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsStore.HoldKey))
        {
            Dispatcher.BeginInvoke(UpdateFootnote);
        }
    }

    private void UpdateFootnote() => Footnote.Text =
        $"Halte die Aufnahme-Taste ({_settings.HoldKey.DisplayName()}) zusammen mit Umschalt " +
        "und sprich einen Befehl, statt zu diktieren. Der Text wird dann nicht eingefügt.";
}
