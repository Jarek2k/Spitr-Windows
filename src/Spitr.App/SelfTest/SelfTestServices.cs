using Spitr.Core.Audio;
using Spitr.Core.Recording;
using Spitr.Core.Settings;

namespace Spitr.App.SelfTest;

/// <summary>
/// Hotkey-Ersatz für den Selftest: Die CI kann keine physischen Tasten drücken,
/// also löst der Runner Press/Release programmatisch aus. Alles hinter dem
/// Seam — der RecordingController merkt keinen Unterschied.
/// </summary>
internal sealed class SelfTestHotkey : IHotkeyService
{
    public event Action<bool>? Pressed;
    public event Action? Released;
#pragma warning disable CS0067 // im Selftest nie ausgelöst, vom Interface gefordert
    public event Action? Cancelled;
    public event Action? ReinsertRequested;
#pragma warning restore CS0067

    public void Start() { }
    public void UpdateHoldKey(HoldKey key) { }
    public void UpdateReinsert(KeyCombo combo) { }
    public void BeginCancelWatch() { }
    public void EndCancelWatch() { }

    public void RaisePressed(bool command) => Pressed?.Invoke(command);
    public void RaiseReleased() => Released?.Invoke();
}

/// <summary>
/// Audio-Ersatz für den Selftest: CI-Runner haben kein Mikrofon, also liefert
/// Stop() das eingecheckte Fixture-WAV als „Aufnahme". Der Rest der Pipeline
/// (Silence-Gate, Whisper, Wörterbuch, Clipboard-Paste) ist echt.
/// </summary>
internal sealed class SelfTestAudioCapture(AudioBuffer canned) : IAudioCaptureService
{
    public event Action? CaptureStarted;
#pragma warning disable CS0067
    public event Action<float>? LevelChanged;
#pragma warning restore CS0067

    public string? PreferredDeviceId { get; set; }

    public void Start() => CaptureStarted?.Invoke();

    public AudioBuffer Stop() => canned;
}

/// <summary>Stiller Feedback-Ersatz — CI-Runner haben kein Ausgabegerät.</summary>
internal sealed class SelfTestFeedback : IFeedbackSoundService
{
    public void PlayReady(Spitr.Core.Feedback.ReadyChimeStyle style) { }
    public void PlayDone() { }
}
