using NAudio.CoreAudioApi;
using NAudio.Wave;
using Spitr.Core.Audio;
using Spitr.Core.Diagnostics;
using System.Runtime.InteropServices;

namespace Spitr.App.Audio;

/// <summary>
/// WASAPI-Umsetzung von <see cref="IAudioCaptureService"/> — Portierung des
/// Verhaltensvertrags aus AudioCaptureService.swift:
///
/// <list type="bullet">
/// <item>Pro <see cref="Start"/> ein FRISCHES <see cref="WasapiCapture"/> — die
/// macOS-Lektion: ein wiederverwendetes Capture-Objekt schleppt Geräte-/
/// Formatzustand über Gerätewechsel mit und crasht (dort: „nullptr == Tap()"
/// nach HAL-Fehlern bei Bluetooth-/USB-Mikros).</item>
/// <item><see cref="CaptureStarted"/> feuert erst auf dem ersten wirklich
/// gelieferten Buffer — das Mikro nimmt dann tatsächlich auf (nach dem
/// Hardware-Warm-up), Trigger für den Bereitschaftston.</item>
/// <item>Pegelmessung pro Block über den <see cref="AdaptiveLevelMeter"/>,
/// dessen Kalibrierung Aufnahmen überdauert (nur der Envelope fällt pro Start
/// auf 0 — wie Swift startEngine()).</item>
/// </list>
///
/// Threading: <see cref="CaptureStarted"/> und <see cref="LevelChanged"/>
/// feuern auf NAudios Capture-Callback-Thread, nicht auf dem UI-Thread — der
/// RecordingController ist dafür threadsicher. Start/Stop selbst serialisiert
/// der Aufrufer (Key-Down/Key-Up laufen nie parallel).
/// </summary>
public sealed class WasapiAudioCaptureService : IAudioCaptureService
{
    private static readonly DiagLog Log = new("audio");

    /// <summary>Engine-Format wie im Original: 16 kHz mono Float.</summary>
    private const double TargetSampleRate = 16_000;

    /// <summary>
    /// WASAPI-Puffergröße in ms. Klein gewählt, damit die Callback-Blöcke in
    /// der Größenordnung des Swift-Taps liegen (1024 Frames ≈ 21 ms bei
    /// 48 kHz) und das Zeitverhalten des Level-Meters dem Original entspricht.
    /// </summary>
    private const int BufferMilliseconds = 30;

    private readonly object _gate = new();
    private readonly AdaptiveLevelMeter _meter = new();
    private readonly List<float> _samples = [];

    private WasapiCapture? _capture;
    private MMDevice? _device;
    private Resampler? _resampler;
    private ManualResetEventSlim? _stopped;
    private bool _signalledStart;
    private bool _sourceIsPcm16;

    /// <inheritdoc/>
    public event Action? CaptureStarted;

    /// <inheritdoc/>
    public event Action<float>? LevelChanged;

    /// <inheritdoc/>
    public string? PreferredDeviceId { get; set; }

    /// <inheritdoc/>
    public void Start()
    {
        lock (_gate)
        {
            if (_capture is not null)
            {
                // Wie das Swift-guard !engine.isRunning: doppelter Start ist ein No-Op.
                Log.Warning("start ignored: capture already running");
                return;
            }
        }

        var device = ResolveDevice();
        WasapiCapture? capture = null;
        try
        {
            capture = new WasapiCapture(device, useEventSync: false, audioBufferMillisecondsLength: BufferMilliseconds);

            // Shared Mode liefert das Mix-Format des Geräts — typisch IEEE-Float 32,
            // 44,1/48 kHz, 1–2 Kanäle; PCM16 kommt bei manchen Treibern vor.
            var format = capture.WaveFormat;
            var standard = format is WaveFormatExtensible extensible ? extensible.ToStandardWaveFormat() : format;
            var isPcm16 = standard.Encoding switch
            {
                WaveFormatEncoding.IeeeFloat when standard.BitsPerSample == 32 => false,
                WaveFormatEncoding.Pcm when standard.BitsPerSample == 16 => true,
                _ => throw new InvalidOperationException(
                    $"Das Mikrofon liefert ein nicht unterstütztes Aufnahmeformat ({standard.Encoding}, {standard.BitsPerSample} Bit)."),
            };

            var stopped = new ManualResetEventSlim(false);
            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                    Log.Warning($"recording stopped with error: {e.Exception.GetType().Name}");
                stopped.Set();
            };

            lock (_gate)
            {
                _samples.Clear();
                _signalledStart = false;
                // Frischer Resampler pro Aufnahme — kein Filterzustand überlebt.
                _resampler = new Resampler(standard.SampleRate, standard.Channels);
                _sourceIsPcm16 = isPcm16;
                // Nur der Envelope fällt auf 0; die selbstkalibrierte
                // Pegelspanne bleibt über Aufnahmen hinweg erhalten (Swift-Semantik).
                _meter.ResetEnvelope();
                _capture = capture;
                _device = device;
                _stopped = stopped;
            }

            capture.StartRecording();
            Log.Info($"capture start: device={(string.IsNullOrEmpty(PreferredDeviceId) ? "default" : "preferred")} in={standard.SampleRate}Hz/{standard.Channels}ch enc={standard.Encoding}");
        }
        catch (Exception ex)
        {
            CleanupFailedStart(capture, device);
            if (ex is InvalidOperationException) throw;
            Log.Error($"capture start failed: {ex.GetType().Name}");
            throw new InvalidOperationException("Die Mikrofon-Aufnahme konnte nicht gestartet werden.", ex);
        }
    }

    /// <inheritdoc/>
    public AudioBuffer Stop()
    {
        WasapiCapture? capture;
        MMDevice? device;
        ManualResetEventSlim? stopped;
        lock (_gate)
        {
            capture = _capture;
            device = _device;
            stopped = _stopped;
        }

        if (capture is null)
        {
            // Ohne laufende Aufnahme: leerer Puffer statt Fehler (Swift-Verhalten).
            return new AudioBuffer([], TargetSampleRate);
        }

        try
        {
            capture.StopRecording();
        }
        catch (Exception ex)
        {
            Log.Warning($"stop recording failed: {ex.GetType().Name}");
        }

        // Begrenzt auf den letzten Callback warten; danach liefert NAudio
        // garantiert keine Daten mehr und das Dispose ist gefahrlos.
        if (stopped?.Wait(TimeSpan.FromSeconds(1)) == false)
            Log.Warning("recording stop timed out after 1s");

        float[] result;
        lock (_gate)
        {
            if (_resampler is not null)
            {
                // Restsamples aus dem Filter spülen, damit das Aufnahmeende
                // nicht abgeschnitten wird.
                _samples.AddRange(_resampler.Flush());
                _resampler = null;
            }
            result = [.. _samples];
            _samples.Clear();
            _signalledStart = false;
            _capture = null;
            _device = null;
            _stopped = null;
        }

        capture.DataAvailable -= OnDataAvailable;
        DisposeQuietly(capture, device, stopped);

        Log.Info($"capture stop: samples={result.Length} ({result.Length / TargetSampleRate:F2}s)");
        return new AudioBuffer(result, TargetSampleRate);
    }

    /// <summary>
    /// Wählt das Aufnahmegerät: bevorzugtes Gerät per stabiler Endpoint-ID,
    /// bei jedem Problem Fallback auf den Systemstandard (Rolle Communications).
    /// Gibt es gar kein Aufnahmegerät (CI-Runner!), fliegt eine
    /// InvalidOperationException mit deutscher Meldung — sie landet im
    /// Fehlerzustand des Controllers.
    /// </summary>
    private MMDevice ResolveDevice()
    {
        using var enumerator = new MMDeviceEnumerator();

        var preferred = PreferredDeviceId;
        if (!string.IsNullOrEmpty(preferred))
        {
            try
            {
                var device = enumerator.GetDevice(preferred);
                if (device.DataFlow == DataFlow.Capture && device.State == DeviceState.Active)
                    return device;
                device.Dispose();
                Log.Warning("preferred mic not active, using system default");
            }
            catch (Exception ex)
            {
                Log.Warning($"preferred mic not found ({ex.GetType().Name}), using system default");
            }
        }

        try
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Kein Mikrofon verfügbar — es wurde kein aktives Aufnahmegerät gefunden.", ex);
        }
    }

    /// <summary>
    /// Läuft auf NAudios Capture-Thread. Konvertiert den Rohblock zu Float,
    /// resampelt auf 16 kHz mono, akkumuliert unter dem Lock und meldet
    /// Capture-Start (einmalig) sowie den Pegel pro Block.
    /// </summary>
    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;

        bool first;
        float level = 0;
        float[] mono;
        lock (_gate)
        {
            // Stop() lief bereits oder der Callback stammt von einer alten
            // Capture-Instanz — verwerfen.
            if (_resampler is null || !ReferenceEquals(sender, _capture)) return;

            // Der erste wirklich gelieferte Buffer: das Mikro ist jetzt live.
            first = !_signalledStart;
            _signalledStart = true;

            mono = _resampler.Process(ConvertToFloat(e.Buffer.AsSpan(0, e.BytesRecorded), _sourceIsPcm16));
            _samples.AddRange(mono);
            if (mono.Length > 0)
                level = _meter.Process(mono);
        }

        // Events außerhalb des Locks feuern — Handler dürfen nicht unter
        // unserem Lock laufen (Deadlock-Gefahr bei Reentranz in Stop()).
        if (first) CaptureStarted?.Invoke();
        if (mono.Length > 0) LevelChanged?.Invoke(level);
    }

    /// <summary>Rohbytes des Geräts → Float-Samples in [-1, 1].</summary>
    private static float[] ConvertToFloat(ReadOnlySpan<byte> bytes, bool pcm16)
    {
        if (!pcm16)
            return MemoryMarshal.Cast<byte, float>(bytes).ToArray();

        var shorts = MemoryMarshal.Cast<byte, short>(bytes);
        var result = new float[shorts.Length];
        for (var i = 0; i < shorts.Length; i++)
            result[i] = shorts[i] / 32768f;
        return result;
    }

    /// <summary>Räumt nach einem fehlgeschlagenen Start alles wieder ab.</summary>
    private void CleanupFailedStart(WasapiCapture? capture, MMDevice device)
    {
        lock (_gate)
        {
            if (capture is not null && ReferenceEquals(_capture, capture))
            {
                _capture = null;
                _device = null;
                _stopped?.Dispose();
                _stopped = null;
                _resampler = null;
            }
        }
        DisposeQuietly(capture, device, null);
    }

    /// <summary>Dispose darf Aufräumpfade nie mit Folgefehlern sprengen.</summary>
    private static void DisposeQuietly(WasapiCapture? capture, MMDevice? device, ManualResetEventSlim? stopped)
    {
        try { capture?.Dispose(); }
        catch (Exception ex) { Log.Warning($"capture dispose failed: {ex.GetType().Name}"); }
        try { device?.Dispose(); }
        catch (Exception ex) { Log.Warning($"device dispose failed: {ex.GetType().Name}"); }
        stopped?.Dispose();
    }
}
