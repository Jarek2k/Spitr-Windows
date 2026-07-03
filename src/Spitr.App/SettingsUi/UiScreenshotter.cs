using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Spitr.App.Audio;
using Spitr.App.Onboarding;
using Spitr.App.SelfTest;
using Spitr.Core.Recording;
using Spitr.Core.Settings;
using Spitr.Core.Text;

namespace Spitr.App.SettingsUi;

/// <summary>
/// CI-Modus `--screenshot-ui &lt;outdir&gt;`: rendert alle sechs Settings-Tabs und
/// das Onboarding als PNGs — das Review-Medium fürs blind entwickelte UI
/// (Screenshots landen als Artifacts und sind vom Handy prüfbar). Läuft mit
/// isolierten Stores samt Beispieldaten; echte Nutzerdaten werden nie berührt.
/// </summary>
internal static class UiScreenshotter
{
    public static async Task<int> RunAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var tempDir = Path.Combine(Path.GetTempPath(), "spitr-uishot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var settings = new SettingsStore(tempDir)
            {
                VocabularyText = "Spitr\nWhisperKit\nJarek",
            };
            var history = new HistoryStore(tempDir);
            history.Record("Dies ist ein Test der Spracheingabe mit Spitr.");
            history.Record("Bitte den Entwurf für das Meeting am Montag vorbereiten.");
            var dictionary = new DictionaryStore(tempDir);
            dictionary.Add("claude", "Claude");
            dictionary.Add("spitr", "Spitr");
            dictionary.Enabled = true;

            using var controller = new RecordingController(
                settings, history, dictionary,
                new SelfTestHotkey(),
                new SelfTestAudioCapture(new Core.Audio.AudioBuffer([], 16_000)),
                new NoopInsertion(), new SelfTestFeedback(),
                new TextReplacementService(),
                (_, m) => new Core.Transcription.WhisperEngine(tempDir, m));

            var window = new SettingsWindow(
                settings, history, dictionary, controller,
                new AudioDeviceService(), Path.Combine(tempDir, "models"));
            window.Show();

            foreach (var tab in Enum.GetValues<SettingsTab>())
            {
                settings.RequestedTab = tab;
                await LetLayoutSettle();
                Save(window, Path.Combine(outputDirectory, $"settings-{tab.ToString().ToLowerInvariant()}.png"));
            }
            window.Close();

            var onboarding = new OnboardingWindow(new SettingsStore(tempDir), Path.Combine(tempDir, "models"));
            onboarding.Show();
            await LetLayoutSettle();
            Save(onboarding, Path.Combine(outputDirectory, "onboarding-step1.png"));
            onboarding.Close();

            Console.WriteLine("[screenshot-ui] OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[screenshot-ui] FAIL: {ex}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Ein paar Frames rendern lassen, damit Layout + Bindings stehen.</summary>
    private static async Task LetLayoutSettle()
    {
        for (var i = 0; i < 3; i++)
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(80);
        }
    }

    private static void Save(Window window, string path)
    {
        if (window.Content is not FrameworkElement root) return;
        const double scale = 1.5;
        var width = (int)Math.Ceiling(root.ActualWidth * scale);
        var height = (int)Math.Ceiling(root.ActualHeight * scale);
        if (width == 0 || height == 0) return;

        // Fensterhintergrund mitrendern, sonst wären Fluent-Flächen transparent.
        var composed = new System.Windows.Controls.Border
        {
            Background = window.Background ?? Brushes.White,
            Child = new Rectangle(root),
            Width = root.ActualWidth,
            Height = root.ActualHeight,
        };
        composed.Measure(new Size(root.ActualWidth, root.ActualHeight));
        composed.Arrange(new Rect(0, 0, root.ActualWidth, root.ActualHeight));

        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(composed);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine($"[screenshot-ui] {path}");
    }

    /// <summary>Visual-Brush-Wrapper, um den lebenden Visual-Tree abzurastern.</summary>
    private sealed class Rectangle : FrameworkElement
    {
        private readonly VisualBrush _brush;

        public Rectangle(FrameworkElement source)
        {
            _brush = new VisualBrush(source) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
            Width = source.ActualWidth;
            Height = source.ActualHeight;
        }

        protected override void OnRender(DrawingContext drawingContext) =>
            drawingContext.DrawRectangle(_brush, null, new Rect(0, 0, Width, Height));
    }

    private sealed class NoopInsertion : ITextInsertionService
    {
        public bool SmartSpacing { get; set; }
        public void Insert(string text) { }
    }
}
