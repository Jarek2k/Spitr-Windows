using System.Net;
using Spitr.Core.Transcription;

namespace Spitr.Core.Tests;

/// <summary>Downloader-Verhalten gegen einen Stub-Handler — kein echtes Netz.</summary>
public class ModelDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spitr-dl-" + Guid.NewGuid().ToString("N"));

    private static readonly WhisperModelCatalog.ModelInfo Model = WhisperModelCatalog.SelectableModels[0];

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage FullResponse(byte[] payload) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(payload),
    };

    [Fact]
    public async Task DownloadsAndRenamesAtomically()
    {
        var payload = new byte[100_000];
        Random.Shared.NextBytes(payload);
        using var downloader = new ModelDownloader(_dir, new StubHandler(_ => FullResponse(payload)));

        var reported = new List<double>();
        await downloader.DownloadAsync(Model, new SynchronousProgress(reported.Add));

        Assert.True(downloader.IsDownloaded(Model));
        Assert.Equal(payload, await File.ReadAllBytesAsync(downloader.PathFor(Model)));
        Assert.False(File.Exists(downloader.PathFor(Model) + ".partial"));
        Assert.Equal(1, reported[^1]);
    }

    [Fact]
    public async Task ExistingFileSkipsNetworkEntirely()
    {
        Directory.CreateDirectory(_dir);
        var handler = new StubHandler(_ => throw new InvalidOperationException("darf nicht angefragt werden"));
        using var downloader = new ModelDownloader(_dir, handler);
        await File.WriteAllBytesAsync(downloader.PathFor(Model), [1, 2, 3]);

        await downloader.DownloadAsync(Model);

        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task ResumesPartialDownloadWithRangeHeader()
    {
        Directory.CreateDirectory(_dir);
        var payload = new byte[50_000];
        Random.Shared.NextBytes(payload);
        const int already = 20_000;

        StubHandler handler = null!;
        handler = new StubHandler(request =>
        {
            Assert.Equal(already, request.Headers.Range?.Ranges.Single().From);
            var rest = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[already..]),
            };
            return rest;
        });

        using var downloader = new ModelDownloader(_dir, handler);
        await File.WriteAllBytesAsync(downloader.PathFor(Model) + ".partial", payload[..already]);

        await downloader.DownloadAsync(Model);

        Assert.Equal(payload, await File.ReadAllBytesAsync(downloader.PathFor(Model)));
    }

    [Fact]
    public async Task ServerIgnoringRangeRestartsFromScratch()
    {
        Directory.CreateDirectory(_dir);
        var payload = new byte[30_000];
        Random.Shared.NextBytes(payload);

        using var downloader = new ModelDownloader(_dir, new StubHandler(_ => FullResponse(payload)));
        await File.WriteAllBytesAsync(downloader.PathFor(Model) + ".partial", new byte[5_000]);

        await downloader.DownloadAsync(Model);

        Assert.Equal(payload, await File.ReadAllBytesAsync(downloader.PathFor(Model)));
    }

    [Fact]
    public async Task TruncatedStreamFailsAndKeepsPartialForResume()
    {
        var payload = new byte[10_000];
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload[..4_000]),
        };
        // Content-Length verspricht mehr, als der Stream liefert.
        response.Content.Headers.ContentLength = payload.Length;

        using var downloader = new ModelDownloader(_dir, new StubHandler(_ => response));

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadAsync(Model));
        Assert.False(downloader.IsDownloaded(Model));
        Assert.True(File.Exists(downloader.PathFor(Model) + ".partial"));
    }

    /// <summary>Progress&lt;T&gt; posted auf den SynchronizationContext — für Tests synchron.</summary>
    private sealed class SynchronousProgress(Action<double> handler) : IProgress<double>
    {
        public void Report(double value) => handler(value);
    }
}
