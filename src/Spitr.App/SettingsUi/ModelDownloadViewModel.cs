using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Spitr.Core.Diagnostics;
using Spitr.Core.Transcription;

namespace Spitr.App.SettingsUi;

/// <summary>
/// UI-Zustand rund um den Whisper-Modell-Download: „liegt das Modell lokal?",
/// laufender Fortschritt und letzter Fehler. Dünner Wrapper um den
/// <see cref="ModelDownloader"/> aus Core, gedacht für Settings- und
/// Onboarding-UI. Auf dem UI-Thread erzeugen und benutzen: das
/// <see cref="Progress{T}"/> wird im Konstruktor-Kontext angelegt und marshallt
/// die Fortschritts-Callbacks damit automatisch auf den Dispatcher.
/// Fehler werfen nicht, sie landen in <see cref="Error"/>.
/// </summary>
public sealed class ModelDownloadViewModel : INotifyPropertyChanged
{
    private static readonly DiagLog Log = new("model-download");

    public event PropertyChangedEventHandler? PropertyChanged;

    private readonly string _modelsDirectory;

    /// <summary>Im UI-Kontext erzeugt — Reports laufen damit über den Dispatcher.</summary>
    private readonly Progress<double> _progressSink;

    /// <summary>Laufender (bzw. zuletzt gestarteter) Download, für Idempotenz.</summary>
    private string? _inFlightId;
    private Task? _inFlightTask;

    public ModelDownloadViewModel(string modelsDirectory)
    {
        _modelsDirectory = modelsDirectory;
        _progressSink = new Progress<double>(p => Progress = p);
    }

    /// <summary>Ob die Modell-Datei lokal liegt. Unbekannte IDs gelten als nicht geladen.</summary>
    public bool IsDownloaded(string modelId) =>
        WhisperModelCatalog.SelectableModels.FirstOrDefault(m => m.Id == modelId) is { } model
        && File.Exists(Path.Combine(_modelsDirectory, model.FileName));

    private bool _isDownloading;
    public bool IsDownloading
    {
        get => _isDownloading;
        private set => Set(ref _isDownloading, value);
    }

    private double _progress;
    /// <summary>Fortschritt 0…1 des laufenden Downloads.</summary>
    public double Progress
    {
        get => _progress;
        private set => Set(ref _progress, value);
    }

    private string? _error;
    /// <summary>Meldung des letzten fehlgeschlagenen Downloads; null, wenn alles gut ist.</summary>
    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    /// <summary>
    /// Stellt sicher, dass das Modell lokal liegt (idempotent): vorhandene Datei
    /// → sofort fertig; derselbe Download läuft schon → dieselbe Task. Ein
    /// Wechsel auf ein anderes Modell reiht sich hinter dem laufenden Download
    /// ein, damit nie zwei Downloads um Progress/IsDownloading konkurrieren.
    /// </summary>
    public Task EnsureDownloadedAsync(string modelId)
    {
        if (WhisperModelCatalog.SelectableModels.FirstOrDefault(m => m.Id == modelId) is not { } model)
        {
            return Task.CompletedTask;
        }
        if (_inFlightId == modelId && _inFlightTask is { } running) return running;
        if (IsDownloaded(modelId)) return Task.CompletedTask;

        var task = RunAsync(model, _inFlightTask);
        _inFlightId = modelId;
        _inFlightTask = task;
        return task;
    }

    private async Task RunAsync(WhisperModelCatalog.ModelInfo model, Task? previous)
    {
        if (previous is not null)
        {
            // Fehler des Vorgängers gehören dem Vorgänger (stehen in Error).
            try { await previous; }
            catch { /* bewusst geschluckt */ }
        }

        IsDownloading = true;
        Progress = 0;
        Error = null;
        try
        {
            // Downloader pro Vorgang: hält seinen HttpClient nur so lange wie nötig.
            using var downloader = new ModelDownloader(_modelsDirectory);
            await downloader.DownloadAsync(model, _progressSink);
            Progress = 1;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Warning($"model download failed: {model.Id} ({ex.GetType().Name})");
        }
        finally
        {
            IsDownloading = false;
            if (_inFlightId == model.Id)
            {
                _inFlightId = null;
                _inFlightTask = null;
            }
        }
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
