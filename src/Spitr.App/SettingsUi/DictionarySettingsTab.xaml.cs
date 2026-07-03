using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Spitr.Core.Settings;

namespace Spitr.App.SettingsUi;

/// <summary>
/// Der Tab „Wörterbuch" — Port von DictionarySettingsView.swift. Der Store ist
/// die Quelle der Wahrheit: jede Aktion geht direkt an ihn, die Liste wird bei
/// seinem PropertyChanged neu aufgebaut (auch Sprachbefehle ändern Regeln,
/// deren Events kommen von einem Hintergrund-Thread → Dispatcher).
/// </summary>
public partial class DictionarySettingsTab : UserControl
{
    private readonly DictionaryStore _dictionary;

    public DictionarySettingsTab(DictionaryStore dictionary)
    {
        _dictionary = dictionary;
        InitializeComponent();
        DataContext = dictionary;

        RefreshRules();
        // Abo nur solange der Tab sichtbar ist — der Store überlebt das Fenster.
        Loaded += (_, _) =>
        {
            RefreshRules();
            _dictionary.PropertyChanged += OnDictionaryChanged;
        };
        Unloaded += (_, _) => _dictionary.PropertyChanged -= OnDictionaryChanged;
    }

    private void OnDictionaryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DictionaryStore.Rules) or nameof(DictionaryStore.Enabled))
        {
            Dispatcher.BeginInvoke(RefreshRules);
        }
    }

    private void RefreshRules()
    {
        // Auswahl über den Neuaufbau retten (Regeln sind Records, Id ist stabil).
        var selectedId = (RulesList.SelectedItem as ReplacementRule)?.Id;
        var rules = _dictionary.Rules.ToList();
        RulesList.ItemsSource = rules;
        if (selectedId is { } id && rules.FirstOrDefault(r => r.Id == id) is { } keep)
        {
            RulesList.SelectedItem = keep;
        }
        EmptyHint.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Wie am Mac: deaktiviertes Wörterbuch dimmt die Liste, bleibt aber editierbar.
        RulesArea.Opacity = _dictionary.Enabled ? 1.0 : 0.55;
    }

    private void OnRulesSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var hasSelection = RulesList.SelectedItem is ReplacementRule;
        EditButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
    }

    private void OnRulesDoubleClick(object sender, MouseButtonEventArgs e) => EditSelected();

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var dialog = new RuleEditDialog("Regel hinzufügen", "", "") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true)
        {
            _dictionary.Add(dialog.Pattern, dialog.Replacement);
        }
    }

    private void OnEditClick(object sender, RoutedEventArgs e) => EditSelected();

    private void EditSelected()
    {
        if (RulesList.SelectedItem is not ReplacementRule rule) return;
        var dialog = new RuleEditDialog("Regel bearbeiten", rule.Pattern, rule.Replacement)
        {
            Owner = Window.GetWindow(this),
        };
        if (dialog.ShowDialog() == true)
        {
            _dictionary.Update(rule with { Pattern = dialog.Pattern, Replacement = dialog.Replacement });
        }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (RulesList.SelectedItem is not ReplacementRule rule) return;
        _dictionary.Delete(rule.Id);
    }
}
