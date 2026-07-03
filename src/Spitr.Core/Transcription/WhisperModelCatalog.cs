namespace Spitr.Core.Transcription;

/// <summary>
/// Die angebotenen Whisper-GGML-Modelle (Parität zur macOS-Modellauswahl).
/// Reine Daten — Download macht der ModelDownloader, Laden die WhisperEngine.
/// </summary>
public static class WhisperModelCatalog
{
    public sealed record ModelInfo(string Id, long ApproxBytes, string Hint)
    {
        public string FileName => $"ggml-{Id}.bin";

        /// <summary>Einzige erlaubte Download-Quelle (einmalig, danach offline).</summary>
        public Uri DownloadUri => new($"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{FileName}");
    }

    /// <summary>Kleinstes zuerst — Reihenfolge ist die Anzeige-Reihenfolge im UI.</summary>
    public static IReadOnlyList<ModelInfo> SelectableModels { get; } =
    [
        new("base", 148_000_000, "schnell, klein"),
        new("small", 488_000_000, "bessere Genauigkeit, empfohlen"),
        new("large-v3", 3_100_000_000, "beste Genauigkeit, sehr groß"),
    ];

    public static string DefaultModel => "base";

    /// <summary>Saniert veraltete persistierte Modell-IDs auf den Default.</summary>
    public static bool IsSelectable(string id) => SelectableModels.Any(m => m.Id == id);
}
