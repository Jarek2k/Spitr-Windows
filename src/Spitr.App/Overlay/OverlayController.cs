using System.ComponentModel;
using System.Windows;
using Spitr.Core.Recording;
using Spitr.Core.Settings;

namespace Spitr.App.Overlay;

/// <summary>
/// Besitzt das schwebende Overlay-Fenster und zeigt/versteckt es im Gleichtakt
/// mit dem Aufnahme-Zustand — der Port des Mac-OverlayControllers: sichtbar,
/// solange aufgenommen wird oder kurz ein Befehls-Ergebnis ansteht, sonst weg.
/// Verstecken baut das Fenster komplett ab (Pendant zu `panel = nil`), damit
/// der ~60-fps-Render-Loop endet statt unsichtbar weiterzuticken; das nächste
/// Zeigen baut es neu auf. Controller-Events kommen von Hook-/Audio-/Worker-
/// Threads und werden auf den Dispatcher gehoben.
/// </summary>
public sealed class OverlayController : IDisposable
{
    private readonly RecordingController _controller;
    private readonly SettingsStore _settings;
    private OverlayWindow? _window;
    /// <summary>Zuletzt gesehene Session — bei Wechsel wird die Waveform-Historie geleert (Mac: `.id(sessionID)`).</summary>
    private int _lastSessionId;

    public OverlayController(RecordingController controller, SettingsStore settings)
    {
        _controller = controller;
        _settings = settings;
        _controller.PropertyChanged += OnControllerChanged;
        _settings.PropertyChanged += OnSettingsChanged;
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Events kommen von beliebigen Threads → auf den UI-Thread heben.
        switch (e.PropertyName)
        {
            case nameof(RecordingController.State):
            case nameof(RecordingController.Mode):
            case nameof(RecordingController.CommandFeedback):
                Dispatch(Update);
                break;
            case nameof(RecordingController.InputLevel):
                Dispatch(() => _window?.PushLevel(_controller.InputLevel));
                break;
            // SessionId braucht kein eigenes Update: der Wechsel wird beim
            // nächsten Update über _lastSessionId abgeholt (State ändert sich
            // im selben Zug auf Recording).
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Stilwechsel bei sichtbarem Overlay sofort übernehmen (der Mac-View
        // beobachtet die Settings reaktiv).
        if (e.PropertyName == nameof(SettingsStore.WaveformStyle))
        {
            Dispatch(Update);
        }
    }

    /// <summary>
    /// Zeigen, solange aufgenommen wird oder ein Befehls-Feedback ansteht;
    /// sonst verstecken — die Show/Hide-Regel des Mac-Originals.
    /// </summary>
    private void Update()
    {
        var isRecording = _controller.State == RecordingState.Recording;
        var feedback = _controller.CommandFeedback;
        if (!isRecording && feedback is null)
        {
            Hide();
            return;
        }

        var window = _window ??= new OverlayWindow();
        window.Apply(
            _settings.WaveformStyle,
            isCommand: _controller.Mode == RecordingMode.Command,
            commandFeedback: feedback,
            commandRecognized: _controller.LastCommandRecognized,
            isRecording: isRecording);

        // Neue Aufnahme-Session → Historie der Waveform frisch starten.
        var session = _controller.SessionId;
        if (session != _lastSessionId)
        {
            _lastSessionId = session;
            window.ResetWaveform();
        }

        window.PositionBottomCenter();
        window.Show();
        window.StartRenderLoop();
    }

    private void Hide()
    {
        if (_window is null) return;
        // Fenster samt Inhalt abbauen — stoppt den Render-Loop garantiert.
        _window.StopRenderLoop();
        _window.Close();
        _window = null;
    }

    private static void Dispatch(Action action) =>
        Application.Current?.Dispatcher.BeginInvoke(action);

    public void Dispose()
    {
        _controller.PropertyChanged -= OnControllerChanged;
        _settings.PropertyChanged -= OnSettingsChanged;
        Hide();
    }
}
