using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Spitr.Core.Overlay;

namespace Spitr.App.Overlay;

/// <summary>
/// Randloses, transparentes Overlay-Fenster — der Port des nicht-aktivierenden
/// NSPanels am Mac: klick-durchlässig (WS_EX_TRANSPARENT), stiehlt nie den
/// Fokus (WS_EX_NOACTIVATE, ShowActivated=False) und taucht nicht in Alt-Tab
/// auf (WS_EX_TOOLWINDOW). <see cref="Apply"/> schaltet zwischen den drei
/// Präsentationen um: Kapsel mit Aufnahme-Inhalt, Kapsel mit Befehls-Feedback,
/// randlose Waveform. Anders als am Mac trägt die Kapsel keinen Drop-Shadow —
/// ein WPF-Layered-Window kann nicht außerhalb seiner Bounds zeichnen.
/// </summary>
public partial class OverlayWindow : Window
{
    // MARK: - Größen-Presets (Port aus OverlayController.swift)

    private static readonly Size CapsuleSize = new(240, 64);
    private static readonly Size KittSize = new(150, 116);
    /// <summary>Randlose Signal-Balken: schmaler und einen Tick höher als die Kapsel.</summary>
    private static readonly Size SignalBareSize = new(168, 64);
    /// <summary>Unterkante des Overlays über dem unteren Rand des Arbeitsbereichs (Mac: minY + 72).</summary>
    private const double BottomMargin = 72;

    // MARK: - Farb-Ports (SpitrTheme.brand + SwiftUI-Systemfarben)

    private static readonly Color YellowColor = Color.FromRgb(0xFF, 0xCC, 0x00);
    private static readonly SolidColorBrush BrandBrush = Frozen(Color.FromRgb(0x4E, 0xF0, 0xA6));
    private static readonly SolidColorBrush RedBrush = Frozen(Color.FromRgb(0xFF, 0x3B, 0x30));
    private static readonly SolidColorBrush YellowBrush = Frozen(YellowColor);
    private static readonly SolidColorBrush GreenBrush = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly SolidColorBrush OrangeBrush = Frozen(Color.FromRgb(0xFF, 0x95, 0x00));

    private static readonly FontFamily GlyphFont = new("Segoe MDL2 Assets");
    private static readonly FontFamily CommandGlyphFont = new("Segoe UI Symbol");

    /// <summary>Der Renderer der aktuellen Präsentation; null beim statischen Befehls-Feedback.</summary>
    private WaveformRenderer? _activeRenderer;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    /// <summary>Wurzel des Inhalts — der Screenshot-Modus rendert sie offscreen in ein Bitmap.</summary>
    internal FrameworkElement OverlayRoot => Root;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Klick-durchlässig + nie aktivierbar + kein Alt-Tab-Eintrag: das
        // Overlay darf dem Fenster, in das wir gleich pasten, niemals den
        // Fokus wegnehmen (Pendant zu nonactivatingPanel/ignoresMouseEvents).
        var hwnd = (HWND)new WindowInteropHelper(this).Handle;
        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        exStyle |= WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                 | WINDOW_EX_STYLE.WS_EX_TRANSPARENT
                 | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
        PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)exStyle);
    }

    protected override void OnClosed(EventArgs e)
    {
        // Sicherheitsnetz: kein CompositionTarget-Abo darf das tote Fenster überleben.
        StopRenderLoop();
        base.OnClosed(e);
    }

    // MARK: - Präsentation

    /// <summary>
    /// Stellt den Overlay-Inhalt auf den aktuellen Zustand ein — der Port des
    /// RecordingOverlay-Bodys inkl. der overlayIsChromeless-Regel: die
    /// randlosen Stile (SignalReactive/SignalBare/KITT) nur im reinen Diktat;
    /// Befehlsmodus und Befehls-Feedback zeigen immer die Kapsel.
    /// </summary>
    public void Apply(WaveformStyle style, bool isCommand, string? commandFeedback, bool commandRecognized, bool isRecording)
    {
        var chromeless = style is WaveformStyle.SignalReactive or WaveformStyle.SignalBare or WaveformStyle.Kitt
                         && !isCommand && commandFeedback is null;

        if (chromeless)
        {
            SetSize(style == WaveformStyle.Kitt ? KittSize : SignalBareSize);
            CapsuleChrome.Visibility = Visibility.Collapsed;
            BareWaveform.Visibility = Visibility.Visible;
            BareWaveform.WaveformStyle = style;
            _activeRenderer = BareWaveform;
            return;
        }

        SetSize(CapsuleSize);
        CapsuleChrome.Visibility = Visibility.Visible;
        BareWaveform.Visibility = Visibility.Collapsed;

        if (commandFeedback is not null && !isRecording)
        {
            // Kurzes Befehls-Ergebnis: Häkchen (erkannt) bzw. Fragezeichen.
            RecordingContent.Visibility = Visibility.Collapsed;
            FeedbackContent.Visibility = Visibility.Visible;
            FeedbackText.Text = commandFeedback;
            FeedbackCircle.Foreground = commandRecognized ? GreenBrush : OrangeBrush;
            FeedbackSymbol.Text = commandRecognized ? "\uF13E" : "\uF142";
            _activeRenderer = null;
            return;
        }

        RecordingContent.Visibility = Visibility.Visible;
        FeedbackContent.Visibility = Visibility.Collapsed;

        if (isCommand)
        {
            // Befehlsmodus: ⌘-Glyphe in Gelb (Mac: command.circle.fill).
            ModeGlyph.FontFamily = CommandGlyphFont;
            ModeGlyph.Text = "\u2318";
            ModeGlyph.Foreground = YellowBrush;
        }
        else
        {
            // Diktat: Mikro-Glyphe — Brand-Grün beim Signal-Stil, sonst Rot.
            ModeGlyph.FontFamily = GlyphFont;
            ModeGlyph.Text = "\uE720";
            ModeGlyph.Foreground = style == WaveformStyle.Signal ? BrandBrush : RedBrush;
        }

        // Signal-Diktat nutzt die grünen zentrierten Balken; der klassische
        // Bars-Stil und der Befehlsmodus die scrollende Waveform (gelb im
        // Befehlsmodus, sonst Weiß 0.9).
        if (!isCommand && style == WaveformStyle.Signal)
        {
            CapsuleWaveform.WaveformStyle = WaveformStyle.Signal;
        }
        else
        {
            CapsuleWaveform.WaveformStyle = WaveformStyle.Bars;
            CapsuleWaveform.Tint = isCommand ? YellowColor : WaveformRenderer.DefaultBarTint;
        }
        _activeRenderer = CapsuleWaveform;
    }

    /// <summary>Unten-mittig im Arbeitsbereich des Primärmonitors (Port der Mac-Position).</summary>
    public void PositionBottomCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Bottom - Height - BottomMargin;
    }

    // MARK: - Waveform-Durchreichung

    /// <summary>Eingangspegel an den aktiven Renderer weiterreichen.</summary>
    public void PushLevel(float level) => _activeRenderer?.PushLevel(level);

    /// <summary>Waveform-Historie zurücksetzen (neue Aufnahme-Session).</summary>
    public void ResetWaveform()
    {
        CapsuleWaveform.ResetHistory();
        BareWaveform.ResetHistory();
    }

    /// <summary>Animation des aktiven Renderers starten; der inaktive wird gestoppt.</summary>
    public void StartRenderLoop()
    {
        if (!ReferenceEquals(_activeRenderer, CapsuleWaveform)) CapsuleWaveform.StopRenderLoop();
        if (!ReferenceEquals(_activeRenderer, BareWaveform)) BareWaveform.StopRenderLoop();
        _activeRenderer?.StartRenderLoop();
    }

    /// <summary>Beide Renderer stoppen — Pflicht beim Verstecken (Mac: Panel-Teardown).</summary>
    public void StopRenderLoop()
    {
        CapsuleWaveform.StopRenderLoop();
        BareWaveform.StopRenderLoop();
    }

    /// <summary>Deterministischer Animationsschritt für den Screenshot-Modus.</summary>
    internal void StepForScreenshot(double dtSeconds) => _activeRenderer?.StepForScreenshot(dtSeconds);

    // MARK: - Helfer

    private void SetSize(Size size)
    {
        Width = size.Width;
        Height = size.Height;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
