using System.ComponentModel;
using System.Runtime.CompilerServices;
using Spitr.Core.Audio;
using Spitr.Core.Commands;
using Spitr.Core.Diagnostics;
using Spitr.Core.Feedback;
using Spitr.Core.Settings;
using Spitr.Core.Text;
using Spitr.Core.Transcription;

namespace Spitr.Core.Recording;

public enum RecordingState
{
    Idle,
    Recording,
    Transcribing,
    Error,
}

public enum RecordingMode
{
    Dictation,
    Command,
}

/// <summary>Zustand fürs Tray-Icon (Port des menuBarSymbol-Mappings).</summary>
public enum TrayState
{
    Idle,
    Paused,
    Recording,
    Command,
    Transcribing,
    Error,
}

/// <summary>
/// Die Statemachine, die alles verbindet: Taste gehalten → Audio aufnehmen →
/// losgelassen → transkribieren → Text einfügen. Besitzt den App-weiten Zustand
/// fürs Tray/Overlay. 1:1-Port des macOS-RecordingControllers; statt MainActor
/// serialisiert ein Lock die Zustandsübergänge (Hook-, Audio- und
/// Transkriptions-Callbacks kommen von verschiedenen Threads).
/// </summary>
public sealed class RecordingController : INotifyPropertyChanged, IDisposable
{
    private static readonly DiagLog Log = new("recording");

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly object _sync = new();

    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly DictionaryStore _dictionary;
    private readonly IHotkeyService _hotkey;
    private readonly IAudioCaptureService _audio;
    private readonly ITextInsertionService _insertion;
    private readonly IFeedbackSoundService _feedback;
    private readonly ITextReplacing _replacement;
    private readonly VoiceCommandInterpreter _interpreter = new();
    private readonly Func<EngineKind, string, ITranscriptionEngine> _engineFactory;
    private readonly RecordingTimings _timings;

    private ITranscriptionEngine _engine;
    private bool _enginePrepared;
    /// <summary>In-flight prepare(): Prewarm und eine Aufnahme teilen sich EINEN Load statt zu racen.</summary>
    private Task? _prepareTask;

    private bool _activated;

    /// <summary>
    /// True, solange die Audio-Engine für eine Aufnahme belegt ist (inkl. des
    /// kurzen Tails nach dem Loslassen). Gate für neue Aufnahmen — bewusst NICHT
    /// an State gekoppelt: ein neuer Druck darf aufnehmen, während der vorige
    /// Clip noch transkribiert.
    /// </summary>
    private bool _isCapturing;

    /// <summary>Der Bereitschaftston der laufenden Aufnahme (sein Lautsprecher-Bleed wird vorn weggeschnitten).</summary>
    private ReadyChimeStyle? _lastCaptureChimeStyle;

    private readonly record struct TranscriptionJob(AudioBuffer Buffer, RecordingMode Mode, int Session);

    /// <summary>Fertige Clips; strikt einer nach dem anderen transkribiert (Engine ist nicht re-entrant).</summary>
    private readonly List<TranscriptionJob> _jobs = [];
    private bool _draining;

    public RecordingController(
        SettingsStore settings,
        HistoryStore history,
        DictionaryStore dictionary,
        IHotkeyService hotkey,
        IAudioCaptureService audio,
        ITextInsertionService insertion,
        IFeedbackSoundService feedback,
        ITextReplacing replacement,
        Func<EngineKind, string, ITranscriptionEngine> engineFactory,
        RecordingTimings? timings = null)
    {
        _settings = settings;
        _history = history;
        _dictionary = dictionary;
        _hotkey = hotkey;
        _audio = audio;
        _insertion = insertion;
        _feedback = feedback;
        _replacement = replacement;
        _engineFactory = engineFactory;
        _timings = timings ?? RecordingTimings.Default;

        _engine = engineFactory(EngineSelector.DefaultKind, settings.WhisperModel);

        _audio.PreferredDeviceId = settings.InputDeviceId;
        _insertion.SmartSpacing = settings.SmartSpacing;

        _hotkey.Pressed += command => StartRecording(command);
        _hotkey.Released += FinishRecording;
        _hotkey.Cancelled += CancelRecording;
        _hotkey.ReinsertRequested += ReinsertLast;
        _hotkey.UpdateHoldKey(settings.HoldKey);
        _hotkey.UpdateReinsert(settings.ReinsertShortcut);

        // Ton in dem Moment, in dem das Mikro wirklich aufnimmt — der Nutzer
        // weiß, wann er sprechen kann, und verliert das erste Wort nicht.
        _audio.CaptureStarted += () =>
        {
            if (!_settings.PlayReadyChime) { _lastCaptureChimeStyle = null; return; }
            var style = _settings.ReadyChimeStyle;
            _lastCaptureChimeStyle = style;
            _feedback.PlayReady(style);
        };
        _audio.LevelChanged += level => InputLevel = level;

        _settings.PropertyChanged += OnSettingChanged;
        Paused = settings.IsPaused;
    }

    // MARK: - Beobachtbarer Zustand

    private RecordingState _state = RecordingState.Idle;
    public RecordingState State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    private string? _errorMessage;
    /// <summary>Beschreibung des letzten Fehlers, solange State == Error.</summary>
    public string? ErrorMessage
    {
        get => _errorMessage;
        private set => Set(ref _errorMessage, value);
    }

    private RecordingMode _mode = RecordingMode.Dictation;
    /// <summary>Ob die laufende Aufnahme ein Sprachbefehl ist (steuert das Overlay).</summary>
    public RecordingMode Mode
    {
        get => _mode;
        private set => Set(ref _mode, value);
    }

    private string? _commandFeedback;
    /// <summary>Kurze Bestätigung des letzten Sprachbefehls, kurz im Overlay gezeigt.</summary>
    public string? CommandFeedback
    {
        get => _commandFeedback;
        private set => Set(ref _commandFeedback, value);
    }

    private bool _lastCommandRecognized;
    public bool LastCommandRecognized
    {
        get => _lastCommandRecognized;
        private set => Set(ref _lastCommandRecognized, value);
    }

    private string? _lastInsertedText;
    /// <summary>Letzte eingefügte Spracheingabe — unabhängig vom (abschaltbaren) Verlauf, für Re-Insert.</summary>
    public string? LastInsertedText
    {
        get => _lastInsertedText;
        private set => Set(ref _lastInsertedText, value);
    }

    private bool _paused;
    /// <summary>Spiegelt settings.IsPaused, damit Tray/Menü reaktiv aktualisieren.</summary>
    public bool Paused
    {
        get => _paused;
        private set => Set(ref _paused, value);
    }

    private float _inputLevel;
    /// <summary>Normalisierter Eingangspegel 0…1 für die Overlay-Waveform.</summary>
    public float InputLevel
    {
        get => _inputLevel;
        private set => Set(ref _inputLevel, value);
    }

    private int _sessionId;
    /// <summary>Zählt pro Aufnahmestart hoch, damit die Waveform ihre Historie zurücksetzt.</summary>
    public int SessionId
    {
        get => _sessionId;
        private set => Set(ref _sessionId, value);
    }

    // MARK: - UI-Ableitungen

    public TrayState TrayState
    {
        get
        {
            if (Paused && State == RecordingState.Idle) return TrayState.Paused;
            return State switch
            {
                RecordingState.Idle => TrayState.Idle,
                RecordingState.Recording => Mode == RecordingMode.Command ? TrayState.Command : TrayState.Recording,
                RecordingState.Transcribing => TrayState.Transcribing,
                _ => TrayState.Error,
            };
        }
    }

    public string StatusText
    {
        get
        {
            if (Paused && State == RecordingState.Idle) return "Pausiert";
            return State switch
            {
                RecordingState.Idle => "Bereit",
                RecordingState.Recording => Mode == RecordingMode.Command ? "Befehl…" : "Aufnahme läuft…",
                RecordingState.Transcribing => "Wird umgewandelt…",
                _ => $"Fehler: {ErrorMessage}",
            };
        }
    }

    /// <summary>Genau anzeigen, was aktiv ist, z. B. "Whisper · base".</summary>
    public string ActiveEngineLabel => $"{_engine.DisplayName} · {_settings.WhisperModel}";

    /// <summary>Ob es eine Spracheingabe zum Korrigieren gibt (steuert den Menüpunkt).</summary>
    public bool CanCorrectHistory => _history.Entries.Count > 0;

    /// <summary>Ob das aktuell konfigurierte Whisper-Modell lokal liegt (steuert Download-UI).</summary>
    public bool IsModelDownloaded => (_engine as WhisperEngine)?.IsModelDownloaded ?? true;

    /// <summary>Alle Sprachbefehle für die Befehle-Tab-Liste (eine Quelle mit dem Matcher).</summary>
    public IReadOnlyList<VoiceCommand> AvailableCommands =>
        _interpreter.Commands(_settings, _history, _dictionary);

    // MARK: - Aktionen (Menü/Tray)

    /// <summary>Einmalig beim Start: Hotkey scharf schalten und Engine vorwärmen.</summary>
    public void Activate()
    {
        lock (_sync)
        {
            if (_activated) return;
            _activated = true;
        }
        _hotkey.Start();
        // Prewarm, damit die erste Spracheingabe nicht auf den Kalt-Load wartet.
        _ = Task.Run(async () =>
        {
            try { await EnsurePreparedAsync().ConfigureAwait(false); }
            catch (Exception e) { Log.Warning($"prewarm failed: {e.Message}"); }
        });
    }

    /// <summary>Pause umschalten; pausiert wird Diktat ignoriert, Befehlsmodus geht weiter.</summary>
    public void TogglePause() => _settings.IsPaused = !_settings.IsPaused;

    /// <summary>Routet das Einstellungsfenster auf den Verlauf-Tab und startet die Korrektur des jüngsten Eintrags.</summary>
    public void BeginCorrectLastDictation()
    {
        var latest = _history.Entries.FirstOrDefault();
        if (latest is null) return;
        _settings.RequestedTab = SettingsTab.History;
        _settings.PendingCorrectionId = latest.Id;
    }

    /// <summary>
    /// Fügt die letzte Spracheingabe erneut ins fokussierte Feld ein — Rettung,
    /// wenn das Original im falschen Fenster landete. Der kurze Beat gibt der
    /// Ziel-App Zeit, den Fokus zurückzubekommen (Menü/Chord hat ihn gestohlen).
    /// </summary>
    public void ReinsertLast()
    {
        var text = LastInsertedText;
        if (text is null) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(_timings.ReinsertFocusDelay).ConfigureAwait(false);
            _insertion.Insert(text);
            Log.Info($"re-inserted last dictation ({text.Length} chars)");
        });
    }

    public void Dispose() => _settings.PropertyChanged -= OnSettingChanged;

    // MARK: - Settings-Reaktionen (Port der Combine-Sinks)

    private void OnSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsStore.WhisperModel):
                RebuildEngine();
                break;
            case nameof(SettingsStore.HoldKey):
                _hotkey.UpdateHoldKey(_settings.HoldKey);
                break;
            case nameof(SettingsStore.ReinsertShortcut):
                _hotkey.UpdateReinsert(_settings.ReinsertShortcut);
                break;
            case nameof(SettingsStore.SmartSpacing):
                _insertion.SmartSpacing = _settings.SmartSpacing;
                break;
            case nameof(SettingsStore.InputDeviceId):
                _audio.PreferredDeviceId = _settings.InputDeviceId;
                break;
            case nameof(SettingsStore.IsPaused):
                Paused = _settings.IsPaused;
                break;
        }
    }

    /// <summary>
    /// Baut die Engine nach einem Modellwechsel neu und wärmt sie proaktiv vor,
    /// damit der Load passiert, während der Nutzer noch in den Einstellungen ist.
    /// </summary>
    private void RebuildEngine()
    {
        lock (_sync)
        {
            (_engine as IDisposable)?.Dispose();
            _engine = _engineFactory(EngineSelector.DefaultKind, _settings.WhisperModel);
            _enginePrepared = false;
            _prepareTask = null;
        }
        Log.Info($"engine rebuilt: id={_engine.Id} model={_settings.WhisperModel}");
        RaisePropertyChanged(nameof(ActiveEngineLabel));
        _ = Task.Run(async () =>
        {
            try { await EnsurePreparedAsync().ConfigureAwait(false); }
            catch (Exception e) { Log.Warning($"prewarm after rebuild failed: {e.Message}"); }
        });
    }

    /// <summary>Lädt das Modell genau einmal; parallele Aufrufer teilen sich den in-flight Load.</summary>
    private Task EnsurePreparedAsync()
    {
        ITranscriptionEngine engine;
        lock (_sync)
        {
            if (_enginePrepared) return Task.CompletedTask;
            if (_prepareTask is { } running) return running;
            engine = _engine;
            _prepareTask = PrepareAndMark(engine);
            return _prepareTask;
        }

        async Task PrepareAndMark(ITranscriptionEngine target)
        {
            try
            {
                await target.PrepareAsync().ConfigureAwait(false);
                lock (_sync)
                {
                    // Nur als vorbereitet markieren, wenn nicht zwischenzeitlich gewechselt wurde.
                    if (ReferenceEquals(_engine, target)) _enginePrepared = true;
                }
            }
            catch
            {
                lock (_sync) { _prepareTask = null; }
                throw;
            }
        }
    }

    // MARK: - Aufnahme-Lifecycle

    private void StartRecording(bool command)
    {
        lock (_sync)
        {
            // Gate auf der Audio-Engine, nicht auf State: der vorige Clip darf
            // noch transkribieren, ohne diesen Druck zu schlucken.
            if (_isCapturing) return;
            // Pausiert: Diktat ignorieren, Befehle erlauben („weiter" per Stimme).
            if (!command && _settings.IsPaused) return;

            Mode = command ? RecordingMode.Command : RecordingMode.Dictation;
            CommandFeedback = null;
            try
            {
                _audio.Start();
                _isCapturing = true;
                InputLevel = 0;
                SessionId += 1;
                State = RecordingState.Recording;
                _hotkey.BeginCancelWatch();
            }
            catch (Exception e)
            {
                ErrorMessage = e.Message;
                State = RecordingState.Error;
                ScheduleIdleReset();
            }
        }
        RaisePropertyChanged(nameof(TrayState));
    }

    /// <summary>Bricht die laufende Aufnahme ab: Audio verwerfen, nichts transkribieren (Esc).</summary>
    private void CancelRecording()
    {
        lock (_sync)
        {
            if (State != RecordingState.Recording) return;
            _hotkey.EndCancelWatch();
            _ = _audio.Stop();
            _isCapturing = false;
            InputLevel = 0;
            Mode = RecordingMode.Dictation;
            // Nicht auf Idle fallen, wenn frühere Clips noch in der Queue stecken.
            State = _draining || _jobs.Count > 0 ? RecordingState.Transcribing : RecordingState.Idle;
        }
        RaisePropertyChanged(nameof(TrayState));
        Log.Info("recording cancelled (Esc)");
    }

    private void FinishRecording()
    {
        int session;
        RecordingMode jobMode;
        lock (_sync)
        {
            if (State != RecordingState.Recording) return;
            _hotkey.EndCancelWatch();
            InputLevel = 0;
            State = RecordingState.Transcribing;
            session = SessionId;
            jobMode = Mode;
        }
        RaisePropertyChanged(nameof(TrayState));

        _ = Task.Run(async () =>
        {
            // Kurz weiter aufnehmen, damit das letzte Wort (Eingabe-Latenz)
            // nicht abgeschnitten wird, wenn die Taste sofort losgelassen wird.
            await Task.Delay(_timings.TrailingCapture).ConfigureAwait(false);
            var buffer = _audio.Stop();
            ReadyChimeStyle? chime;
            lock (_sync)
            {
                // Audio-Engine ist wieder frei — ein neuer Druck darf aufnehmen,
                // während dieser Clip transkribiert.
                _isCapturing = false;
                chime = _lastCaptureChimeStyle;
            }

            // Der Bereitschaftston läuft beim Mikro-Start über die Lautsprecher
            // und blutet in den Aufnahmeanfang — Whisper macht daraus ein
            // "[Musik]". Das Fenster vorn wegschneiden; der Nutzer reagiert dort
            // erst auf den Ton, ein erstes Wort kann nicht verloren gehen.
            if (chime is { } style)
            {
                buffer = buffer.TrimLeading(
                    ChimeSynthesizer.ReadyChimeDuration(style) + _timings.ChimeTrimSlack.TotalSeconds);
            }
            Log.Info($"captured {buffer.Samples.Count} samples @ {buffer.SampleRate} Hz (peak {buffer.PeakDbfs:F1} dBFS)");

            // Fast-stille Clips überspringen: Whisper erfindet aus Stille einen
            // Satz. Post-Capture-Loudness-Check, KEIN VAD — das Mikro nimmt
            // weiterhin strikt nur bei gehaltener Taste auf.
            if (buffer.IsLikelySilent)
            {
                Log.Info($"clip below speech threshold, skipping transcription (mode={jobMode})");
                if (jobMode == RecordingMode.Command)
                {
                    LastCommandRecognized = false;
                    ShowCommandFeedback("Befehl nicht erkannt");
                }
                lock (_sync)
                {
                    if (_jobs.Count == 0 && !_draining && State == RecordingState.Transcribing)
                    {
                        State = RecordingState.Idle;
                    }
                }
                RaisePropertyChanged(nameof(TrayState));
                return;
            }

            lock (_sync) { _jobs.Add(new TranscriptionJob(buffer, jobMode, session)); }
            DrainTranscriptions();
        });
    }

    /// <summary>
    /// Transkribiert Clips strikt nacheinander. Rührt State nie an, solange eine
    /// neue Aufnahme läuft — das Fertigwerden eines alten Clips darf das Overlay
    /// einer frischen Aufnahme nicht verstecken.
    /// </summary>
    private void DrainTranscriptions()
    {
        TranscriptionJob job;
        lock (_sync)
        {
            if (_draining || _jobs.Count == 0) return;
            _draining = true;
            job = _jobs[0];
            _jobs.RemoveAt(0);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await EnsurePreparedAsync().ConfigureAwait(false);
                Log.Info($"transcribing with engine={_engine.Id}");
                var text = await _engine
                    .TranscribeAsync(job.Buffer, _settings.LocaleIdentifier, _settings.Vocabulary)
                    .ConfigureAwait(false);

                if (job.Mode == RecordingMode.Command)
                {
                    HandleCommand(text);
                }
                else
                {
                    var corrected = _replacement.Apply(_dictionary.ActiveRules, text);
                    var trimmed = corrected.Trim();
                    if (trimmed.Length > 0)
                    {
                        _insertion.Insert(trimmed);
                        _history.Record(trimmed);
                        LastInsertedText = trimmed;
                        if (_settings.PlayDoneChime) _feedback.PlayDone();
                        Log.Info($"inserted transcript ({trimmed.Length} chars)");
                    }
                    else
                    {
                        Log.Warning("empty transcript, nothing inserted");
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error($"transcription failed: {e.Message}");
                lock (_sync)
                {
                    // Fehler nur zeigen, wenn sonst nichts läuft — er darf weder
                    // eine frische Aufnahme noch wartende Clips überdecken.
                    if (State != RecordingState.Recording && _jobs.Count == 0)
                    {
                        ErrorMessage = e.Message;
                        State = RecordingState.Error;
                        ScheduleIdleReset();
                    }
                }
            }

            bool more;
            lock (_sync)
            {
                _draining = false;
                // Erst auf Idle setzen, wenn die Queue leer ist und weder
                // aufgenommen wird noch ein Fehler angezeigt wird.
                if (_jobs.Count == 0 && State == RecordingState.Transcribing)
                {
                    State = RecordingState.Idle;
                }
                more = _jobs.Count > 0;
            }
            RaisePropertyChanged(nameof(TrayState));
            if (more) DrainTranscriptions();
        });
    }

    /// <summary>Matcht ein gesprochenes Transkript auf einen Sprachbefehl und führt ihn aus.</summary>
    private void HandleCommand(string transcript)
    {
        var command = _interpreter.Match(transcript, _settings, _history, _dictionary);
        if (command is not null)
        {
            command.Perform();
            Log.Info($"voice command: {command.Id}");
            LastCommandRecognized = true;
            ShowCommandFeedback(command.Title);
        }
        else
        {
            Log.Info("voice command not recognized");
            LastCommandRecognized = false;
            ShowCommandFeedback("Befehl nicht erkannt");
        }
    }

    private void ShowCommandFeedback(string text)
    {
        CommandFeedback = text;
        _ = Task.Run(async () =>
        {
            await Task.Delay(_timings.CommandFeedback).ConfigureAwait(false);
            lock (_sync)
            {
                if (CommandFeedback == text) CommandFeedback = null;
            }
        });
    }

    private void ScheduleIdleReset()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(_timings.ErrorIdleReset).ConfigureAwait(false);
            lock (_sync)
            {
                if (State == RecordingState.Error)
                {
                    State = RecordingState.Idle;
                    ErrorMessage = null;
                }
            }
            RaisePropertyChanged(nameof(TrayState));
        });
    }

    // MARK: - PropertyChanged

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        RaisePropertyChanged(propertyName);
    }

    private void RaisePropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
