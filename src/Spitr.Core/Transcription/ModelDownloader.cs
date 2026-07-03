using Spitr.Core.Diagnostics;

namespace Spitr.Core.Transcription;

/// <summary>
/// Lädt ein Whisper-GGML-Modell einmalig von Hugging Face — der EINZIGE
/// erlaubte Netzwerk-Code im ganzen Projekt (der CI-Job no-network-audit
/// erzwingt das). Download in eine .partial-Datei mit Range-Resume, danach
/// atomares Rename; Fortschritt über IProgress für die Onboarding-/Settings-UI.
/// </summary>
public sealed class ModelDownloader(string modelsDirectory, HttpMessageHandler? handler = null) : IDisposable
{
    private static readonly DiagLog Log = new("model-download");

    private readonly HttpClient _http = handler is null ? new HttpClient() : new HttpClient(handler);

    public string PathFor(WhisperModelCatalog.ModelInfo model) =>
        Path.Combine(modelsDirectory, model.FileName);

    public bool IsDownloaded(WhisperModelCatalog.ModelInfo model) => File.Exists(PathFor(model));

    /// <summary>
    /// Lädt das Modell (idempotent — vorhandene Datei lädt nicht erneut).
    /// <paramref name="progress"/> bekommt 0…1; bei fortgesetztem Download
    /// entsprechend ab dem Resume-Punkt.
    /// </summary>
    public async Task DownloadAsync(
        WhisperModelCatalog.ModelInfo model,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var target = PathFor(model);
        if (File.Exists(target)) { progress?.Report(1); return; }

        Directory.CreateDirectory(modelsDirectory);
        var partial = target + ".partial";
        var resumeFrom = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, model.DownloadUri);
        if (resumeFrom > 0)
        {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(resumeFrom, null);
        }

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // Server ignoriert Range (200 statt 206) → von vorn beginnen.
        if (resumeFrom > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            resumeFrom = 0;
        }
        response.EnsureSuccessStatusCode();

        var expectedTotal = resumeFrom + (response.Content.Headers.ContentLength ?? 0);

        Log.Info($"downloading {model.Id} (resume from {resumeFrom} bytes)");
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var sink = new FileStream(
                         partial,
                         resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                         FileAccess.Write))
        {
            var buffer = new byte[1 << 16];
            long written = resumeFrom;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await sink.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                written += read;
                if (expectedTotal > 0) progress?.Report(Math.Min(1, (double)written / expectedTotal));
            }
        }

        // Validierung: exakt so groß wie angekündigt — eine abgerissene
        // Verbindung darf nie als fertiges Modell durchgehen.
        var actual = new FileInfo(partial).Length;
        if (expectedTotal > 0 && actual != expectedTotal)
        {
            throw new IOException($"Download unvollständig: {actual} von {expectedTotal} Bytes (Resume möglich).");
        }

        File.Move(partial, target, overwrite: true);
        progress?.Report(1);
        Log.Info($"downloaded {model.Id} ({actual} bytes)");
    }

    public void Dispose() => _http.Dispose();
}
