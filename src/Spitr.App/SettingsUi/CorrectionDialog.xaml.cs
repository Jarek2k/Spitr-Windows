using System.Windows;
using System.Windows.Controls;
using Spitr.Core.Settings;
using Spitr.Core.Text;

namespace Spitr.App.SettingsUi;

/// <summary>
/// Macht aus einem falsch erkannten Wort eine dauerhafte Wörterbuch-Regel —
/// Port des CorrectionSheet aus HistorySettingsView.swift. Antippen eines
/// Wort-Chips füllt „Falsch erkannt", „Regel sichern" legt die Regel an,
/// schaltet das Wörterbuch ein (der Nutzer hat gerade eine Regel verlangt)
/// und korrigiert den Verlaufs-Eintrag gleich mit.
/// </summary>
public partial class CorrectionDialog : Window
{
    private readonly HistoryEntry _entry;
    private readonly HistoryStore _history;
    private readonly DictionaryStore _dictionary;

    /// <summary>Verhindert Ping-Pong zwischen Chip-Auswahl und Textfeld.</summary>
    private bool _syncing;

    public CorrectionDialog(HistoryEntry entry, HistoryStore history, DictionaryStore dictionary)
    {
        _entry = entry;
        _history = history;
        _dictionary = dictionary;
        InitializeComponent();

        WordsList.ItemsSource = DistinctWords(entry.Text);
        Loaded += (_, _) => WrongWordBox.Focus();
    }

    /// <summary>
    /// Die erkannten Wörter (Interpunktion abgeschnitten), dedupliziert in
    /// Original-Reihenfolge — die Tipp-Ziele, damit niemand abtippen muss.
    /// </summary>
    private static List<string> DistinctWords(string text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var words = new List<string>();
        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var word = TrimPunctuation(token);
            if (word.Length == 0 || !seen.Add(word)) continue;
            words.Add(word);
        }
        return words;
    }

    /// <summary>Schneidet Nicht-Buchstaben/-Ziffern an beiden Enden ab („Wort," → „Wort").</summary>
    private static string TrimPunctuation(string word)
    {
        var start = 0;
        var end = word.Length;
        while (start < end && !char.IsLetterOrDigit(word[start])) start++;
        while (end > start && !char.IsLetterOrDigit(word[end - 1])) end--;
        return word[start..end];
    }

    private void OnWordSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || WordsList.SelectedItem is not string word) return;
        _syncing = true;
        WrongWordBox.Text = word;
        _syncing = false;
        UpdateSaveEnabled();
        ReplacementBox.Focus();
    }

    private void OnFieldsChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSaveEnabled();
        // Handgetipptes Wort ≠ Chip-Auswahl → Auswahl lösen, damit das UI nicht lügt.
        if (_syncing) return;
        if (WordsList.SelectedItem is string selected
            && !string.Equals(selected, WrongWordBox.Text, StringComparison.OrdinalIgnoreCase))
        {
            _syncing = true;
            WordsList.SelectedItem = null;
            _syncing = false;
        }
    }

    private void UpdateSaveEnabled() => SaveButton.IsEnabled =
        WrongWordBox.Text.Trim().Length > 0 && ReplacementBox.Text.Trim().Length > 0;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var from = WrongWordBox.Text.Trim();
        var to = ReplacementBox.Text.Trim();
        if (from.Length == 0 || to.Length == 0) return;

        _dictionary.Add(from, to);
        // Eine Regel wirkt nur bei aktivem Wörterbuch; der Nutzer hat gerade
        // eine verlangt — also sicher einschalten.
        if (!_dictionary.Enabled) _dictionary.Enabled = true;

        // Diesen Eintrag gleich mitkorrigieren, damit der Verlauf die Korrektur zeigt.
        var corrected = new TextReplacementService()
            .Apply([new ReplacementRule(Guid.NewGuid(), from, to)], _entry.Text);
        _history.Update(_entry.Id, corrected);

        DialogResult = true;
    }
}
