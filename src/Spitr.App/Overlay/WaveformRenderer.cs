using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Spitr.Core.Overlay;

namespace Spitr.App.Overlay;

/// <summary>
/// Zeichnet alle fünf Waveform-Stile des Overlays — der Port der vier
/// SwiftUI-Canvas-Views (SignalReactive/Signal/Waveform/Kitt; SignalBare teilt
/// den Signal-Renderer). Gefüttert wird er per <see cref="PushLevel"/> mit dem
/// normalisierten Eingangspegel; die Animation tickt über
/// CompositionTarget.Rendering und wird beim Verstecken explizit abgehängt
/// (<see cref="StopRenderLoop"/>) — das Pendant zum Panel-Teardown am Mac, der
/// die ~60-fps-Timeline stoppt statt unsichtbar weiterzuticken.
/// Die Simulation läuft mit festen Tick-Raten (60/30/20 Hz wie die
/// Swift-Timer), damit die per-Tick-Glättungsfaktoren identisch wirken.
/// </summary>
public sealed class WaveformRenderer : FrameworkElement
{
    // MARK: - Farbtokens (Port von SpitrTheme + den Swift-Views)

    /// <summary>Heller Mint-Ton oben (#6BFFBC) …</summary>
    private static readonly Color BarTop = Color.FromRgb(107, 255, 188);
    /// <summary>… läuft in ein tieferes Grün unten (#2DC78C) — die Zweiton-Balken der Site.</summary>
    private static readonly Color BarBottom = Color.FromRgb(45, 199, 140);
    /// <summary>KITT-Rot (1.0, 0.13, 0.06).</summary>
    private static readonly Color KittRed = Color.FromRgb(255, 33, 15);
    /// <summary>Default-Tint der Balken-Waveform: Weiß mit 0.9 Deckkraft.</summary>
    public static readonly Color DefaultBarTint = Color.FromArgb(230, 255, 255, 255);

    // MARK: - Konstanten der Signal-Stile

    /// <summary>Die exakten nth-child-Höhen der Site — hält die Silhouette zackig.</summary>
    private static readonly float[] BaseHeights =
    [
        0.24f, 0.60f, 0.90f, 0.45f, 0.75f, 1.0f, 0.55f, 0.80f, 0.35f, 0.65f, 0.90f, 0.40f,
    ];

    /// <summary>Site-Zyklus 1.1 s, Versatz 0.06 s pro Balken.</summary>
    private const float Cycle = 1.1f;
    private const float PhaseStep = 0.06f / 1.1f * 2f * MathF.PI;
    /// <summary>Boden des reaktiven Stils: leise zeigt eine schwache, lebendige Linie statt nichts.</summary>
    private const float IdleFloor = 0.06f;

    private const int BarHistoryCount = 40;
    private const int KittSegmentsPerHalf = 7;

    // MARK: - Layer (Glow + scharfe Balken)

    /// <summary>
    /// Dünner Zeichen-Layer: delegiert OnRender an den Renderer. Zwei Instanzen
    /// bilden KITTs drawLayer-Aufbau nach — unten dieselben Balken mit echtem
    /// 4-px-Gauß-Blur (Glow), oben scharf.
    /// </summary>
    private sealed class DrawLayer : FrameworkElement
    {
        public Action<DrawingContext, Size>? Draw;
        protected override void OnRender(DrawingContext dc) => Draw?.Invoke(dc, RenderSize);
    }

    private readonly DrawLayer _glowLayer;
    private readonly DrawLayer _mainLayer;

    public WaveformRenderer()
    {
        IsHitTestVisible = false;
        _glowLayer = new DrawLayer { Draw = DrawGlow, Effect = new BlurEffect { Radius = 4 }, IsHitTestVisible = false };
        _mainLayer = new DrawLayer { Draw = DrawContent, IsHitTestVisible = false };
        AddVisualChild(_glowLayer);
        AddVisualChild(_mainLayer);
        // Sicherheitsnetz: falls das Element den Baum verlässt, ohne dass
        // StopRenderLoop gerufen wurde, nicht ewig weiterticken.
        Unloaded += (_, _) => StopRenderLoop();
    }

    protected override int VisualChildrenCount => 2;

    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _glowLayer,
        1 => _mainLayer,
        _ => throw new ArgumentOutOfRangeException(nameof(index)),
    };

    protected override Size MeasureOverride(Size availableSize)
    {
        _glowLayer.Measure(availableSize);
        _mainLayer.Measure(availableSize);
        // Füllt den zugewiesenen Platz; ohne endliche Vorgabe keine Eigengröße.
        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var bounds = new Rect(finalSize);
        _glowLayer.Arrange(bounds);
        _mainLayer.Arrange(bounds);
        return finalSize;
    }

    // MARK: - Öffentliche Steuerung

    private WaveformStyle _style = WaveformStyle.SignalReactive;
    /// <summary>Welcher der fünf Stile gezeichnet wird (SignalBare teilt den Signal-Pfad).</summary>
    public WaveformStyle WaveformStyle
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            InvalidateLayers();
        }
    }

    private Color _tint = DefaultBarTint;
    private SolidColorBrush _tintBrush = CreateFrozenBrush(DefaultBarTint);
    /// <summary>Balkenfarbe der Bars-Waveform — Gelb im Befehlsmodus hält den Modus unterscheidbar.</summary>
    public Color Tint
    {
        get => _tint;
        set
        {
            if (_tint == value) return;
            _tint = value;
            _tintBrush = CreateFrozenBrush(value);
            InvalidateLayers();
        }
    }

    /// <summary>Zuletzt gemeldeter normalisierter RMS-Pegel (0…1) vom Audio-Tap.</summary>
    private float _level;

    /// <summary>Neuen Eingangspegel melden; der nächste Simulations-Tick greift ihn ab.</summary>
    public void PushLevel(float level) => _level = Math.Clamp(level, 0f, 1f);

    /// <summary>
    /// Setzt die komplette Simulations-Historie zurück — das Pendant zum
    /// SwiftUI-`.id(sessionID)`, das die Views pro Aufnahme neu erzeugt.
    /// </summary>
    public void ResetHistory()
    {
        _level = 0;
        _phase = 0;
        _amplitude = 0;
        _envelope = 0;
        _kittCenter = 0;
        _kittOuter = 0;
        Array.Clear(_barHistory);
        _acc60 = _acc30 = _acc20 = 0;
        InvalidateLayers();
    }

    // MARK: - Render-Loop

    private bool _loopRunning;
    private long _lastTimestamp;

    /// <summary>Animation starten (abonniert CompositionTarget.Rendering, ~Bildrate).</summary>
    public void StartRenderLoop()
    {
        if (_loopRunning) return;
        _loopRunning = true;
        _lastTimestamp = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Animation stoppen — MUSS beim Verstecken passieren (Mac-Pendant: Panel-Teardown).</summary>
    public void StopRenderLoop()
    {
        if (!_loopRunning) return;
        _loopRunning = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var dt = (now - _lastTimestamp) / (double)Stopwatch.Frequency;
        _lastTimestamp = now;
        // Hänger (Debugger, Standby) nicht als riesigen Sprung nachholen.
        Step(Math.Min(dt, 0.1));
    }

    /// <summary>
    /// Deterministischer Zeitschritt für den Screenshot-Modus — dieselbe
    /// Simulation wie der Live-Loop, nur ohne CompositionTarget.
    /// </summary>
    internal void StepForScreenshot(double dtSeconds) => Step(dtSeconds);

    // MARK: - Simulation (feste Tick-Raten wie die Swift-Timer)

    /// <summary>Freilaufende Phase des Site-Ripples (Signal/Reactive).</summary>
    private float _phase;
    /// <summary>Geglättete Lautheit des Signal-Stils.</summary>
    private float _amplitude;
    /// <summary>Expandierte Lautheits-Hüllkurve des reaktiven Stils.</summary>
    private float _envelope;
    /// <summary>KITT: Mittelsäule (schnell) und Außensäulen (träger, immer identisch).</summary>
    private float _kittCenter;
    private float _kittOuter;
    /// <summary>Ringpuffer der Bars-Waveform — stetig gesampelt, damit sie wie eine Tonspur scrollt.</summary>
    private readonly float[] _barHistory = new float[BarHistoryCount];

    private double _acc60;
    private double _acc30;
    private double _acc20;

    private void Step(double dt)
    {
        var dirty = false;
        switch (_style)
        {
            case WaveformStyle.SignalReactive:
                for (_acc60 += dt; _acc60 >= 1.0 / 60; _acc60 -= 1.0 / 60) { TickReactive(); dirty = true; }
                break;
            case WaveformStyle.Signal:
            case WaveformStyle.SignalBare:
                for (_acc60 += dt; _acc60 >= 1.0 / 60; _acc60 -= 1.0 / 60) { TickSignal(); dirty = true; }
                break;
            case WaveformStyle.Bars:
                for (_acc20 += dt; _acc20 >= 0.05; _acc20 -= 0.05) { TickBars(); dirty = true; }
                break;
            case WaveformStyle.Kitt:
                for (_acc30 += dt; _acc30 >= 1.0 / 30; _acc30 -= 1.0 / 30) { TickKitt(); dirty = true; }
                break;
        }
        if (dirty) InvalidateLayers();
    }

    private void TickSignal()
    {
        _phase += 1f / 60f;
        // Lautheit mit sanfter Kurve mappen, dann glätten — schneller Attack,
        // langsamerer Release, damit es auf Sprache springt und ruhig abklingt.
        var norm = Math.Clamp((_level - 0.12f) / 0.7f, 0f, 1f);
        var target = MathF.Pow(norm, 1.3f);
        var k = target > _amplitude ? 0.5f : 0.12f;
        _amplitude += (target - _amplitude) * k;
    }

    private void TickReactive()
    {
        // Expandierte Hüllkurve: Dead-Zone schluckt Idle-Rauschen, Exponent >1
        // spreizt leise↔laut. Schneller Attack, langsamer Release.
        var norm = Math.Clamp((_level - 0.06f) / 0.82f, 0f, 1f);
        var target = MathF.Pow(norm, 1.8f);
        var k = target > _envelope ? 0.5f : 0.12f;
        _envelope += (target - _envelope) * k;

        // Lautheit treibt auch das TEMPO des Ripples: leise driftet (~0.5×),
        // laut rast (~2×) — lebendiger beim Rufen.
        var speed = 0.5f + 1.5f * _envelope;
        _phase += (1f / 60f) * speed;
    }

    private void TickBars()
    {
        // Ältesten Wert vorn raus, aktuellen Pegel hinten rein — der Scroll
        // läuft stetig, egal ob sich der Pegel ändert.
        Array.Copy(_barHistory, 1, _barHistory, 0, _barHistory.Length - 1);
        _barHistory[^1] = _level;
    }

    private void TickKitt()
    {
        // Sanfte Kurve — leise bleibt niedrig, laut hoch, aber die Mitte kommt
        // durch, damit viel Bewegung drin ist.
        var norm = Math.Clamp((_level - 0.18f) / 0.64f, 0f, 1f);
        var target = MathF.Pow(norm, 1.35f);

        // Mitte schnappt und fällt schnell; Außensäulen ziehen einen Takt nach.
        _kittCenter += (target - _kittCenter) * (target > _kittCenter ? 0.92f : 0.82f);
        _kittOuter += (target - _kittOuter) * (target > _kittOuter ? 0.62f : 0.55f);
    }

    private void InvalidateLayers()
    {
        _mainLayer.InvalidateVisual();
        _glowLayer.InvalidateVisual();
    }

    // MARK: - Zeichnen

    private void DrawContent(DrawingContext dc, Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        switch (_style)
        {
            case WaveformStyle.SignalReactive:
                DrawSignalBars(dc, size, reactive: true);
                break;
            case WaveformStyle.Signal:
            case WaveformStyle.SignalBare:
                DrawSignalBars(dc, size, reactive: false);
                break;
            case WaveformStyle.Bars:
                DrawBars(dc, size);
                break;
            case WaveformStyle.Kitt:
                DrawKitt(dc, size);
                break;
        }
    }

    /// <summary>Glow-Pass: nur KITT zeichnet hier — dieselben Balken, vom Layer-Blur weichgezeichnet.</summary>
    private void DrawGlow(DrawingContext dc, Size size)
    {
        if (_style != WaveformStyle.Kitt || size.Width <= 0 || size.Height <= 0) return;
        DrawKitt(dc, size);
    }

    /// <summary>
    /// Die Signal-Balken der Site: feste zackige Höhen × gestaffeltes
    /// scaleY .35→1, immer in Bewegung. `reactive` schaltet die Hüllkurve um:
    /// Signal hält einen hohen Boden und skaliert sanft, Reactive gated die
    /// ganze Animation über die expandierte Lautheits-Hüllkurve.
    /// </summary>
    private void DrawSignalBars(DrawingContext dc, Size size, bool reactive)
    {
        var n = BaseHeights.Length;
        var slot = size.Width / n;
        // Dünne Balken mit luftigen Lücken, wie die Site (≈5 px).
        var barWidth = Math.Min(slot * 0.42, 5);
        var midY = size.Height / 2;
        var maxH = size.Height;
        var omega = 2f * MathF.PI / Cycle;
        var env = Math.Max(IdleFloor, _envelope);

        for (var i = 0; i < n; i++)
        {
            var osc = 0.5f + 0.5f * MathF.Sin(_phase * omega - i * PhaseStep);
            var scale = 0.35f + 0.65f * osc;

            float frac;
            double opacity;
            if (reactive)
            {
                frac = BaseHeights[i] * scale * env;
                // Helligkeit folgt der Live-Höhe — laute Balken glühen mehr.
                opacity = 0.4 + 0.6 * frac;
            }
            else
            {
                // Idle-Boden + Audio-Gain: leise → leichtes Schimmern, laut → voll.
                var gain = 0.12f + 0.88f * _amplitude;
                frac = BaseHeights[i] * scale * gain;
                // Höhere Balken glühen heller; leichter Odd/Even-Versatz trennt Nachbarn.
                opacity = (0.45 + 0.55 * scale) * (i % 2 == 0 ? 0.82 : 1.0);
            }

            var height = Math.Max(barWidth, frac * maxH);
            var x = i * slot + (slot - barWidth) / 2;
            var rect = new Rect(x, midY - height / 2, barWidth, height);

            var brush = new LinearGradientBrush(
                WithOpacity(BarTop, opacity), WithOpacity(BarBottom, opacity), 90.0);
            brush.Freeze();
            var radius = barWidth / 2;
            dc.DrawRoundedRectangle(brush, null, rect, radius, radius);
        }
    }

    /// <summary>Die scrollende Bars-Waveform: Ringpuffer jüngster Pegel als gespiegelte Balken.</summary>
    private void DrawBars(DrawingContext dc, Size size)
    {
        var slot = size.Width / _barHistory.Length;
        var barWidth = slot * 0.55;
        var midY = size.Height / 2;

        for (var i = 0; i < _barHistory.Length; i++)
        {
            var height = Math.Max(barWidth, _barHistory[i] * size.Height);
            var x = i * slot + (slot - barWidth) / 2;
            var rect = new Rect(x, midY - height / 2, barWidth, height);
            var radius = barWidth / 2;
            dc.DrawRoundedRectangle(_tintBrush, null, rect, radius, radius);
        }
    }

    /// <summary>
    /// KITT-Voice-Box: drei Säulen roter LED-Blöcke, um die Mittellinie
    /// gespiegelt. Die Mitte reagiert hart und wird viel höher; die Außensäulen
    /// sind immer gleich und ziehen nach. Jeder Block verblasst zur Spitze hin.
    /// </summary>
    private void DrawKitt(DrawingContext dc, Size size)
    {
        const int n = 3;
        const int halfSegs = KittSegmentsPerHalf;

        var barWidth = size.Width * 0.19;
        var gapX = size.Width * 0.055;
        var total = n * barWidth + (n - 1) * gapX;
        var startX = (size.Width - total) / 2;

        var midY = size.Height / 2;
        const double segGap = 1;
        var usableHalf = size.Height / 2 - 2;
        var segH = (usableHalf - (halfSegs - 1) * segGap) / halfSegs;
        if (segH <= 0) return;

        for (var i = 0; i < n; i++)
        {
            var isCenter = i == 1;
            // Mitte nutzt ihren vollen Pegel; außen kürzer, ein geteilter Wert
            // (links/rechts identisch), der hinterherläuft.
            var frac = isCenter ? _kittCenter : _kittOuter * 0.5;
            var dynamic = (int)Math.Round(frac * halfSegs, MidpointRounding.AwayFromZero);
            // Idle-Grundlinie: 3 Blöcke Mitte, je 1 außen; wächst von dort.
            var baseLit = isCenter ? 3 : 1;
            var lit = Math.Min(halfSegs, Math.Max(baseLit, dynamic));

            var x = startX + i * (barWidth + gapX);
            for (var s = 0; s < lit; s++)
            {
                var bright = Math.Max(0.12, 1.0 - 0.8 * s / (halfSegs - 1));
                var dy = s * (segH + segGap);
                var up = new Rect(x, midY - segGap / 2 - segH - dy, barWidth, segH);
                var down = new Rect(x, midY + segGap / 2 + dy, barWidth, segH);
                var brush = CreateFrozenBrush(WithOpacity(KittRed, bright));
                dc.DrawRoundedRectangle(brush, null, up, 2, 2);
                dc.DrawRoundedRectangle(brush, null, down, 2, 2);
            }
        }
    }

    // MARK: - Helfer

    /// <summary>Deckkraft multiplikativ auf den Alpha-Kanal der Farbe anwenden.</summary>
    private static Color WithOpacity(Color color, double opacity)
    {
        var clamped = Math.Clamp(opacity, 0.0, 1.0);
        return Color.FromArgb((byte)Math.Round(color.A * clamped), color.R, color.G, color.B);
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
