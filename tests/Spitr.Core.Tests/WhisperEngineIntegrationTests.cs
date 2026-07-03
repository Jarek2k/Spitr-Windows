using Spitr.Core.Audio;
using Spitr.Core.Transcription;

namespace Spitr.Core.Tests;

/// <summary>
/// Echte whisper.cpp-Inferenz gegen das eingecheckte Fixture — läuft lokal auf
/// dem Mac (osx-arm64-Runtime) und in beiden CI-Jobs (Modell wird gecacht).
/// Assertions per Keyword-Containment, nie Exact-Match, damit Modell-Updates
/// die CI nicht brechen.
/// </summary>
[Trait("Category", "Whisper")]
public class WhisperEngineIntegrationTests
{
    /// <summary>Stabiler Cache außerhalb des Repos, von actions/cache wiederverwendet.</summary>
    private static string ModelsDirectory =>
        Environment.GetEnvironmentVariable("SPITR_TEST_MODEL_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".spitr-test-cache", "models");

    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "german_test.wav");

    [Fact]
    public async Task TranscribesGermanFixture()
    {
        var model = WhisperModelCatalog.SelectableModels.Single(m => m.Id == "base");
        using (var downloader = new ModelDownloader(ModelsDirectory))
        {
            await downloader.DownloadAsync(model);
        }

        using var engine = new WhisperEngine(ModelsDirectory, "base");
        await engine.PrepareAsync();

        var buffer = WavFile.ReadMono16(FixturePath);
        Assert.False(buffer.IsLikelySilent); // sonst würde der Controller gar nicht transkribieren

        var text = await engine.TranscribeAsync(buffer, "de-DE", []);

        // Nur Buchstaben vergleichen: Whisper setzt je nach Modellstand mal
        // "Spracheingabe", mal "Sprach-Eingabe" — beides ist richtig erkannt.
        var letters = new string(text.ToLowerInvariant().Where(char.IsLetter).ToArray());
        Assert.Contains("test", letters);
        Assert.Contains("spracheingabe", letters);
    }

    [Fact]
    public async Task MissingModelThrowsModelMissing()
    {
        using var engine = new WhisperEngine(Path.Combine(Path.GetTempPath(), "spitr-no-models"), "base");
        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => engine.PrepareAsync());
        Assert.Equal(TranscriptionErrorKind.ModelMissing, ex.Kind);
    }
}
