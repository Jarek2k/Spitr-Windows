using System.ComponentModel;
using System.Runtime.CompilerServices;
using Spitr.Core.Feedback;
using Spitr.Core.Overlay;
using Spitr.Core.Transcription;

namespace Spitr.Core.Settings;

/// <summary>Die Tabs des Einstellungsfensters — hier, damit Nicht-UI-Code einen Tab anfordern kann.</summary>
public enum SettingsTab
{
    General,
    Vocabulary,
    Dictionary,
    Commands,
    History,
    Diagnostics,
}

/// <summary>
/// Nutzer-Einstellungen, persistiert als JSON (settings.json im übergebenen
/// Verzeichnis — in der App %APPDATA%\Spitr). Single Source of Truth; der
/// RecordingController beobachtet PropertyChanged, das Settings-UI editiert.
/// Jede persistierte Property speichert beim Setzen sofort (wie UserDefaults im
/// Original). Enum-Werte liegen als Strings in der Datei; unbekannte/veraltete
/// Werte fallen einzeln auf ihren Default zurück statt die Datei zu verwerfen.
/// </summary>
public sealed class SettingsStore : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly string _path;
    private bool _loading;

    public SettingsStore(string storageDirectory)
    {
        _path = Path.Combine(storageDirectory, "settings.json");
        _loading = true;
        var data = JsonFileStorage.Load<SettingsData>(_path) ?? new SettingsData();

        _localeIdentifier = data.LocaleIdentifier ?? "de-DE";
        _holdKey = ParseEnum(data.HoldKey, HoldKey.RightCtrl);
        _whisperModel = data.WhisperModel is { } m && WhisperModelCatalog.IsSelectable(m)
            ? m
            : WhisperModelCatalog.DefaultModel;
        _inputDeviceId = data.InputDeviceId ?? "";
        _waveformStyle = ParseEnum(data.WaveformStyle, WaveformStyle.SignalReactive);
        _reinsertShortcut = data.ReinsertShortcut is { } combo && combo.ToKeyCombo() is { IsValid: true } valid
            ? valid
            : KeyCombo.ReinsertDefault;
        _vocabularyText = data.VocabularyText ?? "";
        _playReadyChime = data.PlayReadyChime ?? true;
        _readyChimeStyle = ParseEnum(data.ReadyChimeStyle, ReadyChimeStyle.Double);
        _playDoneChime = data.PlayDoneChime ?? true;
        _smartSpacing = data.SmartSpacing ?? true;
        _hasCompletedOnboarding = data.HasCompletedOnboarding ?? false;
        _verboseLogging = data.VerboseLogging ?? false;
        _loading = false;
    }

    // MARK: - Persistiert

    private string _localeIdentifier;
    /// <summary>BCP-47-Kennung der Erkennungssprache, z. B. "de-DE".</summary>
    public string LocaleIdentifier
    {
        get => _localeIdentifier;
        set => SetAndSave(ref _localeIdentifier, value);
    }

    private HoldKey _holdKey;
    /// <summary>Die Hold-to-Talk-Taste.</summary>
    public HoldKey HoldKey
    {
        get => _holdKey;
        set => SetAndSave(ref _holdKey, value);
    }

    private string _whisperModel;
    /// <summary>Whisper-Modell-ID ("base", "small", "large-v3").</summary>
    public string WhisperModel
    {
        get => _whisperModel;
        set => SetAndSave(ref _whisperModel, value);
    }

    private string _inputDeviceId;
    /// <summary>Geräte-ID des gewählten Mikrofons. Leer → Systemstandard.</summary>
    public string InputDeviceId
    {
        get => _inputDeviceId;
        set => SetAndSave(ref _inputDeviceId, value);
    }

    private WaveformStyle _waveformStyle;
    public WaveformStyle WaveformStyle
    {
        get => _waveformStyle;
        set => SetAndSave(ref _waveformStyle, value);
    }

    private KeyCombo _reinsertShortcut;
    /// <summary>Globaler Chord „Letzte Spracheingabe erneut einfügen".</summary>
    public KeyCombo ReinsertShortcut
    {
        get => _reinsertShortcut;
        set => SetAndSave(ref _reinsertShortcut, value);
    }

    private string _vocabularyText;
    /// <summary>Custom Vocabulary, ein Begriff pro Zeile (Bias-Hint für die Engine).</summary>
    public string VocabularyText
    {
        get => _vocabularyText;
        set => SetAndSave(ref _vocabularyText, value);
    }

    /// <summary>Nicht-leere, getrimmte Vokabular-Begriffe.</summary>
    public IReadOnlyList<string> Vocabulary =>
        _vocabularyText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim())
            .Where(t => t.Length > 0)
            .ToArray();

    private bool _playReadyChime;
    public bool PlayReadyChime
    {
        get => _playReadyChime;
        set => SetAndSave(ref _playReadyChime, value);
    }

    private ReadyChimeStyle _readyChimeStyle;
    public ReadyChimeStyle ReadyChimeStyle
    {
        get => _readyChimeStyle;
        set => SetAndSave(ref _readyChimeStyle, value);
    }

    private bool _playDoneChime;
    public bool PlayDoneChime
    {
        get => _playDoneChime;
        set => SetAndSave(ref _playDoneChime, value);
    }

    private bool _smartSpacing;
    public bool SmartSpacing
    {
        get => _smartSpacing;
        set => SetAndSave(ref _smartSpacing, value);
    }

    private bool _hasCompletedOnboarding;
    public bool HasCompletedOnboarding
    {
        get => _hasCompletedOnboarding;
        set => SetAndSave(ref _hasCompletedOnboarding, value);
    }

    private bool _verboseLogging;
    public bool VerboseLogging
    {
        get => _verboseLogging;
        set => SetAndSave(ref _verboseLogging, value);
    }

    // MARK: - Transient (nicht persistiert)

    private bool _isPaused;
    /// <summary>Pausiert: Diktat wird ignoriert, Befehlsmodus geht weiter (Stimme kann fortsetzen).</summary>
    public bool IsPaused
    {
        get => _isPaused;
        set => Set(ref _isPaused, value);
    }

    private SettingsTab _requestedTab = SettingsTab.General;
    /// <summary>Welchen Tab das Einstellungsfenster beim Öffnen zeigen soll.</summary>
    public SettingsTab RequestedTab
    {
        get => _requestedTab;
        set => Set(ref _requestedTab, value);
    }

    private Guid? _pendingCorrectionId;
    /// <summary>Verlaufs-Eintrag, dessen Korrektur der Verlauf-Tab starten soll (vom View konsumiert).</summary>
    public Guid? PendingCorrectionId
    {
        get => _pendingCorrectionId;
        set => Set(ref _pendingCorrectionId, value);
    }

    // MARK: - Persistenz

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetAndSave<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        if (!_loading) Save();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void Save() => JsonFileStorage.Save(_path, new SettingsData
    {
        LocaleIdentifier = _localeIdentifier,
        HoldKey = _holdKey.ToString(),
        WhisperModel = _whisperModel,
        InputDeviceId = _inputDeviceId,
        WaveformStyle = _waveformStyle.ToString(),
        ReinsertShortcut = SettingsData.KeyComboData.From(_reinsertShortcut),
        VocabularyText = _vocabularyText,
        PlayReadyChime = _playReadyChime,
        ReadyChimeStyle = _readyChimeStyle.ToString(),
        PlayDoneChime = _playDoneChime,
        SmartSpacing = _smartSpacing,
        HasCompletedOnboarding = _hasCompletedOnboarding,
        VerboseLogging = _verboseLogging,
    });

    private static TEnum ParseEnum<TEnum>(string? raw, TEnum fallback) where TEnum : struct, Enum =>
        raw is not null && Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : fallback;

    /// <summary>Serialisierungs-DTO — alle Felder optional, damit fehlende/alte Keys einzeln auf Defaults fallen.</summary>
    private sealed class SettingsData
    {
        public string? LocaleIdentifier { get; set; }
        public string? HoldKey { get; set; }
        public string? WhisperModel { get; set; }
        public string? InputDeviceId { get; set; }
        public string? WaveformStyle { get; set; }
        public KeyComboData? ReinsertShortcut { get; set; }
        public string? VocabularyText { get; set; }
        public bool? PlayReadyChime { get; set; }
        public string? ReadyChimeStyle { get; set; }
        public bool? PlayDoneChime { get; set; }
        public bool? SmartSpacing { get; set; }
        public bool? HasCompletedOnboarding { get; set; }
        public bool? VerboseLogging { get; set; }

        public sealed class KeyComboData
        {
            public ushort VirtualKey { get; set; }
            public int Modifiers { get; set; }
            public string? Label { get; set; }

            public static KeyComboData From(KeyCombo combo) => new()
            {
                VirtualKey = combo.VirtualKey,
                Modifiers = (int)combo.Modifiers,
                Label = combo.Label,
            };

            public KeyCombo? ToKeyCombo() =>
                Label is null ? null : new KeyCombo(VirtualKey, (KeyModifiers)Modifiers, Label);
        }
    }
}
