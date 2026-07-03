using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Spitr.Core.Overlay;

namespace Spitr.App.Overlay;

/// <summary>
/// Screenshot-Modus für die blinde CI-Review (Aufruf: `Spitr.exe
/// --screenshot-overlays ausgabeordner`): rendert das Overlay für jeden
/// Waveform-Stil plus das Befehls-Feedback offscreen in PNGs. Die Renderer
/// werden mit einer deterministischen |sin|-Pegelsequenz gefüttert und per
/// festen Zeitschritten animiert — kein Fenster wird gezeigt, kein Fokus
/// gebraucht (RenderTargetBitmap rendert den Visual-Tree direkt).
/// </summary>
internal static class OverlayScreenshotter
{
    /// <summary>2×-Skalierung, damit die Review-Bilder gut lesbar sind.</summary>
    private const double Scale = 2.0;

    /// <summary>Dunkler Kompositions-Hintergrund — auf purer PNG-Transparenz wären die Balken schwer lesbar.</summary>
    private static readonly SolidColorBrush Backdrop = CreateBackdrop();

    public static async Task<int> RunAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        foreach (var style in Enum.GetValues<WaveformStyle>())
        {
            var path = Path.Combine(outputDirectory, $"overlay-{style.ToString().ToLowerInvariant()}.png");
            CaptureRecording(style, path);
            Console.WriteLine($"[screenshot] {path}");
            // Dispatcher zwischen den Bildern atmen lassen (Render-Queue leeren).
            await Dispatcher.Yield(DispatcherPriority.Background);
        }

        var feedbackPath = Path.Combine(outputDirectory, "overlay-command-feedback.png");
        CaptureCommandFeedback(feedbackPath);
        Console.WriteLine($"[screenshot] {feedbackPath}");

        Console.WriteLine("[screenshot] OK");
        return 0;
    }

    /// <summary>Diktat-Aufnahme im gegebenen Stil: deterministisch füttern, animieren, capturen.</summary>
    private static void CaptureRecording(WaveformStyle style, string path)
    {
        var window = new OverlayWindow();
        try
        {
            window.Apply(style, isCommand: false, commandFeedback: null, commandRecognized: false, isRecording: true);

            // 3 s synthetischer Pegel (|sin|, endet laut) in 60-Hz-Schritten —
            // genug, damit Historie/Hüllkurven eingeschwungen sind und alle
            // Stile sichtbar ausschlagen.
            for (var i = 0; i < 180; i++)
            {
                window.PushLevel(MathF.Abs(MathF.Sin(i * 0.13f)));
                window.StepForScreenshot(1.0 / 60.0);
            }

            Capture(window, path);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Die Befehls-Feedback-Kapsel (Häkchen + Text), wie nach einem erkannten Sprachbefehl.</summary>
    private static void CaptureCommandFeedback(string path)
    {
        var window = new OverlayWindow();
        try
        {
            window.Apply(
                WaveformStyle.SignalReactive,
                isCommand: false,
                commandFeedback: "Diktat pausiert",
                commandRecognized: true,
                isRecording: false);
            Capture(window, path);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Layoutet den Overlay-Inhalt in Preset-Größe, erzwingt den Render-Pass
    /// und schreibt das Ergebnis — über dunklem Hintergrund komponiert — als PNG.
    /// </summary>
    private static void Capture(OverlayWindow window, string path)
    {
        var root = window.OverlayRoot;
        var size = new Size(window.Width, window.Height);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();
        // Ausstehende Render-Arbeit abpumpen, damit OnRender sicher gelaufen ist.
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);

        var composed = new DrawingVisual();
        using (var dc = composed.RenderOpen())
        {
            var bounds = new Rect(size);
            dc.DrawRectangle(Backdrop, null, bounds);
            dc.DrawRectangle(new VisualBrush(root), null, bounds);
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(size.Width * Scale),
            (int)Math.Ceiling(size.Height * Scale),
            96 * Scale,
            96 * Scale,
            PixelFormats.Pbgra32);
        bitmap.Render(composed);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static SolidColorBrush CreateBackdrop()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        brush.Freeze();
        return brush;
    }
}
