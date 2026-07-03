using Spitr.Core.Audio;

namespace Spitr.Core.Transcription;

/// <summary>
/// Der eine Seam, der „welche Speech-Engine" vor dem Rest der App versteckt.
/// Alle Aufrufer hängen an diesem Interface — nie an einer konkreten Engine.
/// </summary>
public interface ITranscriptionEngine
{
    /// <summary>Stabiler Bezeichner (z. B. "whisper").</summary>
    string Id { get; }

    /// <summary>Anzeigename fürs UI.</summary>
    string DisplayName { get; }

    /// <summary>Ob die Engine auf diesem Gerät laufen kann.</summary>
    bool IsAvailable { get; }

    /// <summary>Modell laden/vorwärmen. Muss vor <see cref="TranscribeAsync"/> gelaufen sein.</summary>
    Task PrepareAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Transkribiert einen fertigen Mono-Puffer.
    /// </summary>
    /// <param name="localeIdentifier">BCP-47-Kennung der Erkennungssprache, z. B. "de-DE".</param>
    /// <param name="vocabulary">Eigennamen/Fachbegriffe als Bias-Hinweis; Engines ohne
    /// solche Hints ignorieren die Liste.</param>
    Task<string> TranscribeAsync(
        AudioBuffer audio,
        string localeIdentifier,
        IReadOnlyList<string> vocabulary,
        CancellationToken cancellationToken = default);
}

public enum TranscriptionErrorKind
{
    EngineUnavailable,
    NotPrepared,
    ModelMissing,
    Empty,
    Underlying,
}

public sealed class TranscriptionException(TranscriptionErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public TranscriptionErrorKind Kind => kind;
}
