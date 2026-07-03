using System.Globalization;
using System.Text;

namespace Spitr.Core.Diagnostics;

/// <summary>
/// Persistiert Spitrs eigene Log-Ausgabe in eine rotierende Datei im übergebenen
/// Verzeichnis (in der App %LOCALAPPDATA%\Spitr\logs), damit eine mehrtägige
/// Session im Nachhinein inspizierbar ist (Erkennungs-Ausfälle, Fehler, langsam
/// wachsender Speicher), ohne dauerhaft einen Log-Viewer offen zu halten.
///
/// Zeilen werden ereignisgetrieben geschrieben: jeder DiagLog-Aufruf reicht
/// seine fertig formatierte Nachricht direkt an <see cref="Record"/> weiter.
/// Eine idle App macht keine Logging-Arbeit — nur der optionale Verbose-Sampler
/// tickt periodisch.
///
/// Harte Regel: Die App loggt NIEMALS diktierten Text (nur Längen/Timings),
/// damit die Datei privacy-sicher bleibt; der optionale Verbose-Modus ergänzt
/// nur periodische Speicher-/Thread-Samples, nie Inhalte.
///
/// Alle Datei-Operationen sind Best-Effort wie im Swift-Original (try? überall):
/// ein fehlendes oder schreibgeschütztes Verzeichnis wirft nie, es landet nur
/// still nichts auf der Platte.
///
/// Abweichung vom Swift-Original: statt einer seriellen DispatchQueue schreibt
/// der Port synchron unter einem Lock — das Log-Volumen ist winzig, die
/// Reihenfolge bleibt erhalten, und <see cref="Flush"/> wird trivial.
/// </summary>
public sealed class LogStore : IDisposable
{
    private readonly string _directory;
    private readonly string _currentPath;

    // Aktive Datei rotieren, sobald sie ~1 MB überschreiten würde; eine Handvoll
    // Archive behalten, damit eine lange Session den Ordner nie unbegrenzt
    // wachsen lässt.
    private const int MaxBytes = 1_000_000;
    private const int MaxArchives = 5;

    /// <summary>Serialisiert alle Datei- und Bookkeeping-Arbeit (siehe Klassen-Kommentar).</summary>
    private readonly object _gate = new();

    private Timer? _resourceTimer;

    public LogStore(string directory)
    {
        _directory = directory;
        _currentPath = Path.Combine(directory, "spitr.log");

        // Nur-Owner: Logs enthalten Timings/Geräte-IDs (nie Transkripte), aber
        // auf einer geteilten Maschine haben andere lokale Nutzer darin nichts
        // zu suchen. Unter Windows regelt das bereits das Nutzerprofil
        // (%LOCALAPPDATA%); unter Unix wird 0700 best-effort erzwungen — auch
        // auf einem bereits existierenden Ordner (z. B. von einem älteren Build
        // lockerer hinterlassen).
        try
        {
            Directory.CreateDirectory(directory);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(directory,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch
        {
            // Best-Effort: ohne Verzeichnis läuft jede spätere Schreiboperation
            // still ins Leere (siehe Write).
        }
    }

    /// <summary>Der Ordner mit den Log-Dateien (für „im Explorer anzeigen").</summary>
    public string Folder => _directory;

    // MARK: - Lifecycle

    /// <summary>
    /// Markiert eine neue Session. <paramref name="verbose"/> sampelt zusätzlich
    /// alle paar Minuten Speicher/Threads, damit eine lange Session eine
    /// Ressourcen-Kurve zum Inspizieren auf Lecks hat.
    /// </summary>
    public void Start(bool verbose)
    {
        lock (_gate)
        {
            AppendMeta($"session start — Spitr {AppVersion()}");
            SetResourceSampling(verbose);
        }
    }

    /// <summary>Schaltet den Ressourcen-Sampler live um, wenn der Settings-Schalter wechselt.</summary>
    public void SetVerbose(bool verbose)
    {
        lock (_gate) SetResourceSampling(verbose);
    }

    /// <summary>
    /// Kehrt zurück, wenn alle Zeilen geschrieben sind (z. B. bevor die Datei im
    /// Explorer gezeigt wird). Da der Port synchron unter dem Lock schreibt, ist
    /// hier nichts zu tun — die Methode bleibt für API-Parität mit der
    /// Queue-Barriere des Swift-Originals erhalten.
    /// </summary>
    public void Flush()
    {
        lock (_gate) { }
    }

    /// <summary>Letzter Marker beim Beenden, damit das Session-Ende nicht verloren geht.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            AppendMeta("session end");
            SetResourceSampling(false);
        }
    }

    public void Dispose()
    {
        lock (_gate) SetResourceSampling(false);
    }

    // MARK: - Log-Zeilen aufzeichnen (von DiagLog gerufen)

    /// <summary>
    /// Hängt eine fertig formatierte Log-Zeile an. Der Zeitstempel ist die
    /// Aufrufzeit. Niemals diktierten Text übergeben — nur Events, Timings und
    /// Zähler (harte Regel, siehe Klassen-Kommentar).
    /// </summary>
    public void Record(string category, string symbol, string message)
    {
        var stamp = Stamp(DateTimeOffset.UtcNow);
        lock (_gate) Write($"{stamp} {symbol} [{category}] {message}\n");
    }

    /// <summary>Schreibt eine Session-/Ressourcen-Marker-Zeile (unter dem Lock aufrufen).</summary>
    private void AppendMeta(string meta) =>
        Write($"{Stamp(DateTimeOffset.UtcNow)} ─── {meta} ───\n");

    /// <summary>ISO-8601-UTC mit Millisekunden, wie ISO8601DateFormatter im Original.</summary>
    private static string Stamp(DateTimeOffset date) =>
        date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    // MARK: - Datei-Schreiben & Rotation

    private void Write(string text)
    {
        try
        {
            var data = Encoding.UTF8.GetBytes(text);
            RotateIfNeeded(adding: data.Length);

            var options = new FileStreamOptions
            {
                Mode = FileMode.Append,
                Access = FileAccess.Write,
                Share = FileShare.Read,
            };
            // Neue Dateien nur-Owner-lesbar anlegen (0600, wie createFile im
            // Original); auf Windows greift das Nutzerprofil.
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using var stream = new FileStream(_currentPath, options);
            stream.Write(data);
        }
        catch
        {
            // Best-Effort wie in Swift (try? …): Logging darf die App nie reißen.
        }
    }

    /// <summary>
    /// Schiebt spitr.log → spitr.1.log → … → spitr.N.log und verwirft das
    /// älteste Archiv, sobald die aktive Datei die Größenkappe überschreiten würde.
    /// </summary>
    private void RotateIfNeeded(int adding)
    {
        try
        {
            var info = new FileInfo(_currentPath);
            var current = info.Exists ? info.Length : 0;
            if (current <= 0 || current + adding <= MaxBytes) return;

            File.Delete(ArchivePath(MaxArchives));
            for (var i = MaxArchives - 1; i >= 1; i--)
            {
                var from = ArchivePath(i);
                if (File.Exists(from)) File.Move(from, ArchivePath(i + 1), overwrite: true);
            }
            File.Move(_currentPath, ArchivePath(1), overwrite: true);
        }
        catch
        {
            // Best-Effort: schlägt die Rotation fehl, schreibt Write einfach weiter.
        }
    }

    private string ArchivePath(int n) => Path.Combine(_directory, $"spitr.{n}.log");

    // MARK: - Ressourcen-Sampling

    /// <summary>
    /// Vereinfachte, plattformneutrale Variante des macOS-Samplers
    /// (mach_task_basic_info/task_threads alle 5 Minuten):
    /// Environment.WorkingSet als Resident-Memory-Signal,
    /// ThreadPool.ThreadCount als Proxy für davongelaufene Async-Arbeit.
    /// Nur unter dem Lock aufrufen.
    /// </summary>
    private void SetResourceSampling(bool on)
    {
        _resourceTimer?.Dispose();
        _resourceTimer = null;
        if (!on) return;
        _resourceTimer = new Timer(_ => SampleResources(), null,
            dueTime: TimeSpan.FromSeconds(300), period: TimeSpan.FromSeconds(300));
    }

    private void SampleResources()
    {
        var mem = Environment.WorkingSet / (1024.0 * 1024.0);
        var line = FormattableString.Invariant(
            $"resources mem={mem:F1} MB threads={ThreadPool.ThreadCount}");
        lock (_gate) AppendMeta(line);
    }

    private static string AppVersion() =>
        (System.Reflection.Assembly.GetEntryAssembly() ?? typeof(LogStore).Assembly)
            .GetName().Version?.ToString() ?? "?";
}
