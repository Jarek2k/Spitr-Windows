using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Spitr.Core.Diagnostics;
using Spitr.Core.Settings;

namespace Spitr.App.SettingsUi;

/// <summary>
/// Der Tab „Diagnose" — Port von DiagnosticsSettingsView.swift. Der
/// Verbose-Schalter bindet an settings.VerboseLogging (App.xaml.cs spiegelt
/// das bereits nach DiagLog.Verbose); der Button öffnet den Log-Ordner im
/// Explorer.
/// </summary>
public partial class DiagnosticsSettingsTab : UserControl
{
    private static readonly DiagLog Log = new("settings-ui");

    private readonly string _logFolder;

    public DiagnosticsSettingsTab(SettingsStore settings)
    {
        InitializeComponent();
        DataContext = settings;

        // Der laufende LogStore kennt seinen Ordner; der Fallback entspricht
        // dem Pfad, den App.xaml.cs beim Start verdrahtet.
        _logFolder = DiagLog.Target?.Folder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spitr", "logs");
        LogPathLabel.Text = _logFolder;
    }

    private void OnOpenLogFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            // Erst flushen, damit der Nutzer im Explorer den aktuellen Stand sieht.
            DiagLog.Target?.Flush();
            Directory.CreateDirectory(_logFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_logFolder}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning($"open log folder failed: {ex.GetType().Name}");
        }
    }
}
