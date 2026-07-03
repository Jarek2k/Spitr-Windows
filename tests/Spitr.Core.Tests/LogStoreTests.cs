using Spitr.Core.Diagnostics;

namespace Spitr.Core.Tests;

// Prüft die portierte LogStore-Semantik aus LogStore.swift: zeitgestempelte
// Zeilen mit Kategorie + Level, Rotation an der Größenkappe (max. 5 Archive),
// und die Best-Effort-Regel, dass Logging bei fehlendem/schreibgeschütztem
// Ziel niemals wirft. Dazu die DiagLog-Fassade: null-Target = No-Op, damit
// Unit-Tests anderer Module nie Dateien schreiben.
public sealed class LogStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "spitr-logstore-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        DiagLog.Target = null;
        try { Directory.Delete(_root, recursive: true); } catch { /* Best-Effort-Cleanup */ }
    }

    private string NewDir() => Path.Combine(_root, Guid.NewGuid().ToString("N"));

    private static string LogPath(string dir) => Path.Combine(dir, "spitr.log");

    [Fact]
    public void RecordWritesATimestampedLineWithCategoryAndLevel()
    {
        var dir = NewDir();
        using var store = new LogStore(dir);
        store.Record("recording", "INF", "capture start took 12 ms");
        store.Flush();

        var text = File.ReadAllText(LogPath(dir));
        Assert.Contains("INF [recording] capture start took 12 ms", text);
        // ISO-8601-UTC-Zeitstempel mit Millisekunden am Zeilenanfang.
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z ", text);
    }

    [Fact]
    public void StartAndStopWriteSessionMarkers()
    {
        var dir = NewDir();
        using var store = new LogStore(dir);
        store.Start(verbose: false);
        store.Stop();

        var text = File.ReadAllText(LogPath(dir));
        Assert.Contains("session start", text);
        Assert.Contains("session end", text);
    }

    [Fact]
    public void RotatesOnceTheSizeCapWouldBeExceeded()
    {
        var dir = NewDir();
        using var store = new LogStore(dir);

        // ~64-KB-Zeilen, bis die 1-MB-Kappe überschritten würde: die aktive
        // Datei wandert nach spitr.1.log und beginnt neu.
        var payload = new string('x', 64 * 1024);
        for (var i = 0; i < 20; i++) store.Record("test", "INF", payload);
        store.Flush();

        Assert.True(File.Exists(Path.Combine(dir, "spitr.1.log")), "Archiv spitr.1.log fehlt nach der Rotation");
        Assert.True(new FileInfo(LogPath(dir)).Length < 1_000_000, "Die aktive Datei muss unter der Kappe neu beginnen");
    }

    [Fact]
    public void KeepsAtMostFiveArchives()
    {
        var dir = NewDir();
        using var store = new LogStore(dir);

        // Genug Volumen für > 5 Rotationen (~8,5 MB): die ältesten Archive
        // fallen hinten raus, spitr.6.log darf nie entstehen.
        var payload = new string('x', 64 * 1024);
        for (var i = 0; i < 130; i++) store.Record("test", "INF", payload);
        store.Flush();

        for (var n = 1; n <= 5; n++)
            Assert.True(File.Exists(Path.Combine(dir, $"spitr.{n}.log")), $"Archiv spitr.{n}.log fehlt");
        Assert.False(File.Exists(Path.Combine(dir, "spitr.6.log")), "Mehr als 5 Archive dürfen nie existieren");
    }

    [Fact]
    public void NeverThrowsWhenTheDirectoryCannotBeCreated()
    {
        // Elternpfad ist eine Datei → CreateDirectory und jede Schreiboperation
        // schlagen fehl; alles bleibt ein stiller No-Op (Best-Effort wie Swift).
        Directory.CreateDirectory(_root);
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "keine Directory");
        var dir = Path.Combine(blocker, "logs");

        var ex = Xunit.Record.Exception(() =>
        {
            using var store = new LogStore(dir);
            store.Start(verbose: false);
            store.Record("test", "INF", "message");
            store.Flush();
            store.Stop();
        });

        Assert.Null(ex);
        Assert.False(File.Exists(LogPath(dir)));
    }

    [Fact]
    public void NeverThrowsWhenTheDirectoryVanishesAfterConstruction()
    {
        var dir = NewDir();
        using var store = new LogStore(dir);
        Directory.Delete(dir, recursive: true);

        var ex = Xunit.Record.Exception(() =>
        {
            store.Record("test", "INF", "message");
            store.Stop();
        });
        Assert.Null(ex);
    }

    [Fact]
    public void NeverThrowsWhenTheLogFileIsReadOnly()
    {
        var dir = NewDir();
        using var store = new LogStore(dir);
        store.Record("test", "INF", "before");

        // Datei schreibgeschützt (entzieht unter Unix das Write-Bit, unter
        // Windows setzt es das ReadOnly-Attribut) → Anhängen schlägt fehl,
        // Record bleibt trotzdem ein stiller No-Op.
        var info = new FileInfo(LogPath(dir)) { IsReadOnly = true };
        try
        {
            var ex = Xunit.Record.Exception(() => store.Record("test", "INF", "after"));
            Assert.Null(ex);
        }
        finally
        {
            info.IsReadOnly = false;   // sonst scheitert das Test-Cleanup unter Windows
        }
    }

    [Fact]
    public void DiagLogWithoutTargetIsANoOp()
    {
        // Kein Target (Default in Unit-Tests) → kein Wurf, keine Datei.
        DiagLog.Target = null;
        var log = new DiagLog("recording");
        var ex = Xunit.Record.Exception(() =>
        {
            log.Info("i");
            log.Notice("n");
            log.Warning("w");
            log.Error("e");
            log.Debug("d");
        });
        Assert.Null(ex);
    }

    [Fact]
    public void DiagLogWritesThroughTheProcessWideTarget()
    {
        var dir = NewDir();
        using var store = new LogStore(dir);
        DiagLog.Target = store;
        try
        {
            var log = new DiagLog("recording");
            log.Info("info line");
            log.Warning("warn line");
            log.Error("error line");
            store.Flush();

            var text = File.ReadAllText(LogPath(dir));
            Assert.Contains("INF [recording] info line", text);
            Assert.Contains("WRN [recording] warn line", text);
            Assert.Contains("ERR [recording] error line", text);
        }
        finally
        {
            DiagLog.Target = null;
        }
    }
}
