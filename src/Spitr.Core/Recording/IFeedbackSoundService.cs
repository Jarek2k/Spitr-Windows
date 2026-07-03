using Spitr.Core.Feedback;

namespace Spitr.Core.Recording;

/// <summary>
/// Kurze Audio-Cues für Aufnahmebereitschaft und Abschluss. Die Synthese der
/// Samples liegt plattformneutral in <see cref="ChimeSynthesizer"/>; das
/// Abspielen (WASAPI) in Spitr.App.
/// </summary>
public interface IFeedbackSoundService
{
    void PlayReady(ReadyChimeStyle style);

    void PlayDone();
}
