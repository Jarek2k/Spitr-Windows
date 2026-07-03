using System.Text;
using Spitr.Core.Audio;
using Spitr.Core.Diagnostics;
using Whisper.net;

namespace Spitr.Core.Transcription;

/// <summary>
/// Whisper via Whisper.net (whisper.cpp). Läuft dank nativer Runtimes auf
/// Windows UND macOS — deshalb ist diese Engine lokal auf dem Entwicklungs-Mac
/// integrationstestbar. Erwartet ein bereits heruntergeladenes GGML-Modell
/// (Download macht der ModelDownloader mit Fortschritts-UI); ein fehlendes
/// Modell ist hier ein Fehler, kein impliziter Download.
/// </summary>
public sealed class WhisperEngine(string modelsDirectory, string modelId) : ITranscriptionEngine, IDisposable
{
    private static readonly DiagLog Log = new("whisper");

    private readonly string _modelPath =
        Path.Combine(modelsDirectory, $"ggml-{modelId}.bin");

    private WhisperFactory? _factory;

    public string Id => "whisper";

    public string DisplayName => "Whisper";

    public bool IsAvailable => true;

    /// <summary>Ob das konfigurierte Modell lokal liegt (steuert Onboarding/Download-UI).</summary>
    public bool IsModelDownloaded => File.Exists(_modelPath);

    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (_factory is not null) return;
        if (!IsModelDownloaded)
        {
            throw new TranscriptionException(
                TranscriptionErrorKind.ModelMissing,
                $"Whisper-Modell fehlt: {_modelPath}");
        }

        // Der Modell-Load ist CPU-lastig (Sekunden bei large) — nicht auf dem
        // Aufrufer-Thread blockieren, der Controller wärmt beim App-Start vor.
        var started = DateTime.UtcNow;
        try
        {
            _factory = await Task.Run(() => WhisperFactory.FromPath(_modelPath), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new TranscriptionException(
                TranscriptionErrorKind.Underlying, $"Whisper-Modell konnte nicht geladen werden: {e.Message}", e);
        }
        Log.Info($"model loaded: {modelId} in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
    }

    public async Task<string> TranscribeAsync(
        AudioBuffer audio,
        string localeIdentifier,
        IReadOnlyList<string> vocabulary,
        CancellationToken cancellationToken = default)
    {
        if (_factory is null)
        {
            throw new TranscriptionException(TranscriptionErrorKind.NotPrepared,
                "PrepareAsync wurde nicht aufgerufen.");
        }
        if (audio.Samples.Count == 0)
        {
            throw new TranscriptionException(TranscriptionErrorKind.Empty, "Kein transkribierbares Audio.");
        }

        var builder = _factory.CreateBuilder();

        // "de-DE" → "de"; leer → automatische Spracherkennung (wie im Original).
        var language = LanguageCode(localeIdentifier);
        if (language is null) builder.WithLanguageDetection();
        else builder.WithLanguage(language);

        // Vokabular als Decoder-Prompt — funktionales Äquivalent der
        // WhisperKit-Prompt-Tokens (Bias, keine Garantie).
        if (vocabulary.Count > 0)
        {
            builder.WithPrompt(" " + string.Join(", ", vocabulary));
        }

        var text = new StringBuilder();
        await using (var processor = builder.Build())
        {
            await foreach (var segment in processor.ProcessAsync(audio.RawSamples, cancellationToken)
                               .ConfigureAwait(false))
            {
                text.Append(segment.Text);
            }
        }
        return text.ToString().Trim();
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
    }

    private static string? LanguageCode(string localeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(localeIdentifier)) return null;
        var dash = localeIdentifier.IndexOf('-');
        var code = dash > 0 ? localeIdentifier[..dash] : localeIdentifier;
        return code.ToLowerInvariant();
    }
}
