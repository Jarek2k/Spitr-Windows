using System.ComponentModel;

namespace Spitr.Core.Settings;

/// <summary>
/// Eine einzelne Ersetzungsregel: jedes Ganz-Wort-Vorkommen von
/// <see cref="Pattern"/> (case-insensitiv) wird zu <see cref="Replacement"/>.
/// </summary>
public sealed record ReplacementRule(Guid Id, string Pattern, string Replacement);

/// <summary>
/// Persistentes persönliches Wörterbuch: nutzerdefinierte Ersetzungsregeln, die
/// vor dem Einfügen auf ein Transkript angewendet werden. Rein lokal
/// (dictionary.json im übergebenen Verzeichnis). Lässt sich komplett abschalten
/// — das Feature ist opt-out, ohne die Regeln zu verlieren.
/// </summary>
public sealed class DictionaryStore : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly string _path;
    private readonly List<ReplacementRule> _rules;
    private bool _enabled;

    public DictionaryStore(string storageDirectory)
    {
        _path = Path.Combine(storageDirectory, "dictionary.json");
        var data = JsonFileStorage.Load<DictionaryData>(_path);
        // Default aus — erst ausprobieren, dann bewusst aktivieren.
        _enabled = data?.Enabled ?? false;
        _rules = data?.Rules ?? [];
    }

    /// <summary>
    /// Aus: keine Ersetzungen. Die Regeln bleiben erhalten, damit das
    /// Wieder-Einschalten verlustfrei ist.
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
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveRules)));
        }
    }

    public IReadOnlyList<ReplacementRule> Rules => _rules;

    /// <summary>Anzuwendende Regeln — leer, wenn deaktiviert.</summary>
    public IReadOnlyList<ReplacementRule> ActiveRules => _enabled ? _rules : [];

    /// <summary>Hängt eine leere Regel an (Editier-Platzhalter fürs UI).</summary>
    public void Add()
    {
        _rules.Add(new ReplacementRule(Guid.NewGuid(), "", ""));
        PersistAndNotify();
    }

    /// <summary>
    /// Fügt eine befüllte Regel hinzu (oder aktualisiert sie) — genutzt vom
    /// Korrektur-Flow des Verlaufs, um aus einer Korrektur eine dauerhafte
    /// Ersetzung zu machen. Existiert das Pattern bereits (case-insensitiv),
    /// wird die Regel in place aktualisiert statt dupliziert.
    /// </summary>
    public void Add(string pattern, string replacement)
    {
        var p = pattern.Trim();
        var r = replacement.Trim();
        if (p.Length == 0) return;
        var idx = _rules.FindIndex(rule =>
            string.Equals(rule.Pattern, p, StringComparison.InvariantCultureIgnoreCase));
        if (idx >= 0)
            _rules[idx] = _rules[idx] with { Pattern = p, Replacement = r };
        else
            _rules.Add(new ReplacementRule(Guid.NewGuid(), p, r));
        PersistAndNotify();
    }

    /// <summary>Ersetzt die Regel mit derselben Id. No-op, wenn sie nicht existiert.</summary>
    public void Update(ReplacementRule rule)
    {
        var idx = _rules.FindIndex(r => r.Id == rule.Id);
        if (idx < 0) return;
        _rules[idx] = rule;
        PersistAndNotify();
    }

    public void Delete(Guid id)
    {
        _rules.RemoveAll(r => r.Id == id);
        PersistAndNotify();
    }

    // MARK: - Persistenz

    private void PersistAndNotify()
    {
        Persist();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rules)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ActiveRules)));
    }

    private void Persist() => JsonFileStorage.Save(_path, new DictionaryData
    {
        Enabled = _enabled,
        Rules = _rules,
    });

    /// <summary>Serialisierungs-DTO — Felder optional, damit fehlende Keys auf Defaults fallen.</summary>
    private sealed class DictionaryData
    {
        public bool? Enabled { get; set; }
        public List<ReplacementRule>? Rules { get; set; }
    }
}
