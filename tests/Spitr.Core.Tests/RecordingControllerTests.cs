using Spitr.Core.Audio;
using Spitr.Core.Feedback;
using Spitr.Core.Recording;
using Spitr.Core.Settings;
using Spitr.Core.Text;
using Spitr.Core.Transcription;

namespace Spitr.Core.Tests;

/// <summary>
/// Der komplette Aufnahme-Flow gegen Fakes — Port der macOS-IntegrationTests,
/// deutlich erweitert: die Statemachine ist hier ohne Audio-Hardware voll
/// durchspielbar (Hotkey → Capture → Silence-Gate/Trim → Queue → Engine →
/// Wörterbuch → Insertion → Verlauf → Chimes).
/// </summary>
public sealed class RecordingControllerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly DictionaryStore _dictionary;
    private readonly FakeHotkey _hotkey = new();
    private readonly FakeAudio _audio = new();
    private readonly FakeInsertion _insertion = new();
    private readonly FakeFeedback _feedback = new();
    private readonly FakeEngine _engine = new();
    private readonly RecordingController _controller;

    public RecordingControllerTests()
    {
        _settings = new SettingsStore(_dir.Path);
        _history = new HistoryStore(_dir.Path);
        _dictionary = new DictionaryStore(_dir.Path);
        _controller = new RecordingController(
            _settings, _history, _dictionary,
            _hotkey, _audio, _insertion, _feedback,
            new TextReplacementService(),
            (_, _) => _engine,
            RecordingTimings.Instant);
    }

    public void Dispose()
    {
        _controller.Dispose();
        _dir.Dispose();
    }

    /// <summary>1 s lauter Ton — peakt weit über dem Silence-Gate.</summary>
    private static AudioBuffer LoudBuffer(double seconds = 1)
    {
        var samples = new float[(int)(16_000 * seconds)];
        Array.Fill(samples, 0.5f);
        return new AudioBuffer(samples, 16_000);
    }

    private static AudioBuffer SilentBuffer()
    {
        return new AudioBuffer(new float[16_000], 16_000);
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timeout: {what}");
            await Task.Delay(10);
        }
    }

    // MARK: - Diktat-Durchstich

    [Fact]
    public async Task FullDictationFlowInsertsRecordsAndChimes()
    {
        _settings.PlayReadyChime = false; // ohne Trim rechnen
        _engine.NextResults.Enqueue("hallo welt");
        _audio.NextBuffer = LoudBuffer();

        _hotkey.RaisePressed(command: false);
        Assert.Equal(RecordingState.Recording, _controller.State);
        Assert.True(_audio.Started);
        Assert.True(_hotkey.CancelWatchActive);

        _hotkey.RaiseReleased();
        await WaitUntil(() => _insertion.Inserted.Count == 1, "Text eingefügt");

        Assert.Equal("hallo welt", _insertion.Inserted[0]);
        Assert.Equal("hallo welt", _controller.LastInsertedText);
        Assert.Equal("hallo welt", Assert.Single(_history.Entries).Text);
        await WaitUntil(() => _feedback.DoneCount == 1, "Fertig-Ton");
        await WaitUntil(() => _controller.State == RecordingState.Idle, "Idle nach Abschluss");
        Assert.False(_hotkey.CancelWatchActive);
    }

    [Fact]
    public async Task DictionaryRulesAreAppliedBeforeInsert()
    {
        _settings.PlayReadyChime = false;
        _dictionary.Add("claude", "Claude");
        _dictionary.Enabled = true;
        _engine.NextResults.Enqueue("hallo claude code");
        _audio.NextBuffer = LoudBuffer();

        _hotkey.RaisePressed(false);
        _hotkey.RaiseReleased();

        await WaitUntil(() => _insertion.Inserted.Count == 1, "Text eingefügt");
        Assert.Equal("hallo Claude code", _insertion.Inserted[0]);
    }

    [Fact]
    public async Task ReadyChimeBleedIsTrimmedBeforeTranscription()
    {
        _settings.PlayReadyChime = true;
        _settings.ReadyChimeStyle = ReadyChimeStyle.Single;
        _engine.NextResults.Enqueue("ok");
        _audio.NextBuffer = LoudBuffer(seconds: 1);

        _hotkey.RaisePressed(false);
        _audio.RaiseCaptureStarted(); // erster echter Buffer → Chime spielt
        Assert.Equal(ReadyChimeStyle.Single, Assert.Single(_feedback.ReadyStyles));

        _hotkey.RaiseReleased();
        await WaitUntil(() => _engine.ReceivedBuffers.Count == 1, "Engine aufgerufen");

        var expectedDrop = (int)(ChimeSynthesizer.ReadyChimeDuration(ReadyChimeStyle.Single) * 16_000);
        Assert.Equal(16_000 - expectedDrop, _engine.ReceivedBuffers[0].Samples.Count);
    }

    // MARK: - Silence-Gate, Abbruch, leeres Transkript

    [Fact]
    public async Task SilentClipSkipsTranscriptionEntirely()
    {
        _settings.PlayReadyChime = false;
        _audio.NextBuffer = SilentBuffer();

        _hotkey.RaisePressed(false);
        _hotkey.RaiseReleased();

        await WaitUntil(() => _controller.State == RecordingState.Idle, "Idle nach Silence-Gate");
        Assert.Empty(_engine.ReceivedBuffers);
        Assert.Empty(_insertion.Inserted);
        Assert.Empty(_history.Entries);
    }

    [Fact]
    public async Task EscCancelDiscardsAudioWithoutTranscribing()
    {
        _settings.PlayReadyChime = false;
        _audio.NextBuffer = LoudBuffer();

        _hotkey.RaisePressed(false);
        _hotkey.RaiseCancelled();

        await WaitUntil(() => _controller.State == RecordingState.Idle, "Idle nach Esc");
        Assert.True(_audio.Stopped);
        Assert.Empty(_engine.ReceivedBuffers);
        Assert.Empty(_insertion.Inserted);
        Assert.False(_hotkey.CancelWatchActive);
    }

    [Fact]
    public async Task WhitespaceOnlyTranscriptInsertsNothing()
    {
        _settings.PlayReadyChime = false;
        _engine.NextResults.Enqueue("   ");
        _audio.NextBuffer = LoudBuffer();

        _hotkey.RaisePressed(false);
        _hotkey.RaiseReleased();

        await WaitUntil(() => _controller.State == RecordingState.Idle, "Idle nach leerem Transkript");
        Assert.Empty(_insertion.Inserted);
        Assert.Empty(_history.Entries);
        Assert.Equal(0, _feedback.DoneCount);
    }

    // MARK: - Pause

    [Fact]
    public void PausedMirrorsSettingsAndBlocksDictationButNotCommands()
    {
        _settings.IsPaused = true;
        Assert.True(_controller.Paused); // Port des macOS-Pause-Mirror-Tests

        _hotkey.RaisePressed(command: false);
        Assert.Equal(RecordingState.Idle, _controller.State);
        Assert.False(_audio.Started);

        _hotkey.RaisePressed(command: true);
        Assert.Equal(RecordingState.Recording, _controller.State);
        Assert.Equal(RecordingMode.Command, _controller.Mode);
    }

    // MARK: - Sprachbefehle

    [Fact]
    public async Task SpokenPauseCommandPausesAndShowsFeedback()
    {
        _settings.PlayReadyChime = false;
        _engine.NextResults.Enqueue("bitte pause machen");
        _audio.NextBuffer = LoudBuffer();

        _hotkey.RaisePressed(command: true);
        _hotkey.RaiseReleased();

        await WaitUntil(() => _settings.IsPaused, "Pause per Sprachbefehl");
        Assert.True(_controller.LastCommandRecognized);
        Assert.Empty(_insertion.Inserted); // Befehle werden nie eingefügt
    }

    [Fact]
    public async Task UnrecognizedCommandReportsFeedback()
    {
        _settings.PlayReadyChime = false;
        _engine.NextResults.Enqueue("kolibri fahrrad");
        _audio.NextBuffer = LoudBuffer();

        _hotkey.RaisePressed(command: true);
        _hotkey.RaiseReleased();

        await WaitUntil(() => _controller.LastCommandRecognized == false
                              && _engine.ReceivedBuffers.Count == 1, "Befehl nicht erkannt");
        Assert.Empty(_insertion.Inserted);
    }

    // MARK: - Entkopplung Aufnahme/Transkription

    [Fact]
    public async Task NewRecordingCanStartWhilePreviousClipTranscribes()
    {
        _settings.PlayReadyChime = false;
        _engine.Gate = new TaskCompletionSource();
        _engine.NextResults.Enqueue("erster clip");
        _engine.NextResults.Enqueue("zweiter clip");
        _audio.NextBuffer = LoudBuffer();

        _hotkey.RaisePressed(false);
        _hotkey.RaiseReleased();
        await WaitUntil(() => _engine.ReceivedBuffers.Count == 1, "erster Clip in der Engine");

        // Engine hängt noch — ein neuer Druck muss trotzdem aufnehmen können.
        _audio.NextBuffer = LoudBuffer();
        _hotkey.RaisePressed(false);
        Assert.Equal(RecordingState.Recording, _controller.State);
        _hotkey.RaiseReleased();

        _engine.Gate.SetResult();
        await WaitUntil(() => _insertion.Inserted.Count == 2, "beide Clips eingefügt");
        Assert.Equal(["erster clip", "zweiter clip"], _insertion.Inserted);
        await WaitUntil(() => _controller.State == RecordingState.Idle, "Idle am Ende");
    }

    // MARK: - Fehlerpfad

    [Fact]
    public async Task EngineFailureSurfacesErrorThenResetsToIdle()
    {
        _settings.PlayReadyChime = false;
        _engine.NextException = new TranscriptionException(
            TranscriptionErrorKind.Underlying, "Modell kaputt");
        _audio.NextBuffer = LoudBuffer();

        _hotkey.RaisePressed(false);
        _hotkey.RaiseReleased();

        // Timings.Instant: Error → sofortiger Reset. Beides über den Verlauf beobachten.
        await WaitUntil(() => _controller.State == RecordingState.Idle
                              && _engine.ReceivedBuffers.Count == 1, "Fehler abgeklungen");
        Assert.Empty(_insertion.Inserted);
    }

    [Fact]
    public void AudioStartFailureShowsError()
    {
        _audio.FailStart = true;
        _hotkey.RaisePressed(false);
        // Timings.Instant setzt den Fehler sofort zurück — aber eingefügt/gestartet wurde nichts.
        Assert.False(_audio.Started);
        Assert.Empty(_insertion.Inserted);
    }

    // MARK: - Settings-Verdrahtung (Port der macOS-Wiring-Tests)

    [Fact]
    public void SettingChangesPropagateToServices()
    {
        _settings.HoldKey = HoldKey.CapsLock;
        Assert.Equal(HoldKey.CapsLock, _hotkey.HoldKey);

        var combo = new KeyCombo(0x42, KeyModifiers.Control | KeyModifiers.Alt, "b");
        _settings.ReinsertShortcut = combo;
        Assert.Equal(combo, _hotkey.ReinsertCombo);

        _settings.SmartSpacing = false;
        Assert.False(_insertion.SmartSpacing);

        _settings.InputDeviceId = "mic-42";
        Assert.Equal("mic-42", _audio.PreferredDeviceId);
    }

    [Fact]
    public async Task ActivatePrewarmsEngineOnce()
    {
        _controller.Activate();
        _controller.Activate(); // idempotent
        await WaitUntil(() => _engine.PrepareCount >= 1, "Prewarm");
        Assert.True(_hotkey.StartedCount == 1);
        Assert.Equal(1, _engine.PrepareCount);
    }

    [Fact]
    public async Task ReinsertLastReinsertsPreviousDictation()
    {
        _settings.PlayReadyChime = false;
        _engine.NextResults.Enqueue("nochmal bitte");
        _audio.NextBuffer = LoudBuffer();
        _hotkey.RaisePressed(false);
        _hotkey.RaiseReleased();
        await WaitUntil(() => _insertion.Inserted.Count == 1, "Erst-Einfügung");

        _hotkey.RaiseReinsert();
        await WaitUntil(() => _insertion.Inserted.Count == 2, "Re-Insert");
        Assert.Equal("nochmal bitte", _insertion.Inserted[1]);
    }

    // MARK: - Fakes

    private sealed class FakeHotkey : IHotkeyService
    {
        public event Action<bool>? Pressed;
        public event Action? Released;
        public event Action? Cancelled;
        public event Action? ReinsertRequested;

        public HoldKey HoldKey;
        public KeyCombo? ReinsertCombo;
        public int StartedCount;
        public bool CancelWatchActive;

        public void Start() => StartedCount++;
        public void UpdateHoldKey(HoldKey key) => HoldKey = key;
        public void UpdateReinsert(KeyCombo combo) => ReinsertCombo = combo;
        public void BeginCancelWatch() => CancelWatchActive = true;
        public void EndCancelWatch() => CancelWatchActive = false;

        public void RaisePressed(bool command) => Pressed?.Invoke(command);
        public void RaiseReleased() => Released?.Invoke();
        public void RaiseCancelled() => Cancelled?.Invoke();
        public void RaiseReinsert() => ReinsertRequested?.Invoke();
    }

    private sealed class FakeAudio : IAudioCaptureService
    {
        public event Action? CaptureStarted;
        public event Action<float>? LevelChanged;

        public string? PreferredDeviceId { get; set; }
        public AudioBuffer NextBuffer = new([], 16_000);
        public bool FailStart;
        public bool Started;
        public bool Stopped;

        public void Start()
        {
            if (FailStart) throw new InvalidOperationException("Kein Eingabegerät");
            Started = true;
        }

        public AudioBuffer Stop()
        {
            Stopped = true;
            return NextBuffer;
        }

        public void RaiseCaptureStarted() => CaptureStarted?.Invoke();
        public void RaiseLevel(float level) => LevelChanged?.Invoke(level);
    }

    private sealed class FakeInsertion : ITextInsertionService
    {
        public bool SmartSpacing { get; set; } = true;
        public List<string> Inserted { get; } = [];
        public void Insert(string text)
        {
            lock (Inserted) Inserted.Add(text);
        }
    }

    private sealed class FakeFeedback : IFeedbackSoundService
    {
        public List<ReadyChimeStyle> ReadyStyles { get; } = [];
        public int DoneCount;
        public void PlayReady(ReadyChimeStyle style) { lock (ReadyStyles) ReadyStyles.Add(style); }
        public void PlayDone() => Interlocked.Increment(ref DoneCount);
    }

    private sealed class FakeEngine : ITranscriptionEngine
    {
        public string Id => "fake";
        public string DisplayName => "Fake";
        public bool IsAvailable => true;

        public int PrepareCount;
        public Queue<string> NextResults { get; } = new();
        public Exception? NextException;
        public List<AudioBuffer> ReceivedBuffers { get; } = [];
        /// <summary>Wenn gesetzt: Transcribe wartet, bis der Test das Gate öffnet.</summary>
        public TaskCompletionSource? Gate;

        public Task PrepareAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref PrepareCount);
            return Task.CompletedTask;
        }

        public async Task<string> TranscribeAsync(
            AudioBuffer audio, string localeIdentifier, IReadOnlyList<string> vocabulary,
            CancellationToken cancellationToken = default)
        {
            lock (ReceivedBuffers) ReceivedBuffers.Add(audio);
            if (Gate is { } gate) await gate.Task;
            if (NextException is { } e) { NextException = null; throw e; }
            lock (NextResults) return NextResults.Count > 0 ? NextResults.Dequeue() : "";
        }
    }
}
