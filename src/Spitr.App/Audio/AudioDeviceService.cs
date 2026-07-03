using NAudio.CoreAudioApi;
using Spitr.Core.Diagnostics;

namespace Spitr.App.Audio;

/// <summary>
/// Zählt aktive WASAPI-Aufnahmegeräte für die Mikrofon-Auswahl in den
/// Settings auf — Pendant zu AudioDeviceService.swift. Identifiziert wird über
/// <c>MMDevice.ID</c>: der stabile Endpoint-String, der Neuanstecken übersteht
/// (analog zur Core-Audio-UID auf macOS), nicht über einen flüchtigen Index.
/// </summary>
public sealed class AudioDeviceService
{
    private static readonly DiagLog Log = new("audio");

    /// <summary>Anzeige-Label für „kein bestimmtes Gerät" in den Settings.</summary>
    public static string DefaultLabel => "Systemstandard";

    /// <summary>
    /// Alle aktiven Eingabegeräte als (stabile ID, Anzeigename). Ohne Geräte
    /// oder ohne Audio-Stack (CI-Runner) kommt eine leere Liste — nie eine
    /// Exception.
    /// </summary>
    public IReadOnlyList<(string Id, string Name)> InputDevices()
    {
        var devices = new List<(string Id, string Name)>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    try
                    {
                        devices.Add((device.ID, device.FriendlyName));
                    }
                    catch (Exception ex)
                    {
                        // Ein Gerät ohne lesbaren Property-Store überspringen,
                        // statt die ganze Liste zu verlieren.
                        Log.Warning($"skipping capture device without readable name: {ex.GetType().Name}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning($"input device enumeration failed: {ex.GetType().Name}");
        }
        return devices;
    }
}
