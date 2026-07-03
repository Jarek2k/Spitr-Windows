using System.Text.Json;

namespace Spitr.Core.Settings;

/// <summary>
/// Gemeinsame JSON-Persistenz aller Stores: atomares Schreiben (tmp + Rename),
/// defekte Dateien werden beiseitegelegt statt die App zu reißen.
/// </summary>
public static class JsonFileStorage
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// Lädt <typeparamref name="T"/> aus <paramref name="path"/>. Fehlende Datei →
    /// null. Defekte Datei → wird nach "&lt;name&gt;.corrupt" verschoben und null
    /// geliefert, damit der Store sauber mit Defaults startet statt zu crashen.
    /// </summary>
    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            QuarantineCorruptFile(path);
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Schreibt atomar: erst in eine .tmp-Datei, dann Rename über das Ziel.</summary>
    public static void Save<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
        File.Move(tmp, path, overwrite: true);
    }

    private static void QuarantineCorruptFile(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
            // Bestmöglich — schlimmstenfalls bleibt die defekte Datei liegen und
            // wird beim nächsten Save überschrieben.
        }
    }
}
