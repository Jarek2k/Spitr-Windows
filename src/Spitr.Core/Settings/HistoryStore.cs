using System.ComponentModel;

namespace Spitr.Core.Settings;

/// <summary>Ein Verlaufs-Eintrag. Id und Datum bleiben bei Korrekturen erhalten.</summary>
public sealed class HistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get; init; } = "";
    public DateTimeOffset Date { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Lokaler, löschbarer Diktat-Verlauf. Bleibt auf dem Gerät (history.json im
/// übergebenen Verzeichnis), begrenzt auf die neuesten Einträge. Aufzeichnung
/// lässt sich komplett abschalten — Privacy by Design, der Nutzer behält die
/// Kontrolle darüber, was gespeichert wird.
/// </summary>
public sealed class HistoryStore : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Liste bleibt begrenzt, damit die Datei klein bleibt.</summary>
    private const int Limit = 100;

    private readonly string _path;
    private readonly List<HistoryEntry> _entries;
    private bool _enabled;

    public HistoryStore(string storageDirectory)
    {
        _path = Path.Combine(storageDirectory, "history.json");
        var data = JsonFileStorage.Load<HistoryData>(_path);
        // Default an; reines Komfort-Feature und bleibt vollständig lokal.
        _enabled = data?.Enabled ?? true;
        _entries = data?.Entries ?? [];
    }

    /// <summary>
    /// Aus: Transkripte werden nicht aufgezeichnet. Bestehende Einträge bleiben,
    /// bis der Nutzer sie explizit löscht.
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Persist();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        }
    }

    /// <summary>Neueste zuerst.</summary>
    public IReadOnlyList<HistoryEntry> Entries => _entries;

    /// <summary>Zeichnet ein Transkript auf. No-op wenn deaktiviert oder leer.</summary>
    public void Record(string text)
    {
        var trimmed = text.Trim();
        if (!_enabled || trimmed.Length == 0) return;
        _entries.Insert(0, new HistoryEntry { Text = trimmed });
        if (_entries.Count > Limit) _entries.RemoveRange(Limit, _entries.Count - Limit);
        PersistAndNotify();
    }

    /// <summary>
    /// Ersetzt den Text eines Eintrags in place (Id und Datum bleiben). No-op,
    /// wenn der neue Text leer ist oder der Eintrag nicht mehr existiert.
    /// </summary>
    public void Update(Guid id, string newText)
    {
        var trimmed = newText.Trim();
        if (trimmed.Length == 0) return;
        var idx = _entries.FindIndex(e => e.Id == id);
        if (idx < 0) return;
        _entries[idx] = new HistoryEntry { Id = id, Text = trimmed, Date = _entries[idx].Date };
        PersistAndNotify();
    }

    public void Delete(Guid id)
    {
        _entries.RemoveAll(e => e.Id == id);
        PersistAndNotify();
    }

    public void Clear()
    {
        _entries.Clear();
        PersistAndNotify();
    }

    // MARK: - Persistenz

    private void PersistAndNotify()
    {
        Persist();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Entries)));
    }

    private void Persist() => JsonFileStorage.Save(_path, new HistoryData
    {
        Enabled = _enabled,
        Entries = _entries,
    });

    /// <summary>Serialisierungs-DTO — Felder optional, damit fehlende Keys auf Defaults fallen.</summary>
    private sealed class HistoryData
    {
        public bool? Enabled { get; set; }
        public List<HistoryEntry>? Entries { get; set; }
    }
}
