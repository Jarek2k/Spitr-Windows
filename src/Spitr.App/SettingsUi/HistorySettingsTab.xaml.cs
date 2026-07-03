using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Spitr.Core.Diagnostics;
using Spitr.Core.Settings;

namespace Spitr.App.SettingsUi;

/// <summary>Anzeige-Zeile des Verlaufs: Eintrag + vorformatierter Zeitstempel.</summary>
public sealed record HistoryRow(HistoryEntry Entry, string Text, string Timestamp);

/// <summary>
/// Der Tab „Verlauf" — Port von HistorySettingsView.swift. Jede Zeile bietet
/// Kopieren/Korrigieren/Löschen; „Korrigieren" öffnet den
/// <see cref="CorrectionDialog"/>, der aus dem Fehler eine dauerhafte
/// Wörterbuch-Regel macht. Der Store feuert PropertyChanged auch von
/// Hintergrund-Threads (neue Diktate!) → Refresh über den Dispatcher.
/// </summary>
public partial class HistorySettingsTab : UserControl
{
    private static readonly DiagLog Log = new("settings-ui");

    private readonly HistoryStore _history;
    private readonly DictionaryStore _dictionary;

    public HistorySettingsTab(HistoryStore history, DictionaryStore dictionary)
    {
        _history = history;
        _dictionary = dictionary;
        InitializeComponent();
        DataContext = history;

        RefreshEntries();
        // Abo nur solange der Tab sichtbar ist — der Store überlebt das Fenster.
        Loaded += (_, _) =>
        {
            RefreshEntries();
            _history.PropertyChanged += OnHistoryChanged;
        };
        Unloaded += (_, _) => _history.PropertyChanged -= OnHistoryChanged;
    }

    /// <summary>
    /// Öffnet den Korrektur-Dialog für einen von außen angeforderten Eintrag
    /// (Tray-Menü „Letzte Spracheingabe korrigieren" → settings.PendingCorrectionId).
    /// </summary>
    public void OpenCorrection(Guid entryId, Window owner)
    {
        var entry = _history.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null) return;
        ShowCorrectionDialog(entry, owner);
    }

    private void OnHistoryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryStore.Entries))
        {
            Dispatcher.BeginInvoke(RefreshEntries);
        }
    }

    private void RefreshEntries()
    {
        var rows = _history.Entries
            .Select(e => new HistoryRow(
                e, e.Text, e.Date.LocalDateTime.ToString("g", CultureInfo.CurrentCulture)))
            .ToList();
        EntriesList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearAllButton.IsEnabled = rows.Count > 0;
    }

    private void OnClearAllClick(object sender, RoutedEventArgs e) => _history.Clear();

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        try
        {
            Clipboard.SetText(row.Entry.Text);
        }
        catch (Exception ex)
        {
            // Zwischenablage kann von anderer Software gesperrt sein — kein Drama.
            Log.Warning($"history copy failed: {ex.GetType().Name}");
        }
    }

    private void OnCorrectClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        ShowCorrectionDialog(row.Entry, Window.GetWindow(this));
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        _history.Delete(row.Entry.Id);
    }

    private void ShowCorrectionDialog(HistoryEntry entry, Window? owner)
    {
        var dialog = new CorrectionDialog(entry, _history, _dictionary);
        if (owner is not null) dialog.Owner = owner;
        dialog.ShowDialog();
    }

    /// <summary>Die Verlaufs-Zeile, zu der ein geklickter Zeilen-Button gehört.</summary>
    private static HistoryRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as HistoryRow;
}
