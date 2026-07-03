namespace Spitr.Core.Diagnostics;

/// <summary>
/// Leichte Logging-Fassade, überall statt direkter LogStore-Aufrufe verwendet.
/// Jeder Aufruf reicht die Zeile an den prozessweiten <see cref="Target"/>-Store
/// weiter; ohne gesetztes Target (Unit-Tests anderer Module) ist alles ein
/// No-Op — es wird nie eine Datei angefasst.
///
/// Jede Nachricht ist „public" im Sinne des Originals: Spitr loggt
/// ausschließlich Events, Timings und Zähler, NIEMALS diktierten Text. Diese
/// harte Regel gilt für jeden Aufrufer — nichts Inhaltliches in die message
/// interpolieren; genau sie garantiert, dass die Log-Datei privacy-sicher bleibt.
///
/// Abweichung vom Swift-Original: das Spiegeln nach os.Logger entfällt — .NET
/// hat kein Unified-Log-Pendant, die LogStore-Datei ist der einzige Sink.
/// </summary>
public sealed class DiagLog(string category)
{
    private static LogStore? _target;
    private static bool _verbose;

    /// <summary>
    /// Prozessweiter Ziel-Store. Setzt die App beim Start (und Tests bei
    /// Bedarf); null → alle Aufrufe sind No-Ops.
    /// </summary>
    public static LogStore? Target
    {
        get => Volatile.Read(ref _target);
        set
        {
            Volatile.Write(ref _target, value);
            value?.SetVerbose(_verbose);
        }
    }

    /// <summary>
    /// Globaler Verbose-Schalter (aus den Settings). Wie im Original steuert er
    /// nur das periodische Ressourcen-Sampling des Stores, nicht welche Level
    /// geschrieben werden — Inhalte werden so oder so nie geloggt.
    /// </summary>
    public static bool Verbose
    {
        get => _verbose;
        set
        {
            _verbose = value;
            Target?.SetVerbose(value);
        }
    }

    public void Info(string message) => Target?.Record(category, "INF", message);

    public void Notice(string message) => Target?.Record(category, "NOT", message);

    public void Warning(string message) => Target?.Record(category, "WRN", message);

    public void Error(string message) => Target?.Record(category, "ERR", message);

    public void Debug(string message) => Target?.Record(category, "DBG", message);
}
