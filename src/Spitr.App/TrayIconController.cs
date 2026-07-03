using System.ComponentModel;
using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using H.NotifyIcon;
using Spitr.Core.Recording;

namespace Spitr.App;

/// <summary>
/// Tray-Icon + Menü — das Pendant zur macOS-Menüleiste. Icon-Farbe folgt dem
/// Controller-Zustand (idle/recording/command/transcribing/paused/error).
/// Phase 3: minimales Menü; Einstellungen/Onboarding folgen in Phase 5.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly RecordingController _controller;
    private readonly Dictionary<TrayState, Icon> _icons = [];
    private readonly MenuItem _statusItem = new() { IsEnabled = false };
    private readonly MenuItem _pauseItem = new();
    private readonly MenuItem _reinsertItem = new();

    public event Action? QuitRequested;

    public TrayIconController(RecordingController controller)
    {
        _controller = controller;

        _pauseItem.Click += (_, _) => _controller.TogglePause();
        _reinsertItem.Click += (_, _) => _controller.ReinsertLast();
        var quitItem = new MenuItem { Header = "Spitr beenden" };
        quitItem.Click += (_, _) => QuitRequested?.Invoke();

        var menu = new ContextMenu();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_reinsertItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(quitItem);

        _icon = new TaskbarIcon
        {
            ToolTipText = "Spitr",
            ContextMenu = menu,
        };
        Refresh();
        _icon.ForceCreate();

        _controller.PropertyChanged += OnControllerChanged;
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Controller-Events kommen von Hook-/Audio-/Worker-Threads.
        Application.Current?.Dispatcher.BeginInvoke(Refresh);
    }

    private void Refresh()
    {
        var state = _controller.TrayState;
        _icon.Icon = IconFor(state);
        _icon.ToolTipText = $"Spitr — {_controller.StatusText}";
        _statusItem.Header = _controller.StatusText;
        _pauseItem.Header = _controller.Paused ? "Fortsetzen" : "Pausieren";
        _reinsertItem.Header = "Letzte Spracheingabe erneut einfügen";
        _reinsertItem.IsEnabled = _controller.LastInsertedText is not null;
    }

    /// <summary>
    /// Zustands-Icons zur Laufzeit zeichnen (gefüllter Kreis in Statusfarbe mit
    /// hellem Mikro-Punkt) — kein Asset-Handling, bis in Phase 4 ein richtiges
    /// Icon-Set kommt. Icons werden pro Zustand gecacht.
    /// </summary>
    private Icon IconFor(TrayState state)
    {
        if (_icons.TryGetValue(state, out var cached)) return cached;

        var color = state switch
        {
            TrayState.Recording => Color.FromArgb(220, 60, 60),
            TrayState.Command => Color.FromArgb(90, 120, 240),
            TrayState.Transcribing => Color.FromArgb(240, 170, 50),
            TrayState.Paused => Color.FromArgb(130, 130, 130),
            TrayState.Error => Color.FromArgb(200, 90, 30),
            _ => Color.FromArgb(70, 70, 70),
        };

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var fill = new SolidBrush(color);
            g.FillEllipse(fill, 2, 2, 28, 28);
            // „Mikro-Kapsel" als heller Kern.
            using var core = new SolidBrush(Color.FromArgb(235, 235, 235));
            g.FillEllipse(core, 11, 7, 10, 14);
            g.FillRectangle(core, 14, 21, 4, 5);
        }
        var icon = Icon.FromHandle(bitmap.GetHicon());
        _icons[state] = icon;
        return icon;
    }

    /// <summary>Kurze Tray-Benachrichtigung (z. B. „Ziel-Fenster ist erhöht …").</summary>
    public void ShowNotification(string title, string message) =>
        _icon.ShowNotification(title, message);

    public void Dispose()
    {
        _controller.PropertyChanged -= OnControllerChanged;
        _icon.Dispose();
        foreach (var icon in _icons.Values) icon.Dispose();
    }
}
