namespace Spitr.Core.Tests;

/// <summary>
/// Wegwerf-Verzeichnis pro Test — das Pendant zu den isolierten UserDefaults der
/// macOS-Tests: jeder Test startet mit leerem Storage und berührt nie echte
/// App-Daten. Räumt sich beim Dispose selbst weg.
/// </summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "spitr-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Bestmöglich — liegengebliebene Temp-Verzeichnisse räumt das OS auf.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
