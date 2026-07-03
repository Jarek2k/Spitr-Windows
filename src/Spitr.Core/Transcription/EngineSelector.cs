namespace Spitr.Core.Transcription;

/// <summary>
/// Die verfügbaren Engines. v1 ist Whisper-only (Windows hat kein brauchbares
/// On-Device-Äquivalent zu Apple Speech); der Seam bleibt, damit eine zweite
/// Engine später genau einen Ordner kostet.
/// </summary>
public enum EngineKind
{
    Whisper,
}

/// <summary>Baut die konkrete Engine — einziger Ort, der Implementierungen kennt.</summary>
public sealed class EngineSelector(string modelsDirectory)
{
    public static EngineKind DefaultKind => EngineKind.Whisper;

    public ITranscriptionEngine MakeEngine(EngineKind kind, string whisperModel) => kind switch
    {
        EngineKind.Whisper => new WhisperEngine(modelsDirectory, whisperModel),
        _ => new WhisperEngine(modelsDirectory, whisperModel),
    };
}
